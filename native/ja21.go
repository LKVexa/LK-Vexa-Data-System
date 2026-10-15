package main

// This is a deliberately limited, hosted JA source 0.2 evaluator. Its syntax
// comes from the Programming corpus's canonical EBNF. It is not a full JA21 VM.
// The JP250 route ABI returns a decision; it cannot perform host I/O.

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"math"
	"os"
	"strconv"
	"strings"
	"unicode/utf8"
)

const ja21MaxSource = 1 << 20

// JA21Facts is the complete, immutable host-to-JA policy input ABI. The broker
// remains responsible for deriving these facts from actual transport state.
type JA21Facts struct {
	Transport, Method, Path, Operation                    string
	TokenValid, Loopback, OriginValid, EngineReady, Owner bool
	BodyBytes                                             int64
}

// JA21Decision is the typed JA-to-host route ABI. A successful evaluation can
// still deny a request. A parse/evaluation error must be treated as denial.
type JA21Decision struct {
	Allowed    bool        `json:"allowed"`
	Operation  string      `json:"operation"`
	Status     int         `json:"status"`
	Reason     string      `json:"reason"`
	SourceHash string      `json:"source_hash"`
	Profile    string      `json:"profile"`
	Trace      []JA21Trace `json:"trace"`
}

type JA21Trace struct {
	Statement string `json:"statement"`
	Line      int    `json:"line"`
	Column    int    `json:"column"`
	Kind      string `json:"kind"`
	Outcome   string `json:"outcome"`
}

type jaType uint8

const (
	jaInvalid jaType = iota
	jaText
	jaInteger
	jaFlag
	jaRecord
	jaArray
	jaHash
)

func (t jaType) String() string {
	switch t {
	case jaText:
		return "Text"
	case jaInteger:
		return "Count/integer"
	case jaFlag:
		return "Flag"
	case jaRecord:
		return "Record"
	case jaArray:
		return "Array"
	case jaHash:
		return "Hash"
	}
	return "invalid"
}

type jaValue struct {
	typeOf jaType
	text   string
	number int64
	flag   bool
	fields map[string]jaValue
	items  []jaValue
}

type jaToken struct {
	text      string
	kind      string
	line, col int
}

func jaError(t jaToken, format string, args ...interface{}) error {
	return fmt.Errorf("JA21 at %d:%d: %s", t.line, t.col, fmt.Sprintf(format, args...))
}

// JA21Program is immutable after validation and safe for concurrent evaluation.
type JA21Program struct {
	Module     string
	SourceHash string
	statements []*jaStatement
}

type jaStatement struct {
	kind, name, annotation string
	position               jaToken
	value                  *jaExpr
	body                   []*jaStatement
	id                     string
}

type jaExpr struct {
	kind, op, name string
	position       jaToken
	literal        jaValue
	left, right    *jaExpr
	args           []*jaExpr
	keys           []string
	values         []*jaExpr
	typeOf         jaType
}

// LoadJA21Profile validates syntax and types before exposing a program.
func LoadJA21Profile(path string) (*JA21Program, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("JA21 profile: %w", err)
	}
	defer f.Close()
	raw, err := io.ReadAll(io.LimitReader(f, ja21MaxSource+1))
	if err != nil {
		return nil, fmt.Errorf("JA21 profile read: %w", err)
	}
	if len(raw) > ja21MaxSource {
		return nil, fmt.Errorf("JA21 profile exceeds %d bytes", ja21MaxSource)
	}
	return ParseJA21Profile(string(raw))
}

// ParseJA21Profile is the in-memory equivalent used by conformance tests.
func ParseJA21Profile(source string) (*JA21Program, error) {
	if len(source) > ja21MaxSource || !utf8.ValidString(source) {
		return nil, fmt.Errorf("JA21 source must be bounded valid UTF-8")
	}
	tokens, err := jaLex(source)
	if err != nil {
		return nil, err
	}
	p := jaParser{tokens: tokens}
	program, err := p.parse()
	if err != nil {
		return nil, err
	}
	digest := sha256.Sum256([]byte(source))
	program.SourceHash = hex.EncodeToString(digest[:])
	env := map[string]jaType{}
	if err := jaCheckStatements(program.statements, env); err != nil {
		return nil, err
	}
	jaAssignIDs(program.statements, program.SourceHash)
	return program, nil
}

