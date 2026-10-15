package main

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

const testPhotonConfig = `{"schema":"VEC1/CONFIG/1","version":"0.2.0","default_program":"programs/fabric_probe.pal","resource_quotas":{"max_electrons":128,"max_mailbox":256,"max_outbound":256,"max_program_bytes":2097152}}`

func photonFixture(t *testing.T) (*PhotonBridge, string, string) {
	t.Helper()
	pkg := t.TempDir()
	ws := t.TempDir()
	for _, d := range []string{"config", "programs"} {
		if e := os.MkdirAll(filepath.Join(pkg, "photon", d), 0700); e != nil {
			t.Fatal(e)
		}
	}
	if e := os.WriteFile(filepath.Join(pkg, "photon", "config", "vec1.json"), []byte(testPhotonConfig), 0600); e != nil {
		t.Fatal(e)
	}
	if e := os.WriteFile(filepath.Join(pkg, "photon", "programs", "fabric_probe.pal"), []byte("probe\n"), 0600); e != nil {
		t.Fatal(e)
	}
	p, e := NewPhotonBridge(pkg, ws)
	if e != nil {
		t.Fatal(e)
	}
	return p, pkg, ws
}
func TestPythonCanonicalJSON(t *testing.T) {
	v := map[string]any{"unicode": "é\u2028🚀<&>", "float": json.Number("1.0"), "large": json.Number("1e16"), "small": json.Number("0.0001"), "tiny": json.Number("1e-7"), "integer": json.Number("123456789012345678901"), "negativezero": json.Number("-0.0")}
	got, e := canonicalJSON(v, false)
	if e != nil {
		t.Fatal(e)
	}
	want := "{\"float\":1.0,\"integer\":123456789012345678901,\"large\":1e+16,\"negativezero\":-0.0,\"small\":0.0001,\"tiny\":1e-07,\"unicode\":\"é\u2028🚀<&>\"}"
	if string(got) != want {
		t.Fatalf("canonical mismatch\ngot %s\nwant %s", got, want)
	}
	// Independently computed with Python json.dumps(sort_keys=True,
	// separators=(',', ':'), ensure_ascii=False), not the migrated encoder.
	if hashBytes(got) != "eabdf54a730ad6cf34ca0d8950ac624e63ac535c75cfc58f21bfdb9257bcafe6" {
		t.Fatal("Python canonical hash differs")
	}
	ascii, e := canonicalJSON("é🚀", true)
	if e != nil || string(ascii) != `"\u00e9\ud83d\ude80"` {
		t.Fatalf("ascii %s %v", ascii, e)
	}
	del, e := canonicalJSON("\x7f", true)
	if e != nil || string(del) != `"\u007f"` {
		t.Fatalf("ASCII DEL %s %v", del, e)
	}
}

