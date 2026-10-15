package main

import (
	"path/filepath"
	"strings"
	"testing"
)

func TestWindowsLegacyPathBoundariesAndAliases(t *testing.T) {
	file259 := `C:\` + strings.Repeat("d", 120) + `\` + strings.Repeat("f", 135)
	dir247 := `C:\` + strings.Repeat("d", 244)
	for _, path := range []string{`C:\LKVS`, `D:\Folder & ! space\é 🚀\file.txt`, file259, dir247 + `\a`} {
		if e := windowsPathSafety(path); e != nil {
			t.Fatalf("safe Windows spelling %q: %v", path, e)
		}
	}
	for _, path := range []string{
		file259 + "x", dir247 + `d\a`, `C:\` + strings.Repeat("a", 256),
		`\\server\share\app`, `\\?\C:\LKVS`, `C:relative`, `relative\file`,
		`C:\LKVS\NUL.txt`, `C:\LKVS\NUL .txt`, `C:\LKVS\COM¹.dat`, `C:\LKVS\LPT9`,
		`C:\LKVS\CONIN$`, `C:\LKVS\CONOUT$`, `C:\LKVS\CONIN$.txt`, `C:\LKVS\CONOUT$.txt`,
		`C:\LKVS\name.`, `C:\LKVS\name `, `C:\LKVS\file:stream`, `C:\LKVS\bad<name`,
		"C:\\LKVS\\bad\x00name", `C:\LKVS\..\other`,
	} {
		if e := windowsPathSafety(path); e == nil {
			t.Fatalf("unsafe Windows spelling admitted: %q", path)
		}
	}
}

func TestQualificationRunDirectoryShortAndUnique(t *testing.T) {
	root := testTempDir(t)
	first, e := newQualificationRunDirectory(root)
	if e != nil {
		t.Fatal(e)
	}
	second, e := newQualificationRunDirectory(root)
	if e != nil {
		t.Fatal(e)
	}
	if first == second || len(filepath.Base(first)) != 10 || !strings.HasPrefix(filepath.Base(first), "r-") {
		t.Fatal("qualification directories must be unique with a bounded short basename")
	}
	for _, directory := range []string{first, second} {
		if filepath.Dir(directory) != filepath.Join(root, "workspace", "qualification") {
			t.Fatal("qualification directory escaped its private boundary")
		}
	}
}

func TestPortableRootBudgetUsesUTF16(t *testing.T) {
	ascii100 := `C:\` + strings.Repeat("a", 97)
	unicode100 := `D:\` + strings.Repeat("🚀", 48) + "x"
	for _, root := range []string{ascii100, unicode100} {
		if utf16len(root) != portableRootLimit {
			t.Fatal("boundary fixture is not 100 UTF-16 units")
		}
		if e := portableRootSafety(root); e != nil {
			t.Fatalf("supported root: %v", e)
		}
		if e := portableRootSafety(root + "x"); e == nil {
			t.Fatal("root above 100 UTF-16 units was admitted")
		}
	}
}

func TestQualificationSnapshotPathsFitMaximumRoot(t *testing.T) {
	root := `C:\` + strings.Repeat("r", portableRootLimit-3)
	run := filepath.Join(root, "workspace", "qualification", "r-12345678")
	for _, workspace := range []string{filepath.Join(run, "a"), filepath.Join(run, "t", "case-4294967295")} {
		snapshot := filepath.Join(workspace, "photon", "runtime", "state", "snapshots", strings.Repeat("a", 64)+".json")
		if e := windowsPathSafety(snapshot); e != nil {
			t.Fatalf("qualification snapshot needs too much path space (%d): %v", utf16len(snapshot), e)
		}
	}
}

func TestImmutableScratchBoundary(t *testing.T) {
	for _, relative := range []string{"work", "work/fixture.js", "Work/cache/file.bin", "workspace/state.json", "certs/private.key"} {
		if !excludedImmutable(relative) {
			t.Fatalf("mutable root is unexpectedly sealed: %s", relative)
		}
	}
	for _, relative := range []string{"workbench.js", "source/work/fixture.js", "appliance/node_modules/file.js", "native/main.go"} {
		if excludedImmutable(relative) {
			t.Fatalf("immutable source was unexpectedly excluded: %s", relative)
		}
	}
}