func jaLex(source string) ([]jaToken, error) {
	tokens := make([]jaToken, 0, 256)
	line, col := 1, 1
	for i := 0; i < len(source); {
		if len(tokens) > 16384 {
			return nil, fmt.Errorf("JA21 token limit exceeded")
		}
		c := source[i]
		if c == ' ' || c == '\t' || c == '\r' {
			i++
			col++
			continue
		}
		if c == '\n' {
			tokens = append(tokens, jaToken{"\n", "newline", line, col})
			i++
			line++
			col = 1
			continue
		}
		if c == '/' && i+1 < len(source) && source[i+1] == '/' {
			for i < len(source) && source[i] != '\n' {
				i++
				col++
			}
			continue
		}
		start, startCol := i, col
		if jaLetter(c) {
			for i < len(source) && (jaLetter(source[i]) || jaDigit(source[i]) || source[i] == '_') {
				i++
				col++
			}
			tokens = append(tokens, jaToken{source[start:i], "identifier", line, startCol})
			continue
		}
		if jaDigit(c) {
			for i < len(source) && jaDigit(source[i]) {
				i++
				col++
			}
			tokens = append(tokens, jaToken{source[start:i], "integer", line, startCol})
			continue
		}
		if c == '"' {
			i++
			col++
			closed := false
			for i < len(source) {
				if source[i] == '\n' || source[i] == '\r' {
					return nil, jaError(jaToken{line: line, col: startCol}, "newline in string")
				}
				if source[i] == '\\' {
					i += 2
					col += 2
					continue
				}
				if source[i] == '"' {
					i++
					col++
					closed = true
					break
				}
				i++
				col++
			}
			if !closed || i > len(source) {
				return nil, jaError(jaToken{line: line, col: startCol}, "unterminated string")
			}
			decoded, err := strconv.Unquote(source[start:i])
			if err != nil || !utf8.ValidString(decoded) {
				return nil, jaError(jaToken{line: line, col: startCol}, "invalid escaped string")
			}
			tokens = append(tokens, jaToken{decoded, "string", line, startCol})
			continue
		}
		if i+1 < len(source) {
			pair := source[i : i+2]
			if pair == "<-" || pair == "==" || pair == "!=" || pair == "<=" || pair == ">=" || pair == "&&" || pair == "||" {
				tokens = append(tokens, jaToken{pair, "symbol", line, col})
				i += 2
				col += 2
				continue
			}
		}
		if strings.ContainsRune("{}[]():,.=+-*/%!<>", rune(c)) {
			tokens = append(tokens, jaToken{string(c), "symbol", line, col})
			i++
			col++
			continue
		}
		return nil, jaError(jaToken{line: line, col: col}, "unsupported source character %q", c)
	}
	tokens = append(tokens, jaToken{"", "eof", line, col})
	return tokens, nil
}

