package main

import (
	"bufio"
	"crypto/rand"
	"crypto/sha1"
	"crypto/subtle"
	"crypto/tls"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"mime"
	"net"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
)

type JobResult struct {
	Report map[string]any
	Err    error
}
type Broker struct {
	root                   string
	cfg                    *Config
	store                  *VolumeStore
	sources                *LocalSources
	photon                 *PhotonBridge
	profile                *JA21Program
	token, engineToken     string
	mu                     sync.Mutex
	owner                  *WS
	peers                  map[string]*WS
	phase                  string
	ready, clean, shutting bool
	nativeOps              int
	connections            map[net.Conn]bool
	listener               net.Listener
	wg                     sync.WaitGroup
	readyOnce              sync.Once
	Ready                  chan struct{}
	stopped                chan struct{}
	stopOnce               sync.Once
	jobOnce                sync.Once
	JobDone                chan JobResult
	jobCapture             io.Writer
	outputBytes            int64
}

func newBroker(root string, cfg *Config, store *VolumeStore, sources *LocalSources, photon *PhotonBridge, profile *JA21Program) (*Broker, error) {
	t, e := randomHex(32)
	if e != nil {
		return nil, e
	}
	et, e := randomHex(32)
	if e != nil {
		return nil, e
	}
	return &Broker{root: root, cfg: cfg, store: store, sources: sources, photon: photon, profile: profile, token: t, engineToken: et, peers: map[string]*WS{}, phase: "disconnected", connections: map[net.Conn]bool{}, Ready: make(chan struct{}), stopped: make(chan struct{}), JobDone: make(chan JobResult, 1)}, nil
}
func randomHex(n int) (string, error) {
	b := make([]byte, n)
	if _, e := rand.Read(b); e != nil {
		return "", e
	}
	return hex.EncodeToString(b), nil
}
func (b *Broker) Start() error {
	if b.cfg.Job != nil && (b.cfg.Host != "127.0.0.1" || b.cfg.Port != 0 || b.cfg.TLS != nil || b.cfg.Origin != "" || len(b.cfg.Origins) != 0 || len(b.cfg.Hosts) != 0) {
		return errors.New("standalone VCMD must use an isolated loopback listener with an OS-assigned port and no network overrides")
	}
	l, e := net.Listen("tcp", net.JoinHostPort(b.cfg.Host, fmt.Sprint(b.cfg.Port)))
	if e != nil {
		return e
	}
	if b.cfg.Job != nil {
		address, ok := l.Addr().(*net.TCPAddr)
		if !ok || !address.IP.IsLoopback() || address.Port < 1 {
			_ = l.Close()
			return errors.New("standalone listener did not bind a loopback ephemeral port")
		}
		b.cfg.Port = address.Port
		host := net.JoinHostPort("127.0.0.1", fmt.Sprint(address.Port))
		b.cfg.Origin = "http://" + host
		b.cfg.Origins = map[string]bool{b.cfg.Origin: true}
		b.cfg.Hosts = map[string]bool{host: true}
	}
	if b.cfg.TLS != nil {
		l = tls.NewListener(l, b.cfg.TLS)
	}
	b.listener = l
	go func() {
		for {
			c, e := l.Accept()
			if e != nil {
				return
			}
			b.mu.Lock()
			if b.shutting {
				b.mu.Unlock()
				_ = c.Close()
				return
			}
			b.connections[c] = true
			b.wg.Add(1)
			b.mu.Unlock()
			go func() { defer b.wg.Done(); b.handle(c) }()
		}
	}()
	return nil
}
func (b *Broker) Stop() {
	b.stopOnce.Do(func() {
		b.mu.Lock()
		b.shutting = true
		b.ready = false
		owner := b.owner
		clean := b.clean
		stopped := b.stopped
		b.mu.Unlock()
		if b.listener != nil {
			_ = b.listener.Close()
		}
		if owner != nil && !clean {
			budget := time.NewTimer(8 * time.Second)
			sent := make(chan error, 1)
			go func() { sent <- owner.Obj(map[string]any{"type": "engine.stop"}) }()
			select {
			case e := <-sent:
				if e == nil {
					select {
					case <-stopped:
					case <-budget.C:
					}
				}
			case <-budget.C:
				_ = owner.conn.Close()
			}
			budget.Stop()
		}
		b.mu.Lock()
		peers := make([]*WS, 0, len(b.peers))
		for _, w := range b.peers {
			peers = append(peers, w)
		}
		b.mu.Unlock()
		if owner != nil {
			peers = append(peers, owner)
		}
		closeSockets(peers, 1001)
		b.mu.Lock()
		for c := range b.connections {
			_ = c.Close()
		}
		b.mu.Unlock()
		b.wg.Wait()
	})
}
func (b *Broker) IsReady() bool { b.mu.Lock(); defer b.mu.Unlock(); return b.ready && !b.shutting }
func (b *Broker) Clean() bool   { b.mu.Lock(); defer b.mu.Unlock(); return b.clean }
func (b *Broker) capabilities() map[string]any {
	b.mu.Lock()
	phase := b.phase
	ready := b.ready && !b.shutting
	b.mu.Unlock()
	return map[string]any{"schema": "JA21-CAPABILITIES/1", "version": ReleaseVersion, "product": "JA21 Photon Virtual Server / Terminal / Console", "execution": "browser-backed virtual command server", "nativeWindowRequired": true, "headlessCommandEngine": false, "nodejsRequired": false, "pythonRequired": false, "hostShell": false, "physicalInfrastructureExecution": false, "photonScope": "supplied identity, state/ledger verification and snapshots", "authorization": "shared client token; separate owner token; not per-user tenancy", "transportEncrypted": b.cfg.TLS != nil, "enginePhase": phase, "engineReady": ready, "maxTerminalConnections": 8, "volumeMaxBytes": 2097152, "volumeMaxNodes": 1500, "nativeSourceRootsGranted": b.sources.RootsCount(), "vcmdGrammar": 1, "serviceHost": "compiled native Go adapter", "ja21ServiceProfileRequired": true}
}
func (b *Broker) allow(op, method, path string, token, owner, loopback bool, body int64) error {
	if b.profile == nil {
		return errors.New("JA21 service profile is unavailable")
	}
	transport := "http"
	if b.cfg.TLS != nil {
		transport = "https"
	}
	if strings.HasPrefix(op, "ws.") {
		transport = strings.Replace(transport, "http", "ws", 1)
	}
	d, e := b.profile.Evaluate(JA21Facts{Transport: transport, Method: method, Path: path, Operation: op, TokenValid: token, Loopback: loopback, BodyBytes: body, OriginValid: true, EngineReady: b.IsReady(), Owner: owner})
	if e != nil {
		return e
	}
	if !d.Allowed || d.Operation != op {
		return fmt.Errorf("JA21 profile denied %s: %s", op, d.Reason)
	}
	return nil
}

