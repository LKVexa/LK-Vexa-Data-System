package main

import (
	"bufio"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/binary"
	"fmt"
	"io"
	"math/big"
	"net"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func testBroker(t *testing.T, tlsConfig *tls.Config) *Broker {
	t.Helper()
	root := testTempDir(t)
	store, e := NewVolumeStore(filepath.Join(root, "server"), true)
	if e != nil {
		t.Fatal(e)
	}
	if e = store.Acquire(); e != nil {
		t.Fatal(e)
	}
	sources, e := NewLocalSources(nil)
	if e != nil {
		t.Fatal(e)
	}
	profile, e := LoadJA21Profile("ja21-profile.ja")
	if e != nil {
		t.Fatal(e)
	}
	scheme := "http"
	if tlsConfig != nil {
		scheme = "https"
	}
	origin := scheme + "://localhost:8877"
	cfg := &Config{Host: "127.0.0.1", Port: 0, Origin: origin, Origins: map[string]bool{origin: true}, Hosts: map[string]bool{"localhost:8877": true}, TLS: tlsConfig}
	b, e := newBroker(root, cfg, store, sources, nil, profile)
	if e != nil {
		t.Fatal(e)
	}
	if e = b.Start(); e != nil {
		t.Fatal(e)
	}
	t.Cleanup(func() {
		b.Stop()
		if e := store.Close(); e != nil {
			t.Error(e)
		}
	})
	return b
}
func dialBroker(t *testing.T, b *Broker) net.Conn {
	t.Helper()
	c, e := net.DialTimeout("tcp", b.listener.Addr().String(), 2*time.Second)
	if e != nil {
		t.Fatal(e)
	}
	_ = c.SetDeadline(time.Now().Add(5 * time.Second))
	return c
}
func httpFixture(t *testing.T, b *Broker, request string) string {
	t.Helper()
	c := dialBroker(t, b)
	defer c.Close()
	if e := writeAll(c, []byte(request)); e != nil {
		t.Fatal(e)
	}
	data, e := io.ReadAll(c)
	if e != nil {
		t.Fatal(e)
	}
	return string(data)
}
func TestHTTPBoundsOriginsReadinessAndMethods(t *testing.T) {
	b := testBroker(t, nil)
	cases := []struct{ name, request, status string }{
		{"liveness", "GET /health HTTP/1.1\r\nHost: localhost:8877\r\n\r\n", "200 OK"},
		{"readiness", "GET /ready HTTP/1.1\r\nHost: localhost:8877\r\n\r\n", "503 Service Unavailable"},
		{"capabilities", "HEAD /capabilities HTTP/1.1\r\nHost: localhost:8877\r\n\r\n", "200 OK"},
		{"bad host", "GET /health HTTP/1.1\r\nHost: attacker.test\r\n\r\n", "403 Forbidden"},
		{"bad origin", "GET /health HTTP/1.1\r\nHost: localhost:8877\r\nOrigin: http://attacker.test\r\n\r\n", "403 Forbidden"},
		{"method", "POST /health HTTP/1.1\r\nHost: localhost:8877\r\n\r\n", "405 Method Not Allowed"},
		{"query", "GET /health?a=b HTTP/1.1\r\nHost: localhost:8877\r\n\r\n", "400 Bad Request"},
		{"duplicate header", "GET /health HTTP/1.1\r\nHost: localhost:8877\r\nX-A: 1\r\nx-a: 2\r\n\r\n", "400 Bad Request"},
		{"body", "GET /health HTTP/1.1\r\nHost: localhost:8877\r\nContent-Length: 1\r\n\r\n", "400 Bad Request"},
		{"transfer encoding", "GET /health HTTP/1.1\r\nHost: localhost:8877\r\nTransfer-Encoding: chunked\r\n\r\n", "400 Bad Request"},
		{"traversal", "GET /%2e%2e/test.js HTTP/1.1\r\nHost: localhost:8877\r\n\r\n", "400 Bad Request"},
		{"double encoding", "GET /%252e%252e/test.js HTTP/1.1\r\nHost: localhost:8877\r\n\r\n", "400 Bad Request"},
		{"control path", "GET /a%09.js HTTP/1.1\r\nHost: localhost:8877\r\n\r\n", "400 Bad Request"},
		{"device path", "GET /CON.js HTTP/1.1\r\nHost: localhost:8877\r\n\r\n", "400 Bad Request"},
		{"missing asset", "GET /missing.js HTTP/1.1\r\nHost: localhost:8877\r\n\r\n", "404 Not Found"},
		{"oversized header", "GET /health HTTP/1.1\r\nHost: localhost:8877\r\nX-Large: " + strings.Repeat("a", 9000) + "\r\n\r\n", "400 Bad Request"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := httpFixture(t, b, tc.request)
			if !strings.HasPrefix(got, "HTTP/1.1 "+tc.status+"\r\n") {
				t.Fatalf("response: %q", got)
			}
			if !strings.Contains(got, "X-Content-Type-Options: nosniff\r\n") || !strings.Contains(got, "Content-Security-Policy:") {
				t.Fatal("response security headers missing")
			}
			if tc.name == "capabilities" && strings.SplitN(got, "\r\n\r\n", 2)[1] != "" {
				t.Fatal("HEAD emitted a body")
			}
		})
	}
}

