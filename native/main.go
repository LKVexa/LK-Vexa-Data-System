package main

import (
	"bufio"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"net"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"syscall"
	"time"
)

func main() { os.Exit(runMain(os.Args[1:])) }
func runMain(args []string) int {
	root, e := rootPath()
	if e != nil {
		fmt.Fprintln(os.Stderr, "STARTUP FAILED:", e)
		return 1
	}
	o, c, e := parseOptions(args, root)
	if e == nil {
		if o.Doctor {
			return runDoctor(root)
		}
		if o.Restore != "" {
			e = restoreVolume(root, o.Restore)
			if e == nil {
				return 0
			}
		} else if o.SelfTest {
			e = nativeSelfTest(root)
			if e == nil {
				return 0
			}
		} else {
			var verification map[string]any
			verification, e = verifyPackage(root)
			if e == nil && o.Verify {
				data, _ := json.MarshalIndent(verification, "", "  ")
				fmt.Println(string(data))
				return 0
			}
			if e == nil {
				fmt.Printf("Integrity: %v shipped files matched.\n", verification["filesMatched"])
				var code int
				code, e = runService(root, o, c)
				if e == nil {
					return code
				}
			}
		}
	}
	fmt.Fprintln(os.Stderr, "STARTUP FAILED:", e)
	return 1
}
func runService(root string, o *Options, cfg *Config) (exitCode int, retErr error) {
	profile, e := LoadJA21Profile(filepath.Join(root, "source", "jp250-service.ja"))
	if e != nil {
		return 1, e
	}
	workspace, e := plainPath(filepath.Join(root, "workspace"), true)
	if e != nil {
		return 1, e
	}
	volumeDir := filepath.Join(workspace, "server")
	photonWorkspace := workspace
	if cfg.Job != nil {
		volumeDir = filepath.Join(workspace, "standalone")
		photonWorkspace = volumeDir
	}
	store, e := NewVolumeStore(volumeDir, cfg.Job != nil)
	if e != nil {
		return 1, e
	}
	if e = store.Acquire(); e != nil {
		return 1, e
	}
	defer func() {
		if e := store.Close(); e != nil && retErr == nil {
			retErr = e
			exitCode = 1
		}
	}()
	if _, e = store.Read(); e != nil {
		return 1, e
	}
	sources, e := NewLocalSources(cfg.Roots)
	if e != nil {
		return 1, e
	}
	photon, e := NewPhotonBridge(root, photonWorkspace)
	if e != nil {
		return 1, e
	}
	b, e := newBroker(root, cfg, store, sources, photon, profile)
	if e != nil {
		return 1, e
	}
	if e = b.Start(); e != nil {
		return 1, e
	}
	var child *exec.Cmd
	var childDone chan struct{}
	var childErr error
	defer func() {
		b.Stop()
		if child != nil && childDone != nil {
			select {
			case <-childDone:
			case <-time.After(3 * time.Second):
				_ = child.Process.Kill()
				<-childDone
				fmt.Fprintln(os.Stderr, "Native host did not close after the shutdown request; its process was stopped.")
			}
		}
	}()
	fmt.Printf("JA21 Photon Console %s\nPython: NOT REQUIRED\nNode.js: NOT REQUIRED\nPhoton VEC identity: %s\nPage: %s/\nWebSocket: %s/socket\n", ReleaseVersion, photon.ElectronID(), cfg.Origin, strings.Replace(cfg.Origin, "http", "ws", 1))
	if o.ShowToken {
		fmt.Println("Terminal client token (shared authority): " + b.token)
	}
	if cfg.TLS == nil && !net.ParseIP(cfg.Host).IsLoopback() {
		fmt.Println("WARNING: explicit HTTP/WS mode is unencrypted. Use only an isolated trusted test network.")
	}
	interrupt := make(chan os.Signal, 1)
	signal.Notify(interrupt, os.Interrupt, syscall.SIGTERM)
	defer signal.Stop(interrupt)
	if o.ServerOnly {
		fmt.Println("TRANSPORT ONLY: no native owner Worker attached. Terminal sessions are refused until an authenticated owner engine is ready.")
		<-interrupt
		return 130, nil
	}
	host, e := plainPath(filepath.Join(root, "bin", "JPConsole.exe"), false)
	if e != nil {
		return 1, e
	}
	payload, e := jsonBytes(map[string]any{"protocol": 2, "origin": cfg.Origin, "token": b.token, "engineToken": b.engineToken})
	if e != nil {
		return 1, e
	}
	child = exec.Command(host)
	child.Dir = root
	child.Env = hostEnvironment()
	child.Stdin = bytes.NewReader(payload)
	child.Stdout = os.Stdout
	child.Stderr = os.Stderr
	if e = child.Start(); e != nil {
		child = nil
		return 1, e
	}
	payload = nil
	childDone = make(chan struct{})
	go func() { childErr = child.Wait(); close(childDone) }()
	startup := time.NewTimer(60 * time.Second)
	defer startup.Stop()
	select {
	case <-b.Ready:
		if !b.IsReady() {
			return 1, errors.New("native engine disconnected during readiness transition")
		}
	case <-childDone:
		return 1, fmt.Errorf("native host exited before readiness: %v", childErr)
	case <-startup.C:
		return 1, errors.New("native engine did not become ready within 60 seconds; inspect workspace/logs/console-error.txt and WebView2/TLS prerequisites")
	case <-interrupt:
		return 130, nil
	}
	fmt.Println("JA21 transport + native JavaScript engine ready. Commands / Scripts tabs retained.")
	if cfg.Job != nil {
		var result JobResult
		select {
		case result = <-b.JobDone:
		case <-childDone:
			return 1, errors.New("native window closed before script completion")
		case <-interrupt:
			return 130, nil
		}
		if result.Err != nil {
			return 1, result.Err
		}
		code, e := parseInteger(result.Report["code"])
		if e != nil {
			return 1, e
		}
		if o.Report != "" {
			data, e := jsonBytes(result.Report)
			if e != nil {
				return 1, e
			}
			if e = writeNew(o.Report, data); e != nil {
				return 1, e
			}
		}
		fmt.Printf("\nStandalone result: %d (isolated virtual volume)\nClose the native window to finish this standalone launcher.\n", code)
		select {
		case <-childDone:
			return int(code), nil
		case <-interrupt:
			return 130, nil
		}
	}
	select {
	case <-childDone:
		if !b.Clean() {
			fmt.Fprintln(os.Stderr, "WARNING: engine shutdown was not acknowledged; last completed per-command saves may exist, but unsaved work is not guaranteed.")
			if childErr == nil {
				return 74, nil
			}
		}
		if childErr != nil {
			if x, ok := childErr.(*exec.ExitError); ok {
				return x.ExitCode(), nil
			}
			return 1, childErr
		}
		return 0, nil
	case <-interrupt:
		return 130, nil
	}
}
func hostEnvironment() []string {
	out := []string{}
	for _, entry := range os.Environ() {
		key := strings.ToUpper(strings.SplitN(entry, "=", 2)[0])
		if strings.HasPrefix(key, "JA21_") || strings.HasPrefix(key, "WEBVIEW2_") || strings.HasPrefix(key, "NODE_") || strings.HasPrefix(key, "PYTHON") {
			continue
		}
		out = append(out, entry)
	}
	return out
}

