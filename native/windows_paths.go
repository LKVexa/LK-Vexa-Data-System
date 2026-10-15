package main

import (
	"errors"
	"fmt"
	"path/filepath"
	"strings"
)

// Keep the mixed Win32/.NET Framework/WebView2 package within the legacy path
// envelope. Go's ability to open longer paths does not extend to every child
// process, Windows Explorer, or the managed presenter. Counts are UTF-16 units,
// excluding the terminating NUL, rather than UTF-8 bytes or Unicode runes.
const portableRootLimit = 100
const windowsFilePathLimit = 259
const windowsDirectoryPathLimit = 247

func windowsPathSafety(path string) error {
	if utf16len(path) > windowsFilePathLimit {
		return fmt.Errorf("Windows file path exceeds %d UTF-16 code units; use a shorter path", windowsFilePathLimit)
	}
	volume := filepath.VolumeName(path)
	if len(volume) != 2 || volume[1] != ':' || !((volume[0] >= 'A' && volume[0] <= 'Z') || (volume[0] >= 'a' && volume[0] <= 'z')) || !filepath.IsAbs(path) || strings.HasPrefix(path, `\\`) {
		return errors.New("a local absolute Windows drive path is required")
	}
	parts := strings.FieldsFunc(path[len(volume):], func(r rune) bool { return r == '/' || r == '\\' })
	for _, part := range parts {
		if utf16len(part) > 255 || part == "." || part == ".." || hasControl(part) ||
			strings.ContainsAny(part, `<>:"|?*`) || strings.HasSuffix(part, ".") || strings.HasSuffix(part, " ") || sourceDevice.MatchString(strings.TrimRight(strings.SplitN(part, ".", 2)[0], " ")) {
			return errors.New("invalid, reserved, or aliased Windows path component")
		}
	}
	if utf16len(filepath.Dir(path)) > windowsDirectoryPathLimit {
		return fmt.Errorf("Windows parent directory exceeds %d UTF-16 code units; use a shorter path", windowsDirectoryPathLimit)
	}
	return nil
}

func portableRootSafety(root string) error {
	if e := windowsPathSafety(root); e != nil {
		return e
	}
	if utf16len(strings.TrimRight(root, `\/`)) > portableRootLimit {
		return fmt.Errorf("package root exceeds %d UTF-16 code units; extract the complete package to a short local folder such as C:\\LKVS", portableRootLimit)
	}
	return nil
}
