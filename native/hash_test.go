package main

import (
	"bytes"
	"crypto/sha256"
	"os"
	"path/filepath"
	"testing"
)

func TestStreamingPackageHashAndBounds(t *testing.T) {
	data := bytes.Repeat([]byte("fixed-memory-portable-hash\x00"), 130001)
	file := filepath.Join(testTempDir(t), "large.bin")
	if e := os.WriteFile(file, data, 0600); e != nil {
		t.Fatal(e)
	}
	got, e := hashRegular(file, int64(len(data)))
	if e != nil || got != sha256.Sum256(data) {
		t.Fatalf("multi-buffer hash mismatch: %v", e)
	}
	if _, e = hashRegular(file, int64(len(data)-1)); e == nil {
		t.Fatal("oversized file accepted")
	}
	if _, e = hashRegular(filepath.Dir(file), 512<<20); e == nil {
		t.Fatal("directory accepted for hashing")
	}
}
