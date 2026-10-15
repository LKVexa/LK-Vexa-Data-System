package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestQualificationDoesNotInheritDeveloperRuntimes(t *testing.T) {
	t.Setenv("PATH", `D:\developer\python;D:\developer\node;D:\developer\go`)
	for _, key := range []string{"PYTHONHOME", "NODE_OPTIONS", "JA21_ENGINE_NODE", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", "GOROOT", "GOTOOLCHAIN"} {
		t.Setenv(key, "must-not-be-inherited")
	}
	temp := testTempDir(t)
	values := map[string]string{}
	for _, item := range qualificationEnvironment(temp) {
		pair := strings.SplitN(item, "=", 2)
		if len(pair) == 2 {
			values[strings.ToUpper(pair[0])] = pair[1]
		}
	}
	win := os.Getenv("SystemRoot")
	if win == "" {
		win = `C:\Windows`
	}
	if values["PATH"] != filepath.Join(win, "System32")+";"+win || values["TEMP"] != temp || values["TMP"] != temp {
		t.Fatal("qualification did not isolate executable lookup and temporary files")
	}
	for _, key := range []string{"PYTHONHOME", "NODE_OPTIONS", "JA21_ENGINE_NODE", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", "GOROOT", "GOTOOLCHAIN"} {
		if _, exists := values[key]; exists {
			t.Fatalf("inherited developer/runtime override: %s", key)
		}
	}
	if !strings.Contains(os.Getenv("PATH"), "developer") {
		t.Fatal("qualification changed the parent process environment")
	}
}

func TestQualificationRecordsSkippedCasesSeparately(t *testing.T) {
	passed, skipped := qualificationTestResults("--- PASS: TestOne (0.01s)\n    --- PASS: TestOne/subcase (0.00s)\n--- SKIP: TestOptional (0.00s)\n    --- SKIP: TestLinks/hardlink (0.00s)\nPASS\n")
	if passed != 1 || len(skipped) != 2 {
		t.Fatalf("passed=%d skipped=%v", passed, skipped)
	}
}

func TestPortableWindowsRefusesUNCDeployment(t *testing.T) {
	if _, e := portableWindowsPlatform(`\\server\share\JP300`); e == nil {
		t.Fatal("network renderer deployment accepted")
	}
}