func jaLetter(c byte) bool { return c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' }
func jaDigit(c byte) bool  { return c >= '0' && c <= '9' }

type jaParser struct {
	tokens []jaToken
	i      int
	depth  int
}

func (p *jaParser) peek() jaToken { return p.tokens[p.i] }
func (p *jaParser) next() jaToken {
	t := p.peek()
	if t.kind != "eof" {
		p.i++
	}
	return t
}
func (p *jaParser) accept(s string) bool {
	if p.peek().text == s && p.peek().kind != "string" {
		p.next()
		return true
	}
	return false
}
func (p *jaParser) expect(s string) error {
	if !p.accept(s) {
		return jaError(p.peek(), "expected %q, got %q", s, p.peek().text)
	}
	return nil
}
func (p *jaParser) identifier() (string, error) {
	t := p.next()
	if t.kind != "identifier" {
		return "", jaError(t, "expected identifier")
	}
	return t.text, nil
}
func (p *jaParser) qualified() (string, error) {
	name, err := p.identifier()
	if err != nil {
		return "", err
	}
	for p.accept(".") {
		part, err := p.identifier()
		if err != nil {
			return "", err
		}
		name += "." + part
	}
	return name, nil
}
func (p *jaParser) newlines() {
	for p.peek().kind == "newline" {
		p.next()
	}
}
func (p *jaParser) lineEnd() error {
	if p.peek().kind == "newline" || p.peek().kind == "eof" || p.peek().text == "}" {
		p.newlines()
		return nil
	}
	return jaError(p.peek(), "unexpected token after statement: %q", p.peek().text)
}

func (p *jaParser) parse() (*JA21Program, error) {
	p.newlines()
	for _, word := range []string{"ja", "source", "0", ".", "2"} {
		if err := p.expect(word); err != nil {
			return nil, err
		}
	}
	if err := p.lineEnd(); err != nil {
		return nil, err
	}
	if err := p.expect("module"); err != nil {
		return nil, err
	}
	module, err := p.qualified()
	if err != nil {
		return nil, err
	}
	if err := p.lineEnd(); err != nil {
		return nil, err
	}
	imported := false
	policies := map[string]bool{}
	for p.peek().text == "use" || p.peek().text == "policy" {
		keyword := p.next()
		name, err := p.identifier()
		if err != nil {
			return nil, err
		}
		if keyword.text == "use" {
			if name != "JP250" || imported {
				return nil, jaError(keyword, "only one reviewed JP250 adapter import is supported")
			}
			imported = true
		} else {
			if name != "no_execute_unknown_code" && name != "no_hidden_network" && name != "explicit_host_abi" {
				return nil, jaError(keyword, "unsupported policy %q", name)
			}
			if policies[name] {
				return nil, jaError(keyword, "duplicate policy %q", name)
			}
			policies[name] = true
		}
		if err := p.lineEnd(); err != nil {
			return nil, err
		}
	}
	if !imported || !policies["no_execute_unknown_code"] || !policies["no_hidden_network"] || !policies["explicit_host_abi"] {
		return nil, jaError(p.peek(), "JP250 import and all three explicit safety policies are required")
	}
	var statements []*jaStatement
	for p.peek().kind != "eof" {
		s, err := p.statement()
		if err != nil {
			return nil, err
		}
		statements = append(statements, s)
		if len(statements) > 2048 {
			return nil, jaError(s.position, "statement limit exceeded")
		}
	}
	if len(statements) == 0 {
		return nil, jaError(p.peek(), "empty executable profile")
	}
	return &JA21Program{Module: module, statements: statements}, nil
}

func (p *jaParser) statement() (*jaStatement, error) {
	t := p.next()
	s := &jaStatement{kind: t.text, position: t}
	var err error
	switch t.text {
	case "let":
		s.name, err = p.identifier()
		if err != nil {
			return nil, err
		}
		if s.name == "request" || s.name == "true" || s.name == "false" {
			return nil, jaError(t, "reserved binding %q", s.name)
		}
		if p.accept(":") {
			s.annotation, err = p.identifier()
			if err != nil {
				return nil, err
			}
		}
		if err := p.expect("="); err != nil {
			return nil, err
		}
		s.value, err = p.expression(1)
	case "assert":
		s.value, err = p.expression(1)
	case "route":
		s.name, err = p.identifier()
		if err != nil {
			return nil, err
		}
		if s.name != "JP250" {
			return nil, jaError(t, "unreviewed host route %q", s.name)
		}
		if err := p.expect("<-"); err != nil {
			return nil, err
		}
		s.value, err = p.expression(1)
	case "when":
		s.value, err = p.expression(1)
		if err != nil {
			return nil, err
		}
		if err := p.expect("{"); err != nil {
			return nil, err
		}
		p.depth++
		if p.depth > 32 {
			return nil, jaError(t, "block nesting limit exceeded")
		}
		p.newlines()
		for p.peek().text != "}" {
			if p.peek().kind == "eof" {
				return nil, jaError(t, "unterminated when block")
			}
			child, err := p.statement()
			if err != nil {
				return nil, err
			}
			s.body = append(s.body, child)
		}
		p.next()
		p.depth--
	default:
		return nil, jaError(t, "unsupported statement %q", t.text)
	}
	if err != nil {
		return nil, err
	}
	if err := p.lineEnd(); err != nil {
		return nil, err
	}
	return s, nil
}

func jaPrecedence(op string) int {
	switch op {
	case "||":
		return 1
	case "&&":
		return 2
	case "==", "!=":
		return 3
	case "<", "<=", ">", ">=":
		return 4
	case "+", "-":
		return 5
	case "*", "/", "%":
		return 6
	}
	return 0
}

func (p *jaParser) expression(min int) (*jaExpr, error) {
	p.depth++
	defer func() { p.depth-- }()
	if p.depth > 64 {
		return nil, jaError(p.peek(), "expression nesting limit exceeded")
	}
	left, err := p.primary()
	if err != nil {
		return nil, err
	}
	for p.peek().kind == "symbol" && jaPrecedence(p.peek().text) >= min {
		t := p.next()
		right, err := p.expression(jaPrecedence(t.text) + 1)
		if err != nil {
			return nil, err
		}
		left = &jaExpr{kind: "binary", op: t.text, position: t, left: left, right: right}
	}
	return left, nil
}

func (p *jaParser) primary() (*jaExpr, error) {
	t := p.next()
	e := &jaExpr{position: t}
	switch {
	case t.kind == "string":
		e.kind, e.literal = "literal", jaValue{typeOf: jaText, text: t.text}
	case t.kind == "integer":
		n, err := strconv.ParseInt(t.text, 10, 64)
		if err != nil {
			return nil, jaError(t, "integer out of signed 64-bit range")
		}
		e.kind, e.literal = "literal", jaValue{typeOf: jaInteger, number: n}
	case t.kind == "identifier":
		if t.text == "true" || t.text == "false" {
			e.kind, e.literal = "literal", jaValue{typeOf: jaFlag, flag: t.text == "true"}
			break
		}
		e.kind, e.name = "name", t.text
		for p.accept(".") {
			part, err := p.identifier()
			if err != nil {
				return nil, err
			}
			e.name += "." + part
		}
		if p.accept("(") {
			e.kind = "call"
			p.newlines()
			if !p.accept(")") {
				for {
					arg, err := p.expression(1)
					if err != nil {
						return nil, err
					}
					e.args = append(e.args, arg)
					p.newlines()
					if p.accept(")") {
						break
					}
					if err := p.expect(","); err != nil {
						return nil, err
					}
					p.newlines()
				}
			}
		}
	case t.text == "!" || t.text == "-" || t.text == "+":
		e.kind, e.op = "unary", t.text
		child, err := p.expression(7)
		if err != nil {
			return nil, err
		}
		e.left = child
	case t.text == "(":
		inside, err := p.expression(1)
		if err != nil {
			return nil, err
		}
		if err := p.expect(")"); err != nil {
			return nil, err
		}
		return inside, nil
	case t.text == "{":
		e.kind = "record"
		seen := map[string]bool{}
		p.newlines()
		if !p.accept("}") {
			for {
				key := p.next()
				if key.kind != "string" && key.kind != "identifier" {
					return nil, jaError(key, "expected record field")
				}
				if seen[key.text] {
					return nil, jaError(key, "duplicate record field %q", key.text)
				}
				seen[key.text] = true
				if err := p.expect(":"); err != nil {
					return nil, err
				}
				value, err := p.expression(1)
				if err != nil {
					return nil, err
				}
				e.keys, e.values = append(e.keys, key.text), append(e.values, value)
				p.newlines()
				if p.accept("}") {
					break
				}
				if err := p.expect(","); err != nil {
					return nil, err
				}
				p.newlines()
			}
		}
	case t.text == "[":
		e.kind = "array"
		p.newlines()
		if !p.accept("]") {
			for {
				value, err := p.expression(1)
				if err != nil {
					return nil, err
				}
				e.values = append(e.values, value)
				p.newlines()
				if p.accept("]") {
					break
				}
				if err := p.expect(","); err != nil {
					return nil, err
				}
				p.newlines()
			}
		}
	default:
		return nil, jaError(t, "expected expression, got %q", t.text)
	}
	return e, nil
}

var jaFactTypes = map[string]jaType{
	"request.transport": jaText, "request.method": jaText, "request.path": jaText,
	"request.operation": jaText, "request.token_valid": jaFlag,
	"request.loopback": jaFlag, "request.origin_valid": jaFlag,
	"request.engine_ready": jaFlag, "request.owner": jaFlag,
	"request.body_bytes": jaInteger,
}

func jaCopyTypes(env map[string]jaType) map[string]jaType {
	copy := make(map[string]jaType, len(env))
	for key, value := range env {
		copy[key] = value
	}
	return copy
}

func jaCheckStatements(statements []*jaStatement, env map[string]jaType) error {
	for _, s := range statements {
		t, err := jaCheckExpr(s.value, env)
		if err != nil {
			return err
		}
		switch s.kind {
		case "let":
			if _, exists := env[s.name]; exists {
				return jaError(s.position, "binding %q already exists", s.name)
			}
			if s.annotation != "" {
				declared := map[string]jaType{"Text": jaText, "Count": jaInteger, "Flag": jaFlag, "Record": jaRecord, "Hash": jaHash}[s.annotation]
				if declared == jaInvalid || declared != t {
					return jaError(s.position, "unsupported or incompatible annotation %q for %s", s.annotation, t)
				}
			}
			env[s.name] = t
		case "assert", "when":
			if t != jaFlag {
				return jaError(s.position, "%s requires Flag, got %s", s.kind, t)
			}
			if s.kind == "when" {
				if err := jaCheckStatements(s.body, jaCopyTypes(env)); err != nil {
					return err
				}
			}
		case "route":
			if t != jaRecord || s.value.kind != "record" {
				return jaError(s.position, "JP250 route requires an explicit typed record literal")
			}
			required := map[string]jaType{"allowed": jaFlag, "operation": jaText, "status": jaInteger, "reason": jaText}
			if len(s.value.keys) != len(required) {
				return jaError(s.position, "JP250 route requires exactly allowed, operation, status, reason")
			}
			for i, key := range s.value.keys {
				if want, ok := required[key]; !ok || want != s.value.values[i].typeOf {
					return jaError(s.position, "invalid JP250 route field %q", key)
				}
			}
		}
	}
	return nil
}

func jaCheckExpr(e *jaExpr, env map[string]jaType) (jaType, error) {
	fail := func(format string, args ...interface{}) (jaType, error) {
		return jaInvalid, jaError(e.position, format, args...)
	}
	var t jaType
	switch e.kind {
	case "literal":
		t = e.literal.typeOf
	case "name":
		var ok bool
		t, ok = env[e.name]
		if !ok {
			t, ok = jaFactTypes[e.name]
		}
		if !ok {
			return fail("unknown immutable name %q", e.name)
		}
	case "record", "array":
		for _, v := range e.values {
			if _, err := jaCheckExpr(v, env); err != nil {
				return jaInvalid, err
			}
		}
		t = jaRecord
		if e.kind == "array" {
			t = jaArray
		}
	case "unary":
		child, err := jaCheckExpr(e.left, env)
		if err != nil {
			return jaInvalid, err
		}
		if e.op == "!" && child == jaFlag || (e.op == "+" || e.op == "-") && child == jaInteger {
			t = child
		} else {
			return fail("operator %q does not accept %s", e.op, child)
		}
	case "binary":
		left, err := jaCheckExpr(e.left, env)
		if err != nil {
			return jaInvalid, err
		}
		right, err := jaCheckExpr(e.right, env)
		if err != nil {
			return jaInvalid, err
		}
		if left != right {
			return fail("operator %q requires matching types, got %s and %s", e.op, left, right)
		}
		switch e.op {
		case "&&", "||":
			if left != jaFlag {
				return fail("logical operator requires Flag")
			}
			t = jaFlag
		case "==", "!=":
			if left != jaFlag && left != jaText && left != jaInteger && left != jaHash {
				return fail("equality is restricted to scalar values")
			}
			t = jaFlag
		case "<", "<=", ">", ">=":
			if left != jaInteger {
				return fail("ordered comparison requires integers")
			}
			t = jaFlag
		case "+":
			if left != jaInteger && left != jaText {
				return fail("addition requires integers or Text")
			}
			t = left
		default:
			if left != jaInteger {
				return fail("arithmetic requires integers")
			}
			t = jaInteger
		}
	case "call":
		args := make([]jaType, len(e.args))
		for i, arg := range e.args {
			argType, err := jaCheckExpr(arg, env)
			if err != nil {
				return jaInvalid, err
			}
			args[i] = argType
		}
		switch e.name {
		case "contains":
			if len(args) != 2 || args[0] != jaText || args[1] != jaText {
				return fail("contains requires (Text, Text)")
			}
			t = jaFlag
		case "count":
			if len(args) != 1 || args[0] != jaArray && args[0] != jaRecord {
				return fail("count requires an Array or Record")
			}
			t = jaInteger
		case "size":
			if len(args) != 1 || args[0] != jaText {
				return fail("this profile's size requires Text")
			}
			t = jaInteger
		case "hash":
			if len(args) != 1 || args[0] != jaText {
				return fail("this profile's hash requires Text and uses SHA-256")
			}
			t = jaHash
		default:
			return fail("unreviewed intrinsic %q", e.name)
		}
	default:
		return fail("unsupported expression node")
	}
	e.typeOf = t
	return t, nil
}

func jaAssignIDs(statements []*jaStatement, sourceHash string) {
	for _, s := range statements {
		identity := fmt.Sprintf("%s:%d:%d:%s", sourceHash, s.position.line, s.position.col, s.kind)
		digest := sha256.Sum256([]byte(identity))
		s.id = hex.EncodeToString(digest[:])
		jaAssignIDs(s.body, sourceHash)
	}
}

type jaExecution struct {
	trace    []JA21Trace
	decision *JA21Decision
	facts    map[string]jaValue
}

// Evaluate executes the source-derived expressions and branches. It contains no
// JP250 operation allowlist: changing valid source changes admission behavior.
func (program *JA21Program) Evaluate(input JA21Facts) (JA21Decision, error) {
	denied := JA21Decision{Allowed: false, Status: 403, Operation: input.Operation, Reason: "policy evaluation failed"}
	if program == nil || program.SourceHash == "" {
		return denied, fmt.Errorf("JA21 profile unavailable")
	}
	denied.SourceHash, denied.Profile = program.SourceHash, "jp250.ja02.hosted.1"
	if input.BodyBytes < 0 {
		return denied, fmt.Errorf("JA21 input body_bytes cannot be negative")
	}
	x := jaExecution{facts: map[string]jaValue{
		"request.transport":    {typeOf: jaText, text: input.Transport},
		"request.method":       {typeOf: jaText, text: input.Method},
		"request.path":         {typeOf: jaText, text: input.Path},
		"request.operation":    {typeOf: jaText, text: input.Operation},
		"request.token_valid":  {typeOf: jaFlag, flag: input.TokenValid},
		"request.loopback":     {typeOf: jaFlag, flag: input.Loopback},
		"request.origin_valid": {typeOf: jaFlag, flag: input.OriginValid},
		"request.engine_ready": {typeOf: jaFlag, flag: input.EngineReady},
		"request.owner":        {typeOf: jaFlag, flag: input.Owner},
		"request.body_bytes":   {typeOf: jaInteger, number: input.BodyBytes},
	}}
	if err := x.statements(program.statements, map[string]jaValue{}); err != nil {
		denied.Trace = x.trace
		return denied, err
	}
	if x.decision == nil {
		denied.Trace = x.trace
		return denied, fmt.Errorf("JA21 execution produced no JP250 route")
	}
	decision := *x.decision
	decision.SourceHash, decision.Profile, decision.Trace = program.SourceHash, "jp250.ja02.hosted.1", x.trace
	if decision.Operation != input.Operation {
		denied.Trace = x.trace
		return denied, fmt.Errorf("JA21 route operation must match the requested operation")
	}
	return decision, nil
}

func (x *jaExecution) addTrace(s *jaStatement, outcome string) {
	x.trace = append(x.trace, JA21Trace{s.id, s.position.line, s.position.col, s.kind, outcome})
}

func (x *jaExecution) statements(statements []*jaStatement, env map[string]jaValue) error {
	for _, s := range statements {
		value, err := x.expr(s.value, env)
		if err != nil {
			x.addTrace(s, "failed")
			return err
		}
		switch s.kind {
		case "let":
			if s.annotation == "Count" && value.number < 0 {
				x.addTrace(s, "failed")
				return jaError(s.position, "Count cannot be negative")
			}
			env[s.name] = value
			x.addTrace(s, "bound")
		case "assert":
			if !value.flag {
				x.addTrace(s, "failed")
				return jaError(s.position, "assertion failed")
			}
			x.addTrace(s, "passed")
		case "when":
			if value.flag {
				x.addTrace(s, "entered")
				childEnv := make(map[string]jaValue, len(env))
				for k, v := range env {
					childEnv[k] = v
				}
				if err := x.statements(s.body, childEnv); err != nil {
					return err
				}
			} else {
				x.addTrace(s, "skipped")
			}
		case "route":
			if x.decision != nil {
				x.addTrace(s, "failed")
				return jaError(s.position, "ambiguous execution: multiple JP250 routes")
			}
			status := value.fields["status"].number
			allowed := value.fields["allowed"].flag
			if status < 100 || status > 599 || allowed && (status < 200 || status >= 300) || !allowed && status < 400 {
				x.addTrace(s, "failed")
				return jaError(s.position, "route status is inconsistent with admission")
			}
			x.decision = &JA21Decision{Allowed: allowed, Operation: value.fields["operation"].text, Status: int(status), Reason: value.fields["reason"].text}
			x.addTrace(s, "routed")
		}
	}
	return nil
}

func (x *jaExecution) expr(e *jaExpr, env map[string]jaValue) (jaValue, error) {
	fail := func(format string, args ...interface{}) (jaValue, error) {
		return jaValue{}, jaError(e.position, format, args...)
	}
	switch e.kind {
	case "literal":
		return e.literal, nil
	case "name":
		if value, ok := env[e.name]; ok {
			return value, nil
		}
		if value, ok := x.facts[e.name]; ok {
			return value, nil
		}
		return fail("unbound name %q", e.name)
	case "record", "array":
		value := jaValue{typeOf: e.typeOf, fields: map[string]jaValue{}}
		for i, child := range e.values {
			item, err := x.expr(child, env)
			if err != nil {
				return jaValue{}, err
			}
			if e.kind == "record" {
				value.fields[e.keys[i]] = item
			} else {
				value.items = append(value.items, item)
			}
		}
		return value, nil
	case "unary":
		value, err := x.expr(e.left, env)
		if err != nil {
			return jaValue{}, err
		}
		if e.op == "!" {
			value.flag = !value.flag
		} else if e.op == "-" {
			if value.number == math.MinInt64 {
				return fail("signed integer negation overflow")
			}
			value.number = -value.number
		}
		return value, nil
	case "binary":
		left, err := x.expr(e.left, env)
		if err != nil {
			return jaValue{}, err
		}
		// Short circuit is part of this profile's declared operator semantics.
		if e.op == "&&" && !left.flag || e.op == "||" && left.flag {
			return left, nil
		}
		right, err := x.expr(e.right, env)
		if err != nil {
			return jaValue{}, err
		}
		result := jaValue{typeOf: e.typeOf}
		switch e.op {
		case "&&":
			result.flag = left.flag && right.flag
		case "||":
			result.flag = left.flag || right.flag
		case "==", "!=":
			equal := left.text == right.text && left.number == right.number && left.flag == right.flag
			result.flag = equal
			if e.op == "!=" {
				result.flag = !equal
			}
		case "<":
			result.flag = left.number < right.number
		case "<=":
			result.flag = left.number <= right.number
		case ">":
			result.flag = left.number > right.number
		case ">=":
			result.flag = left.number >= right.number
		case "+":
			if left.typeOf == jaText {
				if len(left.text)+len(right.text) > ja21MaxSource {
					return fail("Text size limit exceeded")
				}
				result.text = left.text + right.text
			} else {
				if right.number > 0 && left.number > math.MaxInt64-right.number || right.number < 0 && left.number < math.MinInt64-right.number {
					return fail("signed integer addition overflow")
				}
				result.number = left.number + right.number
			}
		case "-":
			if right.number < 0 && left.number > math.MaxInt64+right.number || right.number > 0 && left.number < math.MinInt64+right.number {
				return fail("signed integer subtraction overflow")
			}
			result.number = left.number - right.number
		case "*":
			if left.number != 0 && right.number != 0 {
				if left.number == math.MinInt64 && right.number == -1 || right.number == math.MinInt64 && left.number == -1 {
					return fail("signed integer multiplication overflow")
				}
				product := left.number * right.number
				if product/right.number != left.number {
					return fail("signed integer multiplication overflow")
				}
				result.number = product
			}
		case "/", "%":
			if right.number == 0 {
				return fail("integer division by zero")
			}
			if left.number == math.MinInt64 && right.number == -1 {
				return fail("signed integer division overflow")
			}
			if e.op == "/" {
				result.number = left.number / right.number
			} else {
				result.number = left.number % right.number
			}
		}
		return result, nil
	case "call":
		args := make([]jaValue, len(e.args))
		for i, child := range e.args {
			value, err := x.expr(child, env)
			if err != nil {
				return jaValue{}, err
			}
			args[i] = value
		}
		result := jaValue{typeOf: e.typeOf}
		switch e.name {
		case "contains":
			result.flag = strings.Contains(args[0].text, args[1].text)
		case "count":
			if args[0].typeOf == jaRecord {
				result.number = int64(len(args[0].fields))
			} else {
				result.number = int64(len(args[0].items))
			}
		case "size":
			result.number = int64(utf8.RuneCountInString(args[0].text))
		case "hash":
			digest := sha256.Sum256([]byte(args[0].text))
			result.text = hex.EncodeToString(digest[:])
		default:
			return fail("unreviewed intrinsic")
		}
		return result, nil
	}
	return fail("unsupported expression")
}