type request struct {
	method, target, path string
	fields               map[string]string
}

var headerName = regexp.MustCompile("^[a-z0-9!#$%&'*+.^_`|~-]+$")
var targetBad = regexp.MustCompile(`[\x00-\x20\x7f\\]`)
var headerBad = regexp.MustCompile(`[\x00-\x08\x0a-\x1f\x7f]`)
var staticControl = regexp.MustCompile(`[\x00-\x1f\x7f]`)

func readRequest(r *bufio.Reader) (*request, error) {
	lines := []string{}
	total := 0
	for {
		line, e := r.ReadSlice('\n')
		total += len(line)
		if total > 8192 {
			return nil, wireError("header limit")
		}
		if e != nil {
			return nil, e
		}
		if len(line) < 2 || line[len(line)-2] != '\r' {
			return nil, wireError("header line ending")
		}
		for _, c := range line {
			if c > 127 {
				return nil, wireError("non-ASCII header")
			}
		}
		s := string(line[:len(line)-2])
		lines = append(lines, s)
		if len(lines) > 44 {
			return nil, wireError("header count")
		}
		if s == "" {
			break
		}
	}
	parts := strings.Split(lines[0], " ")
	if len(parts) != 3 || parts[2] != "HTTP/1.1" {
		return nil, wireError("request line")
	}
	q := &request{method: parts[0], target: parts[1], fields: map[string]string{}}
	if !strings.HasPrefix(q.target, "/") || strings.HasPrefix(q.target, "//") || len(q.target) > 2048 || targetBad.MatchString(q.target) {
		return nil, wireError("request target")
	}
	for _, line := range lines[1 : len(lines)-1] {
		p := strings.IndexByte(line, ':')
		if p < 1 {
			return nil, wireError("header syntax")
		}
		k := strings.ToLower(line[:p])
		v := line[p+1:]
		if !headerName.MatchString(k) || headerBad.MatchString(v) {
			return nil, wireError("header identity")
		}
		if _, ok := q.fields[k]; ok {
			return nil, wireError("duplicate header")
		}
		q.fields[k] = strings.Trim(v, " \t")
	}
	u, e := url.Parse(q.target)
	if e != nil {
		return nil, e
	}
	if u.RawQuery != "" || u.ForceQuery || u.Fragment != "" || strings.Contains(q.target, "#") {
		return nil, wireError("query/fragment refused")
	}
	q.path = u.EscapedPath()
	if q.path == "" {
		q.path = "/"
	}
	return q, nil
}
func (b *Broker) http(c net.Conn, status string, body []byte, mimetype string, head bool) error {
	if mimetype == "" {
		mimetype = "text/plain; charset=utf-8"
	}
	connect := []string{}
	for o := range b.cfg.Origins {
		connect = append(connect, o, strings.Replace(o, "http", "ws", 1))
	}
	sort.Strings(connect)
	csp := "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; worker-src 'self'; connect-src 'self' " + strings.Join(connect, " ") + "; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"
	h := fmt.Sprintf("HTTP/1.1 %s\r\nContent-Length: %d\r\nContent-Type: %s\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: %s\r\n\r\n", status, len(body), mimetype, csp)
	_ = c.SetWriteDeadline(time.Now().Add(10 * time.Second))
	if e := writeAll(c, []byte(h)); e != nil {
		return e
	}
	if !head {
		return writeAll(c, body)
	}
	return nil
}
func (b *Broker) handle(c net.Conn) {
	var ws *WS
	defer func() {
		if ws != nil {
			ws.CloseBounded(1000, 250*time.Millisecond)
		} else {
			_ = c.Close()
		}
		b.mu.Lock()
		delete(b.connections, c)
		b.mu.Unlock()
	}()
	b.mu.Lock()
	busy := len(b.connections) > 32
	b.mu.Unlock()
	if busy {
		_ = b.http(c, "503 Busy", nil, "", false)
		return
	}
	_ = c.SetReadDeadline(time.Now().Add(10 * time.Second))
	r := bufio.NewReaderSize(c, 16384)
	q, e := readRequest(r)
	if e != nil {
		_ = b.http(c, "400 Bad Request", nil, "", false)
		return
	}
	if q.method != "GET" && q.method != "HEAD" {
		_ = b.http(c, "405 Method Not Allowed", nil, "", false)
		return
	}
	if !b.cfg.Hosts[q.fields["host"]] || (q.fields["origin"] != "" && !b.cfg.Origins[q.fields["origin"]]) {
		_ = b.http(c, "403 Forbidden", nil, "", false)
		return
	}
	if _, ok := q.fields["transfer-encoding"]; ok {
		_ = b.http(c, "400 Bad Request", nil, "", false)
		return
	}
	if v, ok := q.fields["content-length"]; ok && v != "0" {
		_ = b.http(c, "400 Bad Request", nil, "", false)
		return
	}
	remote, _, _ := net.SplitHostPort(c.RemoteAddr().String())
	loopback := net.ParseIP(remote) != nil && net.ParseIP(remote).IsLoopback()
	if q.path == "/socket" || q.path == "/engine" {
		if q.method != "GET" || !b.cfg.Origins[q.fields["origin"]] || !strings.EqualFold(q.fields["upgrade"], "websocket") || !hasToken(q.fields["connection"], "upgrade") || q.fields["sec-websocket-version"] != "13" {
			_ = b.http(c, "403 Forbidden", nil, "", false)
			return
		}
		key := q.fields["sec-websocket-key"]
		decoded, e := decodeBase64(key)
		if e != nil || len(decoded) != 16 {
			_ = b.http(c, "400 Bad Request", nil, "", false)
			return
		}
		proto, protoPresent := q.fields["sec-websocket-protocol"]
		if protoPresent && proto != "hermit.vws.v2" {
			_ = b.http(c, "400 Bad Request", nil, "", false)
			return
		}
		h := sha1.Sum([]byte(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))
		sub := ""
		if proto != "" {
			sub = "Sec-WebSocket-Protocol: hermit.vws.v2\r\n"
		}
		_ = c.SetWriteDeadline(time.Now().Add(10 * time.Second))
		if e = writeAll(c, []byte("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "+base64.StdEncoding.EncodeToString(h[:])+"\r\n"+sub+"\r\n")); e != nil {
			return
		}
		ws = newWS(c, r, 512)
		ws.authUntil = time.Now().Add(5 * time.Second)
		raw, kind, e := ws.Receive()
		if e != nil || kind != 1 {
			ws.Close(1008)
			return
		}
		v, e := strictJSON(raw)
		a, ok := v.(map[string]any)
		if e != nil || !ok || len(a) != 2 || a["type"] != "auth" {
			ws.Close(1008)
			return
		}
		token, ok := a["token"].(string)
		wanted := b.token
		op := "ws.client"
		if q.path == "/engine" {
			wanted = b.engineToken
			op = "ws.engine"
		}
		if !ok || subtle.ConstantTimeCompare([]byte(token), []byte(wanted)) != 1 {
			ws.Close(1008)
			return
		}
		if q.path == "/socket" && !b.IsReady() {
			ws.Close(1013)
			return
		}
		if e = b.allow(op, q.method, q.path, true, q.path == "/engine", loopback, int64(len(raw))); e != nil {
			ws.Close(1008)
			return
		}
		ws.authUntil = time.Time{}
		if q.path == "/engine" {
			ws.limit = 8 * 1024 * 1024
			e = b.engine(ws, loopback)
		} else {
			ws.limit = 6 * 1024 * 1024
			ws.receiveLimit = 16384
			e = b.client(ws)
		}
		if e != nil && !isDisconnect(e) {
			ws.Close(1008)
		}
		return
	}
	op := "http.static"
	switch q.path {
	case "/health":
		op = "http.health"
	case "/ready":
		op = "http.ready"
	case "/capabilities":
		op = "http.capabilities"
	}
	if e = b.allow(op, q.method, q.path, false, false, loopback, 0); e != nil {
		_ = b.http(c, "403 Forbidden", nil, "", false)
		return
	}
	if op != "http.static" {
		ready := b.IsReady()
		b.mu.Lock()
		phase := b.phase
		b.mu.Unlock()
		var value any
		if q.path == "/capabilities" {
			value = b.capabilities()
		} else {
			probe := "liveness"
			if q.path == "/ready" {
				probe = "readiness"
			}
			value = map[string]any{"version": ReleaseVersion, "transport": "Photon/native Go broker", "engineReady": ready, "enginePhase": phase, "nodejsRequired": false, "pythonRequired": false, "probe": probe}
		}
		body, e := jsonBytes(value)
		if e != nil {
			_ = b.http(c, "500 Internal Server Error", nil, "", false)
			return
		}
		status := "200 OK"
		if q.path == "/ready" && !ready {
			status = "503 Service Unavailable"
		}
		_ = b.http(c, status, body, "application/json", q.method == "HEAD")
		return
	}
	decoded, e := url.PathUnescape(q.path)
	if e != nil || strings.ContainsAny(decoded, "\\%") || staticControl.MatchString(decoded) {
		_ = b.http(c, "400 Bad Request", nil, "", false)
		return
	}
	rel := strings.TrimLeft(decoded, "/")
	if rel == "" {
		rel = "index.html"
	}
	for _, part := range strings.Split(rel, "/") {
		if !safeStaticComponent(part) {
			_ = b.http(c, "400 Bad Request", nil, "", false)
			return
		}
	}
	ext := filepath.Ext(rel)
	allowed := map[string]bool{".html": true, ".js": true, ".css": true, ".json": true, ".svg": true, ".png": true, ".ico": true, ".vcmd": true}
	if !allowed[ext] {
		_ = b.http(c, "404 Not Found", nil, "", false)
		return
	}
	p := filepath.Join(b.root, "appliance", "web", filepath.FromSlash(rel))
	body, e := readRegular(p, 6*1024*1024, false)
	if e != nil {
		_ = b.http(c, "404 Not Found", nil, "", false)
		return
	}
	mt := map[string]string{".js": "text/javascript", ".css": "text/css", ".html": "text/html", ".json": "application/json", ".svg": "image/svg+xml", ".vcmd": "text/plain"}[ext]
	if mt == "" {
		mt = mime.TypeByExtension(ext)
		if mt == "" {
			mt = "application/octet-stream"
		}
	}
	_ = b.http(c, "200 OK", body, mt, q.method == "HEAD")
}
func hasToken(s, want string) bool {
	for _, p := range strings.Split(s, ",") {
		if strings.EqualFold(strings.TrimSpace(p), want) {
			return true
		}
	}
	return false
}

