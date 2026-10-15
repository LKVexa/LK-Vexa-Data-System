package main

import (
	"errors"
	"fmt"
	"path/filepath"
	"strings"
	"syscall"
	"unsafe"
)

// RtlGetVersion reports the OS version independently of compatibility manifests.
func windowsVersion() (uint32, uint32, uint32, error) {
	v := struct {
		Size, Major, Minor, Build, Platform uint32
		Description                         [128]uint16
	}{}
	v.Size = uint32(unsafe.Sizeof(v))
	p := syscall.NewLazyDLL("ntdll.dll").NewProc("RtlGetVersion")
	if e := p.Find(); e != nil {
		return 0, 0, 0, e
	}
	status, _, _ := p.Call(uintptr(unsafe.Pointer(&v)))
	if status != 0 {
		return 0, 0, 0, fmt.Errorf("RtlGetVersion returned %x", status)
	}
	return v.Major, v.Minor, v.Build, nil
}

func portableWindowsPlatform(root string) (any, error) {
	major, minor, build, e := windowsVersion()
	detail := map[string]any{"major": int64(major), "minor": int64(minor), "build": int64(build), "root": root}
	if e != nil {
		return detail, e
	}
	if major < 10 || build < 22000 {
		return detail, errors.New("this release requires Windows 11 x64; Windows 10 is not supported")
	}
	if strings.HasPrefix(root, `\\`) {
		return detail, errors.New("extract JP300 to a writable local drive; the bundled Fixed Version WebView2 cannot run from a network/UNC location")
	}
	volume := filepath.VolumeName(root)
	if volume == "" {
		return detail, errors.New("a local absolute package path is required")
	}
	p, e := syscall.UTF16PtrFromString(volume + `\`)
	if e != nil {
		return detail, e
	}
	driveType, _, _ := syscall.NewLazyDLL("kernel32.dll").NewProc("GetDriveTypeW").Call(uintptr(unsafe.Pointer(p)))
	detail["driveType"] = int64(driveType)
	if driveType == 4 {
		return detail, errors.New("mapped network drives are unsupported; extract JP300 to a writable local or removable drive")
	}
	return detail, nil
}
