package main

// This optional development test runs the retained JavaScript engine using Node
// as a test runner and connects it to the real native broker over TCP/RFC6455.
// It does not qualify WPF, WebView2, Worker delivery, rendering, or offline runtime
// packaging. Production executables do not use Node.

import (
	"bufio"
	"bytes"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"testing"
	"time"
)

type engineJSBuffer struct {
	mu sync.Mutex
	b  bytes.Buffer
}

func (b *engineJSBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.b.Write(p)
}
func (b *engineJSBuffer) String() string {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.b.String()
}

type engineJSOwner struct {
	cmd        *exec.Cmd
	input      io.WriteCloser
	ws         *wsFixture
	stderr     engineJSBuffer
	done       chan struct{}
	waitErr    error
	errors     chan error
	mu         sync.Mutex
	rpcs       map[string]int
	nativeRead []string
}

func (o *engineJSOwner) error(err error) {
	select {
	case o.errors <- err:
	default:
	}
}

func startEngineJSOwner(t *testing.T, b *Broker, node, web string) *engineJSOwner {
	t.Helper()
	ws := openWS(t, b, "/engine")
	ws.send(t, map[string]any{"type": "auth", "token": b.engineToken})
	authenticated := ws.receive(t)
	if authenticated["type"] != "engine.authenticated" {
		t.Fatalf("native engine authentication: %v", authenticated)
	}
	// Test helpers give a short total connection deadline. The actual bridge uses
	// bounded per-message read/write deadlines so startup cannot consume it.
	_ = ws.c.SetDeadline(time.Time{})
	fixture, err := filepath.Abs(filepath.Join("fixtures", "owner.mjs"))
	if err != nil {
		t.Fatal(err)
	}
	o := &engineJSOwner{ws: ws, done: make(chan struct{}), errors: make(chan error, 8), rpcs: map[string]int{}}
	o.cmd = exec.Command(node, fixture, web)
	o.cmd.Dir = filepath.Dir(fixture)
	o.cmd.Stderr = &o.stderr
	o.input, err = o.cmd.StdinPipe()
	if err != nil {
		t.Fatal(err)
	}
	output, err := o.cmd.StdoutPipe()
	if err != nil {
		t.Fatal(err)
	}
	if err = o.cmd.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		_ = o.ws.c.Close()
		_ = o.input.Close()
		select {
		case <-o.done:
		default:
			_ = o.cmd.Process.Kill()
			<-o.done
		}
	})
	go func() {
		scanner := bufio.NewScanner(output)
		scanner.Buffer(make([]byte, 65536), 10*1024*1024)
		for scanner.Scan() {
			line := append([]byte(nil), scanner.Bytes()...)
			value, err := strictJSON(line)
			if err != nil || object(value) == nil {
				o.error(fmt.Errorf("retained JS owner emitted invalid transport JSON: %v", err))
				return
			}
			message := object(value)
			if message["type"] == "rpc" {
				op := str(message["op"])
				args := object(message["args"])
				if op == "photon" {
					op += "." + str(args["action"])
				}
				o.mu.Lock()
				o.rpcs[op]++
				if op == "local.read" {
					o.nativeRead = append(o.nativeRead, str(args["path"]))
				}
				o.mu.Unlock()
			}
			_ = ws.c.SetWriteDeadline(time.Now().Add(10 * time.Second))
			if err := writeAll(ws.c, maskedFrame(1, true, line)); err != nil {
				o.error(fmt.Errorf("retained JS owner to actual broker: %w", err))
				return
			}
		}
		if err := scanner.Err(); err != nil {
			o.error(err)
		}
	}()
	go func() {
		defer o.input.Close()
		for {
			_ = ws.c.SetReadDeadline(time.Now().Add(20 * time.Second))
			data, opcode, err := readServerFrame(ws.r)
			if err != nil || opcode == 8 {
				return
			}
			if opcode != 1 {
				o.error(fmt.Errorf("unexpected native owner frame opcode %d", opcode))
				return
			}
			// Native JSON text frames already end with LF. Preserve one JSON-line
			// delimiter rather than inserting an empty second input record.
			if err = writeAll(o.input, append(bytes.TrimRight(data, "\r\n"), '\n')); err != nil {
				o.error(err)
				return
			}
		}
	}()
	initial, _ := jsonBytes(authenticated)
	if err = writeAll(o.input, initial); err != nil {
		t.Fatal(err)
	}
	go func() { o.waitErr = o.cmd.Wait(); close(o.done) }()
	select {
	case <-b.Ready:
	case err := <-o.errors:
		t.Fatalf("retained JS engine startup: %v; stderr: %s", err, o.stderr.String())
	case <-o.done:
		t.Fatalf("retained JS engine exited during startup: %v; stderr: %s", o.waitErr, o.stderr.String())
	case <-time.After(15 * time.Second):
		t.Fatalf("retained JS engine readiness timeout; stderr: %s", o.stderr.String())
	}
	if !b.IsReady() {
		t.Fatal("JS engine signaled readiness but broker is not ready")
	}
	return o
}