var deviceName = regexp.MustCompile(`(?i)^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$`)

func safeStaticComponent(p string) bool {
	return p != "" && p != "." && p != ".." && !strings.ContainsAny(p, ":<>\"|?*") && !strings.HasSuffix(p, ".") && !strings.HasSuffix(p, " ") && !deviceName.MatchString(strings.SplitN(p, ".", 2)[0])
}
func decodeBase64(s string) ([]byte, error) {
	v, e := base64.StdEncoding.Strict().DecodeString(s)
	if e != nil || base64.StdEncoding.EncodeToString(v) != s {
		return nil, errors.New("noncanonical base64")
	}
	return v, nil
}

func (b *Broker) client(w *WS) error {
	pid, e := randomHex(12)
	if e != nil {
		return e
	}
	b.mu.Lock()
	owner := b.owner
	if b.shutting || !b.ready || owner == nil || len(b.peers) >= 8 {
		b.mu.Unlock()
		w.Close(1013)
		return nil
	}
	b.peers[pid] = w
	b.mu.Unlock()
	defer func() {
		b.mu.Lock()
		delete(b.peers, pid)
		same := b.owner == owner
		b.mu.Unlock()
		if same {
			_ = owner.Obj(map[string]any{"type": "peer.close", "id": pid})
		}
	}()
	if e = owner.Obj(map[string]any{"type": "peer.open", "id": pid}); e != nil {
		return e
	}
	window := time.Now()
	count, total := 0, 0
	for {
		data, op, e := w.Receive()
		if e != nil {
			return e
		}
		if len(data) > 16384 {
			return wireError("client input limit")
		}
		if time.Since(window) >= time.Second {
			window = time.Now()
			count, total = 0, 0
		}
		count++
		total += len(data)
		if count > 250 || total > 65536 {
			return wireError("client input rate")
		}
		value := string(data)
		if op == 2 {
			value = base64.StdEncoding.EncodeToString(data)
		}
		if e = owner.Obj(map[string]any{"type": "peer.data", "id": pid, "binary": op == 2, "data": value}); e != nil {
			return e
		}
	}
}
func (b *Broker) engine(w *WS, loopback bool) error {
	b.mu.Lock()
	if b.owner != nil {
		b.mu.Unlock()
		w.Close(1008)
		return nil
	}
	if b.shutting || b.nativeOps > 0 {
		b.mu.Unlock()
		w.Close(1013)
		return nil
	}
	b.owner = w
	b.phase = "initializing"
	b.clean = false
	b.ready = false
	b.stopped = make(chan struct{})
	b.mu.Unlock()
	timer := time.AfterFunc(30*time.Second, func() {
		b.mu.Lock()
		close := b.owner == w && !b.ready
		b.mu.Unlock()
		if close {
			w.Close(1013)
		}
	})
	defer timer.Stop()
	defer func() {
		b.mu.Lock()
		if b.owner != w {
			b.mu.Unlock()
			return
		}
		b.owner = nil
		b.phase = "disconnected"
		b.ready = false
		peers := make([]*WS, 0, len(b.peers))
		for _, p := range b.peers {
			peers = append(peers, p)
		}
		b.mu.Unlock()
		closeSockets(peers, 1011)
		if b.cfg.Job != nil {
			b.finishJob(nil, errors.New("engine disconnected before script completion"))
		}
	}()
	if e := w.Obj(map[string]any{"type": "engine.authenticated", "version": ReleaseVersion}); e != nil {
		return e
	}
	rateAt := time.Now()
	rpcCount := 0
	for {
		raw, op, e := w.Receive()
		if e != nil {
			return e
		}
		if op != 1 {
			return wireError("engine JSON required")
		}
		value, e := strictJSON(raw)
		m, ok := value.(map[string]any)
		if e != nil || !ok {
			return wireError("engine message")
		}
		kind, _ := m["type"].(string)
		switch kind {
		case "rpc":
			b.mu.Lock()
			stopped := b.phase == "stopped"
			b.mu.Unlock()
			if stopped {
				return wireError("RPC after shutdown acknowledgement")
			}
			rid, ok := m["rid"].(string)
			rpcOp, opOK := m["op"].(string)
			if !ok || len(rid) < 1 || len(rid) > 80 || !opOK {
				return wireError("RPC shape")
			}
			args := map[string]any{}
			if v, exists := m["args"]; exists {
				args, ok = v.(map[string]any)
				if !ok {
					return wireError("RPC arguments")
				}
			}
			if time.Since(rateAt) > time.Second {
				rateAt = time.Now()
				rpcCount = 0
			}
			rpcCount++
			if rpcCount > 100 {
				return wireError("RPC rate")
			}
			result, callErr := b.rpc(rpcOp, args, loopback, int64(len(raw)))
			answer := map[string]any{"type": "rpc.result", "rid": rid, "ok": callErr == nil}
			if callErr != nil {
				message := callErr.Error()
				if len(message) > 400 {
					message = message[:400]
				}
				answer["error"] = message
			} else {
				answer["result"] = result
			}
			if e = w.Obj(answer); e != nil {
				return e
			}
		case "engine.ready":
			b.mu.Lock()
			valid := b.phase == "initializing" && !b.ready && !b.shutting
			if valid {
				b.phase = "ready"
				b.clean = false
				b.ready = true
			}
			b.mu.Unlock()
			if !valid {
				return wireError("invalid ready transition")
			}
			timer.Stop()
			b.readyOnce.Do(func() { close(b.Ready) })
			if b.cfg.Job != nil {
				if e = w.Obj(map[string]any{"type": "job", "job": b.cfg.Job}); e != nil {
					return e
				}
			}
		case "engine.halted":
			b.mu.Lock()
			valid := b.phase == "ready" && m["reason"] == "poweroff"
			if valid {
				b.phase = "halted"
				b.ready = false
			}
			b.mu.Unlock()
			if !valid {
				return wireError("invalid halt transition")
			}
		case "engine.stopped":
			b.mu.Lock()
			b.phase = "stopped"
			b.clean = m["ok"] == true
			b.ready = false
			select {
			case <-b.stopped:
			default:
				close(b.stopped)
			}
			b.mu.Unlock()
		case "engine.ping":
			if e = w.Obj(map[string]any{"type": "engine.pong"}); e != nil {
				return e
			}
		case "peer.send":
			id, _ := m["id"].(string)
			s, ok := m["data"].(string)
			binary, bok := m["binary"].(bool)
			if !ok || !bok {
				return wireError("peer output")
			}
			b.mu.Lock()
			peer := b.peers[id]
			b.mu.Unlock()
			if peer != nil {
				data := []byte(s)
				opcode := byte(1)
				if binary {
					data, e = decodeBase64(s)
					if e != nil {
						return e
					}
					opcode = 2
				}
				if e = peer.Send(data, opcode); e != nil {
					peer.Close(1008)
				}
			}
		case "peer.close":
			id, _ := m["id"].(string)
			b.mu.Lock()
			peer := b.peers[id]
			b.mu.Unlock()
			if peer != nil {
				peer.Close(1000)
			}
		case "job.output":
			if b.cfg.Job == nil {
				return wireError("unexpected job output")
			}
			s, ok := m["data"].(string)
			stream, _ := m["stream"].(string)
			if !ok || (stream != "stdout" && stream != "stderr") {
				return wireError("job output")
			}
			b.outputBytes += int64(len(s))
			if b.outputBytes > 2097152 {
				return wireError("job output limit")
			}
			dest := os.Stdout
			if stream == "stderr" {
				dest = os.Stderr
			}
			var output io.Writer = dest
			if b.jobCapture != nil {
				output = io.MultiWriter(dest, b.jobCapture)
			}
			if _, e = io.WriteString(output, s); e != nil {
				return e
			}
		case "job.done":
			if b.cfg.Job == nil {
				return wireError("unexpected job completion")
			}
			report, ok := m["report"].(map[string]any)
			if !ok {
				return wireError("job status")
			}
			code, e := parseInteger(report["code"])
			if e != nil || code < 0 || code > 255 {
				return wireError("job status")
			}
			if !b.finishJob(report, nil) {
				return wireError("duplicate job completion")
			}
		default:
			return wireError("unknown engine message")
		}
	}
}
func closeSockets(sockets []*WS, code uint16) {
	var wg sync.WaitGroup
	for _, w := range sockets {
		wg.Add(1)
		go func(w *WS) { defer wg.Done(); w.CloseBounded(code, 250*time.Millisecond) }(w)
	}
	wg.Wait()
}
func (b *Broker) finishJob(report map[string]any, e error) bool {
	sent := false
	b.jobOnce.Do(func() { sent = true; b.JobDone <- JobResult{Report: report, Err: e} })
	return sent
}
func (b *Broker) rpc(op string, args map[string]any, loopback bool, size int64) (any, error) {
	policyOp := op
	action := ""
	if op == "photon" {
		action, _ = args["action"].(string)
		policyOp = "photon." + action
	}
	if e := b.allow(policyOp, "RPC", "/engine", true, true, loopback, size); e != nil {
		return nil, e
	}
	b.mu.Lock()
	b.nativeOps++
	b.mu.Unlock()
	defer func() { b.mu.Lock(); b.nativeOps--; b.mu.Unlock() }()
	switch op {
	case "store.read":
		if len(args) == 0 {
			return b.store.Read()
		}
	case "store.write":
		if v, ok := args["record"]; ok && len(args) == 1 {
			if e := b.store.Write(v); e != nil {
				return nil, e
			}
			return true, nil
		}
	case "local.read":
		path, ok := args["path"].(string)
		parent := ""
		valid := ok && (len(args) == 1 || len(args) == 2)
		if v, exists := args["parent"]; exists && v != nil {
			var pOK bool
			parent, pOK = v.(string)
			valid = valid && pOK
		}
		for k := range args {
			if k != "path" && k != "parent" {
				valid = false
			}
		}
		if valid {
			return b.sources.Read(path, parent)
		}
	case "photon":
		if len(args) == 1 && (action == "status" || action == "verify" || action == "snapshot") {
			return b.photon.Call(action)
		}
	}
	return nil, errors.New("unsupported capability operation")
}