type wsFixture struct {
	c net.Conn
	r *bufio.Reader
}

func openWS(t *testing.T, b *Broker, path string) *wsFixture {
	t.Helper()
	c := dialBroker(t, b)
	r := bufio.NewReader(c)
	request := "GET " + path + " HTTP/1.1\r\nHost: localhost:8877\r\nOrigin: " + b.cfg.Origin + "\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: AAECAwQFBgcICQoLDA0ODw==\r\nSec-WebSocket-Protocol: hermit.vws.v2\r\n\r\n"
	if e := writeAll(c, []byte(request)); e != nil {
		t.Fatal(e)
	}
	status, e := r.ReadString('\n')
	if e != nil || !strings.HasPrefix(status, "HTTP/1.1 101 ") {
		t.Fatalf("upgrade: %s %v", status, e)
	}
	for {
		line, e := r.ReadString('\n')
		if e != nil {
			t.Fatal(e)
		}
		if line == "\r\n" {
			break
		}
	}
	return &wsFixture{c, r}
}
func (w *wsFixture) send(t *testing.T, value any) {
	t.Helper()
	data, e := jsonBytes(value)
	if e != nil {
		t.Fatal(e)
	}
	if e = writeAll(w.c, maskedFrame(1, true, data)); e != nil {
		t.Fatal(e)
	}
}
func (w *wsFixture) receive(t *testing.T) map[string]any {
	t.Helper()
	data, op, e := readServerFrame(w.r)
	if e != nil || op != 1 {
		t.Fatalf("JSON frame opcode=%d payload=%q error=%v", op, data, e)
	}
	v, e := strictJSON(data)
	if e != nil {
		t.Fatal(e)
	}
	m, ok := v.(map[string]any)
	if !ok {
		t.Fatal("non-object frame")
	}
	return m
}
func (w *wsFixture) closeCode(t *testing.T) uint16 {
	t.Helper()
	data, op, e := readServerFrame(w.r)
	if e != nil || op != 8 || len(data) < 2 {
		t.Fatalf("close frame opcode=%d payload=%q error=%v", op, data, e)
	}
	return binary.BigEndian.Uint16(data)
}
func TestWebSocketAuthorizationReadinessAndRelay(t *testing.T) {
	b := testBroker(t, nil)
	unready := openWS(t, b, "/socket")
	unready.send(t, map[string]any{"type": "auth", "token": b.token})
	if code := unready.closeCode(t); code != 1013 {
		t.Fatalf("unready code %d", code)
	}
	unready.c.Close()
	wrong := openWS(t, b, "/engine")
	wrong.send(t, map[string]any{"type": "auth", "token": b.token})
	if code := wrong.closeCode(t); code != 1008 {
		t.Fatalf("wrong credential code %d", code)
	}
	wrong.c.Close()
	engine := openWS(t, b, "/engine")
	defer engine.c.Close()
	engine.send(t, map[string]any{"type": "auth", "token": b.engineToken})
	if m := engine.receive(t); m["type"] != "engine.authenticated" {
		t.Fatalf("engine auth %v", m)
	}
	engine.send(t, map[string]any{"type": "engine.ready"})
	select {
	case <-b.Ready:
	case <-time.After(time.Second):
		t.Fatal("ready state did not transition")
	}
	if got := httpFixture(t, b, "GET /ready HTTP/1.1\r\nHost: localhost:8877\r\n\r\n"); !strings.HasPrefix(got, "HTTP/1.1 200 OK") {
		t.Fatalf("ready response %q", got)
	}
	second := openWS(t, b, "/engine")
	second.send(t, map[string]any{"type": "auth", "token": b.engineToken})
	if code := second.closeCode(t); code != 1008 {
		t.Fatalf("second owner code %d", code)
	}
	second.c.Close()
	client := openWS(t, b, "/socket")
	defer client.c.Close()
	client.send(t, map[string]any{"type": "auth", "token": b.token})
	opened := engine.receive(t)
	if opened["type"] != "peer.open" {
		t.Fatalf("peer open %v", opened)
	}
	id, _ := opened["id"].(string)
	if len(id) != 24 {
		t.Fatalf("peer ID %q", id)
	}
	client.send(t, map[string]any{"command": "help"})
	input := engine.receive(t)
	if input["type"] != "peer.data" || input["id"] != id || input["binary"] != false {
		t.Fatalf("peer data %v", input)
	}
	engine.send(t, map[string]any{"type": "peer.send", "id": id, "binary": false, "data": "terminal fixture"})
	data, op, e := readServerFrame(client.r)
	if e != nil || op != 1 || string(data) != "terminal fixture" {
		t.Fatalf("terminal relay %q %d %v", data, op, e)
	}
	if e = writeAll(client.c, maskedFrame(2, true, []byte{0, 255, 4})); e != nil {
		t.Fatal(e)
	}
	input = engine.receive(t)
	if input["binary"] != true || input["data"] != "AP8E" {
		t.Fatalf("binary peer data %v", input)
	}
	engine.send(t, map[string]any{"type": "peer.send", "id": id, "binary": true, "data": "AQID"})
	data, op, e = readServerFrame(client.r)
	if e != nil || op != 2 || string(data) != string([]byte{1, 2, 3}) {
		t.Fatalf("binary output %v %d %v", data, op, e)
	}
	engine.send(t, map[string]any{"type": "rpc", "rid": "read1", "op": "store.read", "args": map[string]any{}})
	answer := engine.receive(t)
	if answer["type"] != "rpc.result" || answer["rid"] != "read1" || answer["ok"] != true || answer["result"] != nil {
		t.Fatalf("initial store %v", answer)
	}
	engine.send(t, map[string]any{"type": "rpc", "rid": "reject1", "op": "host.exec", "args": map[string]any{}})
	answer = engine.receive(t)
	if answer["ok"] != false {
		t.Fatalf("unknown native capability admitted %v", answer)
	}
	engine.send(t, map[string]any{"type": "engine.halted", "reason": "poweroff"})
	engine.send(t, map[string]any{"type": "engine.ping"})
	if m := engine.receive(t); m["type"] != "engine.pong" {
		t.Fatal(m)
	}
	if b.IsReady() {
		t.Fatal("halt left readiness true")
	}
	engine.send(t, map[string]any{"type": "engine.stopped", "ok": true})
	engine.send(t, map[string]any{"type": "engine.ping"})
	_ = engine.receive(t)
	if !b.Clean() {
		t.Fatal("shutdown acknowledgement not retained")
	}
}
func TestBrokerShutdownRequestsPersistenceAcknowledgement(t *testing.T) {
	b := testBroker(t, nil)
	engine := openWS(t, b, "/engine")
	defer engine.c.Close()
	engine.send(t, map[string]any{"type": "auth", "token": b.engineToken})
	_ = engine.receive(t)
	engine.send(t, map[string]any{"type": "engine.ready"})
	<-b.Ready
	done := make(chan struct{})
	go func() { b.Stop(); close(done) }()
	if m := engine.receive(t); m["type"] != "engine.stop" {
		t.Fatalf("shutdown request %v", m)
	}
	engine.send(t, map[string]any{"type": "engine.stopped", "ok": true})
	_, _, _ = readServerFrame(engine.r)
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("shutdown did not complete after acknowledgement")
	}
	if !b.Clean() {
		t.Fatal("clean save outcome lost")
	}
}
func TestTLSBrokerUsesValidatedTLSAndRejectsOldProtocol(t *testing.T) {
	key, e := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if e != nil {
		t.Fatal(e)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "localhost"}, DNSNames: []string{"localhost"}, NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}, BasicConstraintsValid: true}
	der, e := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if e != nil {
		t.Fatal(e)
	}
	cert, e := x509.ParseCertificate(der)
	if e != nil {
		t.Fatal(e)
	}
	pool := x509.NewCertPool()
	pool.AddCert(cert)
	b := testBroker(t, &tls.Config{Certificates: []tls.Certificate{{Certificate: [][]byte{der}, PrivateKey: key}}, MinVersion: tls.VersionTLS12})
	c, e := tls.DialWithDialer(&net.Dialer{Timeout: 2 * time.Second}, "tcp", b.listener.Addr().String(), &tls.Config{RootCAs: pool, ServerName: "localhost", MinVersion: tls.VersionTLS12})
	if e != nil {
		t.Fatal(e)
	}
	defer c.Close()
	_ = c.SetDeadline(time.Now().Add(3 * time.Second))
	_ = writeAll(c, []byte("GET /health HTTP/1.1\r\nHost: localhost:8877\r\n\r\n"))
	body, e := io.ReadAll(c)
	if e != nil || !strings.HasPrefix(string(body), "HTTP/1.1 200 OK") {
		t.Fatalf("TLS health %s %v", body, e)
	}
	old, e := tls.DialWithDialer(&net.Dialer{Timeout: 2 * time.Second}, "tcp", b.listener.Addr().String(), &tls.Config{RootCAs: pool, ServerName: "localhost", MinVersion: tls.VersionTLS10, MaxVersion: tls.VersionTLS10})
	if e == nil {
		old.Close()
		t.Fatal("TLS1.0 was accepted")
	}
}
func TestHTTPParserRetainsFrameCoalescedWithUpgrade(t *testing.T) {
	b := testBroker(t, nil)
	c := dialBroker(t, b)
	defer c.Close()
	auth, _ := jsonBytes(map[string]any{"type": "auth", "token": b.engineToken})
	request := fmt.Sprintf("GET /engine HTTP/1.1\r\nHost: localhost:8877\r\nOrigin: %s\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: AAECAwQFBgcICQoLDA0ODw==\r\n\r\n", b.cfg.Origin)
	if e := writeAll(c, append([]byte(request), maskedFrame(1, true, auth)...)); e != nil {
		t.Fatal(e)
	}
	r := bufio.NewReader(c)
	for {
		line, e := r.ReadString('\n')
		if e != nil {
			t.Fatal(e)
		}
		if line == "\r\n" {
			break
		}
	}
	w := &wsFixture{c, r}
	if m := w.receive(t); m["type"] != "engine.authenticated" {
		t.Fatal(m)
	}
	w.send(t, map[string]any{"type": "engine.stopped", "ok": true})
	w.send(t, map[string]any{"type": "engine.ping"})
	_ = w.receive(t)
}