func TestPythonCanonicalExponentAndUnicodeOrdering(t *testing.T) {
	cases := []struct{ raw, want string }{
		{`[1e-5,1e16,-0.0,1000000.0,1.2345678901234567]`, `[1e-05,1e+16,-0.0,1000000.0,1.2345678901234567]`},
		{`{"\ud83d\ude80":1,"\ue000":0}`, "{\"\ue000\":0,\"🚀\":1}"},
	}
	for _, tc := range cases {
		v, e := strictJSON([]byte(tc.raw))
		if e != nil {
			t.Fatal(e)
		}
		b, e := canonicalJSON(v, false)
		if e != nil || string(b) != tc.want {
			t.Fatalf("canonical %s: %s %v", tc.raw, b, e)
		}
	}
}
func TestPhotonIdentitySnapshotRestart(t *testing.T) {
	p, pkg, ws := photonFixture(t)
	id := p.ElectronID()
	status, e := p.Call("status")
	if e != nil {
		t.Fatal(e)
	}
	if object(status)["state_and_ledger_verified"] != true {
		t.Fatal(status)
	}
	snap, e := p.Call("snapshot")
	if e != nil {
		t.Fatal(e)
	}
	r := object(snap)
	h := str(r["snapshot_hash"])
	if _, e = p.loadSnapshot(h); e != nil {
		t.Fatal(e)
	}
	verify, e := p.Call("verify")
	if e != nil || object(verify)["ok"] != true {
		t.Fatalf("verify %v %v", verify, e)
	}
	again, e := NewPhotonBridge(pkg, ws)
	if e != nil {
		t.Fatal(e)
	}
	if again.ElectronID() != id {
		t.Fatal("identity replaced")
	}
	o, e := again.load(id, true)
	if e != nil {
		t.Fatal(e)
	}
	if object(o["runtime"])["base_snapshot"] != h {
		t.Fatal("snapshot provenance lost")
	}
	if _, e = p.Call("execute"); e == nil {
		t.Fatal("unsupported execution silently accepted")
	}
}
func TestPhotonBindingAndMissingIdentityFailClosed(t *testing.T) {
	p, pkg, ws := photonFixture(t)
	if e := os.Remove(p.binding); e != nil {
		t.Fatal(e)
	}
	v, e := p.Call("verify")
	if e != nil || object(v)["ok"] != false {
		t.Fatalf("missing binding %v %v", v, e)
	}
	if e := atomicJSON(p.binding, map[string]any{"schema": "JA21-PHOTON-CONSOLE/1", "electron_id": p.eid}); e != nil {
		t.Fatal(e)
	}
	if e := os.Remove(p.statePath(p.eid)); e != nil {
		t.Fatal(e)
	}
	v, e = p.Call("verify")
	if e != nil || object(v)["ok"] != false {
		t.Fatalf("missing identity %v %v", v, e)
	}
	if _, e = NewPhotonBridge(pkg, ws); e == nil {
		t.Fatal("replaced missing identity on startup")
	}
}
func TestPhotonTamperAndTornLedger(t *testing.T) {
	p, _, _ := photonFixture(t)
	state, e := p.load(p.eid, true)
	if e != nil {
		t.Fatal(e)
	}
	object(state["properties"])["energy"] = 99
	if e = atomicJSON(p.statePath(p.eid), state); e != nil {
		t.Fatal(e)
	}
	v, e := p.Call("verify")
	if e != nil || object(v)["ok"] != false {
		t.Fatalf("state tamper %v %v", v, e)
	}
	p2, _, _ := photonFixture(t)
	path := p2.ledgerPath(p2.eid)
	b, e := readRegular(path, 8*1024*1024, false)
	if e != nil {
		t.Fatal(e)
	}
	if e = atomicWrite(path, bytes.TrimSuffix(b, []byte{'\n'})); e != nil {
		t.Fatal(e)
	}
	v, e = p2.Call("verify")
	if e != nil || object(v)["ok"] != false {
		t.Fatalf("torn ledger %v %v", v, e)
	}
	if _, e = p2.Call("snapshot"); e == nil {
		t.Fatal("snapshotted corrupt ledger")
	}
}
func TestPhotonSnapshotTamperAndInputDrift(t *testing.T) {
	p, pkg, ws := photonFixture(t)
	v, e := p.Call("snapshot")
	if e != nil {
		t.Fatal(e)
	}
	r := object(v)
	object(object(r["state"])["properties"])["energy"] = 42
	if e = atomicJSON(filepath.Join(p.snapshots, str(r["snapshot_hash"])+".json"), r); e != nil {
		t.Fatal(e)
	}
	v, e = p.Call("verify")
	if e != nil || object(v)["ok"] != false {
		t.Fatalf("snapshot tamper %v %v", v, e)
	}
	if e = os.WriteFile(filepath.Join(pkg, "photon", "programs", "fabric_probe.pal"), []byte("drift"), 0600); e != nil {
		t.Fatal(e)
	}
	if _, e = NewPhotonBridge(pkg, ws); e == nil {
		t.Fatal("accepted input drift")
	}
}
func TestLedgerOrderAndPayloadIntegrity(t *testing.T) {
	p, _, _ := photonFixture(t)
	path := p.ledgerPath(p.eid)
	raw, e := readRegular(path, 8*1024*1024, false)
	if e != nil {
		t.Fatal(e)
	}
	v, e := strictJSON(bytes.TrimSpace(raw))
	if e != nil {
		t.Fatal(e)
	}
	r := object(v)
	object(r["payload"])["state_sha256"] = zeroHash
	b, _ := jsonBytes(r)
	if e = atomicWrite(path, b); e != nil {
		t.Fatal(e)
	}
	result, _ := verifyLedger(path)
	if result["ok"] != false || result["reason"] != "event hash mismatch" {
		t.Fatal(result)
	}
}

