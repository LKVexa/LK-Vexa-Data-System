package main

// These Windows primitives preserve the JP250 single-writer and local-file
// contracts. In particular, lock files are never unlinked on shutdown.
import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"unicode/utf16"
	"unicode/utf8"
	"unsafe"
)

const maxRecord = 6 * 1024 * 1024

var kernelStorage = syscall.NewLazyDLL("kernel32.dll")
var lockFileExProc = kernelStorage.NewProc("LockFileEx")
var moveFileExProc = kernelStorage.NewProc("MoveFileExW")

type winIdentity struct {
	volume, high, low, links, attrs uint32
	size                            uint64
	write, creation                 uint64
}

func handleIdentity(h syscall.Handle) (winIdentity, error) {
	var x syscall.ByHandleFileInformation
	if err := syscall.GetFileInformationByHandle(h, &x); err != nil {
		return winIdentity{}, err
	}
	return winIdentity{x.VolumeSerialNumber, x.FileIndexHigh, x.FileIndexLow, x.NumberOfLinks, x.FileAttributes, uint64(x.FileSizeHigh)<<32 | uint64(x.FileSizeLow), uint64(x.LastWriteTime.HighDateTime)<<32 | uint64(x.LastWriteTime.LowDateTime), uint64(x.CreationTime.HighDateTime)<<32 | uint64(x.CreationTime.LowDateTime)}, nil
}
func (a winIdentity) sameFile(b winIdentity) bool {
	return a.volume == b.volume && a.high == b.high && a.low == b.low
}
func openHandle(path string, access, share, creation, flags uint32) (syscall.Handle, error) {
	p, e := syscall.UTF16PtrFromString(path)
	if e != nil {
		return 0, e
	}
	return syscall.CreateFile(p, access, share, nil, creation, flags, 0)
}
func namedIdentity(path string) (winIdentity, error) {
	h, e := openHandle(path, 0, syscall.FILE_SHARE_READ|syscall.FILE_SHARE_WRITE|syscall.FILE_SHARE_DELETE, syscall.OPEN_EXISTING, 0x00200000|0x02000000)
	if e != nil {
		return winIdentity{}, e
	}
	defer syscall.CloseHandle(h)
	return handleIdentity(h)
}

func plainPath(value string, allowMissing bool) (string, error) {
	if strings.HasPrefix(value, `\\`) || strings.HasPrefix(value, "//") {
		return "", errors.New("use a local drive, not a UNC or device path")
	}
	p, e := filepath.Abs(value)
	if e != nil {
		return "", e
	}
	p = filepath.Clean(p)
	if strings.HasPrefix(p, `\\`) {
		return "", errors.New("use a local drive, not a UNC or device path")
	}
	volume := filepath.VolumeName(p)
	rest := strings.TrimPrefix(p, volume)
	cur := volume + string(os.PathSeparator)
	for _, part := range strings.FieldsFunc(rest, func(r rune) bool { return r == '/' || r == '\\' }) {
		cur = filepath.Join(cur, part)
		s, err := os.Lstat(cur)
		if os.IsNotExist(err) && allowMissing {
			continue
		}
		if err != nil {
			return "", err
		}
		if s.Mode()&os.ModeSymlink != 0 {
			return "", errors.New("symbolic links and reparse points are refused")
		}
		data, ok := s.Sys().(*syscall.Win32FileAttributeData)
		if ok && data.FileAttributes&0x400 != 0 {
			return "", errors.New("symbolic links and reparse points are refused")
		}
	}
	return p, nil
}
func readRegular(path string, limit int64, missing bool) ([]byte, error) {
	p, e := plainPath(path, true)
	if e != nil {
		return nil, e
	}
	before, e := namedIdentity(p)
	if e != nil {
		if missing && os.IsNotExist(e) {
			return nil, nil
		}
		return nil, e
	}
	if before.attrs&(0x10|0x400) != 0 || before.links != 1 || before.size > uint64(limit) {
		return nil, errors.New("expected a bounded single-link regular file")
	}
	// FILE_SHARE_READ excludes writers and replacement while this handle is open.
	h, e := openHandle(p, syscall.GENERIC_READ, syscall.FILE_SHARE_READ, syscall.OPEN_EXISTING, 0x00200000)
	if e != nil {
		return nil, e
	}
	f := os.NewFile(uintptr(h), p)
	defer f.Close()
	opened, e := handleIdentity(h)
	if e != nil {
		return nil, e
	}
	if opened != before {
		return nil, errors.New("file identity changed")
	}
	b, e := io.ReadAll(io.LimitReader(f, limit+1))
	if e != nil {
		return nil, e
	}
	if int64(len(b)) > limit {
		return nil, errors.New("read limit exceeded")
	}
	after, e := handleIdentity(h)
	if e != nil {
		return nil, e
	}
	if after != opened || uint64(len(b)) != opened.size {
		return nil, errors.New("file changed during read")
	}
	if _, e = plainPath(p, false); e != nil {
		return nil, e
	}
	named, e := namedIdentity(p)
	if e != nil {
		return nil, e
	}
	if named != after {
		return nil, errors.New("named file identity changed")
	}
	return b, nil
}