func restoreVolume(root, source string) error {
	data, e := readRegular(source, 6*1024*1024, false)
	if e != nil {
		return e
	}
	v, e := strictJSON(data)
	if e != nil {
		return e
	}
	record, ok := v.(map[string]any)
	if !ok {
		return errors.New("volume object required")
	}
	if record["schema"] == "JA21-EXPORT/1" {
		record = map[string]any{"schema": "JA21-VOLUME/1", "version": ReleaseVersion, "writtenAt": 0, "snap": record["snap"]}
	}
	if e = validateVolume(record); e != nil {
		return e
	}
	dir := filepath.Join(root, "workspace", "server")
	store, e := NewVolumeStore(dir, false)
	if e != nil {
		return e
	}
	if e = store.Acquire(); e != nil {
		return e
	}
	defer store.Close()
	previous, e := store.Read()
	if e != nil {
		return e
	}
	if previous != nil {
		old, e := readRegular(filepath.Join(dir, "volume.json"), 6*1024*1024, false)
		if e != nil {
			return e
		}
		backup := filepath.Join(dir, fmt.Sprintf("backup-%d.json", time.Now().UnixNano()))
		if e = writeNew(backup, old); e != nil {
			return e
		}
		fmt.Println("Previous volume preserved:", backup)
	}
	if e = store.Write(record); e != nil {
		return e
	}
	fmt.Println("Restore completed; source was retained unchanged.")
	return nil
}
func writeNew(path string, data []byte) error {
	p, e := plainPath(path, true)
	if e != nil {
		return e
	}
	f, e := os.OpenFile(p, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if e != nil {
		return e
	}
	defer f.Close()
	if e = writeAll(f, data); e != nil {
		return e
	}
	return f.Sync()
}

var checksumLine = regexp.MustCompile(`^([0-9a-f]{64})  (.+)$`)

func excludedImmutable(rel string) bool {
	p := strings.Split(rel, "/")
	for _, s := range p {
		if strings.EqualFold(s, ".git") {
			return true
		}
	}
	if p[0] == "workspace" || p[0] == "certs" {
		return true
	}
	switch rel {
	case "server-config.json", "SHA256SUMS.txt", "package.integrity.json":
		return true
	}
	return false
}
func verifyPackage(root string) (map[string]any, error) {
	root, e := plainPath(root, false)
	if e != nil {
		return nil, e
	}
	manifest, e := readRegular(filepath.Join(root, "SHA256SUMS.txt"), 8*1024*1024, false)
	if e != nil {
		return nil, e
	}
	seen := map[string]bool{}
	count := 0
	scanner := bufio.NewScanner(bytes.NewReader(manifest))
	scanner.Buffer(make([]byte, 4096), 32768)
	for scanner.Scan() {
		line := strings.TrimSuffix(scanner.Text(), "\r")
		m := checksumLine.FindStringSubmatch(line)
		if m == nil {
			return nil, errors.New("invalid checksum manifest line")
		}
		rel := m[2]
		if strings.ContainsAny(rel, "\\:") || strings.HasPrefix(rel, "/") {
			return nil, errors.New("invalid checksum path")
		}
		for _, p := range strings.Split(rel, "/") {
			if p == "" || p == "." || p == ".." {
				return nil, errors.New("invalid checksum path")
			}
		}
		fold := strings.ToLower(rel)
		if seen[fold] {
			return nil, errors.New("duplicate checksum path")
		}
		seen[fold] = true
		p, e := plainPath(filepath.Join(root, filepath.FromSlash(rel)), false)
		if e != nil {
			return nil, e
		}
		if !strings.HasPrefix(strings.ToLower(p), strings.ToLower(root)+string(os.PathSeparator)) {
			return nil, errors.New("checksum path escaped root")
		}
		data, e := readRegular(p, 512*1024*1024, false)
		if e != nil {
			return nil, e
		}
		hash := sha256.Sum256(data)
		if hex.EncodeToString(hash[:]) != m[1] {
			return nil, fmt.Errorf("checksum mismatch: %s", rel)
		}
		count++
	}
	if e = scanner.Err(); e != nil {
		return nil, e
	}
	expected := map[string]bool{}
	e = filepath.WalkDir(root, func(path string, d fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		if path == root {
			return nil
		}
		rel, e := filepath.Rel(root, path)
		if e != nil {
			return e
		}
		rel = filepath.ToSlash(rel)
		if excludedImmutable(rel) {
			if d.IsDir() {
				return filepath.SkipDir
			}
			return nil
		}
		if _, e = plainPath(path, false); e != nil {
			return e
		}
		if strings.EqualFold(d.Name(), "__pycache__") || strings.EqualFold(filepath.Ext(path), ".pyc") || strings.EqualFold(filepath.Ext(path), ".pyo") {
			return fmt.Errorf("unreviewed executable cache: %s", rel)
		}
		if !d.IsDir() {
			expected[strings.ToLower(rel)] = true
		}
		return nil
	})
	if e != nil {
		return nil, e
	}
	difference := []string{}
	for p := range seen {
		if !expected[p] {
			difference = append(difference, p)
		}
	}
	for p := range expected {
		if !seen[p] {
			difference = append(difference, p)
		}
	}
	if len(difference) > 0 {
		sort.Strings(difference)
		if len(difference) > 8 {
			difference = difference[:8]
		}
		return nil, fmt.Errorf("manifest coverage differs: %v", difference)
	}
	if _, e = LoadJA21Profile(filepath.Join(root, "source", "jp250-service.ja")); e != nil {
		return nil, e
	}
	return map[string]any{"schema": "JA21-PHOTON-VERIFY/2", "version": ReleaseVersion, "filesMatched": count, "pythonRequired": false, "nodejsRequired": false, "nativeServiceProfileParsed": true}, nil
}

func nativeSelfTest(root string) error {
	profile, e := LoadJA21Profile(filepath.Join(root, "source", "jp250-service.ja"))
	if e != nil {
		return e
	}
	checks := 0
	if _, e = strictJSON([]byte(`{"a":1,"a":2}`)); e == nil {
		return errors.New("self-test: duplicate JSON keys admitted")
	}
	checks++
	if _, e = canonicalOrigin("https://example.test"); e != nil {
		return e
	}
	if _, e = canonicalOrigin("https://example.test:443"); e == nil {
		return errors.New("self-test: noncanonical origin admitted")
	}
	checks++
	denied, e := profile.Evaluate(JA21Facts{Transport: "wss", Operation: "unknown.operation", OriginValid: true, TokenValid: true, Owner: true})
	if e == nil && denied.Allowed {
		return errors.New("self-test: unknown JA21 operation admitted")
	}
	checks++
	allowed, e := profile.Evaluate(JA21Facts{Transport: "https", Method: "GET", Path: "/health", Operation: "http.health", OriginValid: true})
	if e != nil || !allowed.Allowed {
		return errors.New("self-test: declared HTTP health route unavailable")
	}
	checks++
	a, c := net.Pipe()
	done := make(chan error, 1)
	go func() { defer c.Close(); done <- writeAll(c, []byte{0x81, 0x82, 1, 2, 3, 4, 'o' ^ 1, 'k' ^ 2}) }()
	w := newWS(a, bufio.NewReader(a), 512)
	payload, opcode, e := w.Receive()
	_ = a.Close()
	if e != nil || opcode != 1 || string(payload) != "ok" {
		return errors.New("self-test: masked RFC6455 text failed")
	}
	if e = <-done; e != nil {
		return e
	}
	checks++
	fmt.Printf("Native self-test: %d checks passed (strict JSON, origin policy, actual JA21 decisions and RFC6455 parsing).\n", checks)
	return nil
}