func (o *engineJSOwner) requireRPC(t *testing.T, op string, minimum int) {
	t.Helper()
	o.mu.Lock()
	count := o.rpcs[op]
	o.mu.Unlock()
	if count < minimum {
		t.Fatalf("real native capability %s was called %d times, expected >=%d", op, count, minimum)
	}
}

func stopEngineJSBroker(t *testing.T, b *Broker, o *engineJSOwner) {
	t.Helper()
	b.Stop()
	if !b.Clean() {
		t.Fatalf("retained JS runtime did not acknowledge real shutdown/flush; stderr: %s", o.stderr.String())
	}
	select {
	case <-o.done:
		if o.waitErr != nil {
			t.Fatalf("retained JS development runner exited: %v; stderr: %s", o.waitErr, o.stderr.String())
		}
	case <-time.After(3 * time.Second):
		t.Fatalf("retained JS runtime retained live handles after shutdown; stderr: %s", o.stderr.String())
	}
	if err := b.store.Close(); err != nil {
		t.Fatal(err)
	}
}

func newEngineJSBroker(t *testing.T, packageRoot, workspace, grant string) *Broker {
	t.Helper()
	store, err := NewVolumeStore(filepath.Join(workspace, "server"), false)
	if err != nil {
		t.Fatal(err)
	}
	if err = store.Acquire(); err != nil {
		t.Fatal(err)
	}
	sources, err := NewLocalSources([]string{grant})
	if err != nil {
		t.Fatal(err)
	}
	photon, err := NewPhotonBridge(packageRoot, workspace)
	if err != nil {
		t.Fatal(err)
	}
	profile, err := LoadJA21Profile("ja21-profile.ja")
	if err != nil {
		t.Fatal(err)
	}
	origin := "http://localhost:8877"
	cfg := &Config{Host: "127.0.0.1", Port: 0, Origin: origin, Origins: map[string]bool{origin: true}, Hosts: map[string]bool{"localhost:8877": true}}
	b, err := newBroker(packageRoot, cfg, store, sources, photon, profile)
	if err != nil {
		t.Fatal(err)
	}
	if err = b.Start(); err != nil {
		_ = store.Close()
		t.Fatal(err)
	}
	t.Cleanup(func() { b.Stop(); _ = store.Close() })
	return b
}

type engineJSTerminal struct {
	w                    *wsFixture
	sid                  string
	inputSeq, outputSeq  uint64
	consumed, lastCredit int64
}

func openEngineJSTerminal(t *testing.T, b *Broker) *engineJSTerminal {
	t.Helper()
	w := openWS(t, b, "/socket")
	t.Cleanup(func() { _ = w.c.Close() })
	w.send(t, map[string]any{"type": "auth", "token": b.token})
	hello := w.receive(t)
	if hello["type"] != "hello" || object(hello["payload"])["protocol"] != "hermit.vws.v2" {
		t.Fatalf("real JS session hello: %v", hello)
	}
	w.send(t, map[string]any{"v": 2, "type": "session.open", "rid": "integration-open", "payload": map[string]any{"cols": 100, "rows": 30, "creditBytes": 262144}})
	opened := w.receive(t)
	sid := str(opened["sid"])
	if opened["type"] != "session.opened" || sid == "" {
		t.Fatalf("real JS session open: %v", opened)
	}
	w.send(t, map[string]any{"v": 2, "type": "session.start", "sid": sid})
	return &engineJSTerminal{w: w, sid: sid}
}