func strictJSON(data []byte) (any, error) {
	if !utf8.Valid(data) {
		return nil, errors.New("JSON must be UTF-8")
	}
	// Go's standard decoder replaces unpaired UTF-16 escapes with U+FFFD.
	// Refuse them explicitly so persisted names and hash inputs never change.
	quoted := false
	for i := 0; i < len(data); i++ {
		if data[i] == '"' {
			quoted = !quoted
			continue
		}
		if !quoted || data[i] != '\\' {
			continue
		}
		i++
		if i >= len(data) || data[i] != 'u' {
			continue
		}
		if i+4 >= len(data) {
			return nil, errors.New("unfinished Unicode escape")
		}
		x, err := strconv.ParseUint(string(data[i+1:i+5]), 16, 16)
		if err != nil {
			return nil, errors.New("invalid Unicode escape")
		}
		i += 4
		if x >= 0xd800 && x <= 0xdbff {
			if i+6 >= len(data) || data[i+1] != '\\' || data[i+2] != 'u' {
				return nil, errors.New("unpaired Unicode surrogate is unsupported")
			}
			y, err := strconv.ParseUint(string(data[i+3:i+7]), 16, 16)
			if err != nil || y < 0xdc00 || y > 0xdfff {
				return nil, errors.New("unpaired Unicode surrogate is unsupported")
			}
			i += 6
		} else if x >= 0xdc00 && x <= 0xdfff {
			return nil, errors.New("unpaired Unicode surrogate is unsupported")
		}
	}
	d := json.NewDecoder(bytes.NewReader(data))
	d.UseNumber()
	var parse func(int) (any, error)
	parse = func(depth int) (any, error) {
		if depth > 256 {
			return nil, errors.New("JSON nesting limit exceeded")
		}
		t, e := d.Token()
		if e != nil {
			return nil, e
		}
		if delim, ok := t.(json.Delim); ok {
			if depth >= 256 {
				return nil, errors.New("JSON nesting limit exceeded")
			}
			switch delim {
			case '{':
				m := map[string]any{}
				for d.More() {
					k, e := d.Token()
					if e != nil {
						return nil, e
					}
					s, ok := k.(string)
					if !ok {
						return nil, errors.New("JSON object key required")
					}
					if _, exists := m[s]; exists {
						return nil, errors.New("duplicate JSON key")
					}
					v, e := parse(depth + 1)
					if e != nil {
						return nil, e
					}
					m[s] = v
				}
				if _, e = d.Token(); e != nil {
					return nil, e
				}
				return m, nil
			case '[':
				a := []any{}
				for d.More() {
					v, e := parse(depth + 1)
					if e != nil {
						return nil, e
					}
					a = append(a, v)
				}
				if _, e = d.Token(); e != nil {
					return nil, e
				}
				return a, nil
			default:
				return nil, errors.New("unexpected JSON delimiter")
			}
		}
		if n, ok := t.(json.Number); ok {
			if strings.ContainsAny(n.String(), ".eE") {
				v, e := n.Float64()
				if e != nil || math.IsInf(v, 0) || math.IsNaN(v) {
					return nil, errors.New("nonfinite JSON number")
				}
			} else if len(strings.TrimPrefix(n.String(), "-")) > 4300 {
				return nil, errors.New("JSON integer digit limit exceeded")
			}
		}
		return t, nil
	}
	v, e := parse(0)
	if e != nil {
		return nil, e
	}
	if _, e = d.Token(); e != io.EOF {
		if e == nil {
			e = errors.New("trailing JSON data")
		}
		return nil, e
	}
	return v, nil
}
func jsonBytes(v any) ([]byte, error) {
	b, e := canonicalJSON(v, true)
	if e != nil {
		return nil, e
	}
	return append(b, '\n'), nil
}
func atomicWrite(path string, data []byte) error {
	p, e := plainPath(path, true)
	if e != nil {
		return e
	}
	dir := filepath.Dir(p)
	if e = os.MkdirAll(dir, 0700); e != nil {
		return e
	}
	if _, e = plainPath(dir, false); e != nil {
		return e
	}
	if x, e := namedIdentity(p); e == nil {
		if x.links != 1 || x.attrs&(0x10|0x400) != 0 {
			return errors.New("unsafe replacement target")
		}
	} else if !os.IsNotExist(e) {
		return e
	}
	f, e := os.CreateTemp(dir, ".write-*.tmp")
	if e != nil {
		return e
	}
	tmp := f.Name()
	defer os.Remove(tmp)
	if _, e = f.Write(data); e == nil {
		e = f.Sync()
	}
	closeErr := f.Close()
	if e != nil {
		return e
	}
	if closeErr != nil {
		return closeErr
	}
	if _, e = plainPath(p, true); e != nil {
		return e
	}
	// MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH.
	a, e := syscall.UTF16PtrFromString(tmp)
	if e != nil {
		return e
	}
	b, e := syscall.UTF16PtrFromString(p)
	if e != nil {
		return e
	}
	r, _, x := moveFileExProc.Call(uintptr(unsafe.Pointer(a)), uintptr(unsafe.Pointer(b)), 0x1|0x8)
	if r == 0 {
		return x
	}
	return nil
}
func number(v any) (float64, bool) {
	switch n := v.(type) {
	case json.Number:
		f, e := strconv.ParseFloat(string(n), 64)
		return f, e == nil && !math.IsInf(f, 0) && !math.IsNaN(f)
	case float64:
		return n, !math.IsNaN(n) && !math.IsInf(n, 0)
	case float32:
		return float64(n), true
	case int:
		return float64(n), true
	case int64:
		return float64(n), true
	case uint64:
		return float64(n), true
	}
	return 0, false
}
func integer(v any) (int64, bool) {
	switch n := v.(type) {
	case json.Number:
		if strings.ContainsAny(string(n), ".eE") {
			return 0, false
		}
		x, e := strconv.ParseInt(string(n), 10, 64)
		return x, e == nil
	case int:
		return int64(n), true
	case int64:
		return n, true
	case uint64:
		if n <= math.MaxInt64 {
			return int64(n), true
		}
	}
	return 0, false
}
func object(v any) map[string]any { m, _ := v.(map[string]any); return m }
func array(v any) []any           { a, _ := v.([]any); return a }
func str(v any) string            { s, _ := v.(string); return s }
func utf16len(s string) int       { return len(utf16.Encode([]rune(s))) }
func validateVolume(value any) error {
	r := object(value)
	if r == nil || r["schema"] != "JA21-VOLUME/1" {
		return errors.New("unsupported volume schema")
	}
	s := object(r["snap"])
	if s == nil {
		return errors.New("invalid snapshot root")
	}
	v, ok := integer(s["v"])
	if !ok || v != 1 || object(s["root"])["k"] != "d" {
		return errors.New("invalid snapshot root")
	}
	nodes, used := 0, 0
	var visit func(any, int, string) error
	visit = func(raw any, depth int, path string) error {
		nodes++
		if nodes > 1500 || depth > 64 || utf16len(path) > 4096 {
			return errors.New("snapshot structure quota exceeded")
		}
		o := object(raw)
		if o == nil {
			return errors.New("invalid snapshot node")
		}
		m, mok := integer(o["m"])
		t, tok := number(o["t"])
		if !mok || m < 0 || m > 511 || !tok || t < 0 || t > 8640000000000000 {
			return errors.New("invalid snapshot metadata")
		}
		switch o["k"] {
		case "f":
			c, ok := o["c"].(string)
			if !ok {
				return errors.New("invalid file encoding")
			}
			switch o["e"] {
			case "utf8":
				used += len([]byte(c))
			case "b64":
				b, e := base64.StdEncoding.Strict().DecodeString(c)
				if e != nil || base64.StdEncoding.EncodeToString(b) != c {
					return errors.New("noncanonical base64")
				}
				used += len(b)
			default:
				return errors.New("invalid file encoding")
			}
			if used > 2097152 {
				return errors.New("snapshot file-byte quota exceeded")
			}
		case "d":
			ch, ok := o["ch"].([]any)
			if !ok {
				return errors.New("invalid snapshot node")
			}
			seen := map[string]bool{}
			for _, raw := range ch {
				p, ok := raw.([]any)
				if !ok || len(p) != 2 {
					return errors.New("invalid directory pair")
				}
				n, ok := p[0].(string)
				if !ok || n == "" || n == "." || n == ".." || utf16len(n) > 255 || strings.Contains(n, "/") || hasControl(n) || seen[n] {
					return errors.New("invalid/duplicate directory name")
				}
				seen[n] = true
				if e := visit(p[1], depth+1, path+"/"+n); e != nil {
					return e
				}
			}
		default:
			return errors.New("invalid snapshot node")
		}
		return nil
	}
	if e := visit(s["root"], 0, ""); e != nil {
		return e
	}
	b, e := jsonBytes(r)
	if e != nil {
		return e
	}
	if len(b) > maxRecord {
		return errors.New("encoded volume quota exceeded")
	}
	return nil
}
func hasControl(s string) bool {
	for _, r := range s {
		if r < 32 || r == 127 {
			return true
		}
	}
	return false
}

