package main

import (
	"os"
	"path/filepath"
	"reflect"
	"testing"
)

func TestBootNativeDispatchAndArgumentPreservation(t *testing.T) {
	for _, args := range [][]string{nil, {"--boot"}} {
		got, action, e := bootArguments(args)
		if e != nil || action != "launch" || !reflect.DeepEqual(got, []string{"--local"}) {
			t.Fatalf("default: %v %s %v", got, action, e)
		}
	}
	path := `D:\Folder & ! name\source & !.vcmd`
	args := []string{"--boot", "--vcmd", path, "--report", `D:\Reports & !\result.json`}
	got, _, e := bootArguments(args)
	if e != nil || !reflect.DeepEqual(got, args[1:]) {
		t.Fatalf("argument vector changed: %v %v", got, e)
	}
	for _, mode := range []string{"--qualify", "--test"} {
		_, action, e := bootArguments([]string{"--boot", mode})
		if e != nil || action != "qualify" {
			t.Fatalf("qualification route: %s %v", action, e)
		}
	}
	for _, args := range [][]string{{"--boot", "--boot"}, {"--boot", "--test", "--local"}, {"--boot", "--help", "--network"}} {
		if _, _, e := bootArguments(args); e == nil {
			t.Fatalf("ambiguous boot arguments admitted: %v", args)
		}
	}
}
func TestBootLocalAndLANModesIgnoreNetworkConfig(t *testing.T) {
	root := testTempDir(t)
	if e := os.WriteFile(filepath.Join(root, "server-config.json"), []byte(`{"host":"0.0.0.0","allowInsecureNetwork":true,"publicOrigin":"http://192.0.2.2:8877"}`), 0600); e != nil {
		t.Fatal(e)
	}
	args, _, e := bootArguments([]string{"--boot"})
	if e != nil {
		t.Fatal(e)
	}
	_, cfg, e := parseOptions(args, root)
	if e != nil || cfg.Host != "127.0.0.1" || cfg.TLS != nil || cfg.Origin != "http://127.0.0.1:8877" {
		t.Fatalf("unsafe default: %+v %v", cfg, e)
	}
	if _, _, e = parseOptions([]string{"--network"}, root); e == nil {
		t.Fatal("TLS network mode accepted insecure configuration")
	}
	for _, args := range [][]string{{"--local", "--host", "0.0.0.0"}, {"--local", "--config", "server-config.json"}, {"--network", "--allow-insecure-network"}, {"--local", "--lan-unsafe"}, {"--lan-unsafe", "--tls-cert", "cert.pem"}} {
		if _, _, e := parseOptions(args, root); e == nil {
			t.Fatalf("mode conflict admitted: %v", args)
		}
	}
	_, cfg, e = parseOptions([]string{"--lan-unsafe", "--host", "127.0.0.1"}, root)
	if e != nil || cfg.TLS != nil || cfg.Origin != "http://127.0.0.1:8877" {
		t.Fatalf("explicit LAN settings/config isolation: %+v %v", cfg, e)
	}
}
