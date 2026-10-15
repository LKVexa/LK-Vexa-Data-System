package main

import (
	"os"
	"strings"
	"testing"
)

func jaTestProfile(t *testing.T) *JA21Program {
	t.Helper()
	program, err := LoadJA21Profile("ja21-profile.ja")
	if err != nil {
		t.Fatal(err)
	}
	return program
}

func jaTestSource(body string) string {
	return "ja source 0.2\nmodule test.policy\nuse JP250\npolicy no_execute_unknown_code\npolicy no_hidden_network\npolicy explicit_host_abi\n" + body + "\n"
}

func jaTestRoute(allowed, operation, status string) string {
	return "route JP250 <- {\"allowed\": " + allowed + ", \"operation\": " + operation + ", \"status\": " + status + ", \"reason\": \"test\"}\n"
}

func TestJA21AdmissionMatrix(t *testing.T) {
	program := jaTestProfile(t)
	base := JA21Facts{Operation: "http.health", OriginValid: true}
	cases := []struct {
		name  string
		facts JA21Facts
		allow bool
	}{
		{"public probe", base, true},
		{"public static remote HTTPS", JA21Facts{Operation: "http.static", Transport: "https", OriginValid: true}, true},
		{"unknown origin", JA21Facts{Operation: "http.health"}, false},
		{"unknown operation", JA21Facts{Operation: "store.delete", OriginValid: true, TokenValid: true, Owner: true}, false},
		{"unauthenticated engine", JA21Facts{Operation: "ws.engine", OriginValid: true}, false},
		{"authenticated remote engine", JA21Facts{Operation: "ws.engine", Transport: "wss", OriginValid: true, TokenValid: true}, true},
		{"client engine not ready", JA21Facts{Operation: "ws.client", OriginValid: true, TokenValid: true}, false},
		{"client engine ready", JA21Facts{Operation: "ws.client", OriginValid: true, TokenValid: true, EngineReady: true}, true},
		{"read requires owner", JA21Facts{Operation: "store.read", OriginValid: true, TokenValid: true}, false},
		{"write owner", JA21Facts{Operation: "store.write", OriginValid: true, TokenValid: true, Owner: true}, true},
		{"owner without token", JA21Facts{Operation: "store.write", OriginValid: true, Owner: true}, false},
		{"max body accepted", JA21Facts{Operation: "store.write", OriginValid: true, TokenValid: true, Owner: true, BodyBytes: 16777216}, true},
		{"oversize denied", JA21Facts{Operation: "store.write", OriginValid: true, TokenValid: true, Owner: true, BodyBytes: 16777217}, false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			decision, err := program.Evaluate(tc.facts)
			if err != nil {
				t.Fatal(err)
			}
			if decision.Allowed != tc.allow || decision.Operation != tc.facts.Operation {
				t.Fatalf("unexpected admission: %+v", decision)
			}
			if decision.SourceHash != program.SourceHash || len(decision.Trace) == 0 {
				t.Fatal("missing observed source/statement evidence")
			}
		})
	}
}

func TestJA21SourceControlsAuthorization(t *testing.T) {
	raw, err := os.ReadFile("ja21-profile.ja")
	if err != nil {
		t.Fatal(err)
	}
	// A valid policy source edit changes dispatch without changing the host.
	changed := strings.ReplaceAll(string(raw), `request.operation == "http.health"`, `request.operation == "test.extra"`)
	program, err := ParseJA21Profile(changed)
	if err != nil {
		t.Fatal(err)
	}
	for _, item := range []struct {
		op    string
		allow bool
	}{{"http.health", false}, {"test.extra", true}} {
		decision, err := program.Evaluate(JA21Facts{Operation: item.op, OriginValid: true})
		if err != nil || decision.Allowed != item.allow {
			t.Fatalf("source edit did not control operation %q: %+v, %v", item.op, decision, err)
		}
	}
}

func TestJA21RejectsUnsupportedAndIllTypedSource(t *testing.T) {
	validRoute := jaTestRoute("true", "request.operation", "200")
	cases := []string{
		strings.Replace(jaTestSource(validRoute), "source 0.2", "source 0.3", 1),
		strings.Replace(jaTestSource(validRoute), "use JP250", "use Shell", 1),
		strings.Replace(jaTestSource(validRoute), "policy no_hidden_network\n", "", 1),
		jaTestSource("function run() -> Text { return \"x\" }"),
		jaTestSource("let a = host.exec(\"cmd\")\n" + validRoute),
		jaTestSource("route Shell <- {}"),
		jaTestSource("let a: Flag = 1\n" + validRoute),
		jaTestSource("let a = true + false\n" + validRoute),
		jaTestSource("let a = request.missing\n" + validRoute),
		jaTestSource("when 1 {\n" + validRoute + "}\n"),
		jaTestSource("let a = 1\nlet a = 2\n" + validRoute),
		jaTestSource("let request = 1\n" + validRoute),
		jaTestSource("let a = 9223372036854775808\n" + validRoute),
		jaTestSource("route JP250 <- {allowed: true, operation: request.operation, status: 200, status: 201, reason: \"x\"}"),
		jaTestSource("route JP250 <- {allowed: \"true\", operation: request.operation, status: 200, reason: \"x\"}"),
		jaTestSource("route JP250 <- {allowed: true, operation: request.operation, status: 200, reason: \"x\", ignored: true}"),
		jaTestSource("let a = true;\n" + validRoute),
		jaTestSource("let a = \"unterminated\n" + validRoute),
		jaTestSource("let a = \"\\xff\"\n" + validRoute),
		jaTestSource("when true {\n" + validRoute),
	}
	for i, source := range cases {
		if _, err := ParseJA21Profile(source); err == nil {
			t.Errorf("invalid case %d accepted: %s", i, source)
		}
	}
}

