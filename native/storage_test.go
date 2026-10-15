package main

import (
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

func volumeFixture() map[string]any {
	return map[string]any{"schema": "JA21-VOLUME/1", "snap": map[string]any{"v": 1, "root": map[string]any{"k": "d", "m": 493, "t": 1700000000000, "ch": []any{[]any{"hello.txt", map[string]any{"k": "f", "m": 420, "t": 1700000000000, "e": "utf8", "c": "héllo 🚀\n"}}}}}}
}

func TestVolumeCrashHelper(t *testing.T) {
	dir := os.Getenv("JP250_TEST_CRASH_VOLUME")
	if dir == "" {
		return
	}
	s, e := NewVolumeStore(dir, false)
	if e != nil {
		os.Exit(20)
	}
	if e = s.Acquire(); e != nil {
		os.Exit(21)
	}
	// Deliberately bypass Close: the OS must release the range lock on crash.
	os.Exit(0)
}
func TestVolumeLockReleasedAfterProcessExit(t *testing.T) {
	dir := t.TempDir()
	cmd := exec.Command(os.Args[0], "-test.run=^TestVolumeCrashHelper$")
	cmd.Env = append(os.Environ(), "JP250_TEST_CRASH_VOLUME="+dir)
	if b, e := cmd.CombinedOutput(); e != nil {
		t.Fatalf("crash helper: %s %v", b, e)
	}
	s, e := NewVolumeStore(dir, false)
	if e != nil {
		t.Fatal(e)
	}
	if e = s.Acquire(); e != nil {
		t.Fatal(e)
	}
	defer s.Close()
	if e = s.Write(volumeFixture()); e != nil {
		t.Fatal(e)
	}
}
func TestStrictJSONBoundaries(t *testing.T) {
	for _, s := range []string{`{"a":1,"a":2}`, `{"a":{"x":1,"x":2}}`, `NaN`, `1e999`, `{} {}`, `"\ud800"`, `"\udc00"`, strings.Repeat("[", 257) + strings.Repeat("]", 257)} {
		if _, e := strictJSON([]byte(s)); e == nil {
			t.Errorf("accepted %s", s[:min(len(s), 80)])
		}
	}
	v, e := strictJSON([]byte(`{"n":123456789012345678901,"a":1.0}`))
	if e != nil {
		t.Fatal(e)
	}
	if _, ok := object(v)["n"].(json.Number); !ok {
		t.Fatal("lost integer precision")
	}
}
func TestVolumePersistenceLockAndDisappearance(t *testing.T) {
	dir := t.TempDir()
	s, e := NewVolumeStore(dir, false)
	if e != nil {
		t.Fatal(e)
	}
	if e = s.Acquire(); e != nil {
		t.Fatal(e)
	}
	if v, e := s.Read(); e != nil || v != nil {
		t.Fatalf("initial %v %v", v, e)
	}
	if e = s.Write(volumeFixture()); e != nil {
		t.Fatal(e)
	}
	if _, e = s.Read(); e != nil {
		t.Fatal(e)
	}
	other, _ := NewVolumeStore(dir, false)
	if e = other.Acquire(); e == nil {
		other.Close()
		t.Fatal("concurrent writer acquired lock")
	}
	if e = s.Close(); e != nil {
		t.Fatal(e)
	}
	if e = other.Acquire(); e != nil {
		t.Fatalf("restart lock: %v", e)
	}
	defer other.Close()
	if _, e = other.Read(); e != nil {
		t.Fatal(e)
	}
	if e = os.Remove(filepath.Join(dir, "volume.json")); e != nil {
		t.Fatal(e)
	}
	if _, e = other.Read(); e == nil {
		t.Fatal("seen volume disappearance treated as empty")
	}
	if _, e = os.Stat(filepath.Join(dir, "server.lock")); e != nil {
		t.Fatal("lock sentinel removed")
	}
}
func TestLegacyLockAndUnsafeFiles(t *testing.T) {
	dir := t.TempDir()
	if e := os.WriteFile(filepath.Join(dir, "server.lock"), []byte(`{"pid":12}`), 0600); e != nil {
		t.Fatal(e)
	}
	s, _ := NewVolumeStore(dir, false)
	if e := s.Acquire(); e == nil {
		s.Close()
		t.Fatal("accepted legacy lock")
	}
	path := filepath.Join(dir, "file")
	if e := os.WriteFile(path, []byte("data"), 0600); e != nil {
		t.Fatal(e)
	}
	if _, e := readRegular(path, 2, false); e == nil {
		t.Fatal("accepted oversized file")
	}
	t.Run("hardlink_rejection", func(t *testing.T) {
		if e := os.Link(path, filepath.Join(dir, "hard")); e != nil {
			if os.IsPermission(e) {
				t.Skipf("OS sandbox denied creation of the hardlink fixture: %v", e)
			}
			t.Fatal(e)
		}
		if _, e := readRegular(path, 10, false); e == nil {
			t.Fatal("accepted multi-link file")
		}
		if e := atomicWrite(path, []byte("new")); e == nil {
			t.Fatal("replaced multi-link file")
		}
	})
}
func TestVolumeSchemaQuotas(t *testing.T) {
	for _, change := range []func(map[string]any){func(r map[string]any) { object(r["snap"])["v"] = true }, func(r map[string]any) { object(object(r["snap"])["root"])["m"] = 512 }, func(r map[string]any) { object(object(r["snap"])["root"])["ch"] = []any{[]any{"..", map[string]any{}}} }, func(r map[string]any) {
		root := object(object(r["snap"])["root"])
		root["ch"] = []any{[]any{"a", map[string]any{"k": "f", "m": 420, "t": 0, "e": "b64", "c": "YQ==\n"}}}
	}, func(r map[string]any) { object(object(r["snap"])["root"])["t"] = -1 }} {
		r := volumeFixture()
		change(r)
		if e := validateVolume(r); e == nil {
			t.Fatal("accepted invalid volume")
		}
	}
	if e := validateVolume(volumeFixture()); e != nil {
		t.Fatal(e)
	}
}
func TestSourceGrantConfinement(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "test.vcmd")
	if e := os.WriteFile(p, []byte("\xef\xbb\xbf#!vcmd 1\r\necho hello\r\n"), 0600); e != nil {
		t.Fatal(e)
	}
	s, e := NewLocalSources([]string{dir})
	if e != nil {
		t.Fatal(e)
	}
	v, e := s.Read("test.vcmd", "")
	if e != nil {
		t.Fatal(e)
	}
	if object(v)["text"] != "#!vcmd 1\necho hello\n" {
		t.Fatal(v)
	}
	for _, path := range []string{"../test.vcmd", "test.txt", "https://a/test.vcmd", `\\server\share\test.vcmd`, "NUL.vcmd", "file.vcmd:evil", "trail .vcmd "} {
		if _, e = s.Read(path, ""); e == nil {
			t.Errorf("accepted %s", path)
		}
	}
	if _, e = s.Read(filepath.Join(t.TempDir(), "other.vcmd"), ""); e == nil {
		t.Fatal("accepted outside path")
	}
	none, _ := NewLocalSources(nil)
	if _, e = none.Read(p, ""); e == nil {
		t.Fatal("accepted without grant")
	}
}