type fileLock struct {
	f    *os.File
	id   winIdentity
	path string
}

func acquireFileLock(path string) (*fileLock, error) {
	p, e := plainPath(path, true)
	if e != nil {
		return nil, e
	}
	if e = os.MkdirAll(filepath.Dir(p), 0700); e != nil {
		return nil, e
	}
	if _, e = plainPath(filepath.Dir(p), false); e != nil {
		return nil, e
	}
	h, e := openHandle(p, syscall.GENERIC_READ|syscall.GENERIC_WRITE, syscall.FILE_SHARE_READ|syscall.FILE_SHARE_WRITE, syscall.OPEN_ALWAYS, 0x00200000)
	if e != nil {
		return nil, e
	}
	f := os.NewFile(uintptr(h), p)
	good := false
	defer func() {
		if !good {
			f.Close()
		}
	}()
	id, e := handleIdentity(h)
	if e != nil {
		return nil, e
	}
	if id.attrs&(0x10|0x400) != 0 || id.links != 1 {
		return nil, errors.New("unsafe lock identity")
	}
	n, e := namedIdentity(p)
	if e != nil {
		return nil, e
	}
	if !n.sameFile(id) {
		return nil, errors.New("unsafe lock identity")
	}
	if id.size == 0 {
		if _, e = f.Write([]byte{0}); e != nil {
			return nil, e
		}
		if e = f.Sync(); e != nil {
			return nil, e
		}
	}
	var ov syscall.Overlapped
	r, _, x := lockFileExProc.Call(uintptr(h), 0x2|0x1, 0, 1, 0, uintptr(unsafe.Pointer(&ov)))
	if r == 0 {
		return nil, fmt.Errorf("volume is busy or lock cannot be acquired: %w", x)
	}
	good = true
	return &fileLock{f, id, p}, nil
}
func (l *fileLock) close() error {
	if l == nil || l.f == nil {
		return nil
	}
	x, e := namedIdentity(l.path)
	if e == nil && !x.sameFile(l.id) {
		e = errors.New("volume lock identity changed; inspect the workspace")
	}
	c := l.f.Close()
	l.f = nil
	if e != nil {
		return e
	}
	return c
}

