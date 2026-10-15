package main

import (
	"crypto/sha256"
	"encoding/hex"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestConfigurationRejectsAmbiguityAndRequiresTLSChoice(t *testing.T) {
	root := testTempDir(t)
	good := []string{"--allow-insecure-network", "--host", "127.0.0.1", "--public-origin", "http://127.0.0.1:8877"}
	if _, cfg, e := parseOptions(good, root); e != nil || cfg.Origin != "http://127.0.0.1:8877" {
		t.Fatalf("explicit insecure config: %v %v", cfg, e)
	}
	for name, args := range map[string][]string{
		"TLS default": {}, "duplicate option": {"--port", "8877", "--port", "8878"}, "port zero": append(append([]string{}, good...), "--port", "0"),
		"hostname bind": append(append([]string{}, good...), "--host", "localhost"), "verify combined": {"--verify", "--server-only"},
		"trace without job": {"--trace"}, "incomplete TLS": {"--tls-cert", "a.pem"}, "unknown option": {"--unknown"},
		"unexpected positional": {"one"}, "restore args": {"--restore", "one", "two"}, "standalone transport only": {"--vcmd", "x.vcmd", "--server-only"},
	} {
		t.Run(name, func(t *testing.T) {
			if _, _, e := parseOptions(args, root); e == nil {
				t.Fatal("invalid configuration admitted")
			}
		})
	}
	for name, text := range map[string]string{"unknown key": `{"unreviewed":1}`, "duplicate JSON": `{"port":8877,"port":8878}`, "float port": `{"port":8877.0}`, "bad bool": `{"allowInsecureNetwork":"true"}`, "bad array": `{"vcmdRoots":[1]}`} {
		t.Run(name, func(t *testing.T) {
			path := filepath.Join(root, "config-"+strings.ReplaceAll(name, " ", "-")+".json")
			if e := os.WriteFile(path, []byte(text), 0600); e != nil {
				t.Fatal(e)
			}
			if _, _, e := parseOptions([]string{"--config", path}, root); e == nil {
				t.Fatal("invalid JSON configuration admitted")
			}
		})
	}
}
func TestCanonicalOrigins(t *testing.T) {
	for _, s := range []string{"http://127.0.0.1:8877", "https://localhost", "https://[::1]:8877", "https://example.test:8443"} {
		if got, e := canonicalOrigin(s); e != nil || got != s {
			t.Fatalf("valid origin %q: %q %v", s, got, e)
		}
	}
	for _, s := range []string{"https://EXAMPLE.test", "https://example.test:443", "http://example.test:80", "https://example.test/", "https://user@example.test", "https://0.0.0.0:8877", "https://[::]:8877", "https://*.test", "ftp://example.test", "https://example.test?x=y", "https://example.test#x", "https://example.test:0", "https://example.test:65536"} {
		if _, e := canonicalOrigin(s); e == nil {
			t.Fatalf("invalid origin %q admitted", s)
		}
	}
}

func TestStrictCLIFlagSpellingAndPresenceOnlyBooleans(t *testing.T) {
	root := testTempDir(t)
	config := []byte(`{"host":"127.0.0.1","publicOrigin":"http://127.0.0.1:8877","allowInsecureNetwork":true}`)
	if e := os.WriteFile(filepath.Join(root, "server-config.json"), config, 0600); e != nil {
		t.Fatal(e)
	}
	for name, args := range map[string][]string{
		"single dash":               {"-host", "127.0.0.1"},
		"mixed spelling duplicate":  {"--host", "127.0.0.1", "-host", "0.0.0.0"},
		"insecure false assignment": {"--allow-insecure-network=false"},
		"show token assignment":     {"--show-token=true"},
		"server only assignment":    {"--server-only=true"},
		"verify assignment":         {"--verify=true"},
		"doctor assignment":         {"--doctor=false"},
	} {
		t.Run(name, func(t *testing.T) {
			if _, _, e := parseOptions(args, root); e == nil {
				t.Fatal("unsupported flag spelling/value admitted")
			}
		})
	}
	if o, c, e := parseOptions([]string{"--server-only"}, root); e != nil || !o.ServerOnly || c.TLS != nil {
		t.Fatalf("presence-only flag or explicit insecure config changed: %v %v %v", o, c, e)
	}
}

func TestStandaloneVCMDDefaultsAndRejectsNetworkOverrides(t *testing.T) {
	root := testTempDir(t)
	script := filepath.Join(root, "standalone fixture.vcmd")
	if e := os.WriteFile(script, []byte("#!vcmd 1\necho isolated\n"), 0600); e != nil {
		t.Fatal(e)
	}
	if e := os.WriteFile(filepath.Join(root, "server-config.json"), []byte("deliberately invalid network configuration"), 0600); e != nil {
		t.Fatal(e)
	}
	o, c, e := parseOptions([]string{"--vcmd", script, "--check", "--trace", "--report", filepath.Join(root, "report.json")}, root)
	if e != nil {
		t.Fatal(e)
	}
	if c.Host != "127.0.0.1" || c.Port != 0 || c.TLS != nil || c.Origin != "" || len(c.Origins) != 0 || len(c.Hosts) != 0 {
		t.Fatalf("standalone network defaults: %+v", c)
	}
	if len(c.Roots) != 1 || c.Roots[0] != root || c.Job["path"] != script || c.Job["mode"] != "check" || c.Job["trace"] != true || o.Report == "" {
		t.Fatalf("standalone job/source grants: %+v %+v", o, c)
	}
	if _, granted, e := parseOptions([]string{"--vcmd", script, "--vcmd-root", root}, root); e != nil || len(granted.Roots) != 1 || granted.Roots[0] != root {
		t.Fatalf("explicit local grant: %+v %v", granted, e)
	}
	for option, args := range map[string][]string{
		"config": {"--config", filepath.Join(root, "server-config.json")}, "host": {"--host", "127.0.0.1"}, "port": {"--port", "0"},
		"public-origin": {"--public-origin", "http://127.0.0.1:8877"}, "allow-origin": {"--allow-origin", "http://localhost:8877"},
		"tls-cert": {"--tls-cert", "cert.pem"}, "tls-key": {"--tls-key", "key.pem"}, "insecure": {"--allow-insecure-network"},
	} {
		t.Run(option, func(t *testing.T) {
			args = append([]string{"--vcmd", script}, args...)
			if _, _, e := parseOptions(args, root); e == nil || !strings.Contains(e.Error(), "cannot override isolated standalone") {
				t.Fatalf("network override result: %v", e)
			}
		})
	}
}

func TestDoctorStandaloneOfflineReportMissingRenderer(t *testing.T) {
	root := testTempDir(t)
	o, c, e := parseOptions([]string{"--doctor"}, root)
	if e != nil || !o.Doctor || c != nil {
		t.Fatalf("doctor CLI: %v %v %v", o, c, e)
	}
	if _, _, e = parseOptions([]string{"--doctor", "--server-only"}, root); e == nil {
		t.Fatal("doctor combined with operational option")
	}
	if code := runDoctor(root); code != 2 {
		t.Fatalf("missing portable dependencies should return2, got%d", code)
	}
	data, e := readRegular(filepath.Join(root, "workspace", "doctor.json"), 65536, false)
	if e != nil {
		t.Fatal(e)
	}
	v, e := strictJSON(data)
	if e != nil {
		t.Fatal(e)
	}
	m := object(v)
	if m["portableReady"] != false || m["noProcessesLaunched"] != true || m["noNetworkAccess"] != true {
		t.Fatalf("doctor outcomes: %v", m)
	}
	found := false
	for _, raw := range array(m["checks"]) {
		check := object(raw)
		if check["name"] == "fixed-webview2-renderer" {
			found = true
			if check["ok"] != false || !strings.Contains(str(check["detail"]), "not ready for portable offline launch") {
				t.Fatalf("renderer diagnosis: %v", check)
			}
		}
	}
	if !found {
		t.Fatal("fixed renderer prerequisite missing from report")
	}
	left, e := filepath.Glob(filepath.Join(root, "workspace", ".doctor-probe-*.tmp"))
	if e != nil || len(left) != 0 {
		t.Fatalf("owned probe not removed: %v %v", left, e)
	}
}
func TestVerifierDetectsTamperAndCoverage(t *testing.T) {
	root := testTempDir(t)
	profile, e := os.ReadFile("ja21-profile.ja")
	if e != nil {
		t.Fatal(e)
	}
	if e = os.MkdirAll(filepath.Join(root, "source"), 0700); e != nil {
		t.Fatal(e)
	}
	files := map[string][]byte{"source/jp250-service.ja": profile, "asset.txt": []byte("reviewed fixture")}
	manifest := ""
	for name, data := range files {
		if e = os.WriteFile(filepath.Join(root, filepath.FromSlash(name)), data, 0600); e != nil {
			t.Fatal(e)
		}
		h := sha256.Sum256(data)
		manifest += hex.EncodeToString(h[:]) + "  " + name + "\n"
	}
	if e = os.WriteFile(filepath.Join(root, "SHA256SUMS.txt"), []byte(manifest), 0600); e != nil {
		t.Fatal(e)
	}
	if _, e = verifyPackage(root); e != nil {
		t.Fatal(e)
	}
	if e = os.WriteFile(filepath.Join(root, "asset.txt"), []byte("modified"), 0600); e != nil {
		t.Fatal(e)
	}
	if _, e = verifyPackage(root); e == nil {
		t.Fatal("tamper was admitted")
	}
	if e = os.WriteFile(filepath.Join(root, "asset.txt"), files["asset.txt"], 0600); e != nil {
		t.Fatal(e)
	}
	if e = os.WriteFile(filepath.Join(root, "unexpected.js"), []byte("unreviewed"), 0600); e != nil {
		t.Fatal(e)
	}
	if _, e = verifyPackage(root); e == nil {
		t.Fatal("unexpected executable source was admitted")
	}
}
