package main

import (
	"os"
	"testing"
)

func testTempDir(t *testing.T) string {
	t.Helper()
	if os.Getenv("JP300_RETAIN_QUALIFICATION_FIXTURES") != "1" {
		return t.TempDir()
	}
	// Qualification keeps each case beneath its run-specific TEMP directory as
	// filesystem evidence. Explicit deletion and persistence assertions in tests
	// still run; only Go's automatic recursive fixture cleanup is omitted.
	directory, err := os.MkdirTemp(os.TempDir(), "case-")
	if err != nil {
		t.Fatal(err)
	}
	return directory
}
