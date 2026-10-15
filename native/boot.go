package main

import (
	"errors"
	"strings"
)

const bootUsage = `JP300 single launcher

BOOT.cmd                              Open the local console (127.0.0.1:8877).
BOOT.cmd --local [--port PORT]         Select a different local port.
BOOT.cmd --network [--show-token]      Use server-config.json and require TLS.
BOOT.cmd --lan-unsafe [options]        Explicit unencrypted LAN test mode.
BOOT.cmd --doctor                     Save offline prerequisite diagnostics.
BOOT.cmd --verify                     Verify immutable package integrity.
BOOT.cmd --self-test                  Run quick native checks.
BOOT.cmd --test                       Run installation qualification.
BOOT.cmd --qualify                    Run native regressions and real-window tests.
BOOT.cmd --restore "export.json"      Restore a virtual-volume export.
BOOT.cmd --vcmd "file.vcmd" [--check] Run an isolated standalone script.
BOOT.cmd --help                       Show these modes.

Local mode ignores server-config.json and never exposes a LAN listener.
LAN test mode ignores TLS configuration; provide its settings explicitly.
Quote paths containing spaces, & or !. Relative script/report paths use the
calling directory. Installation qualification records this OS's results;
it cannot establish that Windows was freshly installed.
`

// The batch wrapper forwards the original argument vector without CALL, SHIFT,
// delayed expansion, or a second command interpreter. All mode selection is here.
func bootArguments(original []string) ([]string, string, error) {
	args := append([]string{}, original...)
	wrapped := false
	if len(args) > 0 && args[0] == "--boot" {
		wrapped = true
		args = args[1:]
	}
	for _, a := range args {
		if strings.SplitN(a, "=", 2)[0] == "--boot" {
			return nil, "", errors.New("--boot must appear once as the first argument")
		}
	}
	if len(args) == 0 {
		return []string{"--local"}, "launch", nil
	}
	for _, a := range args {
		switch a {
		case "--qualify", "--test":
			if len(args) != 1 {
				return nil, "", errors.New("--qualify and --test must be used alone")
			}
			return nil, "qualify", nil
		case "--help":
			if len(args) != 1 {
				return nil, "", errors.New("--help must be used alone")
			}
			return nil, "help", nil
		}
	}
	if !wrapped {
		return args, "launch", nil
	}
	independent := false
	for _, a := range args {
		switch strings.SplitN(a, "=", 2)[0] {
		case "--local", "--network", "--lan-unsafe", "--doctor", "--verify", "--self-test", "--restore", "--vcmd":
			independent = true
		}
	}
	if !independent {
		args = append([]string{"--local"}, args...)
	}
	return args, "launch", nil
}
