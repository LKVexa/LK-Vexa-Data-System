package main

import (
	"crypto/tls"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
)

const ReleaseVersion = "3.0.0"

type Options struct {
	ConfigPath, Host, PublicOrigin, TLSCert, TLSKey                         string
	Port                                                                    int
	Origins, Roots                                                          repeatedFlag
	Insecure, ShowToken, ServerOnly, Check, Trace, Verify, SelfTest, Doctor bool
	VCMD, Report, Restore                                                   string
}
type Config struct {
	Host           string
	Port           int
	Origin         string
	Origins, Hosts map[string]bool
	Roots          []string
	TLS            *tls.Config
	Job            map[string]any
}
type repeatedFlag []string

func (s *repeatedFlag) String() string     { return strings.Join(*s, ",") }
func (s *repeatedFlag) Set(v string) error { *s = append(*s, v); return nil }

func parseOptions(args []string, root string) (*Options, *Config, error) {
	o := &Options{}
	if len(args) > 0 && args[0] == "--restore" {
		if len(args) != 2 {
			return nil, nil, errors.New("usage: RESTORE.cmd <export.json>")
		}
		o.Restore = args[1]
		return o, nil, nil
	}
	seen := map[string]bool{}
	presenceOnly := map[string]bool{"--allow-insecure-network": true, "--show-token": true, "--server-only": true, "--check": true, "--trace": true, "--verify": true, "--self-test": true, "--doctor": true}
	for _, a := range args {
		if strings.HasPrefix(a, "-") && !strings.HasPrefix(a, "--") {
			return nil, nil, errors.New("use full --long-option spelling; single-dash aliases are not supported")
		}
		if strings.HasPrefix(a, "--") {
			k := strings.SplitN(a, "=", 2)[0]
			if presenceOnly[k] && strings.Contains(a, "=") {
				return nil, nil, fmt.Errorf("%s is a presence-only flag and does not accept an assigned value", k)
			}
			if seen[k] && k != "--vcmd-root" && k != "--allow-origin" {
				return nil, nil, fmt.Errorf("duplicate command-line option: %s", k)
			}
			seen[k] = true
		}
	}
	f := flag.NewFlagSet("JPBroker", flag.ContinueOnError)
	f.SetOutput(io.Discard)
	f.StringVar(&o.ConfigPath, "config", "", "")
	f.StringVar(&o.Host, "host", "", "")
	f.IntVar(&o.Port, "port", 0, "")
	f.StringVar(&o.PublicOrigin, "public-origin", "", "")
	f.Var(&o.Origins, "allow-origin", "")
	f.Var(&o.Roots, "vcmd-root", "")
	f.StringVar(&o.TLSCert, "tls-cert", "", "")
	f.StringVar(&o.TLSKey, "tls-key", "", "")
	f.BoolVar(&o.Insecure, "allow-insecure-network", false, "")
	f.BoolVar(&o.ShowToken, "show-token", false, "")
	f.BoolVar(&o.ServerOnly, "server-only", false, "")
	f.StringVar(&o.VCMD, "vcmd", "", "")
	f.BoolVar(&o.Check, "check", false, "")
	f.BoolVar(&o.Trace, "trace", false, "")
	f.StringVar(&o.Report, "report", "", "")
	f.BoolVar(&o.Verify, "verify", false, "")
	f.BoolVar(&o.SelfTest, "self-test", false, "")
	f.BoolVar(&o.Doctor, "doctor", false, "")
	if err := f.Parse(args); err != nil {
		return nil, nil, err
	}
	if len(f.Args()) > 0 {
		return nil, nil, errors.New("unexpected positional argument")
	}
	if o.Verify || o.SelfTest || o.Doctor {
		if len(args) != 1 {
			return nil, nil, errors.New("verification, self-test and doctor must be used alone")
		}
		return o, nil, nil
	}
	if (o.Check || o.Trace || o.Report != "") && o.VCMD == "" {
		return nil, nil, errors.New("--check, --trace and --report require --vcmd")
	}
	if o.VCMD != "" && o.ServerOnly {
		return nil, nil, errors.New("standalone VCMD requires its native engine window")
	}
	if o.VCMD != "" {
		for _, option := range []string{"--config", "--host", "--port", "--public-origin", "--allow-origin", "--tls-cert", "--tls-key", "--allow-insecure-network"} {
			if seen[option] {
				return nil, nil, fmt.Errorf("%s cannot override isolated standalone VCMD networking; use --vcmd-root for explicit local source grants", option)
			}
		}
		c, e := standaloneConfig(o, root)
		return o, c, e
	}
	path := o.ConfigPath
	if path == "" {
		path = "server-config.json"
	}
	if !filepath.IsAbs(path) {
		path = filepath.Join(root, path)
	}
	path, err := plainPath(path, true)
	if err != nil {
		return nil, nil, err
	}
	var data []byte
	if !(o.VCMD != "" && o.ConfigPath == "") {
		data, err = readRegular(path, 65536, o.ConfigPath == "")
		if err != nil {
			return nil, nil, err
		}
	}
	m := map[string]any{}
	if data != nil {
		v, e := strictJSON([]byte(strings.TrimPrefix(string(data), "\ufeff")))
		if e != nil {
			return nil, nil, e
		}
		var ok bool
		m, ok = v.(map[string]any)
		if !ok {
			return nil, nil, errors.New("configuration object required")
		}
	}
	allowed := map[string]bool{"host": true, "port": true, "publicOrigin": true, "allowedOrigins": true, "tlsCert": true, "tlsKey": true, "allowInsecureNetwork": true, "vcmdRoots": true}
	for k := range m {
		if !allowed[k] {
			return nil, nil, fmt.Errorf("unknown configuration field: %s", k)
		}
	}
	for _, k := range []string{"host", "publicOrigin", "tlsCert", "tlsKey"} {
		if v, ok := m[k]; ok {
			if _, ok = v.(string); !ok {
				return nil, nil, fmt.Errorf("configuration string required: %s", k)
			}
		}
	}
	host := "0.0.0.0"
	port := 8877
	pub, cert, key := "", "", ""
	insecure := false
	if v, ok := m["host"].(string); ok {
		host = v
	}
	if v, ok := m["publicOrigin"].(string); ok {
		pub = v
	}
	if v, ok := m["tlsCert"].(string); ok {
		cert = v
	}
	if v, ok := m["tlsKey"].(string); ok {
		key = v
	}
	if v, ok := m["port"]; ok {
		n, e := parseInteger(v)
		if e != nil || n < 1024 || n > 65535 {
			return nil, nil, errors.New("config port must be 1024..65535")
		}
		port = int(n)
	}
	if v, ok := m["allowInsecureNetwork"]; ok {
		var good bool
		insecure, good = v.(bool)
		if !good {
			return nil, nil, errors.New("boolean required for allowInsecureNetwork")
		}
	}
	origins, e := stringArray(m["allowedOrigins"])
	if e != nil {
		return nil, nil, e
	}
	roots, e := stringArray(m["vcmdRoots"])
	if e != nil {
		return nil, nil, e
	}
	if seen["--host"] {
		host = o.Host
	}
	if seen["--port"] {
		port = o.Port
	}
	if seen["--public-origin"] {
		pub = o.PublicOrigin
	}
	if seen["--tls-cert"] {
		cert = o.TLSCert
	}
	if seen["--tls-key"] {
		key = o.TLSKey
	}
	if o.Insecure {
		insecure = true
	}
	if port < 1024 || port > 65535 {
		return nil, nil, errors.New("port must be 1024..65535")
	}
	if net.ParseIP(host) == nil {
		return nil, nil, errors.New("bind host must be an explicit IP address")
	}
	origins = append(origins, o.Origins...)
	roots = append(roots, o.Roots...)
	if len(origins) > 8 {
		return nil, nil, errors.New("at most 8 extra origins")
	}
	if len(roots) > 8 {
		return nil, nil, errors.New("at most 8 source roots")
	}
	if (cert == "") != (key == "") {
		return nil, nil, errors.New("set both TLS certificate and key")
	}
	if cert == "" && !insecure {
		return nil, nil, errors.New("TLS is required; configure certificate/key or explicitly select --allow-insecure-network for an isolated test network")
	}
	c := &Config{Host: host, Port: port, Origins: map[string]bool{}, Hosts: map[string]bool{}}
	scheme := "http"
	if cert != "" {
		if !filepath.IsAbs(cert) {
			cert = filepath.Join(root, cert)
		}
		if !filepath.IsAbs(key) {
			key = filepath.Join(root, key)
		}
		cb, e := readRegular(cert, 1048576, false)
		if e != nil {
			return nil, nil, e
		}
		kb, e := readRegular(key, 1048576, false)
		if e != nil {
			return nil, nil, e
		}
		pair, e := tls.X509KeyPair(cb, kb)
		if e != nil {
			return nil, nil, e
		}
		c.TLS = &tls.Config{Certificates: []tls.Certificate{pair}, MinVersion: tls.VersionTLS12}
		scheme = "https"
	}
	if pub == "" {
		advertised := host
		if host == "0.0.0.0" || host == "::" {
			advertised, e = localIP()
			if e != nil {
				return nil, nil, e
			}
		}
		pub = scheme + "://" + net.JoinHostPort(advertised, strconv.Itoa(port))
	}
	pub, e = canonicalOrigin(pub)
	if e != nil {
		return nil, nil, e
	}
	c.Origin = pub
	for _, origin := range append([]string{pub}, origins...) {
		origin, e = canonicalOrigin(origin)
		if e != nil {
			return nil, nil, e
		}
		if !strings.HasPrefix(origin, scheme+"://") {
			return nil, nil, errors.New("origin scheme must match TLS mode")
		}
		c.Origins[origin] = true
		u, _ := url.Parse(origin)
		c.Hosts[u.Host] = true
	}
	for _, r := range roots {
		if !filepath.IsAbs(r) {
			r = filepath.Join(root, r)
		}
		p, e := plainPath(r, false)
		if e != nil {
			return nil, nil, e
		}
		c.Roots = append(c.Roots, p)
	}
	return o, c, nil
}

