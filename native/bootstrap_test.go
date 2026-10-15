package main

import (
	"encoding/json"
	"path/filepath"
	"strings"
	"testing"
)

func TestConsoleBootstrapQualificationIsHostOnlyAndOptional(t *testing.T) {
	for _, qualification := range []string{"", filepath.Join(testTempDir(t), "workspace", "qualification", "run", "application")} {
		data, e := consoleBootstrap("http://127.0.0.1:12345", strings.Repeat("a", 64), strings.Repeat("b", 64), qualification)
		if e != nil {
			t.Fatal(e)
		}
		var value map[string]any
		if e = json.Unmarshal(data, &value); e != nil {
			t.Fatal(e)
		}
		wantFields := 4
		if qualification != "" {
			wantFields = 5
			if value["qualificationWorkspace"] != qualification {
				t.Fatal("qualification workspace was not sent through the private envelope")
			}
		} else if _, exists := value["qualificationWorkspace"]; exists {
			t.Fatal("ordinary session unexpectedly includes a workspace override")
		}
		if len(value) != wantFields || value["protocol"] != float64(2) || value["origin"] != "http://127.0.0.1:12345" || value["token"] != strings.Repeat("a", 64) || value["engineToken"] != strings.Repeat("b", 64) {
			t.Fatal("private protocol2 fields changed")
		}
	}
	if _, e := consoleBootstrap("http://127.0.0.1:12345", strings.Repeat("a", 64), strings.Repeat("b", 64), strings.Repeat("x", 2048)); e == nil {
		t.Fatal("oversized native bootstrap admitted")
	}
}

func TestQualificationWorkspaceCannotEscapeItsPrivateBoundary(t *testing.T) {
	root := testTempDir(t)
	boundary := filepath.Join(root, "workspace", "qualification")
	want := filepath.Join(boundary, "run-1", "application")
	if got, e := qualificationWorkspacePath(root, want); e != nil || got != want {
		t.Fatalf("valid qualification workspace: %q %v", got, e)
	}
	for _, value := range []string{
		"relative",
		boundary,
		filepath.Join(root, "workspace"),
		filepath.Join(root, "workspace", "qualification-sibling", "application"),
		filepath.Join(boundary, "..", "server"),
		filepath.Join(boundary, "run", "application:stream"),
		filepath.Join(boundary, "run", "application."),
		filepath.Join(boundary, "run", "application "),
	} {
		if _, e := qualificationWorkspacePath(root, value); e == nil {
			t.Fatalf("invalid qualification workspace admitted: %q", value)
		}
	}
}