func TestStandaloneBrokerAssignsLoopbackOriginAndRejectsOverrides(t *testing.T) {
	for name, cfg := range map[string]*Config{
		"nonloopback":       {Host: "0.0.0.0", Port: 0, Job: map[string]any{}},
		"fixed port":        {Host: "127.0.0.1", Port: 8877, Job: map[string]any{}},
		"TLS override":      {Host: "127.0.0.1", Port: 0, TLS: &tls.Config{}, Job: map[string]any{}},
		"advertised origin": {Host: "127.0.0.1", Port: 0, Origin: "http://127.0.0.1:8877", Job: map[string]any{}},
		"additional origin": {Host: "127.0.0.1", Port: 0, Origins: map[string]bool{"http://attacker.test": true}, Job: map[string]any{}},
	} {
		t.Run(name, func(t *testing.T) {
			b := &Broker{cfg: cfg}
			if e := b.Start(); e == nil {
				b.Stop()
				t.Fatal("standalone override admitted")
			}
		})
	}
	cfg := &Config{Host: "127.0.0.1", Port: 0, Job: map[string]any{}}
	b := &Broker{cfg: cfg, connections: map[net.Conn]bool{}}
	if e := b.Start(); e != nil {
		t.Fatal(e)
	}
	defer b.Stop()
	address, ok := b.listener.Addr().(*net.TCPAddr)
	if !ok || !address.IP.IsLoopback() || address.Port == 0 || cfg.Port != address.Port {
		t.Fatalf("listener address: %v config%+v", b.listener.Addr(), cfg)
	}
	host := net.JoinHostPort("127.0.0.1", fmt.Sprint(address.Port))
	want := "http://" + host
	if cfg.Origin != want || len(cfg.Origins) != 1 || !cfg.Origins[want] || len(cfg.Hosts) != 1 || !cfg.Hosts[host] {
		t.Fatalf("bound origin/Host allowlists: %+v", cfg)
	}
}
