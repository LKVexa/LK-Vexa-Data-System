// VB JA21 DF_Medium Browser 9.0.0 — VEC1 electron substrate, presenter side.
//
// The VEC1 Electron Substitute (VEC1_ES 0.1.1-candidate) supplies a portable
// virtual-electron identity / state / clone / evidence control plane. This file is the
// presenter half of that substrate: it binds the browser's application lifecycle to VEC
// electrons and writes them in exactly the sealed on-disk format that substrate/vec1
// defines, so `python substrate\vec1\vecctl.py presenter-audit` can verify every byte
// this presenter wrote without the browser embedding Python.
//
// Mapping (what an Electron-class framework would own, expressed as VEC electrons):
//   browser session  -> one session electron   (GENESIS at launch, RETIRE at exit)
//   browser tab      -> one child electron     (forked from a sealed session snapshot)
//   navigation       -> PRESENTATION ledger event on that tab's electron
//
// Claim discipline, enforced here and re-checked by vecctl:
//   * A presenter electron never records a fabric execution event and never carries a
//     fabric verdict. Presenting a document is not cross-node differential agreement.
//     The event vocabulary below is the whole of what the presenter may write, and
//     vec1/presenter.py rejects any ledger that steps outside it.
//   * Strict four-node fabric execution stays the promotion gate and stays fail-closed.
//     N_LARGE and N_XLARGE are not vendored in this browser package, so strict execution
//     reports BLOCKED. That is reported, never papered over.
//   * If anything about the substrate cannot be verified, the substrate reports DEGRADED
//     or BLOCKED and writes nothing. Browsing is never silently attributed to a substrate
//     that is not actually sealing state.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace VBJA21
{
    // ---------------------------------------------------------------------------------
    // Canonical JSON value model.
    //
    // Mirrors vec1.common.canonical_bytes: UTF-8, object keys sorted by code point,
    // separators ',' and ':', no ensure_ascii escaping. Only the types the VEC state
    // schema uses are representable: null, bool, 64-bit integer, string, array, object.
    // Floating point is deliberately absent — there is no interoperable canonical form
    // for it, and no VEC state field needs one.
    // ---------------------------------------------------------------------------------
    internal abstract class VecNode
    {
        public abstract void Write(StringBuilder b);
        public abstract VecNode Clone();
    }

    internal sealed class VecNull : VecNode
    {
        public static readonly VecNull Value = new VecNull();
        private VecNull() { }
        public override void Write(StringBuilder b) { b.Append("null"); }
        public override VecNode Clone() { return Value; }
    }

    internal sealed class VecBool : VecNode
    {
        public readonly bool V;
        public VecBool(bool v) { V = v; }
        public override void Write(StringBuilder b) { b.Append(V ? "true" : "false"); }
        public override VecNode Clone() { return new VecBool(V); }
    }

    internal sealed class VecInt : VecNode
    {
        public readonly long V;
        public VecInt(long v) { V = v; }
        public override void Write(StringBuilder b) { b.Append(V.ToString(CultureInfo.InvariantCulture)); }
        public override VecNode Clone() { return new VecInt(V); }
    }

    internal sealed class VecStr : VecNode
    {
        public readonly string V;
        public VecStr(string v) { V = v == null ? "" : v; }
        public override void Write(StringBuilder b) { VecCanonical.WriteString(b, V); }
        public override VecNode Clone() { return new VecStr(V); }
    }

    internal sealed class VecArr : VecNode
    {
        public readonly List<VecNode> Items = new List<VecNode>();
        public VecArr() { }
        public VecArr Add(VecNode n) { Items.Add(n); return this; }
        public VecArr Add(string s) { Items.Add(new VecStr(s)); return this; }
        public VecArr Add(long v) { Items.Add(new VecInt(v)); return this; }
        public override void Write(StringBuilder b)
        {
            b.Append('[');
            int i;
            for (i = 0; i < Items.Count; i++) { if (i > 0) b.Append(','); Items[i].Write(b); }
            b.Append(']');
        }
        public override VecNode Clone()
        {
            VecArr a = new VecArr();
            int i;
            for (i = 0; i < Items.Count; i++) a.Items.Add(Items[i].Clone());
            return a;
        }
    }

    internal sealed class VecObj : VecNode
    {
        private readonly Dictionary<string, VecNode> _map = new Dictionary<string, VecNode>(StringComparer.Ordinal);

        public VecObj Set(string key, VecNode value)
        {
            if (key == null) throw new Exception("VEC canonical JSON: null object key.");
            int i;
            for (i = 0; i < key.Length; i++)
                if (key[i] > (char)0x7E || key[i] < (char)0x20)
                    throw new Exception("VEC canonical JSON: object keys are restricted to printable ASCII so key ordering is identical in both implementations.");
            _map[key] = value == null ? (VecNode)VecNull.Value : value;
            return this;
        }
        public VecObj Set(string key, string value) { return Set(key, value == null ? (VecNode)VecNull.Value : new VecStr(value)); }
        public VecObj Set(string key, long value) { return Set(key, new VecInt(value)); }
        public VecObj Set(string key, bool value) { return Set(key, new VecBool(value)); }
        public VecObj SetNull(string key) { return Set(key, VecNull.Value); }

        public bool Has(string key) { return _map.ContainsKey(key); }
        public void Remove(string key) { _map.Remove(key); }
        public VecNode Get(string key) { VecNode n; return _map.TryGetValue(key, out n) ? n : null; }
        public VecObj Obj(string key)
        {
            VecObj o = Get(key) as VecObj;
            if (o == null) throw new Exception("VEC state: expected object at '" + key + "'.");
            return o;
        }
        public string Str(string key) { VecStr s = Get(key) as VecStr; return s == null ? null : s.V; }
        public long Int(string key) { VecInt v = Get(key) as VecInt; return v == null ? 0 : v.V; }
        public bool Flag(string key) { VecBool v = Get(key) as VecBool; return v != null && v.V; }

        public List<string> SortedKeys()
        {
            List<string> keys = new List<string>(_map.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        public override void Write(StringBuilder b)
        {
            b.Append('{');
            List<string> keys = SortedKeys();
            int i;
            for (i = 0; i < keys.Count; i++)
            {
                if (i > 0) b.Append(',');
                VecCanonical.WriteString(b, keys[i]);
                b.Append(':');
                _map[keys[i]].Write(b);
            }
            b.Append('}');
        }

        public override VecNode Clone()
        {
            VecObj o = new VecObj();
            foreach (KeyValuePair<string, VecNode> kv in _map) o._map[kv.Key] = kv.Value.Clone();
            return o;
        }
        public VecObj CloneObj() { return (VecObj)Clone(); }

        // Pretty form, only for the bytes written to disk. Hashing always uses the canonical form.
        public void WritePretty(StringBuilder b, int indent)
        {
            List<string> keys = SortedKeys();
            if (keys.Count == 0) { b.Append("{}"); return; }
            b.Append("{\n");
            int i;
            for (i = 0; i < keys.Count; i++)
            {
                Pad(b, indent + 2);
                VecCanonical.WriteString(b, keys[i]);
                b.Append(": ");
                WriteValuePretty(b, _map[keys[i]], indent + 2);
                if (i < keys.Count - 1) b.Append(',');
                b.Append('\n');
            }
            Pad(b, indent);
            b.Append('}');
        }
        private static void Pad(StringBuilder b, int n) { int i; for (i = 0; i < n; i++) b.Append(' '); }
        private static void WriteValuePretty(StringBuilder b, VecNode n, int indent)
        {
            VecObj o = n as VecObj;
            if (o != null) { o.WritePretty(b, indent); return; }
            VecArr a = n as VecArr;
            if (a != null)
            {
                if (a.Items.Count == 0) { b.Append("[]"); return; }
                b.Append("[\n");
                int i;
                for (i = 0; i < a.Items.Count; i++)
                {
                    Pad(b, indent + 2);
                    WriteValuePretty(b, a.Items[i], indent + 2);
                    if (i < a.Items.Count - 1) b.Append(',');
                    b.Append('\n');
                }
                Pad(b, indent);
                b.Append(']');
                return;
            }
            n.Write(b);
        }
    }

    internal static class VecCanonical
    {
        // Python json.dumps(ensure_ascii=False) escape set: the two structural characters
        // plus the five short control escapes; every other control character below 0x20
        // becomes \u00xx. Nothing else is escaped — not '/', not DEL, not U+2028/U+2029.
        public static void WriteString(StringBuilder b, string s)
        {
            b.Append('"');
            int i;
            for (i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') b.Append("\\\"");
                else if (c == '\\') b.Append("\\\\");
                else if (c == '\n') b.Append("\\n");
                else if (c == '\r') b.Append("\\r");
                else if (c == '\t') b.Append("\\t");
                else if (c == '\b') b.Append("\\b");
                else if (c == '\f') b.Append("\\f");
                else if (c < (char)0x20) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            b.Append('"');
        }

        public static string Text(VecNode n)
        {
            StringBuilder b = new StringBuilder(512);
            n.Write(b);
            return b.ToString();
        }

        public static byte[] Bytes(VecNode n) { return new UTF8Encoding(false).GetBytes(Text(n)); }

        public static string Sha256(VecNode n) { return RuntimeSeal.Sha256(Bytes(n)); }

        public static string PrettyText(VecObj o)
        {
            StringBuilder b = new StringBuilder(2048);
            o.WritePretty(b, 0);
            b.Append('\n');
            return b.ToString();
        }

        // Startup conformance gate. The golden vectors are produced by the Python control
        // plane; if this serializer disagrees with any of them by a single byte, every hash
        // the presenter would seal would be wrong, so the substrate refuses to start.
        public static string SelfTest(string vectorsPath)
        {
            if (!File.Exists(vectorsPath)) throw new Exception("VEC canonical vectors are missing: " + vectorsPath);
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = Int32.MaxValue;
            Dictionary<string, object> doc = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(vectorsPath, Encoding.UTF8));
            object[] vectors = (object[])doc["vectors"];
            if (vectors.Length == 0) throw new Exception("VEC canonical vectors file declares no vectors.");
            int i;
            for (i = 0; i < vectors.Length; i++)
            {
                Dictionary<string, object> v = (Dictionary<string, object>)vectors[i];
                string name = Convert.ToString(v["name"]);
                VecNode value = FromDeserialized(v["value"]);
                string got = Text(value);
                string want = Convert.ToString(v["canonical"]);
                if (!String.Equals(got, want, StringComparison.Ordinal))
                    throw new Exception("VEC canonical JSON self-test failed on vector '" + name + "'.");
                if (!String.Equals(Sha256(value), Convert.ToString(v["sha256"]), StringComparison.Ordinal))
                    throw new Exception("VEC canonical hash self-test failed on vector '" + name + "'.");
            }
            return vectors.Length.ToString(CultureInfo.InvariantCulture) + " canonical vectors PASS";
        }

        // JavaScriptSerializer hands back Dictionary/object[]/primitives; map that onto the
        // canonical model. Only used by the self-test, which is why non-integral numbers are
        // a hard error rather than a silent conversion.
        public static VecNode FromDeserialized(object o)
        {
            if (o == null) return VecNull.Value;
            if (o is string) return new VecStr((string)o);
            if (o is bool) return new VecBool((bool)o);
            if (o is int) return new VecInt((int)o);
            if (o is long) return new VecInt((long)o);
            if (o is decimal)
            {
                decimal d = (decimal)o;
                if (Decimal.Truncate(d) != d) throw new Exception("VEC canonical JSON: non-integral number is not representable.");
                return new VecInt(Decimal.ToInt64(d));
            }
            if (o is double)
            {
                double d = (double)o;
                if (Math.Floor(d) != d || Math.Abs(d) > 9.007199254740991E15) throw new Exception("VEC canonical JSON: non-integral number is not representable.");
                return new VecInt((long)d);
            }
            if (o is object[])
            {
                object[] a = (object[])o;
                VecArr arr = new VecArr();
                int i;
                for (i = 0; i < a.Length; i++) arr.Items.Add(FromDeserialized(a[i]));
                return arr;
            }
            Dictionary<string, object> map = o as Dictionary<string, object>;
            if (map != null)
            {
                VecObj obj = new VecObj();
                foreach (KeyValuePair<string, object> kv in map) obj.Set(kv.Key, FromDeserialized(kv.Value));
                return obj;
            }
            throw new Exception("VEC canonical JSON: unsupported value type " + o.GetType().FullName);
        }
    }

    // ---------------------------------------------------------------------------------
    // Sealed writes.
    // ---------------------------------------------------------------------------------
    internal static class VecIo
    {
        public static void WriteAtomic(string path, string text)
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            RuntimeSeal.RejectReparsePoints(dir, path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            // Do not append a GUID to a 64-character snapshot basename. A short
            // independent name in the same directory retains atomic replacement.
            string tmp = PortablePaths.RequireWindowsPath(System.IO.Path.Combine(dir, ".write-" + Guid.NewGuid().ToString("N") + ".tmp"));
            try
            {
                using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] data = new UTF8Encoding(false).GetBytes(text);
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null, true);
                else File.Move(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        public static void AppendLine(string path, string line)
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            RuntimeSeal.RejectReparsePoints(dir, path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                byte[] data = new UTF8Encoding(false).GetBytes(line + "\n");
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
        }

        // Byte-range lock on substrate/runtime/state/.vec1.lock. vecctl takes the same lock
        // through msvcrt.locking, which is the same Win32 LockFile range, so a concurrent
        // vecctl invocation and this presenter cannot interleave a state write.
        public static IDisposable Lock(string lockPath, int timeoutMs)
        {
            return new VecFileLock(lockPath, timeoutMs);
        }
    }

    internal sealed class VecFileLock : IDisposable
    {
        private FileStream _fs;
        private bool _held;
        public VecFileLock(string path, int timeoutMs)
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            int waited = 0;
            while (true)
            {
                try
                {
                    _fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                    _fs.Lock(0, 1);
                    _held = true;
                    return;
                }
                catch (IOException)
                {
                    if (_fs != null) { try { _fs.Dispose(); } catch { } _fs = null; }
                    if (waited >= timeoutMs) throw new Exception("VEC substrate lock is held by another process; state was not written.");
                    System.Threading.Thread.Sleep(50);
                    waited += 50;
                }
            }
        }
        public void Dispose()
        {
            try { if (_held && _fs != null) _fs.Unlock(0, 1); }
            catch { }
            finally { if (_fs != null) { try { _fs.Dispose(); } catch { } } _held = false; _fs = null; }
        }
    }

    // ---------------------------------------------------------------------------------
    // Hash-chained ledger (write side).
    //
    // The presenter only ever appends to ledgers it created in this process, so the chain
    // head is authoritative in memory and no unverified file is ever extended. vecctl
    // re-derives the whole chain independently.
    // ---------------------------------------------------------------------------------
    internal sealed class VecLedger
    {
        public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
        private readonly string _path;
        private string _head = Genesis;
        private long _seq;
        private readonly int _maxEvents;

        public VecLedger(string path, int maxEvents)
        {
            _path = path;
            _maxEvents = maxEvents;
            if (File.Exists(path)) throw new Exception("VEC ledger already exists and would be extended without verification: " + path);
        }
        public string Head { get { return _head; } }
        public long Count { get { return _seq; } }
        public bool Full { get { return _seq >= _maxEvents; } }

        public string Append(long tick, string kind, VecObj payload)
        {
            if (Full) throw new Exception("VEC ledger event quota reached for this electron.");
            VecObj rec = new VecObj();
            rec.Set("schema", "VEC/LEDGER_EVENT/1");
            rec.Set("seq", _seq);
            rec.Set("tick", tick);
            rec.Set("utc", VecSubstrate.UtcNow());
            rec.Set("kind", kind);
            rec.Set("payload", payload == null ? new VecObj() : payload);
            rec.Set("prev_hash", _head);
            string hash = VecCanonical.Sha256(rec);
            rec.Set("hash", hash);
            VecIo.AppendLine(_path, VecCanonical.Text(rec));
            _head = hash;
            _seq++;
            return hash;
        }
    }

    // ---------------------------------------------------------------------------------
    // One VEC electron owned by the presenter.
    // ---------------------------------------------------------------------------------
    internal sealed class VecElectron
    {
        public readonly string Id;
        public readonly string Role;
        public VecObj State;
        public readonly VecLedger Ledger;
        private readonly VecSubstrate _sub;

        public VecElectron(VecSubstrate sub, string id, string role, VecObj state, VecLedger ledger)
        {
            _sub = sub; Id = id; Role = role; State = state; Ledger = ledger;
        }
        public string Status { get { return State.Obj("runtime").Str("execution_status"); } }
        public long Tick { get { return State.Obj("runtime").Int("tick"); } }
        public bool Retired { get { return State.Obj("security").Flag("retired"); } }
        public string StateHash { get { return State.Obj("hashes").Str("canonical_state_sha256"); } }
        public void Save() { _sub.Save(this); }
    }

    // ---------------------------------------------------------------------------------
    // The substrate itself.
    // ---------------------------------------------------------------------------------
    public sealed class VecSubstrate
    {
        public const string ProductVersion = BrowserVersion.Full;
        private const string SchemaState = "VEC/ELECTRON_STATE/1";
        private const string SchemaGenome = "VEC/electron_genome/1";
        private const string SchemaSnapshot = "VEC/SNAPSHOT/1";

        private readonly string _packageRoot;
        private readonly string _root;          // <package>/substrate
        private readonly string _stateDir;      // portable workspace/state/vec1
        private readonly string _electronsDir;
        private readonly string _snapshotsDir;
        private readonly string _lockPath;
        private readonly string _configSha;
        private readonly string _policySha;
        private readonly string _substrateVersion;
        private readonly int _maxElectrons;
        private readonly int _maxLedgerEvents;
        private readonly List<VecElectron> _owned = new List<VecElectron>();
        private readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();
        private VecElectron _session;
        private string _sessionSnapshot;

        public string Verdict { get; private set; }        // BOUND | DEGRADED | BLOCKED
        public string Detail { get; private set; }
        public string CanonicalSelfTest { get; private set; }
        public List<string> BoundNodes { get; private set; }
        public List<string> UnvendoredNodes { get; private set; }
        public List<string> Notes { get; private set; }
        public bool Sealing { get { return Verdict == "BOUND" && _session != null; } }
        public string SessionId { get { return _session == null ? null : _session.Id; } }
        public int ElectronCount { get { return _owned.Count; } }

        public static string UtcNow()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        }

        public VecSubstrate(string packageRoot)
        {
            _packageRoot = packageRoot;
            _root = System.IO.Path.Combine(packageRoot, "substrate");
            _stateDir = PortablePaths.Vec1State(packageRoot);
            _electronsDir = System.IO.Path.Combine(_stateDir, "electrons");
            _snapshotsDir = System.IO.Path.Combine(_stateDir, "snapshots");
            _lockPath = System.IO.Path.Combine(_stateDir, ".vec1.lock");
            Notes = new List<string>();
            BoundNodes = new List<string>();
            UnvendoredNodes = new List<string>();
            Verdict = "BLOCKED";
            Detail = "Substrate not evaluated.";
            _maxElectrons = 96;
            _maxLedgerEvents = 4096;
            _substrateVersion = "unknown";
            _configSha = "";
            _policySha = "";

            try
            {
                if (!Directory.Exists(_root)) { Detail = "substrate/ is not present in this package."; return; }

                CanonicalSelfTest = VecCanonical.SelfTest(System.IO.Path.Combine(_root, "evidence", "presenter", "CANONICAL_VECTORS.json"));

                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = Int32.MaxValue;

                Dictionary<string, object> seal = (Dictionary<string, object>)js.DeserializeObject(
                    File.ReadAllText(System.IO.Path.Combine(_root, "config", "CONFIG_SEAL.json"), Encoding.UTF8));
                _configSha = Convert.ToString(seal["configuration_sha256"]);
                _policySha = Convert.ToString(seal["security_policy_sha256"]);
                RuntimeSeal.RequireSha256(_configSha, "substrate configuration seal");
                RuntimeSeal.RequireSha256(_policySha, "substrate security-policy seal");

                Dictionary<string, object> cfg = (Dictionary<string, object>)js.DeserializeObject(
                    File.ReadAllText(System.IO.Path.Combine(_root, "config", "vec1.json"), Encoding.UTF8));
                if (!String.Equals(VecCanonical.Sha256(VecCanonical.FromDeserialized(cfg)), _configSha, StringComparison.Ordinal))
                    throw new Exception("substrate configuration seal does not match the live configuration");
                _substrateVersion = Convert.ToString(cfg["version"]);
                if (!Convert.ToBoolean(cfg["strict_four_node_execution"])) Notes.Add("substrate config has relaxed strict four-node execution");
                if (!String.Equals(Convert.ToString(cfg["network_policy"]), "deny", StringComparison.Ordinal))
                    throw new Exception("substrate network policy is not deny");
                object presenterObj;
                if (cfg.TryGetValue("presenter", out presenterObj) && presenterObj is Dictionary<string, object>)
                {
                    Dictionary<string, object> pres = (Dictionary<string, object>)presenterObj;
                    if (!Convert.ToBoolean(pres["enabled"])) { Detail = "Presenter binding is disabled in substrate/config/vec1.json."; return; }
                    if (Convert.ToBoolean(pres["records_fabric_verdict"])) throw new Exception("substrate config would let the presenter record a fabric verdict");
                    _maxElectrons = Convert.ToInt32(pres["max_presenter_electrons"]);
                    _maxLedgerEvents = Convert.ToInt32(pres["max_ledger_events_per_electron"]);
                    if (_maxElectrons < 2 || _maxElectrons > 4096 || _maxLedgerEvents < 8 || _maxLedgerEvents > 1000000)
                        throw new Exception("substrate presenter quotas are out of range");
                }

                Dictionary<string, object> policy = (Dictionary<string, object>)js.DeserializeObject(
                    File.ReadAllText(System.IO.Path.Combine(_root, "config", "security_policy.json"), Encoding.UTF8));
                if (!String.Equals(VecCanonical.Sha256(VecCanonical.FromDeserialized(policy)), _policySha, StringComparison.Ordinal))
                    throw new Exception("substrate security-policy seal does not match the live policy");
                string[] failClosed = new string[] { "integrity_failure", "schema_failure", "node_disagreement", "unbound_required_node" };
                int fc;
                for (fc = 0; fc < failClosed.Length; fc++)
                    if (!String.Equals(Convert.ToString(policy[failClosed[fc]]), "fail-closed", StringComparison.Ordinal))
                        throw new Exception("substrate security policy is not fail-closed for " + failClosed[fc]);
                if (!String.Equals(Convert.ToString(policy["network"]), "deny", StringComparison.Ordinal))
                    throw new Exception("substrate security policy does not deny network");

                ReadNodeBinding(js);
                CountStaleElectrons();

                Directory.CreateDirectory(_electronsDir);
                Directory.CreateDirectory(_snapshotsDir);
                PortableFabric.Record("workspace.state", "vec1-state-root", _stateDir);

                Verdict = "BOUND";
                Detail = "VEC1 " + _substrateVersion + " · " + CanonicalSelfTest;
            }
            catch (Exception ex)
            {
                Verdict = "BLOCKED";
                Detail = ex.Message;
            }
        }

        private void ReadNodeBinding(JavaScriptSerializer js)
        {
            string p = System.IO.Path.Combine(_root, "NODES.json");
            if (!File.Exists(p)) throw new Exception("substrate/NODES.json is missing");
            Dictionary<string, object> doc = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(p, Encoding.UTF8));
            Dictionary<string, object> nodes = (Dictionary<string, object>)doc["nodes"];
            foreach (KeyValuePair<string, object> kv in nodes)
            {
                Dictionary<string, object> rec = (Dictionary<string, object>)kv.Value;
                object container = rec.ContainsKey("container") ? rec["container"] : null;
                if (container == null) { UnvendoredNodes.Add(kv.Key); continue; }
                string rel = Convert.ToString(container);
                string containerDir = RuntimeSeal.Contained(_packageRoot, rel);
                string vmName = Convert.ToString(rec["vm_dirname"]);
                if (String.IsNullOrWhiteSpace(vmName) || vmName.IndexOfAny(new char[] { '/', '\\', ':' }) >= 0 || vmName == "." || vmName == "..")
                    throw new Exception("Unsafe node VM directory name for " + kv.Key);
                string vmDir = RuntimeSeal.Contained(containerDir, "vm/" + vmName);
                if (Directory.Exists(vmDir)) BoundNodes.Add(kv.Key);
                else
                {
                    UnvendoredNodes.Add(kv.Key);
                    Notes.Add(kv.Key + " declares container '" + rel + "' but its VM directory is absent");
                }
            }
            BoundNodes.Sort(StringComparer.Ordinal);
            UnvendoredNodes.Sort(StringComparer.Ordinal);
            if (UnvendoredNodes.Count > 0)
                Notes.Add("strict four-node fabric execution is BLOCKED: " + String.Join(", ", UnvendoredNodes.ToArray()) + " not vendored in this package");
        }

        private void CountStaleElectrons()
        {
            StaleElectrons = 0;
            if (!Directory.Exists(_electronsDir)) return;
            string[] dirs = Directory.GetDirectories(_electronsDir);
            int i;
            for (i = 0; i < dirs.Length; i++)
                if (File.Exists(System.IO.Path.Combine(dirs[i], "state.json"))) StaleElectrons++;
            if (StaleElectrons > 0)
                Notes.Add(StaleElectrons.ToString(CultureInfo.InvariantCulture) + " electron(s) from earlier sessions are on disk; `vecctl presenter-gc` retires any left unretired");
        }
        public int StaleElectrons { get; private set; }

        // ---- identity ----------------------------------------------------------------
        private string NewId()
        {
            byte[] salt = new byte[32];
            _rng.GetBytes(salt);
            StringBuilder material = new StringBuilder();
            int i;
            for (i = 0; i < salt.Length; i++) material.Append(salt[i].ToString("x2", CultureInfo.InvariantCulture));
            material.Append('|').Append(DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            string h = RuntimeSeal.Sha256(new UTF8Encoding(false).GetBytes(material.ToString()));
            return "vec1-" + h.Substring(0, 24);
        }

        private string ElectronDir(string id) { return System.IO.Path.Combine(_electronsDir, id); }

        // ---- sealing -----------------------------------------------------------------
        // Mirrors vec1.core.ElectronStore.save: updated_utc and hashes are excluded from the
        // canonical state hash, and every derived hash is recomputed on every save.
        internal void Save(VecElectron e)
        {
            VecObj obj = e.State;
            obj.Set("updated_utc", UtcNow());
            VecObj logical = obj.CloneObj();
            logical.Remove("updated_utc");
            logical.Remove("hashes");
            VecObj hashes = new VecObj();
            hashes.Set("canonical_state_sha256", VecCanonical.Sha256(logical));
            hashes.Set("genome_sha256", VecCanonical.Sha256(obj.Obj("genome")));
            hashes.Set("capability_sha256", VecCanonical.Sha256(obj.Obj("genome").Get("capabilities")));
            hashes.Set("configuration_sha256", _configSha);
            obj.Set("hashes", hashes);
            VecIo.WriteAtomic(System.IO.Path.Combine(ElectronDir(e.Id), "state.json"), VecCanonical.PrettyText(obj));
        }

        private static void SetStatus(VecObj obj, string status)
        {
            obj.Obj("runtime").Set("execution_status", status);
            obj.Obj("properties").Set("state", status);
        }

        private VecObj NewGenome()
        {
            VecObj g = new VecObj();
            g.Set("schema", SchemaGenome);
            g.Set("version", _substrateVersion);
            g.Set("program", "presenter://ja21-omni-bin");
            g.Set("program_sha256", "");
            VecArr targets = new VecArr();
            targets.Add("N_SMALL").Add("N_MEDIUM").Add("N_LARGE").Add("N_XLARGE");
            g.Set("targets", targets);
            g.Set("network", "deny");
            VecArr caps = new VecArr();
            caps.Add("presentation.record").Add("state.snapshot").Add("state.clone").Add("evidence.read");
            g.Set("capabilities", caps);
            return g;
        }

        private VecObj NewStateSkeleton(string id, string role)
        {
            VecObj obj = new VecObj();
            obj.Set("schema", SchemaState);
            obj.Set("electron_id", id);
            obj.Set("generation_id", 0L);
            obj.SetNull("parent_id");
            VecArr lineage = new VecArr(); lineage.Add(id);
            obj.Set("lineage", lineage);

            VecObj genesis = new VecObj();
            genesis.Set("created_utc", UtcNow());
            VecObj genome = NewGenome();
            genesis.Set("genome_sha256", VecCanonical.Sha256(genome));
            obj.Set("genesis_record", genesis);
            obj.Set("genome", genome);

            VecObj props = new VecObj();
            props.Set("charge", -1L); props.Set("energy", 0L); props.Set("orbital", "PRESENTER_LOCAL");
            props.Set("state", "READY"); props.Set("phase", 0L); props.Set("spin", "UP");
            VecArr mv = new VecArr(); mv.Add(0L).Add(0L).Add(0L);
            props.Set("momentum_vector", mv);
            props.Set("position_node_vector", new VecArr());
            props.Set("interaction_radius", "PRESENTER_LOCAL");
            obj.Set("properties", props);

            VecObj rt = new VecObj();
            rt.Set("tick", 0L); rt.Set("instruction_counter", 0L); rt.Set("execution_status", "READY");
            rt.Set("mailbox", new VecArr()); rt.Set("outbound_queue", new VecArr());
            rt.SetNull("base_snapshot"); rt.SetNull("last_receipt");
            obj.Set("runtime", rt);

            VecObj sec = new VecObj();
            sec.Set("network", "deny"); sec.Set("plugins", new VecArr());
            sec.Set("filesystem", "package-runtime-only"); sec.Set("host_calls", "DF-only");
            sec.Set("suspended", false); sec.Set("retired", false);
            obj.Set("security", sec);

            VecObj prov = new VecObj();
            prov.Set("origin", "presenter");
            prov.Set("role", role);
            prov.Set("session_id", _session == null ? id : _session.Id);
            prov.Set("product", "VB JA21 DF_Medium Browser");
            prov.Set("version", ProductVersion);
            prov.Set("records_fabric_verdict", false);
            prov.Set("security_policy_sha256", _policySha);
            obj.Set("provenance", prov);

            obj.Set("hashes", new VecObj());
            return obj;
        }

        // ---- lifecycle ---------------------------------------------------------------
        public string BeginSession()
        {
            PortableFabric.Record("workspace.state", "session-begin", "VEC1 portable session electron");
            if (Verdict != "BOUND" || _session != null) return null;
            try
            {
                using (VecIo.Lock(_lockPath, 4000))
                {
                    string id = NewId();
                    VecObj obj = NewStateSkeleton(id, "session");
                    VecLedger ledger = new VecLedger(System.IO.Path.Combine(ElectronDir(id), "ledger.jsonl"), _maxLedgerEvents);
                    VecElectron e = new VecElectron(this, id, "session", obj, ledger);
                    Save(e);
                    VecObj payload = new VecObj();
                    payload.Set("genome_sha256", obj.Obj("genesis_record").Str("genome_sha256"));
                    payload.Set("state_sha256", e.StateHash);
                    payload.Set("role", "session");
                    payload.Set("bound_nodes", JoinArr(BoundNodes));
                    payload.Set("strict_execution", "BLOCKED_UNVENDORED_NODES");
                    ledger.Append(0, "GENESIS", payload);
                    _session = e;
                    _owned.Add(e);
                    _sessionSnapshot = Snapshot(e);
                    return id;
                }
            }
            catch (Exception ex) { Degrade("session electron not sealed: " + ex.Message); return null; }
        }

        private static VecArr JoinArr(List<string> items)
        {
            VecArr a = new VecArr();
            int i;
            for (i = 0; i < items.Count; i++) a.Add(items[i]);
            return a;
        }

        // Mirrors vec1.core.ElectronStore._snapshot_locked exactly, so vecctl can load and
        // re-verify the snapshot record it produces.
        private string Snapshot(VecElectron e)
        {
            string prior = e.Status;
            SetStatus(e.State, "FROZEN");
            Save(e);
            VecObj core = e.State.CloneObj();
            core.Remove("updated_utc");
            string snapHash = VecCanonical.Sha256(core);
            VecObj rec = new VecObj();
            rec.Set("schema", SchemaSnapshot);
            rec.Set("snapshot_hash", snapHash);
            rec.Set("electron_id", e.Id);
            rec.Set("generation_id", e.State.Int("generation_id"));
            rec.Set("state", core);
            rec.Set("created_utc", UtcNow());
            string sp = System.IO.Path.Combine(_snapshotsDir, snapHash + ".json");
            if (!File.Exists(sp)) VecIo.WriteAtomic(sp, VecCanonical.PrettyText(rec));
            VecObj payload = new VecObj();
            payload.Set("snapshot_hash", snapHash);
            e.Ledger.Append(e.Tick, "SNAPSHOT", payload);
            e.State.Obj("runtime").Set("base_snapshot", snapHash);
            SetStatus(e.State, (prior == "RUNNING" || prior == "FROZEN") ? "READY" : prior);
            Save(e);
            return snapHash;
        }

        /// <summary>Fork a tab electron from the sealed session snapshot. Returns null when
        /// the substrate is not sealing, in which case the tab simply carries no electron.</summary>
        public string BindTab(string label)
        {
            PortableFabric.Record("session.snapshot", "tab-bind", label == null ? "tab" : label);
            if (!Sealing) return null;
            if (_owned.Count >= _maxElectrons)
            {
                Degrade("presenter electron quota reached (" + _maxElectrons.ToString(CultureInfo.InvariantCulture) + "); further tabs are unbound");
                return null;
            }
            try
            {
                using (VecIo.Lock(_lockPath, 4000))
                {
                    string cid = NewId();
                    VecObj child = _session.State.CloneObj();
                    child.Remove("updated_utc");
                    child.Set("electron_id", cid);
                    child.Set("parent_id", _session.Id);
                    child.Set("generation_id", _session.State.Int("generation_id") + 1);
                    VecArr lineage = new VecArr();
                    VecArr parentLineage = (VecArr)_session.State.Get("lineage");
                    int i;
                    for (i = 0; i < parentLineage.Items.Count; i++) lineage.Items.Add(parentLineage.Items[i].Clone());
                    lineage.Add(cid);
                    child.Set("lineage", lineage);

                    VecObj genesis = new VecObj();
                    genesis.Set("created_utc", UtcNow());
                    genesis.Set("forked_from", _session.Id);
                    genesis.Set("snapshot_hash", _sessionSnapshot);
                    genesis.Set("genome_sha256", _session.State.Obj("genesis_record").Str("genome_sha256"));
                    child.Set("genesis_record", genesis);

                    child.Obj("runtime").Set("base_snapshot", _sessionSnapshot);
                    child.Obj("security").Set("suspended", false);
                    child.Obj("provenance").Set("role", "tab");
                    child.Obj("provenance").Set("session_id", _session.Id);
                    child.Obj("provenance").Set("label", label == null ? "" : Trim(label, 120));
                    SetStatus(child, "READY");

                    VecLedger ledger = new VecLedger(System.IO.Path.Combine(ElectronDir(cid), "ledger.jsonl"), _maxLedgerEvents);
                    VecElectron e = new VecElectron(this, cid, "tab", child, ledger);
                    Save(e);

                    VecObj payload = new VecObj();
                    payload.Set("parent_id", _session.Id);
                    payload.Set("snapshot_hash", _sessionSnapshot);
                    ledger.Append(child.Obj("runtime").Int("tick"), "CLONE_GENESIS", payload);

                    VecObj fork = new VecObj();
                    fork.Set("child_id", cid);
                    fork.Set("snapshot_hash", _sessionSnapshot);
                    _session.Ledger.Append(_session.Tick, "FORK", fork);

                    _owned.Add(e);
                    return cid;
                }
            }
            catch (Exception ex) { Degrade("tab electron not sealed: " + ex.Message); return null; }
        }

        /// <summary>Record one presentation on a tab electron. Never a fabric verdict.</summary>
        public void RecordPresentation(string electronId, string host, string verdict, long bytes, long sceneRecords, long scriptsIsolated, string reason)
        { RecordPresentation(electronId,host,verdict,bytes,sceneRecords,scriptsIsolated,reason,"df-medium-native"); }

        public void RecordPresentation(string electronId, string host, string verdict, long bytes, long sceneRecords, long scriptsIsolated, string reason, string presentationEngine)
        {
            VecElectron e = Find(electronId);
            if (e == null || !Sealing || e.Retired) return;
            try
            {
                using (VecIo.Lock(_lockPath, 2000))
                {
                    if (e.Ledger.Full) { Degrade("ledger event quota reached for an electron; presentations are no longer recorded"); return; }
                    VecObj rt = e.State.Obj("runtime");
                    rt.Set("tick", rt.Int("tick") + 1);
                    rt.Set("instruction_counter", rt.Int("instruction_counter") + sceneRecords);
                    VecObj props = e.State.Obj("properties");
                    props.Set("phase", (props.Int("phase") + 1) % 2);
                    props.Set("spin", String.Equals(props.Str("spin"), "UP", StringComparison.Ordinal) ? "DOWN" : "UP");
                    props.Set("energy", props.Int("energy") + (bytes > 0 ? 1 : 0));
                    SetStatus(e.State, "READY");
                    Save(e);

                    VecObj payload = new VecObj();
                    payload.Set("host", Trim(host, 255));
                    payload.Set("bytes", bytes);
                    payload.Set("scene_records", sceneRecords);
                    payload.Set("scripts_isolated", scriptsIsolated);
                    payload.Set("presentation_engine", Trim(presentationEngine, 80));
                    payload.Set("state_sha256", e.StateHash);
                    payload.Set("fabric_verdict", VecNull.Value);
                    if (reason != null) payload.Set("reason", Trim(reason, 400));
                    e.Ledger.Append(e.Tick, String.Equals(verdict, "ok", StringComparison.Ordinal) ? "PRESENTATION" : "PRESENTATION_FAILED", payload);
                }
            }
            catch (Exception ex) { Degrade("presentation not recorded: " + ex.Message); }
        }

        /// <summary>Retire a tab electron. Terminal and monotonic, exactly like vecctl retire.</summary>
        public void RetireTab(string electronId)
        {
            VecElectron e = Find(electronId);
            if (e == null || !Sealing || e.Retired) return;
            try
            {
                using (VecIo.Lock(_lockPath, 2000))
                {
                    e.State.Obj("security").Set("retired", true);
                    SetStatus(e.State, "RETIRED");
                    Save(e);
                    e.Ledger.Append(e.Tick, "RETIRE", new VecObj());
                }
            }
            catch (Exception ex) { Degrade("tab electron not retired: " + ex.Message); }
        }

        /// <summary>Retire every electron this presenter owns. Called once on window close.</summary>
        public void EndSession()
        {
            PortableFabric.Record("history.replay", "session-end", "VEC1 session retirement");
            if (_session == null) return;
            int i;
            for (i = _owned.Count - 1; i >= 0; i--)
            {
                VecElectron e = _owned[i];
                if (e.Retired) continue;
                try
                {
                    using (VecIo.Lock(_lockPath, 2000))
                    {
                        e.State.Obj("security").Set("retired", true);
                        SetStatus(e.State, "RETIRED");
                        Save(e);
                        e.Ledger.Append(e.Tick, "RETIRE", new VecObj());
                    }
                }
                catch { }
            }
            _session = null;
        }

        private VecElectron Find(string id)
        {
            if (id == null) return null;
            int i;
            for (i = 0; i < _owned.Count; i++) if (String.Equals(_owned[i].Id, id, StringComparison.Ordinal)) return _owned[i];
            return null;
        }

        private void Degrade(string note)
        {
            if (Verdict == "BOUND") Verdict = "DEGRADED";
            if (!Notes.Contains(note)) Notes.Add(note);
            Detail = note;
        }

        private static string Trim(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n);
        }

        // ---- presentation for the UI --------------------------------------------------
        public string ShortStatus()
        {
            if (Verdict == "BOUND") return "VEC1 substrate · " + _owned.Count.ToString(CultureInfo.InvariantCulture) + " electrons sealed";
            if (Verdict == "DEGRADED") return "VEC1 substrate · degraded";
            return "VEC1 substrate · not bound";
        }

        public string LongStatus()
        {
            StringBuilder b = new StringBuilder();
            b.Append("VEC1 electron substrate ").Append(_substrateVersion).Append(" · ").Append(Verdict).Append('\n');
            b.Append(Detail).Append("\n\n");
            b.Append("Session electron: ").Append(_session == null ? "none" : _session.Id).Append('\n');
            b.Append("Electrons sealed this session: ").Append(_owned.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            b.Append("Node containers bound: ").Append(BoundNodes.Count == 0 ? "none" : String.Join(", ", BoundNodes.ToArray())).Append('\n');
            b.Append("Not vendored in this package: ").Append(UnvendoredNodes.Count == 0 ? "none" : String.Join(", ", UnvendoredNodes.ToArray())).Append('\n');
            b.Append("Strict four-node fabric execution: ").Append(UnvendoredNodes.Count == 0 ? "available via vecctl verify" : "BLOCKED (fail-closed)").Append('\n');
            b.Append("Fabric verdict recorded by this presenter: none, by design\n");
            int i;
            for (i = 0; i < Notes.Count; i++) b.Append("· ").Append(Notes[i]).Append('\n');
            b.Append("\nAudit from a shell:\n  python substrate\\vec1\\vecctl.py presenter-audit\n  python substrate\\vec1\\vecctl.py nodes");
            return b.ToString();
        }
    }
}
