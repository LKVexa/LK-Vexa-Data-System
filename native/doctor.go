package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
)

// runDoctor is offline and never starts a listener, renderer or other process.
// Exit 0 means the packaged prerequisites are present and the integrity/profile
// checks pass; exit 2 means not ready; exit 1 means the report could not be saved.
func runDoctor(root string) int      { return doctorReport(root, true) }
func runDoctorQuiet(root string) int { return doctorReport(root, false) }
func doctorReport(root string, verbose bool) int {
	checks := []any{}
	ready := true
	add := func(name string, detail any, e error) {
		ok := e == nil
		if !ok {
			ready = false
			detail = e.Error()
		}
		checks = append(checks, map[string]any{"name": name, "ok": ok, "detail": detail})
	}
	var platformErr error
	if runtime.GOOS != "windows" || runtime.GOARCH != "amd64" {
		platformErr = errors.New("this release requires Windows x64")
	}
	add("windows-x64", runtime.GOOS+"/"+runtime.GOARCH, platformErr)
	platform, platformCheck := portableWindowsPlatform(root)
	add("windows-11-local-deployment", platform, platformCheck)
	verification, e := verifyPackage(root)
	add("immutable-package-integrity", verification, e)
	profile, e := LoadJA21Profile(filepath.Join(root, "source", "jp250-service.ja"))
	var profileDetail any
	if e == nil {
		profileDetail = map[string]any{"path": "source/jp250-service.ja", "sha256": profile.SourceHash}
	}
	add("ja21-service-profile", profileDetail, e)
	for _, rel := range []string{"bin/JPConsole.exe", "runtime/webview2-sdk/1.0.4191.47/Microsoft.Web.WebView2.Core.dll", "runtime/webview2-sdk/1.0.4191.47/Microsoft.Web.WebView2.Wpf.dll", "runtime/webview2-sdk/1.0.4191.47/WebView2Loader.dll"} {
		add(rel, rel, doctorFile(filepath.Join(root, filepath.FromSlash(rel))))
	}
	fixed := "runtime/webview2-fixed/msedgewebview2.exe"
	e = doctorFile(filepath.Join(root, filepath.FromSlash(fixed)))
	if e != nil {
		e = fmt.Errorf("app-local Fixed Version WebView2 is missing or invalid; this package is not ready for portable offline launch: %w", e)
	}
	add("fixed-webview2-renderer", fixed, e)
	windowsDir := os.Getenv("SystemRoot")
	if windowsDir == "" {
		windowsDir = `C:\Windows`
	}
	framework := filepath.Join(windowsDir, "Microsoft.NET", "Framework64", "v4.0.30319")
	for _, rel := range []string{"mscorlib.dll", "System.dll", "System.Xaml.dll", "WPF/PresentationFramework.dll", "WPF/PresentationCore.dll", "WPF/WindowsBase.dll"} {
		path := filepath.Join(framework, filepath.FromSlash(rel))
		add("windows-dotnet-wpf/"+rel, path, doctorFile(path))
	}
	workspace := filepath.Join(root, "workspace")
	add("portable-workspace-write-read-sync", workspace, doctorProbe(workspace))
	report := map[string]any{"schema": "JA21-PHOTON-DOCTOR/2", "version": ReleaseVersion, "portableReady": ready, "checks": checks, "noProcessesLaunched": true, "noNetworkAccess": true, "rendererPresenceIsNotStartupQualification": true}
	data, e := jsonBytes(report)
	if e != nil {
		fmt.Fprintln(os.Stderr, "Doctor report encoding failed:", e)
		return 1
	}
	reportPath := filepath.Join(workspace, "doctor.json")
	if e = atomicWrite(reportPath, data); e != nil {
		report["reportSaveError"] = e.Error()
		pretty, _ := json.MarshalIndent(report, "", "  ")
		fmt.Println(string(pretty))
		fmt.Fprintln(os.Stderr, "Doctor report could not be saved:", e)
		return 1
	}
	if verbose {
		pretty, _ := json.MarshalIndent(report, "", "  ")
		fmt.Println(string(pretty))
		fmt.Println("Doctor report:", reportPath)
	} else if ready {
		fmt.Println("Portable installation checks passed.")
	} else {
		fmt.Fprintln(os.Stderr, "Portable installation checks failed. Details:", reportPath)
		for _, value := range checks {
			check := value.(map[string]any)
			if check["ok"] == false {
				fmt.Fprintf(os.Stderr, "  %s: %v\n", check["name"], check["detail"])
			}
		}
	}
	if !ready {
		return 2
	}
	return 0
}
func doctorFile(path string) error {
	p, e := plainPath(path, false)
	if e != nil {
		return e
	}
	info, e := os.Stat(p)
	if e != nil {
		return e
	}
	if !info.Mode().IsRegular() || info.Size() == 0 {
		return errors.New("expected a nonempty regular file")
	}
	return nil
}
func doctorProbe(directory string) error {
	p, e := plainPath(directory, true)
	if e != nil {
		return e
	}
	if e = os.MkdirAll(p, 0700); e != nil {
		return e
	}
	if _, e = plainPath(p, false); e != nil {
		return e
	}
	f, e := os.CreateTemp(p, ".doctor-probe-*.tmp")
	if e != nil {
		return e
	}
	path := f.Name()
	defer os.Remove(path)
	defer f.Close()
	data := []byte("JA21 native offline doctor probe\n")
	if e = writeAll(f, data); e != nil {
		return e
	}
	if e = f.Sync(); e != nil {
		return e
	}
	if e = f.Close(); e != nil {
		return e
	}
	read, e := readRegular(path, 256, false)
	if e != nil {
		return e
	}
	if !bytes.Equal(read, data) {
		return errors.New("workspace probe read did not match its written bytes")
	}
	return nil
}
