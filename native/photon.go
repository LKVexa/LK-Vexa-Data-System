package main

import (
	"bytes"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"reflect"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode/utf8"
)

// Python's VEC canonical JSON is deliberately reproduced here. encoding/json's
// HTML escaping, exponent selection and integral-float formatting would change
// existing ledger and state hashes.
func canonicalJSON(v any, ascii bool) ([]byte, error) {
	var b bytes.Buffer
	var write func(any) error
	write = func(x any) error {
		switch t := x.(type) {
		case nil:
			b.WriteString("null")
		case bool:
			if t {
				b.WriteString("true")
			} else {
				b.WriteString("false")
			}
		case string:
			if !utf8.ValidString(t) {
				return errors.New("invalid UTF-8 string")
			}
			b.WriteByte('"')
			for _, r := range t {
				switch r {
				case '"':
					b.WriteString(`\"`)
				case '\\':
					b.WriteString(`\\`)
				case '\b':
					b.WriteString(`\b`)
				case '\f':
					b.WriteString(`\f`)
				case '\n':
					b.WriteString(`\n`)
				case '\r':
					b.WriteString(`\r`)
				case '\t':
					b.WriteString(`\t`)
				default:
					if r < 32 || ascii && r >= 127 {
						if r <= 65535 {
							fmt.Fprintf(&b, `\u%04x`, r)
						} else {
							r -= 0x10000
							fmt.Fprintf(&b, `\u%04x\u%04x`, 0xd800+(r>>10), 0xdc00+(r&1023))
						}
					} else {
						b.WriteRune(r)
					}
				}
			}
			b.WriteByte('"')
		case json.Number:
			s := string(t)
			if strings.ContainsAny(s, ".eE") {
				f, e := strconv.ParseFloat(s, 64)
				if e != nil {
					return e
				}
				return write(f)
			}
			if s == "-0" {
				s = "0"
			}
			b.WriteString(s)
		case float64:
			if math.IsNaN(t) || math.IsInf(t, 0) {
				return errors.New("nonfinite JSON number")
			}
			s := strconv.FormatFloat(t, 'e', -1, 64)
			i := strings.IndexByte(s, 'e')
			exp, _ := strconv.Atoi(s[i+1:])
			if exp >= -4 && exp < 16 {
				s = strconv.FormatFloat(t, 'f', -1, 64)
				if !strings.Contains(s, ".") {
					s += ".0"
				}
			}
			b.WriteString(s)
		case float32:
			return write(float64(t))
		case int:
			b.WriteString(strconv.Itoa(t))
		case int64:
			b.WriteString(strconv.FormatInt(t, 10))
		case uint64:
			b.WriteString(strconv.FormatUint(t, 10))
		case map[string]any:
			keys := make([]string, 0, len(t))
			for k := range t {
				keys = append(keys, k)
			}
			sort.Strings(keys)
			b.WriteByte('{')
			for i, k := range keys {
				if i > 0 {
					b.WriteByte(',')
				}
				if e := write(k); e != nil {
					return e
				}
				b.WriteByte(':')
				if e := write(t[k]); e != nil {
					return e
				}
			}
			b.WriteByte('}')
		case []any:
			b.WriteByte('[')
			for i, x := range t {
				if i > 0 {
					b.WriteByte(',')
				}
				if e := write(x); e != nil {
					return e
				}
			}
			b.WriteByte(']')
		default:
			r := reflect.ValueOf(x)
			if r.Kind() == reflect.Slice || r.Kind() == reflect.Array {
				a := make([]any, r.Len())
				for i := range a {
					a[i] = r.Index(i).Interface()
				}
				return write(a)
			}
			if r.Kind() == reflect.Map && r.Type().Key().Kind() == reflect.String {
				m := map[string]any{}
				iter := r.MapRange()
				for iter.Next() {
					m[iter.Key().String()] = iter.Value().Interface()
				}
				return write(m)
			}
			return fmt.Errorf("unsupported JSON type %T", x)
		}
		return nil
	}
	if e := write(v); e != nil {
		return nil, e
	}
	return b.Bytes(), nil
}
func hashBytes(b []byte) string { h := sha256.Sum256(b); return hex.EncodeToString(h[:]) }
func hashJSON(v any) (string, error) {
	b, e := canonicalJSON(v, false)
	if e != nil {
		return "", e
	}
	return hashBytes(b), nil
}
func atomicJSON(path string, v any) error {
	b, e := canonicalJSON(v, false)
	if e != nil {
		return e
	}
	var out bytes.Buffer
	if e = json.Indent(&out, b, "", "  "); e != nil {
		return e
	}
	out.WriteByte('\n')
	return atomicWrite(path, out.Bytes())
}
func readJSON(path string, limit int64) (map[string]any, error) {
	b, e := readRegular(path, limit, false)
	if e != nil {
		return nil, e
	}
	v, e := strictJSON(b)
	if e != nil {
		return nil, e
	}
	m := object(v)
	if m == nil {
		return nil, errors.New("JSON object required")
	}
	return m, nil
}
func copyJSON(m map[string]any) map[string]any {
	b, _ := canonicalJSON(m, false)
	v, _ := strictJSON(b)
	return object(v)
}
func logical(m map[string]any) map[string]any {
	x := map[string]any{}
	for k, v := range m {
		if k != "updated_utc" && k != "hashes" {
			x[k] = v
		}
	}
	return x
}
func utcNow() string { return time.Now().UTC().Format("2006-01-02T15:04:05.000000Z") }