func TestPhotonReceiptFileIntegrity(t *testing.T) {
	p, _, _ := photonFixture(t)
	rel := "runtime/evidence/executions/" + p.eid + "-00000001.json"
	path, e := p.receiptPath(rel)
	if e != nil {
		t.Fatal(e)
	}
	receipt := map[string]any{"schema": "VEC/EXECUTION_RECEIPT/1", "electron_id": p.eid, "tick": 1, "event_log_hash": "receipt-test"}
	if e = atomicJSON(path, receipt); e != nil {
		t.Fatal(e)
	}
	b, e := readRegular(path, 8*1024*1024, false)
	if e != nil {
		t.Fatal(e)
	}
	if e = p.event(p.eid, 1, "EXECUTION_VERIFIED", map[string]any{"receipt": rel, "receipt_sha256": hashBytes(b), "event_log_hash": "receipt-test"}); e != nil {
		t.Fatal(e)
	}
	o, e := p.load(p.eid, true)
	if e != nil {
		t.Fatal(e)
	}
	object(o["runtime"])["last_receipt"] = rel
	if _, e = p.save(o); e != nil {
		t.Fatal(e)
	}
	if p.audit()["ok"] != true {
		t.Fatal("valid receipt failed verification")
	}
	receipt["event_log_hash"] = "modified"
	if e = atomicJSON(path, receipt); e != nil {
		t.Fatal(e)
	}
	if p.audit()["ok"] != false {
		t.Fatal("changed receipt accepted")
	}
}

func TestPhotonFailedSnapshotDoesNotChangeState(t *testing.T) {
	p, _, _ := photonFixture(t)
	before, e := readRegular(p.statePath(p.eid), 8*1024*1024, false)
	if e != nil {
		t.Fatal(e)
	}
	if e = atomicWrite(p.ledgerPath(p.eid), []byte{}); e != nil {
		t.Fatal(e)
	}
	if _, e = p.Call("snapshot"); e == nil {
		t.Fatal("accepted empty ledger")
	}
	after, e := readRegular(p.statePath(p.eid), 8*1024*1024, false)
	if e != nil {
		t.Fatal(e)
	}
	if !bytes.Equal(before, after) {
		t.Fatal("failed snapshot changed canonical state")
	}
}