func (s *engineJSTerminal) input(t *testing.T, command string) {
	t.Helper()
	s.inputSeq++
	frame := make([]byte, 7, 7+len(command)+1)
	frame[0] = 1 // Retained VWS KIND_INPUT, independent of RFC6455 opcode.
	seq := s.inputSeq
	for i := 6; i >= 1; i-- {
		frame[i] = byte(seq)
		seq >>= 8
	}
	frame = append(frame, []byte(command+"\r")...)
	_ = s.w.c.SetWriteDeadline(time.Now().Add(10 * time.Second))
	if err := writeAll(s.w.c, maskedFrame(2, true, frame)); err != nil {
		t.Fatal(err)
	}
}

func (s *engineJSTerminal) message(t *testing.T, output *strings.Builder) map[string]any {
	t.Helper()
	_ = s.w.c.SetReadDeadline(time.Now().Add(12 * time.Second))
	data, opcode, err := readServerFrame(s.w.r)
	if err != nil {
		t.Fatalf("real JS terminal frame: %v; output: %s", err, output.String())
	}
	if opcode == 2 {
		if len(data) < 8 || data[0] != 2 {
			t.Fatal("invalid real JS VWS output frame")
		}
		seq := uint64(0)
		for _, c := range data[1:7] {
			seq = seq<<8 | uint64(c)
		}
		if seq != s.outputSeq+1 {
			t.Fatalf("JS VWS sequence jumped from %d to %d", s.outputSeq, seq)
		}
		s.outputSeq = seq
		output.Write(data[7:])
		s.consumed += int64(len(data) - 7)
		if s.consumed-s.lastCredit >= 65536 {
			s.lastCredit = s.consumed
			s.w.send(t, map[string]any{"v": 2, "type": "flow.credit", "sid": s.sid, "payload": map[string]any{"creditBytes": 262144 + s.consumed}})
		}
		return nil
	}
	if opcode != 1 {
		t.Fatalf("JS terminal closed before expected result, opcode=%d payload=%q; output: %s", opcode, data, output.String())
	}
	value, err := strictJSON(data)
	if err != nil || object(value) == nil {
		t.Fatalf("bad JS control JSON: %s, %v", data, err)
	}
	message := object(value)
	if rid := str(message["rid"]); rid != "" && len(data) > 32768 {
		s.w.send(t, map[string]any{"v": 2, "type": "control.ack", "sid": s.sid, "payload": map[string]any{"rid": rid}})
	}
	return message
}

func (s *engineJSTerminal) command(t *testing.T, command string, expected int64) string {
	t.Helper()
	s.input(t, command)
	var output strings.Builder
	for i := 0; i < 512; i++ {
		m := s.message(t, &output)
		if m == nil {
			continue
		}
		if m["type"] == "command.complete" {
			payload := object(m["payload"])
			code, ok := integer(payload["code"])
			if m["sid"] != s.sid || !ok || code != expected || payload["persistence"] != "committed" {
				t.Fatalf("real command %q: result=%v output=%s", command, m, output.String())
			}
			return output.String()
		}
		if m["type"] == "session.exit" || m["type"] == "error" {
			t.Fatalf("real command %q failed: %v; output: %s", command, m, output.String())
		}
	}
	t.Fatal("retained JS terminal exceeded result frame bound")
	return ""
}

func engineJSQuoted(path string) string { return "'" + strings.ReplaceAll(path, "'", "'\\''") + "'" }