var electronIDPattern = regexp.MustCompile(`^vec1-[0-9a-f]{24}$`)
var shaPattern = regexp.MustCompile(`^[0-9a-f]{64}$`)
var statuses = map[string]bool{"READY": true, "RUNNING": true, "VERIFIED": true, "FROZEN": true, "SUSPENDED": true, "FAULTED": true, "BLOCKED": true, "RETIRED": true}

const zeroHash = "0000000000000000000000000000000000000000000000000000000000000000"

type PhotonBridge struct {
	root, source, eid, binding, electrons, snapshots, receipts, lockPath string
	config, quotas                                                       map[string]any
	mu                                                                   sync.Mutex
}

func NewPhotonBridge(packageRoot, workspace string) (*PhotonBridge, error) {
	p, e := plainPath(filepath.Join(workspace, "photon"), true)
	if e != nil {
		return nil, e
	}
	s := &PhotonBridge{root: p, source: filepath.Join(packageRoot, "photon")}
	if e = os.MkdirAll(p, 0700); e != nil {
		return nil, e
	}
	if e = s.inspect(); e != nil {
		return nil, e
	}
	for _, dir := range []string{"config", "programs"} {
		base := filepath.Join(s.source, dir)
		e = filepath.WalkDir(base, func(src string, d os.DirEntry, err error) error {
			if err != nil {
				return err
			}
			if d.IsDir() {
				_, e := plainPath(src, false)
				return e
			}
			b, e := readRegular(src, 2097152, false)
			if e != nil {
				return e
			}
			rel, e := filepath.Rel(s.source, src)
			if e != nil {
				return e
			}
			dst := filepath.Join(s.root, rel)
			old, e := readRegular(dst, 2097152, true)
			if e != nil {
				return e
			}
			if old != nil {
				if !bytes.Equal(old, b) {
					return errors.New("Photon instance input drift; preserve and inspect workspace/photon")
				}
				return nil
			}
			return atomicWrite(dst, b)
		})
		if e != nil {
			return nil, e
		}
	}
	s.electrons = filepath.Join(p, "runtime", "state", "electrons")
	s.snapshots = filepath.Join(p, "runtime", "state", "snapshots")
	s.receipts = filepath.Join(p, "runtime", "evidence", "executions")
	s.lockPath = filepath.Join(p, "runtime", "state", ".vec1.lock")
	s.binding = filepath.Join(p, "console-binding.json")
	for _, d := range []string{s.electrons, s.snapshots, s.receipts} {
		if e = os.MkdirAll(d, 0700); e != nil {
			return nil, e
		}
		if _, e = plainPath(d, false); e != nil {
			return nil, e
		}
	}
	s.config, e = readJSON(filepath.Join(p, "config", "vec1.json"), 2097152)
	if e != nil {
		return nil, e
	}
	s.quotas = object(s.config["resource_quotas"])
	saved, e := readRegular(s.binding, 4096, true)
	if e != nil {
		return nil, e
	}
	var binding map[string]any
	if saved != nil {
		v, e := strictJSON(saved)
		if e != nil {
			return nil, e
		}
		binding = object(v)
		if binding == nil {
			return nil, errors.New("invalid Photon binding")
		}
	}
	ids, dirs, e := s.ids()
	if e != nil {
		return nil, e
	}
	if !reflect.DeepEqual(ids, dirs) || len(ids) > 1 {
		return nil, errors.New("incomplete or unexpected Photon identity records; preserve workspace/photon for recovery")
	}
	if binding != nil {
		if len(binding) != 2 || binding["schema"] != "JA21-PHOTON-CONSOLE/1" || len(ids) != 1 || ids[0] != binding["electron_id"] {
			return nil, errors.New("Photon console binding no longer matches its saved identity; no replacement was created")
		}
		s.eid = ids[0]
	} else if len(ids) == 1 {
		s.eid = ids[0]
	} else {
		if e = s.create(); e != nil {
			return nil, e
		}
	}
	if _, e = s.verified(false); e != nil {
		return nil, e
	}
	if binding == nil {
		if e = atomicJSON(s.binding, map[string]any{"schema": "JA21-PHOTON-CONSOLE/1", "electron_id": s.eid}); e != nil {
			return nil, e
		}
	}
	return s, nil
}
func (s *PhotonBridge) ElectronID() string { return s.eid }
func (s *PhotonBridge) inspect() error {
	count := 0
	var total int64
	return filepath.WalkDir(s.root, func(p string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		rel, e := filepath.Rel(s.root, p)
		if e != nil {
			return e
		}
		depth := 0
		if rel != "." {
			depth = len(strings.Split(rel, string(os.PathSeparator)))
		}
		if d.IsDir() && depth > 8 {
			return errors.New("Photon directory depth limit")
		}
		if _, e = plainPath(p, false); e != nil {
			return e
		}
		if rel == "." {
			return nil
		}
		count++
		if count > 1024 {
			return errors.New("Photon file-count limit")
		}
		id, e := namedIdentity(p)
		if e != nil {
			return e
		}
		if id.attrs&0x10 != 0 {
			return nil
		}
		info, e := os.Lstat(p)
		if e != nil {
			return e
		}
		if !info.Mode().IsRegular() || id.links != 1 || id.size > 8*1024*1024 {
			return errors.New("unsafe or oversized Photon file")
		}
		total += int64(id.size)
		if total > 32*1024*1024 {
			return errors.New("Photon byte quota exceeded")
		}
		return nil
	})
}
func (s *PhotonBridge) ids() ([]string, []string, error) {
	entries, e := os.ReadDir(s.electrons)
	if e != nil {
		return nil, nil, e
	}
	ids, all := []string{}, []string{}
	for _, d := range entries {
		all = append(all, d.Name())
		if d.IsDir() && electronIDPattern.MatchString(d.Name()) {
			info, e := os.Stat(filepath.Join(s.electrons, d.Name(), "state.json"))
			if e == nil && info.Mode().IsRegular() {
				ids = append(ids, d.Name())
			}
		}
	}
	sort.Strings(ids)
	sort.Strings(all)
	return ids, all, nil
}
func (s *PhotonBridge) assertIdentity(binding bool) error {
	ids, dirs, e := s.ids()
	if e != nil {
		return e
	}
	if len(ids) != 1 || len(dirs) != 1 || ids[0] != s.eid || dirs[0] != s.eid {
		return errors.New("Photon identity is missing or unexpected records appeared; verification cannot treat an empty store as healthy")
	}
	if binding {
		b, e := readRegular(s.binding, 4096, true)
		if e != nil {
			return e
		}
		if b == nil {
			return errors.New("Photon console binding changed or disappeared")
		}
		v, e := strictJSON(b)
		if e != nil {
			return e
		}
		m := object(v)
		if len(m) != 2 || m["schema"] != "JA21-PHOTON-CONSOLE/1" || m["electron_id"] != s.eid {
			return errors.New("Photon console binding changed or disappeared")
		}
	}
	return nil
}
func (s *PhotonBridge) statePath(eid string) string {
	return filepath.Join(s.electrons, eid, "state.json")
}
func (s *PhotonBridge) ledgerPath(eid string) string {
	return filepath.Join(s.electrons, eid, "ledger.jsonl")
}
func quota(m map[string]any, key string, defaultValue int64) int64 {
	if n, ok := integer(m[key]); ok {
		return n
	}
	return defaultValue
}
func (s *PhotonBridge) stateProblems(o map[string]any) []any {
	p := []any{}
	add := func(x string) { p = append(p, x) }
	if o == nil || o["schema"] != "VEC/ELECTRON_STATE/1" {
		return []any{"unexpected or missing state schema"}
	}
	eid := str(o["electron_id"])
	if !electronIDPattern.MatchString(eid) {
		add("invalid electron id; expected vec1-<24 hex>")
	}
	if n, ok := integer(o["generation_id"]); !ok || n < 0 {
		add("generation_id must be a non-negative integer")
	}
	if o["parent_id"] != nil && !electronIDPattern.MatchString(str(o["parent_id"])) {
		add("invalid parent electron id")
	}
	lineage, ok := o["lineage"].([]any)
	if !ok || len(lineage) == 0 {
		add("lineage must be a non-empty list")
	} else {
		seen := map[string]bool{}
		for _, x := range lineage {
			v := str(x)
			if !electronIDPattern.MatchString(v) {
				add("invalid lineage electron id")
				break
			}
			if seen[v] {
				add("lineage contains duplicate electron ids")
			}
			seen[v] = true
		}
		if str(lineage[len(lineage)-1]) != eid {
			add("lineage must end with electron_id")
		}
	}
	g := object(o["genome"])
	if g == nil || g["schema"] != "VEC/electron_genome/1" {
		add("unexpected or missing genome schema")
	} else {
		if !shaPattern.MatchString(str(g["program_sha256"])) {
			add("invalid program_sha256")
		}
		if g["network"] != "deny" {
			add("genome network policy must be deny")
		}
	}
	r := object(o["runtime"])
	props := object(o["properties"])
	sec := object(o["security"])
	if r == nil {
		add("runtime must be an object")
	} else {
		for _, key := range []string{"tick", "instruction_counter"} {
			if n, ok := integer(r[key]); !ok || n < 0 {
				add("runtime." + key + " must be a non-negative integer")
			}
		}
		if !statuses[str(r["execution_status"])] {
			add("invalid execution_status")
		}
		for key, q := range map[string]string{"mailbox": "max_mailbox", "outbound_queue": "max_outbound"} {
			a, ok := r[key].([]any)
			if !ok {
				add("runtime." + key + " must be a list")
			} else if lim := quota(s.quotas, q, 0); lim > 0 && int64(len(a)) > lim {
				add("runtime." + key + " exceeds " + q)
			}
		}
		if r["base_snapshot"] != nil && !shaPattern.MatchString(str(r["base_snapshot"])) {
			add("invalid base snapshot hash")
		}
	}
	if props == nil {
		add("properties must be an object")
	} else if r != nil && !equalJSON(props["state"], r["execution_status"]) {
		add("properties.state does not match runtime.execution_status")
	}
	if sec == nil {
		add("security must be an object")
	} else {
		if sec["network"] != "deny" {
			add("security.network must be deny")
		}
		if sec["filesystem"] != "package-runtime-only" {
			add("security.filesystem must be package-runtime-only")
		}
		a, aok := sec["suspended"].(bool)
		b, bok := sec["retired"].(bool)
		if !aok {
			add("security.suspended must be boolean")
		}
		if !bok {
			add("security.retired must be boolean")
		}
		if b && r != nil && r["execution_status"] != "RETIRED" {
			add("retired electron must have RETIRED status")
		}
		if a && !b && r != nil && r["execution_status"] != "SUSPENDED" {
			add("suspended electron must have SUSPENDED status")
		}
	}
	if o["integrity"] != nil {
		in := object(o["integrity"])
		if in == nil {
			add("integrity must be an object")
		} else {
			if n, ok := integer(in["ledger_events"]); !ok || n < 0 {
				add("integrity.ledger_events must be non-negative integer")
			}
			if !shaPattern.MatchString(str(in["ledger_head_sha256"])) {
				add("invalid ledger head hash")
			}
		}
	}
	return p
}
func (s *PhotonBridge) checkState(o map[string]any) map[string]any {
	p := s.stateProblems(o)
	h := object(o["hashes"])
	got, e := hashJSON(logical(o))
	if e != nil {
		p = append(p, e.Error())
	}
	g := object(o["genome"])
	gh, _ := hashJSON(g)
	caps := g["capabilities"]
	if caps == nil {
		caps = []any{}
	}
	ch, _ := hashJSON(caps)
	cfg, _ := hashJSON(s.config)
	gok := h["genome_sha256"] == gh
	cok := h["capability_sha256"] == ch
	return map[string]any{"ok": h["canonical_state_sha256"] == got && len(p) == 0 && gok && cok, "recorded": h["canonical_state_sha256"], "computed": got, "schema_ok": len(p) == 0, "schema_problems": p, "genome_hash_ok": gok, "capability_hash_ok": cok, "configuration_current": h["configuration_sha256"] == cfg}
}
func (s *PhotonBridge) load(eid string, verify bool) (map[string]any, error) {
	if !electronIDPattern.MatchString(eid) {
		return nil, errors.New("invalid electron id")
	}
	o, e := readJSON(s.statePath(eid), 8*1024*1024)
	if e != nil {
		return nil, e
	}
	if o["electron_id"] != eid {
		return nil, errors.New("state file names a different electron")
	}
	if verify && s.checkState(o)["ok"] != true {
		return nil, errors.New("electron state hash mismatch; state was modified outside vecctl (restore from a sealed snapshot)")
	}
	return o, nil
}
func (s *PhotonBridge) save(o map[string]any) (map[string]any, error) {
	o = copyJSON(o)
	if p := s.stateProblems(o); len(p) > 0 {
		return nil, fmt.Errorf("state schema/policy validation failed: %v", p)
	}
	o["updated_utc"] = utcNow()
	h := object(o["hashes"])
	if h == nil {
		h = map[string]any{}
		o["hashes"] = h
	}
	v, e := hashJSON(logical(o))
	if e != nil {
		return nil, e
	}
	h["canonical_state_sha256"] = v
	h["genome_sha256"], e = hashJSON(o["genome"])
	if e != nil {
		return nil, e
	}
	g := object(o["genome"])
	caps := g["capabilities"]
	if caps == nil {
		caps = []any{}
	}
	h["capability_sha256"], e = hashJSON(caps)
	if e != nil {
		return nil, e
	}
	h["configuration_sha256"], e = hashJSON(s.config)
	if e != nil {
		return nil, e
	}
	if e = atomicJSON(s.statePath(str(o["electron_id"])), o); e != nil {
		return nil, e
	}
	return o, nil
}
func verifyLedger(path string) (map[string]any, []map[string]any) {
	raw, e := readRegular(path, 8*1024*1024, true)
	if e != nil {
		return map[string]any{"ok": false, "events": 0, "reason": e.Error()}, nil
	}
	if raw == nil {
		return map[string]any{"ok": true, "events": 0, "head": zeroHash, "missing": true}, []map[string]any{}
	}
	fail := func(n int, reason string, seq any) (map[string]any, []map[string]any) {
		r := map[string]any{"ok": false, "events": n, "reason": reason}
		if seq != nil {
			r["seq"] = seq
		}
		return r, nil
	}
	if !utf8.Valid(raw) {
		return fail(0, "non-UTF-8 ledger", nil)
	}
	lines := strings.Split(string(raw), "\n")
	torn := strings.TrimSpace(lines[len(lines)-1]) != ""
	recs := []map[string]any{}
	prev := zeroHash
	for i, line := range lines {
		if strings.TrimSpace(line) == "" {
			continue
		}
		v, e := strictJSON([]byte(line))
		if e != nil {
			if torn && i == len(lines)-1 {
				return map[string]any{"ok": false, "events": len(recs), "head": prev, "reason": "torn trailing write (interrupted append)", "torn_tail": true, "line": i + 1}, nil
			}
			return fail(0, fmt.Sprintf("ledger: malformed record at line %d", i+1), nil)
		}
		r := object(v)
		if r == nil {
			return fail(len(recs), "record is not an object", nil)
		}
		seq := r["seq"]
		if r["schema"] != "VEC/LEDGER_EVENT/1" {
			return fail(len(recs), "unexpected ledger schema", seq)
		}
		if str(r["kind"]) == "" {
			return fail(len(recs), "invalid event kind", seq)
		}
		if object(r["payload"]) == nil {
			return fail(len(recs), "event payload is not an object", seq)
		}
		if n, ok := integer(r["tick"]); !ok || n < 0 {
			return fail(len(recs), "invalid event tick", seq)
		}
		if str(r["utc"]) == "" {
			return fail(len(recs), "missing event timestamp", seq)
		}
		n, ok := integer(seq)
		if !ok || n != int64(len(recs)) {
			return fail(len(recs), "sequence gap or reorder", seq)
		}
		if r["prev_hash"] != prev {
			return fail(len(recs), "prev_hash mismatch", seq)
		}
		test := map[string]any{}
		for k, v := range r {
			if k != "hash" {
				test[k] = v
			}
		}
		h, e := hashJSON(test)
		if e != nil || r["hash"] != h {
			return fail(len(recs), "event hash mismatch", seq)
		}
		prev = h
		recs = append(recs, r)
	}
	if torn {
		return map[string]any{"ok": false, "events": len(recs), "head": prev, "reason": "torn trailing write (interrupted append)", "torn_tail": true, "line": len(lines)}, nil
	}
	return map[string]any{"ok": true, "events": len(recs), "head": prev, "missing": false}, recs
}
func (s *PhotonBridge) event(eid string, tick any, kind string, payload map[string]any) error {
	path := s.ledgerPath(eid)
	v, _ := verifyLedger(path)
	if v["ok"] != true {
		return fmt.Errorf("ledger failed verification: %v", v["reason"])
	}
	r := map[string]any{"schema": "VEC/LEDGER_EVENT/1", "seq": v["events"], "tick": tick, "utc": utcNow(), "kind": kind, "payload": payload, "prev_hash": v["head"]}
	h, e := hashJSON(r)
	if e != nil {
		return e
	}
	r["hash"] = h
	b, e := jsonBytes(r)
	if e != nil {
		return e
	}
	old, e := readRegular(path, 8*1024*1024, true)
	if e != nil {
		return e
	}
	if len(old)+len(b) > 8*1024*1024 {
		return errors.New("Photon ledger file quota exceeded")
	}
	if e = atomicWrite(path, append(old, b...)); e != nil {
		return e
	}
	o, e := s.load(eid, true)
	if e != nil {
		return e
	}
	n, _ := integer(v["events"])
	o["integrity"] = map[string]any{"ledger_events": n + 1, "ledger_head_sha256": h}
	_, e = s.save(o)
	return e
}
func (s *PhotonBridge) create() error {
	l, e := acquireFileLock(s.lockPath)
	if e != nil {
		return e
	}
	defer l.close()
	ids, _, e := s.ids()
	if e != nil {
		return e
	}
	if int64(len(ids)) >= quota(s.quotas, "max_electrons", 128) {
		return errors.New("electron quota reached")
	}
	if len(ids) > 0 {
		return errors.New("Photon identity appeared during initialization; retry without replacing it")
	}
	rel := str(s.config["default_program"])
	if rel == "" {
		rel = "programs/fabric_probe.pal"
	}
	rel = strings.ReplaceAll(rel, `\`, "/")
	path := filepath.Join(s.root, filepath.FromSlash(rel))
	if !pathWithin(s.root, path) {
		return errors.New("program path escapes package root")
	}
	limit := quota(s.quotas, "max_program_bytes", 2097152)
	if limit <= 0 {
		limit = 2097152
	}
	program, e := readRegular(path, limit, false)
	if e != nil {
		return e
	}
	var random [32]byte
	if _, e = rand.Read(random[:]); e != nil {
		return e
	}
	s.eid = "vec1-" + hashBytes(random[:])[:24]
	if _, e = os.Lstat(filepath.Join(s.electrons, s.eid)); !os.IsNotExist(e) {
		return errors.New("unable to allocate unique Photon identity")
	}
	now := utcNow()
	g := map[string]any{"schema": "VEC/electron_genome/1", "version": "0.2.0", "program": rel, "program_sha256": hashBytes(program), "targets": []any{"N_SMALL", "N_MEDIUM", "N_LARGE", "N_XLARGE"}, "network": "deny", "capabilities": []any{"fabric.execute", "state.snapshot", "state.clone", "evidence.read"}}
	gh, e := hashJSON(g)
	if e != nil {
		return e
	}
	o := map[string]any{"schema": "VEC/ELECTRON_STATE/1", "electron_id": s.eid, "generation_id": 0, "parent_id": nil, "lineage": []any{s.eid}, "genesis_record": map[string]any{"created_utc": now, "genome_sha256": gh}, "genome": g, "properties": map[string]any{"charge": -1, "energy": 0, "orbital": "UNASSIGNED", "state": "READY", "phase": 0, "spin": "UP", "momentum_vector": []any{0, 0, 0}, "position_node_vector": []any{}, "interaction_radius": "DF0_LOCAL"}, "runtime": map[string]any{"tick": 0, "instruction_counter": 0, "execution_status": "READY", "mailbox": []any{}, "outbound_queue": []any{}, "base_snapshot": nil, "last_receipt": nil}, "security": map[string]any{"network": "deny", "plugins": []any{}, "filesystem": "package-runtime-only", "host_calls": "DF-only", "suspended": false, "retired": false}, "hashes": map[string]any{}}
	o, e = s.save(o)
	if e != nil {
		return e
	}
	return s.event(s.eid, 0, "GENESIS", map[string]any{"genome_sha256": gh, "state_sha256": object(o["hashes"])["canonical_state_sha256"]})
}
func (s *PhotonBridge) loadSnapshot(hash string) (map[string]any, error) {
	if !shaPattern.MatchString(hash) {
		return nil, errors.New("invalid snapshot hash")
	}
	rec, e := readJSON(filepath.Join(s.snapshots, hash+".json"), 8*1024*1024)
	if e != nil {
		return nil, e
	}
	state := object(rec["state"])
	got, e := hashJSON(rec["state"])
	if e != nil || rec["snapshot_hash"] != hash || got != hash {
		return nil, errors.New("snapshot hash mismatch; snapshot refused")
	}
	if state == nil || !equalJSON(rec["electron_id"], state["electron_id"]) || !equalJSON(rec["generation_id"], state["generation_id"]) {
		return nil, errors.New("snapshot envelope/state identity mismatch")
	}
	if s.checkState(state)["ok"] != true {
		return nil, errors.New("snapshot contains invalid or unsealed state")
	}
	return rec, nil
}
func equalJSON(a, b any) bool {
	x, e := canonicalJSON(a, false)
	if e != nil {
		return false
	}
	y, e := canonicalJSON(b, false)
	return e == nil && bytes.Equal(x, y)
}
func (s *PhotonBridge) snapshotAuthorized(o, state map[string]any, hash string) (bool, string) {
	source := str(state["electron_id"])
	found := false
	for _, x := range array(o["lineage"]) {
		if x == source {
			found = true
		}
	}
	if !found {
		return false, "snapshot source is not this electron or an ancestor"
	}
	v, recs := verifyLedger(s.ledgerPath(source))
	if v["ok"] != true {
		return false, fmt.Sprint("snapshot source ledger is invalid: ", v["reason"])
	}
	for _, r := range recs {
		if r["kind"] == "SNAPSHOT" && object(r["payload"])["snapshot_hash"] == hash {
			return true, "source_snapshot_event"
		}
	}
	return false, "snapshot hash is not authorized by the source ledger"
}
func (s *PhotonBridge) receiptPath(rel string) (string, error) {
	p := filepath.Join(s.root, filepath.FromSlash(rel))
	if !pathWithin(s.receipts, p) {
		return "", errors.New("receipt path escapes execution receipt root")
	}
	return p, nil
}
func (s *PhotonBridge) audit() map[string]any {
	out := map[string]any{}
	ids, _, e := s.ids()
	if e != nil {
		return map[string]any{"schema": "VEC1/STATE_AUDIT/2", "electrons": out, "ok": false, "reason": e.Error()}
	}
	allOK := true
	for _, eid := range ids {
		ledger, recs := verifyLedger(s.ledgerPath(eid))
		rec := map[string]any{"ledger": ledger}
		if ledger["ok"] == true {
			if len(recs) == 0 {
				rec["ledger_semantics"] = map[string]any{"ok": false, "reason": "electron exists but ledger has no genesis event"}
			} else {
				kind := recs[0]["kind"]
				expected := "CLONE_GENESIS"
				if kind == "GENESIS" {
					expected = "GENESIS"
				}
				rec["ledger_semantics"] = map[string]any{"ok": kind == "GENESIS" || kind == "CLONE_GENESIS", "first_kind": kind, "expected": expected}
			}
		}
		o, e := s.load(eid, false)
		if e != nil {
			rec["state"] = map[string]any{"ok": false, "reason": e.Error()}
		} else {
			rec["state"] = s.checkState(o)
			anchor := object(o["integrity"])
			rec["ledger_anchor"] = map[string]any{"ok": ledger["ok"] == true && equalJSON(anchor["ledger_events"], ledger["events"]) && anchor["ledger_head_sha256"] == ledger["head"], "recorded_events": anchor["ledger_events"], "actual_events": ledger["events"], "recorded_head": anchor["ledger_head_sha256"], "actual_head": ledger["head"]}
			runtime := object(o["runtime"])
			if base := str(runtime["base_snapshot"]); base != "" {
				snap, e := s.loadSnapshot(base)
				if e != nil {
					rec["base_snapshot"] = map[string]any{"ok": false, "hash": base, "reason": e.Error()}
				} else {
					ok, reason := s.snapshotAuthorized(o, object(snap["state"]), base)
					rec["base_snapshot"] = map[string]any{"ok": ok, "hash": base, "provenance": reason}
				}
			}
			checks := []any{}
			receiptsOK := true
			for _, ev := range recs {
				if ev["kind"] != "EXECUTION_VERIFIED" {
					continue
				}
				payload := object(ev["payload"])
				rel := str(payload["receipt"])
				item := map[string]any{"path": payload["receipt"], "tick": ev["tick"], "ok": false}
				path, e := s.receiptPath(rel)
				var b []byte
				var receipt map[string]any
				if e == nil {
					b, e = readRegular(path, 8*1024*1024, false)
				}
				if e == nil {
					v, x := strictJSON(b)
					e = x
					receipt = object(v)
					if receipt == nil && e == nil {
						e = errors.New("receipt is not an object")
					}
				}
				if e != nil {
					item["reason"] = e.Error()
				} else {
					h := hashBytes(b)
					hok := h == payload["receipt_sha256"]
					iok := receipt["schema"] == "VEC/EXECUTION_RECEIPT/1" && receipt["electron_id"] == eid && equalJSON(receipt["tick"], ev["tick"])
					eok := equalJSON(receipt["event_log_hash"], payload["event_log_hash"])
					item["hash_ok"] = hok
					item["identity_ok"] = iok
					item["event_log_hash_ok"] = eok
					item["file_sha256"] = h
					item["ok"] = hok && iok && eok
				}
				if item["ok"] != true {
					receiptsOK = false
				}
				checks = append(checks, item)
			}
			rec["receipts"] = map[string]any{"ok": receiptsOK, "count": len(checks), "checks": checks}
			if last := str(runtime["last_receipt"]); last != "" {
				ok := false
				var source any
				for _, x := range checks {
					m := object(x)
					if m["ok"] == true && m["path"] == last {
						ok = true
					}
				}
				if !ok {
					path, e := s.receiptPath(last)
					if e == nil {
						b, e := readRegular(path, 8*1024*1024, false)
						if e == nil {
							v, e := strictJSON(b)
							if e == nil {
								r := object(v)
								source = r["electron_id"]
								found := false
								for _, x := range array(o["lineage"]) {
									if equalJSON(x, source) {
										found = true
									}
								}
								if found && electronIDPattern.MatchString(str(source)) {
									sv, sr := verifyLedger(s.ledgerPath(str(source)))
									if sv["ok"] == true {
										for _, ev := range sr {
											p := object(ev["payload"])
											if ev["kind"] == "EXECUTION_VERIFIED" && p["receipt"] == last && p["receipt_sha256"] == hashBytes(b) {
												ok = true
											}
										}
									}
								}
							}
						}
					}
				}
				rec["last_receipt"] = map[string]any{"ok": ok, "path": last, "source_electron": source}
			}
		}
		ok := true
		for _, v := range rec {
			if m := object(v); m != nil && m["ok"] != true {
				ok = false
			}
		}
		rec["ok"] = ok
		if !ok {
			allOK = false
		}
		out[eid] = rec
	}
	return map[string]any{"schema": "VEC1/STATE_AUDIT/2", "electrons": out, "ok": allOK}
}
func (s *PhotonBridge) verified(binding bool) (map[string]any, error) {
	if e := s.inspect(); e != nil {
		return nil, e
	}
	if e := s.assertIdentity(binding); e != nil {
		return nil, e
	}
	v := s.audit()
	if v["ok"] != true {
		return nil, errors.New("Photon canonical state/ledger failed verification; preserve the workspace")
	}
	return v, nil
}
func (s *PhotonBridge) snapshot() (any, error) {
	l, e := acquireFileLock(s.lockPath)
	if e != nil {
		return nil, e
	}
	defer l.close()
	if _, e = s.verified(true); e != nil {
		return nil, e
	}
	entries, e := os.ReadDir(s.snapshots)
	if e != nil {
		return nil, e
	}
	count := 0
	for _, d := range entries {
		if strings.HasSuffix(d.Name(), ".json") {
			count++
		}
	}
	if count >= 128 {
		return nil, errors.New("Photon snapshot quota reached (128)")
	}
	o, e := s.load(s.eid, true)
	if e != nil {
		return nil, e
	}
	r := object(o["runtime"])
	prior := r["execution_status"]
	r["execution_status"] = "FROZEN"
	object(o["properties"])["state"] = "FROZEN"
	o, e = s.save(o)
	if e != nil {
		return nil, e
	}
	core := copyJSON(o)
	delete(core, "updated_utc")
	hash, e := hashJSON(core)
	if e != nil {
		return nil, e
	}
	rec := map[string]any{"schema": "VEC/SNAPSHOT/1", "snapshot_hash": hash, "electron_id": s.eid, "generation_id": o["generation_id"], "state": core, "created_utc": utcNow()}
	path := filepath.Join(s.snapshots, hash+".json")
	if _, e = os.Lstat(path); os.IsNotExist(e) {
		if e = atomicJSON(path, rec); e != nil {
			return nil, e
		}
	} else if e != nil {
		return nil, e
	} else {
		existing, e := s.loadSnapshot(hash)
		if e != nil {
			return nil, e
		}
		if existing["electron_id"] != s.eid {
			return nil, errors.New("snapshot hash collision/provenance mismatch")
		}
	}
	if e = s.event(s.eid, object(o["runtime"])["tick"], "SNAPSHOT", map[string]any{"snapshot_hash": hash}); e != nil {
		return nil, e
	}
	o, e = s.load(s.eid, true)
	if e != nil {
		return nil, e
	}
	r = object(o["runtime"])
	r["base_snapshot"] = hash
	if prior == "RUNNING" || prior == "FROZEN" {
		prior = "READY"
	}
	r["execution_status"] = prior
	object(o["properties"])["state"] = prior
	if _, e = s.save(o); e != nil {
		return nil, e
	}
	if _, e = s.verified(true); e != nil {
		return nil, e
	}
	return rec, nil
}
func (s *PhotonBridge) Call(action string) (any, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if action != "status" && action != "verify" && action != "snapshot" {
		return nil, errors.New("Photon exposes status, verify and snapshot only; no build/native execution API")
	}
	if action == "verify" {
		if e := s.inspect(); e != nil {
			return nil, e
		}
		if e := s.assertIdentity(true); e != nil {
			return map[string]any{"schema": "JA21-PHOTON-VERIFY/1", "ok": false, "reason": e.Error()}, nil
		}
		return s.audit(), nil
	}
	verification, e := s.verified(true)
	if e != nil {
		return nil, e
	}
	if action == "snapshot" {
		return s.snapshot()
	}
	o, e := s.load(s.eid, true)
	if e != nil {
		return nil, e
	}
	return map[string]any{"schema": "JA21-PHOTON-BINDING/1", "provider": "native compatible adapter for Generic Photon 0.2.0", "electron_id": s.eid, "state": object(o["runtime"])["execution_status"], "state_and_ledger_verified": verification["ok"], "canonical_state_sha256": object(o["hashes"])["canonical_state_sha256"], "ledger_anchor": o["integrity"], "network_policy": object(o["security"])["network"], "console_engine": "SPIRAL in native JA21 WebView2 Worker", "native_four_node_execution": "NOT RUN / NOT BOUND by this adapter", "nodejs_required": false, "snapshot_scope": "Photon identity/state, NOT the terminal virtual filesystem"}, nil
}