// This disk fixture was produced by the unchanged supplied Python VEC 0.2.0
// create()/snapshot(), with deterministic identity and clock for reproducibility.
const suppliedPythonFixture = `{"config":"{\"schema\":\"VEC1/CONFIG/1\",\"version\":\"0.2.0\",\"default_program\":\"programs/fabric_probe.pal\",\"resource_quotas\":{\"max_electrons\":128,\"max_mailbox\":256,\"max_outbound\":256,\"max_program_bytes\":2097152}}","ledger":"{\"hash\":\"dca2a711f228d72475ae17de854712ca1df703d05adbc6d56d17bc79c4933d7d\",\"kind\":\"GENESIS\",\"payload\":{\"genome_sha256\":\"9aca5d8a0d97d4995b61bf09e38dcc58f07142c5c17f3124c0b4197ab8319aaa\",\"state_sha256\":\"3807c98719fe22d70651c9e9894cf92f2a5ff131cd6622c70a245716424803d3\"},\"prev_hash\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"schema\":\"VEC/LEDGER_EVENT/1\",\"seq\":0,\"tick\":0,\"utc\":\"2026-10-14T12:34:56.123456Z\"}\n{\"hash\":\"a8651c2552ccbc99387175368e06f6a46fef995114d9e10ce54d64c42fd774b0\",\"kind\":\"SNAPSHOT\",\"payload\":{\"snapshot_hash\":\"c0438b49a6e24c7dcef172ee155ff121f567292924d69e3ba561a1c944936aca\"},\"prev_hash\":\"dca2a711f228d72475ae17de854712ca1df703d05adbc6d56d17bc79c4933d7d\",\"schema\":\"VEC/LEDGER_EVENT/1\",\"seq\":1,\"tick\":0,\"utc\":\"2026-10-14T12:34:56.123456Z\"}\n","snapshot":{"created_utc":"2026-10-14T12:34:56.123456Z","electron_id":"vec1-0123456789abcdef01234567","generation_id":0,"schema":"VEC/SNAPSHOT/1","snapshot_hash":"c0438b49a6e24c7dcef172ee155ff121f567292924d69e3ba561a1c944936aca","state":{"electron_id":"vec1-0123456789abcdef01234567","generation_id":0,"genesis_record":{"created_utc":"2026-10-14T12:34:56.123456Z","genome_sha256":"9aca5d8a0d97d4995b61bf09e38dcc58f07142c5c17f3124c0b4197ab8319aaa"},"genome":{"capabilities":["fabric.execute","state.snapshot","state.clone","evidence.read"],"network":"deny","program":"programs/fabric_probe.pal","program_sha256":"25be323556dad377abb57fe7ec8c4b99a6527f488dda28d0c9b686528659c909","schema":"VEC/electron_genome/1","targets":["N_SMALL","N_MEDIUM","N_LARGE","N_XLARGE"],"version":"0.2.0"},"hashes":{"canonical_state_sha256":"fd39d3abf6f3b4b67ae6ad39fdefd927d35275c99c0eb373f770f05ddc48d128","capability_sha256":"e74d1c8b29416646f27c1e9fad1d92065b876707961d70281da143d7a5ae04a4","configuration_sha256":"9a4747364f9b83b50f25aaaf3961d5d1facd8e8756e917d2dbe51ab567f82bae","genome_sha256":"9aca5d8a0d97d4995b61bf09e38dcc58f07142c5c17f3124c0b4197ab8319aaa"},"integrity":{"ledger_events":1,"ledger_head_sha256":"dca2a711f228d72475ae17de854712ca1df703d05adbc6d56d17bc79c4933d7d"},"lineage":["vec1-0123456789abcdef01234567"],"parent_id":null,"properties":{"charge":-1,"energy":0,"interaction_radius":"DF0_LOCAL","momentum_vector":[0,0,0],"orbital":"UNASSIGNED","phase":0,"position_node_vector":[],"spin":"UP","state":"FROZEN"},"runtime":{"base_snapshot":null,"execution_status":"FROZEN","instruction_counter":0,"last_receipt":null,"mailbox":[],"outbound_queue":[],"tick":0},"schema":"VEC/ELECTRON_STATE/1","security":{"filesystem":"package-runtime-only","host_calls":"DF-only","network":"deny","plugins":[],"retired":false,"suspended":false}}},"state":{"electron_id":"vec1-0123456789abcdef01234567","generation_id":0,"genesis_record":{"created_utc":"2026-10-14T12:34:56.123456Z","genome_sha256":"9aca5d8a0d97d4995b61bf09e38dcc58f07142c5c17f3124c0b4197ab8319aaa"},"genome":{"capabilities":["fabric.execute","state.snapshot","state.clone","evidence.read"],"network":"deny","program":"programs/fabric_probe.pal","program_sha256":"25be323556dad377abb57fe7ec8c4b99a6527f488dda28d0c9b686528659c909","schema":"VEC/electron_genome/1","targets":["N_SMALL","N_MEDIUM","N_LARGE","N_XLARGE"],"version":"0.2.0"},"hashes":{"canonical_state_sha256":"743a96e21f6bda5c783fbcb545935a20b8043a3066a20ea964dd4d4c11af45f9","capability_sha256":"e74d1c8b29416646f27c1e9fad1d92065b876707961d70281da143d7a5ae04a4","configuration_sha256":"9a4747364f9b83b50f25aaaf3961d5d1facd8e8756e917d2dbe51ab567f82bae","genome_sha256":"9aca5d8a0d97d4995b61bf09e38dcc58f07142c5c17f3124c0b4197ab8319aaa"},"integrity":{"ledger_events":2,"ledger_head_sha256":"a8651c2552ccbc99387175368e06f6a46fef995114d9e10ce54d64c42fd774b0"},"lineage":["vec1-0123456789abcdef01234567"],"parent_id":null,"properties":{"charge":-1,"energy":0,"interaction_radius":"DF0_LOCAL","momentum_vector":[0,0,0],"orbital":"UNASSIGNED","phase":0,"position_node_vector":[],"spin":"UP","state":"READY"},"runtime":{"base_snapshot":"c0438b49a6e24c7dcef172ee155ff121f567292924d69e3ba561a1c944936aca","execution_status":"READY","instruction_counter":0,"last_receipt":null,"mailbox":[],"outbound_queue":[],"tick":0},"schema":"VEC/ELECTRON_STATE/1","security":{"filesystem":"package-runtime-only","host_calls":"DF-only","network":"deny","plugins":[],"retired":false,"suspended":false},"updated_utc":"2026-10-14T12:34:56.123456Z"}}`