func TestJA21RuntimeFailuresDenyAndDiscardRoute(t *testing.T) {
	cases := []string{
		"assert false\n" + jaTestRoute("true", "request.operation", "200"),
		jaTestRoute("true", "request.operation", "200") + "assert false\n",
		jaTestRoute("true", "request.operation", "200") + jaTestRoute("true", "request.operation", "200"),
		"when false {\n" + jaTestRoute("true", "request.operation", "200") + "}\n",
		jaTestRoute("true", "request.operation", "403"),
		jaTestRoute("false", "request.operation", "200"),
		jaTestRoute("true", "\"rewritten-operation\"", "200"),
		"let a = 9223372036854775807 + 1\n" + jaTestRoute("true", "request.operation", "200"),
		"let a = -9223372036854775807 - 2\n" + jaTestRoute("true", "request.operation", "200"),
		"let a = 3037000500 * 3037000500\n" + jaTestRoute("true", "request.operation", "200"),
		"let a = 10 / 0\n" + jaTestRoute("true", "request.operation", "200"),
		"let a = 10 % 0\n" + jaTestRoute("true", "request.operation", "200"),
		"let a = -9223372036854775807 - 1\nlet b = -a\n" + jaTestRoute("true", "request.operation", "200"),
		"let a = -9223372036854775807 - 1\nlet b = a / -1\n" + jaTestRoute("true", "request.operation", "200"),
		"let a: Count = -1\n" + jaTestRoute("true", "request.operation", "200"),
	}
	for i, body := range cases {
		program, err := ParseJA21Profile(jaTestSource(body))
		if err != nil {
			t.Fatalf("case %d parse: %v", i, err)
		}
		decision, err := program.Evaluate(JA21Facts{Operation: "test"})
		if err == nil || decision.Allowed {
			t.Errorf("runtime failure %d did not deny: %+v, %v", i, decision, err)
		}
	}
	program := jaTestProfile(t)
	if decision, err := program.Evaluate(JA21Facts{Operation: "http.health", OriginValid: true, BodyBytes: -1}); err == nil || decision.Allowed {
		t.Fatal("negative host byte length accepted")
	}
}

func TestJA21OperatorsIntrinsicsAndStableTrace(t *testing.T) {
	body := `let numbers = [1, 2, 3]
let record: Record = {one: 1, text: "value"}
let sum = -3 + 5 * 2
assert sum == 7
assert count(numbers) == 3
assert count(record) == 2
assert size("世界") == 2
assert contains("JP250", "250")
let digest: Hash = hash("abc")
assert digest == hash("abc")
assert (false && (1 / 0 == 0)) == false
assert true || (1 / 0 == 0)
assert !false
assert 7 % 4 == 3
assert 7 / 2 == 3
assert "JA" + "21" == "JA21"
` + jaTestRoute("true", "request.operation", "200")
	program, err := ParseJA21Profile(jaTestSource(body))
	if err != nil {
		t.Fatal(err)
	}
	first, err := program.Evaluate(JA21Facts{Operation: "test"})
	if err != nil || !first.Allowed {
		t.Fatalf("valid evaluator program failed: %+v, %v", first, err)
	}
	second, err := program.Evaluate(JA21Facts{Operation: "test"})
	if err != nil || len(first.Trace) != len(second.Trace) {
		t.Fatal("repeat execution failed")
	}
	for i := range first.Trace {
		if first.Trace[i] != second.Trace[i] || len(first.Trace[i].Statement) != 64 {
			t.Fatal("unstable source-derived statement evidence")
		}
	}
}

func TestJA21BlockScopeDoesNotLeak(t *testing.T) {
	body := "when true {\nlet scoped: Flag = true\n}\nwhen scoped {\n" + jaTestRoute("true", "request.operation", "200") + "}\n"
	if _, err := ParseJA21Profile(jaTestSource(body)); err == nil {
		t.Fatal("block-local binding leaked")
	}
}

func TestJA21ParallelEvaluation(t *testing.T) {
	program := jaTestProfile(t)
	for i := 0; i < 16; i++ {
		t.Run("request", func(t *testing.T) {
			t.Parallel()
			decision, err := program.Evaluate(JA21Facts{Operation: "http.health", OriginValid: true})
			if err != nil || !decision.Allowed {
				t.Errorf("parallel immutable evaluation failed: %v", err)
			}
		})
	}
}