type VolumeStore struct {
	directory, path string
	memory, seen    bool
	record          any
	lock            *fileLock
	mu              sync.Mutex
}

func NewVolumeStore(directory string, memory bool) (*VolumeStore, error) {
	p, e := plainPath(directory, true)
	if e != nil {
		return nil, e
	}
	return &VolumeStore{directory: p, path: filepath.Join(p, "volume.json"), memory: memory}, nil
}
func (s *VolumeStore) Acquire() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.lock != nil {
		return errors.New("this volume is already held by this instance")
	}
	if e := os.MkdirAll(s.directory, 0700); e != nil {
		return e
	}
	path := filepath.Join(s.directory, "server.lock")
	b, e := readRegular(path, 4096, true)
	if e != nil {
		return e
	}
	if len(b) > 0 && b[0] != 0 {
		v, e := strictJSON(b)
		if e != nil {
			return errors.New("unrecognized volume lock; preserve and inspect it")
		}
		if object(v)["schema"] != "JA21-VOLUME-LOCK/2" {
			return errors.New("legacy volume lock: stop the older console and restore its export into a fresh installation")
		}
	}
	l, e := acquireFileLock(path)
	if e != nil {
		return e
	}
	b, e = jsonBytes(map[string]any{"schema": "JA21-VOLUME-LOCK/2", "pid": os.Getpid(), "product": "JA21 Photon native host"})
	if e == nil {
		_, e = l.f.Seek(0, 0)
	}
	if e == nil {
		_, e = l.f.Write(b)
	}
	if e == nil {
		e = l.f.Truncate(int64(len(b)))
	}
	if e == nil {
		e = l.f.Sync()
	}
	if e != nil {
		l.close()
		return e
	}
	s.lock = l
	return nil
}
func (s *VolumeStore) Read() (any, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.memory {
		return s.record, nil
	}
	b, e := readRegular(s.path, maxRecord, !s.seen)
	if e != nil {
		return nil, e
	}
	if b == nil {
		return nil, nil
	}
	s.seen = true
	v, e := strictJSON(b)
	if e != nil {
		return nil, e
	}
	if e = validateVolume(v); e != nil {
		return nil, e
	}
	return v, nil
}
func (s *VolumeStore) Write(v any) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if e := validateVolume(v); e != nil {
		return e
	}
	b, e := jsonBytes(v)
	if e != nil {
		return e
	}
	if s.memory {
		copy, e := strictJSON(b)
		if e != nil {
			return e
		}
		s.record = copy
		return nil
	}
	if e = atomicWrite(s.path, b); e == nil {
		s.seen = true
	}
	return e
}
func (s *VolumeStore) Close() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.lock == nil {
		return nil
	}
	e := s.lock.close()
	s.lock = nil
	return e
}