func engineJSFixturePath(t *testing.T, variable string, candidates []string, required string) string {
	t.Helper()
	if explicit := os.Getenv(variable); explicit != "" {
		candidates = []string{explicit}
	}
	for _, candidate := range candidates {
		absolute, err := filepath.Abs(candidate)
		if err != nil {
			continue
		}
		if info, err := os.Stat(filepath.Join(absolute, required)); err == nil && info.Mode().IsRegular() {
			return absolute
		}
	}
	t.Fatalf("retained development fixture absent: set %s to a directory containing %s (searched %v)", variable, required, candidates)
	return ""
}

// Explicit opt-in example:
// JA21_ENGINE_NODE=<developer node.exe> go test -run TestRetainedJSEngineNativeBrokerIntegration -v
func TestRetainedJSEngineNativeBrokerIntegration(t *testing.T) {
	node := os.Getenv("JA21_ENGINE_NODE")
	if node == "" {
		t.Skip("optional retained-engine development test: set JA21_ENGINE_NODE; production does not require Node")
	}
	if _, err := os.Stat(node); err != nil {
		t.Fatal(err)
	}
	web := engineJSFixturePath(t, "JA21_ENGINE_WEB", []string{
		filepath.Join("..", "appliance", "web"),                           // Distributed JP300/native tree.
		filepath.Join("..", "..", "outputs", "JP300", "appliance", "web"), // Development workspace.
	}, "photon-engine.js")
	packageRoot := engineJSFixturePath(t, "JA21_ENGINE_SOURCE_ROOT", []string{
		filepath.Dir(filepath.Dir(web)),        // Prefer the candidate being exercised.
		filepath.Join("..", "source", "JP250"), // Preserved source while candidate is assembled.
	}, filepath.Join("photon", "config", "vec1.json"))
	t.Logf("Development runner only: retained engine=%s; native Photon input=%s; WPF/WebView2/Worker UI NOT exercised", web, packageRoot)
	workspace := testTempDir(t)
	grant := testTempDir(t)
	rootScript := filepath.Join(grant, "main.vcmd")
	childScript := filepath.Join(grant, "child.vcmd")
	outside := filepath.Join(testTempDir(t), "outside.vcmd")
	for path, content := range map[string]string{
		rootScript:  "#!vcmd 1\n@name Actual native include integration\n@include \"child.vcmd\"\necho ROOT_INCLUDE_FINISHED\n",
		childScript: "#!vcmd 1\necho INCLUDED_NATIVE > /tmp/native-include.txt\ncat /tmp/native-include.txt\n",
		outside:     "#!vcmd 1\necho OUTSIDE_GRANT_MUST_NOT_EXECUTE > /tmp/outside-grant.txt\n",
	} {
		if err := os.WriteFile(path, []byte(content), 0600); err != nil {
			t.Fatal(err)
		}
	}
	b := newEngineJSBroker(t, packageRoot, workspace, grant)
	if result := httpFixture(t, b, "GET /ready HTTP/1.1\r\nHost: localhost:8877\r\n\r\n"); !strings.HasPrefix(result, "HTTP/1.1 503 ") {
		t.Fatal("broker readiness did not wait for actual retained engine")
	}
	identity := b.photon.ElectronID()
	owner := startEngineJSOwner(t, b, node, web)
	terminal := openEngineJSTerminal(t, b)
	terminal.command(t, "echo RETAINED_NATIVE > /tmp/native-retained.txt", 0)
	if output := terminal.command(t, "cat /tmp/native-retained.txt", 0); !strings.Contains(output, "RETAINED_NATIVE\r\n") {
		t.Fatalf("actual echo/redirection/cat semantics failed: %s", output)
	}
	if output := terminal.command(t, "run --local "+engineJSQuoted(rootScript), 0); !strings.Contains(output, "INCLUDED_NATIVE") || !strings.Contains(output, "ROOT_INCLUDE_FINISHED") || !strings.Contains(output, "3/3 lines; 0 failures") {
		t.Fatalf("actual VCMD include graph did not execute: %s", output)
	}
	if output := terminal.command(t, "run --local "+engineJSQuoted(outside), 2); !strings.Contains(output, "outside granted roots") {
		t.Fatalf("native file grant did not constrain retained VCMD engine: %s", output)
	}
	if output := terminal.command(t, "photon status", 0); !strings.Contains(output, identity) || !strings.Contains(output, "state_and_ledger_verified") {
		t.Fatalf("actual native Photon status unavailable: %s", output)
	}
	terminal.command(t, "photon verify", 0)
	snapshotOutput := terminal.command(t, "photon snapshot", 0)
	match := regexp.MustCompile(`"snapshot_hash":\s*"([0-9a-f]{64})"`).FindStringSubmatch(snapshotOutput)
	if len(match) != 2 {
		t.Fatalf("actual Photon snapshot result missing: %s", snapshotOutput)
	}
	if _, err := readRegular(filepath.Join(b.photon.snapshots, match[1]+".json"), 8*1024*1024, false); err != nil {
		t.Fatalf("snapshot command produced no native persisted snapshot: %v", err)
	}
	for op, minimum := range map[string]int{"store.read": 1, "store.write": 2, "local.read": 3, "photon.status": 1, "photon.verify": 1, "photon.snapshot": 1} {
		owner.requireRPC(t, op, minimum)
	}
	stopEngineJSBroker(t, b, owner)
	if _, err := readRegular(filepath.Join(workspace, "server", "volume.json"), maxRecord, false); err != nil {
		t.Fatalf("actual broker disk store absent after shutdown: %v", err)
	}

	// Restart both sides with a fresh broker token, a fresh JS runtime, and the
	// same native disk store and Photon binding. No in-memory semantic state is reused.
	b2 := newEngineJSBroker(t, packageRoot, workspace, grant)
	if b2.photon.ElectronID() != identity {
		t.Fatal("broker restart replaced native Photon identity")
	}
	owner2 := startEngineJSOwner(t, b2, node, web)
	terminal2 := openEngineJSTerminal(t, b2)
	for _, file := range []struct{ path, marker string }{{"/tmp/native-retained.txt", "RETAINED_NATIVE"}, {"/tmp/native-include.txt", "INCLUDED_NATIVE"}} {
		if output := terminal2.command(t, "cat "+file.path, 0); !strings.Contains(output, file.marker+"\r\n") {
			t.Fatalf("real JS/native disk restart lost %s: %s", file.path, output)
		}
	}
	terminal2.command(t, "photon verify", 0)
	terminal2.input(t, "poweroff")
	var shutdownOutput strings.Builder
	halted := false
	for i := 0; i < 256; i++ {
		message := terminal2.message(t, &shutdownOutput)
		if message == nil {
			continue
		}
		if message["type"] == "runtime.halt" && object(message["payload"])["reason"] == "poweroff" {
			halted = true
		}
		if message["type"] == "session.exit" {
			break
		}
	}
	if !halted || b2.IsReady() || !strings.Contains(shutdownOutput.String(), "flushing volume0") {
		t.Fatalf("actual virtual poweroff failed: halted=%v ready=%v output=%s", halted, b2.IsReady(), shutdownOutput.String())
	}
	if result := httpFixture(t, b2, "GET /ready HTTP/1.1\r\nHost: localhost:8877\r\n\r\n"); !strings.HasPrefix(result, "HTTP/1.1 503 ") {
		t.Fatal("poweroff did not clear actual broker readiness")
	}
	unready := openWS(t, b2, "/socket")
	unready.send(t, map[string]any{"type": "auth", "token": b2.token})
	if code := unready.closeCode(t); code != 1013 {
		t.Fatalf("poweroff admitted a new terminal: close=%d", code)
	}
	_ = unready.c.Close()
	stopEngineJSBroker(t, b2, owner2)
	owner2.requireRPC(t, "store.read", 1)
	owner2.requireRPC(t, "photon.verify", 1)
	t.Log("Observed: actual retained commands and VCMD includes, native grants, disk persistence across both restarts, Photon status/verify/snapshot, readiness, virtual poweroff, and acknowledged shutdown")
}
