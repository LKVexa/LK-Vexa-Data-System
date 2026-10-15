package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"
)

// Qualification checks a real installation. A successful run cannot determine
// whether the surrounding OS was freshly installed, so that assertion is never
// inferred from a new folder, restricted PATH, or an operator-supplied flag.
func runQualification(root string) int {
	if code := runDoctorQuiet(root); code != 0 {
		return code
	}
	runDir := filepath.Join(root, "workspace", "qualification", time.Now().UTC().Format("20060102T150405.000000000Z"))
	if e := os.MkdirAll(filepath.Join(runDir, "temp"), 0700); e != nil {
		fmt.Fprintln(os.Stderr, e)
		return 1
	}
	report := map[string]any{
		"schema": "JP300-INSTALLATION-QUALIFICATION/1", "version": ReleaseVersion,
		"startedUtc": time.Now().UTC().Format(time.RFC3339), "packageRoot": root,
		"passed": false, "cleanMachineCertified": false,
		"scope":          "Actual installation tests on the executing Windows OS. Clean-machine provenance must be established separately.",
		"pythonRequired": false, "nodeRequired": false, "targetCompilationRequired": false,
		"installedRendererFallback": false, "pathRestricted": true,
		"testFixturesRetained": true, "testFixtureDirectory": filepath.Join(runDir, "temp"),
	}
	resultPath := filepath.Join(runDir, "result.json")
	finish := func(e error) int {
		report["finishedUtc"] = time.Now().UTC().Format(time.RFC3339)
		report["passed"] = e == nil
		if e != nil {
			report["error"] = e.Error()
		}
		data, encodeErr := jsonBytes(report)
		if encodeErr == nil {
			encodeErr = atomicWrite(resultPath, data)
		}
		if encodeErr != nil {
			fmt.Fprintln(os.Stderr, "Could not save qualification report:", encodeErr)
			return 1
		}
		fmt.Println("Installation test report:", resultPath)
		if e != nil {
			fmt.Fprintln(os.Stderr, "Installation tests failed:", e)
			return 1
		}
		fmt.Println("Installation tests passed. A restricted environment is not proof of a fresh Windows installation.")
		return 0
	}
	platform, e := portableWindowsPlatform(root)
	if e != nil {
		return finish(e)
	}
	report["platform"] = platform
	if e = nativeSelfTest(root); e != nil {
		return finish(e)
	}

	// Execute the shipped precompiled tests without any developer runtime lookup.
	testLog, e := os.OpenFile(filepath.Join(runDir, "native-tests.txt"), os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if e != nil {
		return finish(e)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 630*time.Second)
	cmd := exec.CommandContext(ctx, filepath.Join(root, "bin", "JPTests.exe"), "-test.v", "-test.timeout=600s")
	cmd.Dir = filepath.Join(root, "native")
	cmd.Env = append(qualificationEnvironment(filepath.Join(runDir, "temp")), "JP300_RETAIN_QUALIFICATION_FIXTURES=1")
	var tests bytes.Buffer
	cmd.Stdout = io.MultiWriter(testLog, &tests)
	cmd.Stderr = cmd.Stdout
	fmt.Println("Running the packaged native regression tests...")
	e = cmd.Run()
	cancel()
	closeErr := testLog.Close()
	if e != nil {
		return finish(fmt.Errorf("packaged native regression tests: %w; inspect native-tests.txt", e))
	}
	if closeErr != nil {
		return finish(closeErr)
	}
	passed, skipped := qualificationTestResults(tests.String())
	if passed == 0 {
		return finish(errors.New("native regression program did not report any passed tests"))
	}
	report["nativeTopLevelPassed"] = passed
	report["nativeSkipped"] = skipped

	// Loading the actual managed adapter also resolves CLR/WPF prerequisites.
	ctx, cancel = context.WithTimeout(context.Background(), 30*time.Second)
	probe := exec.CommandContext(ctx, filepath.Join(root, "bin", "JPConsole.exe"), "--probe")
	probe.Dir = root
	probe.Env = qualificationEnvironment(filepath.Join(runDir, "temp"))
	probeData, e := probe.CombinedOutput()
	cancel()
	if e != nil {
		return finish(fmt.Errorf("native CLR/WPF/renderer probe failed: %w: %s", e, string(probeData)))
	}
	var managed map[string]any
	if e = json.Unmarshal(bytes.TrimSpace(probeData), &managed); e != nil {
		return finish(fmt.Errorf("managed probe report: %w", e))
	}
	adapter, _ := managed["adapterAssembly"].(string)
	renderer, _ := managed["rendererFolder"].(string)
	version, _ := managed["rendererVersion"].(string)
	if managed["ok"] != true || managed["process64"] != true || managed["installedRendererFallback"] != false ||
		!strings.EqualFold(filepath.Clean(adapter), filepath.Join(root, "bin", "Microsoft.Web.WebView2.Core.dll")) ||
		!strings.EqualFold(filepath.Clean(renderer), filepath.Join(root, "runtime", "webview2-fixed")) || version == "" {
		return finish(errors.New("managed probe did not confirm the bundled x64 adapter and Fixed Version renderer"))
	}
	report["managedProbe"] = managed
	if e = atomicWrite(filepath.Join(runDir, "managed-probe.json"), probeData); e != nil {
		return finish(e)
	}

	// This starts the actual WPF presenter and its Worker, then uses the actual
	// native granted-file/Photon paths. It cannot be satisfied by a Node fixture.
	o := &Options{VCMD: filepath.Join(root, "validation", "portable-smoke.vcmd"), Report: filepath.Join(runDir, "standalone-result.json"), CloseAfterJob: true, QualificationWorkspace: filepath.Join(runDir, "application")}
	if e = os.MkdirAll(filepath.Join(o.QualificationWorkspace, "temp"), 0700); e != nil {
		return finish(e)
	}
	var output bytes.Buffer
	o.JobOutputSink = &output
	cfg, e := standaloneConfig(o, root)
	if e != nil {
		return finish(e)
	}
	code, e := runService(root, o, cfg)
	if e != nil {
		return finish(fmt.Errorf("actual native console qualification: %w", e))
	}
	if code != 0 {
		return finish(fmt.Errorf("actual native console returned %d", code))
	}
	for _, marker := range []string{"JP300_QUALIFY_BEGIN", "JP300_FILE_READBACK_OK", "JP300_QUALIFY_END", `"schema": "VEC1/STATE_AUDIT/2"`, `"snapshot_hash"`} {
		if !strings.Contains(output.String(), marker) {
			return finish(fmt.Errorf("actual native command output lacks required marker %q", marker))
		}
	}
	if e = atomicWrite(filepath.Join(runDir, "native-command-output.txt"), output.Bytes()); e != nil {
		return finish(e)
	}
	report["actualNativeWindowAndWorkerPassed"] = true
	report["actualCommandFileReadbackPassed"] = true
	report["actualNativePhotonVerifyAndSnapshotPassed"] = true
	report["gracefulPresenterExitAndPersistenceAcknowledgement"] = true
	report["applicationDataIsolated"] = true
	return finish(nil)
}

func qualificationEnvironment(temp string) []string {
	result := []string{}
	for _, item := range os.Environ() {
		key := strings.ToUpper(strings.SplitN(item, "=", 2)[0])
		if key == "PATH" || key == "TEMP" || key == "TMP" || strings.HasPrefix(key, "JA21_") || strings.HasPrefix(key, "WEBVIEW2_") || strings.HasPrefix(key, "PYTHON") || strings.HasPrefix(key, "NODE_") || strings.HasPrefix(key, "GO") {
			continue
		}
		result = append(result, item)
	}
	win := os.Getenv("SystemRoot")
	if win == "" {
		win = `C:\Windows`
	}
	return append(result, "PATH="+filepath.Join(win, "System32")+";"+win, "TEMP="+temp, "TMP="+temp)
}

func qualificationTestResults(output string) (int, []string) {
	passed := 0
	skipped := []string{}
	for _, line := range strings.Split(output, "\n") {
		if strings.HasPrefix(line, "--- PASS: ") {
			passed++
		}
		if s := strings.TrimSpace(line); strings.HasPrefix(s, "--- SKIP: ") {
			skipped = append(skipped, s)
		}
	}
	return passed, skipped
}