type sourceRoot struct {
	path string
	id   winIdentity
}
type LocalSources struct{ roots []sourceRoot }

var sourceScheme = regexp.MustCompile(`^[A-Za-z][\w+.-]*://`)
var sourceDevice = regexp.MustCompile(`(?i)^(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)`)

func validateSourceSpelling(s string) error {
	if s == "" || utf16len(s) > 4096 || hasControl(s) || strings.HasPrefix(s, `\\`) || strings.HasPrefix(s, "//") || sourceScheme.MatchString(s) {
		return errors.New("invalid/traversing/network source path")
	}
	parts := strings.FieldsFunc(s, func(r rune) bool { return r == '/' || r == '\\' })
	for i, p := range parts {
		if p == ".." {
			return errors.New("invalid/traversing/network source path")
		}
		if i == 0 && len(p) == 2 && p[1] == ':' {
			continue
		}
		if strings.Contains(p, ":") || strings.HasSuffix(p, ".") || strings.HasSuffix(p, " ") || sourceDevice.MatchString(p) {
			return errors.New("device/stream path refused")
		}
	}
	return nil
}
func NewLocalSources(roots []string) (*LocalSources, error) {
	if len(roots) > 8 {
		return nil, errors.New("at most 8 source roots")
	}
	s := &LocalSources{}
	for _, r := range roots {
		if e := validateSourceSpelling(r); e != nil {
			return nil, e
		}
		p, e := plainPath(r, false)
		if e != nil {
			return nil, e
		}
		x, e := namedIdentity(p)
		if e != nil {
			return nil, e
		}
		if x.attrs&0x10 == 0 {
			return nil, errors.New("VCMD root must be a directory")
		}
		s.roots = append(s.roots, sourceRoot{p, x})
	}
	return s, nil
}
func (s *LocalSources) RootsCount() int { return len(s.roots) }
func pathWithin(root, path string) bool {
	r := strings.ToLower(filepath.Clean(root))
	p := strings.ToLower(filepath.Clean(path))
	return strings.HasPrefix(p, r+string(os.PathSeparator))
}
func (s *LocalSources) Read(request, parent string) (any, error) {
	if len(s.roots) == 0 {
		return nil, errors.New("UNAVAILABLE: no server-local .vcmd root granted")
	}
	if e := validateSourceSpelling(request); e != nil {
		return nil, e
	}
	if !strings.HasSuffix(strings.ToLower(request), ".vcmd") {
		return nil, errors.New("only .vcmd sources are admitted")
	}
	if parent != "" {
		if e := validateSourceSpelling(parent); e != nil {
			return nil, e
		}
	}
	if !filepath.IsAbs(request) && parent == "" && len(s.roots) != 1 {
		return nil, errors.New("use absolute paths with multiple roots")
	}
	q := request
	if !filepath.IsAbs(q) {
		base := s.roots[0].path
		if parent != "" {
			base = filepath.Dir(parent)
		}
		q = filepath.Join(base, q)
	}
	p, e := plainPath(q, false)
	if e != nil {
		return nil, e
	}
	var root *sourceRoot
	for i := range s.roots {
		if pathWithin(s.roots[i].path, p) {
			root = &s.roots[i]
			break
		}
	}
	if root == nil {
		return nil, errors.New("source is outside granted roots")
	}
	id, e := namedIdentity(root.path)
	if e != nil || !id.sameFile(root.id) {
		return nil, errors.New("source root identity changed")
	}
	b, e := readRegular(p, 524288, false)
	if e != nil {
		return nil, e
	}
	if !utf8.Valid(b) {
		return nil, errors.New("VCMD source must be UTF-8")
	}
	text := strings.TrimPrefix(string(b), "\ufeff")
	text = strings.ReplaceAll(text, "\r\n", "\n")
	if strings.TrimSpace(strings.SplitN(text, "\n", 2)[0]) != "#!vcmd 1" {
		return nil, errors.New("invalid VCMD/1 header or control character")
	}
	for _, r := range text {
		if r < 32 && r != '\t' && r != '\n' || r == 127 {
			return nil, errors.New("invalid VCMD/1 header or control character")
		}
	}
	if _, e = plainPath(root.path, false); e != nil {
		return nil, e
	}
	id, e = namedIdentity(root.path)
	if e != nil || !id.sameFile(root.id) {
		return nil, errors.New("source root identity changed during read")
	}
	return map[string]any{"id": p, "text": text}, nil
}