func standaloneConfig(o *Options, root string) (*Config, error) {
	p, e := plainPath(o.VCMD, false)
	if e != nil {
		return nil, e
	}
	if !strings.EqualFold(filepath.Ext(p), ".vcmd") {
		return nil, errors.New("standalone source must be .vcmd")
	}
	if len(o.Roots) > 8 {
		return nil, errors.New("at most8 source roots")
	}
	c := &Config{Host: "127.0.0.1", Port: 0, Origins: map[string]bool{}, Hosts: map[string]bool{}}
	for _, r := range o.Roots {
		if !filepath.IsAbs(r) {
			r = filepath.Join(root, r)
		}
		r, e = plainPath(r, false)
		if e != nil {
			return nil, e
		}
		c.Roots = append(c.Roots, r)
	}
	if len(c.Roots) == 0 {
		c.Roots = []string{filepath.Dir(p)}
	}
	mode := "run"
	if o.Check {
		mode = "check"
	}
	c.Job = map[string]any{"path": p, "mode": mode, "trace": o.Trace}
	return c, nil
}

func canonicalOrigin(value string) (string, error) {
	if len(value) > 512 || regexp.MustCompile(`[\x00-\x20\x7f\\]`).MatchString(value) {
		return "", errors.New("invalid origin")
	}
	u, e := url.Parse(value)
	if e != nil {
		return "", e
	}
	h := strings.ToLower(u.Hostname())
	if (u.Scheme != "http" && u.Scheme != "https") || h == "" || u.User != nil || u.Path != "" || u.RawQuery != "" || u.Fragment != "" || u.Opaque != "" || strings.Contains(u.Host, "*") || h == "0.0.0.0" || h == "::" || !regexp.MustCompile(`^[a-z0-9.:-]+$`).MatchString(h) {
		return "", errors.New("use an exact canonical http(s) origin without path, wildcard or credentials")
	}
	p := u.Port()
	if p != "" {
		n, e := strconv.Atoi(p)
		if e != nil || n < 1 || n > 65535 {
			return "", errors.New("invalid origin port")
		}
		if (u.Scheme == "http" && n == 80) || (u.Scheme == "https" && n == 443) {
			p = ""
		}
	}
	if strings.Contains(h, ":") {
		h = "[" + h + "]"
	}
	v := u.Scheme + "://" + h
	if p != "" {
		v += ":" + p
	}
	if v != value {
		return "", errors.New("origin must use lowercase canonical spelling and omit default port")
	}
	return v, nil
}
func localIP() (string, error) {
	as, e := net.InterfaceAddrs()
	if e != nil {
		return "", e
	}
	for _, a := range as {
		ip, _, e := net.ParseCIDR(a.String())
		if e == nil && ip.To4() != nil && !ip.IsLoopback() && ip.IsGlobalUnicast() {
			return ip.String(), nil
		}
	}
	return "", errors.New("no non-loopback address detected; supply --public-origin")
}
func stringArray(v any) ([]string, error) {
	if v == nil {
		return nil, nil
	}
	a, ok := v.([]any)
	if !ok {
		return nil, errors.New("configuration string array required")
	}
	s := make([]string, 0, len(a))
	for _, v := range a {
		x, ok := v.(string)
		if !ok {
			return nil, errors.New("configuration string array required")
		}
		s = append(s, x)
	}
	return s, nil
}
func parseInteger(v any) (int64, error) {
	switch n := v.(type) {
	case json.Number:
		return n.Int64()
	case int:
		return int64(n), nil
	case int64:
		return n, nil
	case float64:
		if n == float64(int64(n)) {
			return int64(n), nil
		}
	}
	return 0, errors.New("integer required")
}

func rootPath() (string, error) {
	p, e := os.Executable()
	if e != nil {
		return "", e
	}
	p, e = filepath.EvalSymlinks(p)
	if e != nil {
		return "", e
	}
	d := filepath.Dir(p)
	if strings.EqualFold(filepath.Base(d), "bin") {
		d = filepath.Dir(d)
	}
	return plainPath(d, false)
}