func TestSuppliedPythonSnapshotCompatibility(t *testing.T) {
	raw, e := strictJSON([]byte(suppliedPythonFixture))
	if e != nil {
		t.Fatal(e)
	}
	f := object(raw)
	pkg := t.TempDir()
	ws := t.TempDir()
	state := object(f["state"])
	id := str(state["electron_id"])
	root := filepath.Join(ws, "photon")
	edir := filepath.Join(root, "runtime", "state", "electrons", id)
	snapdir := filepath.Join(root, "runtime", "state", "snapshots")
	for _, d := range []string{filepath.Join(pkg, "photon", "config"), filepath.Join(pkg, "photon", "programs"), edir, snapdir} {
		if e = os.MkdirAll(d, 0700); e != nil {
			t.Fatal(e)
		}
	}
	if e = os.WriteFile(filepath.Join(pkg, "photon", "config", "vec1.json"), []byte(str(f["config"])), 0600); e != nil {
		t.Fatal(e)
	}
	if e = os.WriteFile(filepath.Join(pkg, "photon", "programs", "fabric_probe.pal"), []byte("probe\n"), 0600); e != nil {
		t.Fatal(e)
	}
	if e = atomicJSON(filepath.Join(edir, "state.json"), state); e != nil {
		t.Fatal(e)
	}
	if e = atomicWrite(filepath.Join(edir, "ledger.jsonl"), []byte(str(f["ledger"]))); e != nil {
		t.Fatal(e)
	}
	snap := object(f["snapshot"])
	if e = atomicJSON(filepath.Join(snapdir, str(snap["snapshot_hash"])+".json"), snap); e != nil {
		t.Fatal(e)
	}
	if e = atomicJSON(filepath.Join(root, "console-binding.json"), map[string]any{"schema": "JA21-PHOTON-CONSOLE/1", "electron_id": id}); e != nil {
		t.Fatal(e)
	}
	p, e := NewPhotonBridge(pkg, ws)
	if e != nil {
		t.Fatal(e)
	}
	if p.ElectronID() != id {
		t.Fatal("Python identity replaced")
	}
	result, e := p.Call("verify")
	if e != nil || object(result)["ok"] != true {
		t.Fatalf("supplied Python verification: %v %v", result, e)
	}
	if _, e = p.Call("snapshot"); e != nil {
		t.Fatal(e)
	}
}
