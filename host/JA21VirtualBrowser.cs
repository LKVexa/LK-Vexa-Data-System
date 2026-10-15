using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace VBJA21
{
    /// <summary>Single source of truth for the presenter version string.</summary>
    public static class BrowserVersion
    {
        public const string Full = "9.8.7";
        public const string Window = "JA21 Portable Optical Instrument 9.8.7";
        public const string Engine = "DF_Medium JA21 Web Engine";
    }

    public sealed class Ja21Diagnostic : Exception
    {
        public Ja21Diagnostic(string message) : base(message) { }
    }

    public sealed class Ja21Vm
    {
        private readonly Dictionary<string, object> _unit;
        private readonly Dictionary<string, object> _functions;
        private readonly Dictionary<string, object> _records;
        private int _steps;
        private int _calls;

        public Ja21Vm(Dictionary<string, object> unit)
        {
            _unit = unit;
            _functions = Dict(unit["functions"]);
            _records = Dict(unit["records"]);
        }

        public object Run(string name, params object[] args)
        {
            _steps = 0;
            _calls = 0;
            return Invoke(name, args);
        }

        private void Tick()
        {
            _steps++;
            if (_steps > 20000) throw new Ja21Diagnostic("Execution budget exceeded");
        }

        private object Invoke(string name, object[] args)
        {
            Tick();
            _calls++;
            if (_calls > 48) throw new Ja21Diagnostic("Call depth exceeded");
            try
            {
                object fnObj;
                if (!_functions.TryGetValue(name, out fnObj)) throw new Ja21Diagnostic("Unknown function " + name);
                Dictionary<string, object> fn = Dict(fnObj);
                object[] pars = Arr(fn["params"]);
                if (args.Length != pars.Length) throw new Ja21Diagnostic("Arity mismatch " + name);
                Dictionary<string, object> scope = new Dictionary<string, object>(StringComparer.Ordinal);
                int i;
                for (i = 0; i < pars.Length; i++)
                {
                    Dictionary<string, object> p = Dict(pars[i]);
                    scope[Str(p["name"])] = Check(args[i], Str(p["type"]));
                }
                EvalResult r = Block(Arr(fn["body"]), scope);
                if (!r.Returned) throw new Ja21Diagnostic("Missing return from " + name);
                return Check(r.Value, Str(fn["returns"]));
            }
            finally { _calls--; }
        }

        private EvalResult Block(object[] statements, Dictionary<string, object> parent)
        {
            Dictionary<string, object> scope = new Dictionary<string, object>(parent, StringComparer.Ordinal);
            int i;
            for (i = 0; i < statements.Length; i++)
            {
                Tick();
                Dictionary<string, object> s = Dict(statements[i]);
                string op = Str(s["op"]);
                if (op == "return") return new EvalResult(true, Eval(Dict(s["value"]), scope));
                if (op == "let")
                {
                    string n = Str(s["name"]);
                    if (scope.ContainsKey(n)) throw new Ja21Diagnostic("Duplicate binding " + n);
                    object v = Eval(Dict(s["value"]), scope);
                    object t;
                    if (s.TryGetValue("type", out t) && t != null && Str(t).Length > 0) v = Check(v, Str(t));
                    scope[n] = v;
                    continue;
                }
                if (op == "assert")
                {
                    if (!(bool)Check(Eval(Dict(s["value"]), scope), "Flag")) throw new Ja21Diagnostic("Assertion failed");
                    continue;
                }
                if (op == "if")
                {
                    bool cond = (bool)Check(Eval(Dict(s["condition"]), scope), "Flag");
                    object[] branch = Arr(cond ? s["yes"] : s["no"]);
                    EvalResult nested = Block(branch, scope);
                    if (nested.Returned) return nested;
                    continue;
                }
                throw new Ja21Diagnostic("Unsupported statement " + op);
            }
            return new EvalResult(false, null);
        }

        private object Eval(Dictionary<string, object> node, Dictionary<string, object> scope)
        {
            Tick();
            string op = Str(node["op"]);
            if (op == "literal") return node.ContainsKey("value") ? node["value"] : null;
            if (op == "name")
            {
                string n = Str(node["name"]); object v;
                if (!scope.TryGetValue(n, out v)) throw new Ja21Diagnostic("Unknown field " + n);
                return v;
            }
            if (op == "get")
            {
                Dictionary<string, object> obj = Dict(Eval(Dict(node["object"]), scope));
                string key = Str(node["key"]); object v;
                if (IsForbidden(key) || !obj.TryGetValue(key, out v)) throw new Ja21Diagnostic("Unknown field " + key);
                return v;
            }
            if (op == "record")
            {
                Dictionary<string, object> v = new Dictionary<string, object>(StringComparer.Ordinal);
                object[] fields = Arr(node["fields"]); int i;
                for (i = 0; i < fields.Length; i++)
                {
                    Dictionary<string, object> f = Dict(fields[i]);
                    v[Str(f["name"])] = Eval(Dict(f["value"]), scope);
                }
                return Check(v, Str(node["name"]));
            }
            if (op == "call")
            {
                string name = CallName(Dict(node["callee"]));
                object[] argNodes = Arr(node["args"]); object[] args = new object[argNodes.Length]; int i;
                for (i = 0; i < argNodes.Length; i++) args[i] = Eval(Dict(argNodes[i]), scope);
                if (name == "URL.analyze") return UrlAnalyze(Str(args[0]));
                if (name == "URL.encode") return Uri.EscapeDataString(Str(args[0]));
                return Invoke(name, args);
            }
            if (op == "unary")
            {
                string kind = Str(node["kind"]); object v = Eval(Dict(node["value"]), scope);
                if (kind == "not") return !(bool)Check(v, "Flag");
                if (kind == "-") return -ToLong(Check(v, "Count"));
                throw new Ja21Diagnostic("Unsupported unary operation " + kind);
            }
            if (op == "binary")
            {
                object a = Eval(Dict(node["left"]), scope); string kind = Str(node["kind"]);
                if (kind == "and")
                {
                    bool av = (bool)Check(a, "Flag");
                    return av && (bool)Check(Eval(Dict(node["right"]), scope), "Flag");
                }
                if (kind == "or")
                {
                    bool av = (bool)Check(a, "Flag");
                    return av || (bool)Check(Eval(Dict(node["right"]), scope), "Flag");
                }
                object b = Eval(Dict(node["right"]), scope);
                if (kind == "==") return StrictEqual(a, b);
                if (kind == "!=") return !StrictEqual(a, b);
                if (a is string && b is string)
                {
                    string sa = (string)a, sb = (string)b;
                    if (kind == "+") return sa + sb;
                    int c = String.CompareOrdinal(sa, sb);
                    if (kind == "<") return c < 0; if (kind == ">") return c > 0; if (kind == "<=") return c <= 0; if (kind == ">=") return c >= 0;
                }
                if (IsInteger(a) && IsInteger(b))
                {
                    long x = ToLong(a), y = ToLong(b);
                    if (kind == "+") return x + y;
                    if (kind == "-") return x - y;
                    if (kind == "*") return x * y;
                    if (kind == "/") { if (y == 0) throw new Ja21Diagnostic("Division by zero"); if (x % y != 0) throw new Ja21Diagnostic("Count division produced a non-integer"); return x / y; }
                    if (kind == "<") return x < y; if (kind == ">") return x > y; if (kind == "<=") return x <= y; if (kind == ">=") return x >= y;
                }
                throw new Ja21Diagnostic("Operand type mismatch");
            }
            throw new Ja21Diagnostic("Unsupported operation " + op);
        }

        private object Check(object value, string type)
        {
            if (type == "Text")
            {
                string s = value as string;
                if (s == null || s.Length > 16384) throw new Ja21Diagnostic("Expected bounded Text");
                return s;
            }
            if (type == "Count")
            {
                if (!IsInteger(value)) throw new Ja21Diagnostic("Expected Count");
                long n = ToLong(value);
                if (n < 0 || n > 9007199254740991L) throw new Ja21Diagnostic("Expected Count");
                if (n <= Int32.MaxValue) return (int)n;
                return n;
            }
            if (type == "Flag")
            {
                if (!(value is bool)) throw new Ja21Diagnostic("Expected Flag");
                return value;
            }
            object fieldsObj;
            if (!_records.TryGetValue(type, out fieldsObj)) throw new Ja21Diagnostic("Expected " + type);
            Dictionary<string, object> d = value as Dictionary<string, object>;
            object[] fields = Arr(fieldsObj);
            if (d == null || d.Count != fields.Length) throw new Ja21Diagnostic("Expected " + type);
            int i;
            for (i = 0; i < fields.Length; i++)
            {
                Dictionary<string, object> f = Dict(fields[i]); string n = Str(f["name"]); object v;
                if (IsForbidden(n) || !d.TryGetValue(n, out v)) throw new Ja21Diagnostic("Expected " + type);
                Check(v, Str(f["type"]));
            }
            return d;
        }

        private static Dictionary<string, object> UrlAnalyze(string input)
        {
            if (input.Length > 16384) throw new Ja21Diagnostic("Address exceeds limit");
            string text = input.Trim();
            bool local = Regex.IsMatch(text, @"^(?:localhost|\[[0-9a-f:]+\]|\d{1,3}(?:\.\d{1,3}){3})(?::\d+)?(?:[/?#].*)?$", RegexOptions.IgnoreCase);
            bool host = Regex.IsMatch(text, @"^[^\s/:?#]+\.[^\s/:?#]+(?::\d+)?(?:[/?#].*)?$");
            bool explicitScheme = !local && !host && Regex.IsMatch(text, @"^[a-z][a-z\d+.-]*:", RegexOptions.IgnoreCase);
            string scheme = ""; bool valid = false; Uri uri;
            if (Uri.TryCreate(text, UriKind.Absolute, out uri))
            {
                scheme = uri.Scheme.ToLowerInvariant();
                valid = (scheme == "https" || scheme == "http") && !String.IsNullOrEmpty(uri.Host) && String.IsNullOrEmpty(uri.UserInfo);
            }
            if (explicitScheme)
            {
                int i; for (i = 0; i < text.Length; i++) if (text[i] < 32 || text[i] == 127) valid = false;
            }
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["text"] = text; d["scheme"] = scheme; d["explicit"] = explicitScheme; d["local"] = local; d["host"] = host; d["valid"] = valid;
            return d;
        }

        private string CallName(Dictionary<string, object> node)
        {
            string op = Str(node["op"]);
            if (op == "name") return Str(node["name"]);
            if (op == "get") return CallName(Dict(node["object"])) + "." + Str(node["key"]);
            throw new Ja21Diagnostic("Only named calls are supported");
        }

        private static bool StrictEqual(object a, object b)
        {
            if (a == null || b == null) return a == b;
            if (IsInteger(a) && IsInteger(b)) return ToLong(a) == ToLong(b);
            if (a.GetType() != b.GetType()) return false;
            return Object.Equals(a, b);
        }
        private static bool IsInteger(object v) { return v is Byte || v is SByte || v is Int16 || v is UInt16 || v is Int32 || v is UInt32 || v is Int64; }
        private static long ToLong(object v) { return Convert.ToInt64(v); }
        private static bool IsForbidden(string s) { return s == "__proto__" || s == "prototype" || s == "constructor"; }
        private static string Str(object o) { if (o == null) return ""; return Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture); }
        private static Dictionary<string, object> Dict(object o) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d == null) throw new Ja21Diagnostic("Expected object"); return d; }
        private static object[] Arr(object o) { object[] a = o as object[]; if (a == null) throw new Ja21Diagnostic("Expected array"); return a; }

        private sealed class EvalResult
        {
            public readonly bool Returned; public readonly object Value;
            public EvalResult(bool returned, object value) { Returned = returned; Value = value; }
        }
    }

    public static class RuntimeSeal
    {
        public static void Verify(string root)
        {
            string path = System.IO.Path.Combine(root, "engine", "ja21", "runtime.integrity.json");
            JavaScriptSerializer js = new JavaScriptSerializer();
            Dictionary<string, object> seal = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
            object sealVersion;
            if (!seal.TryGetValue("version", out sealVersion) || !String.Equals(Convert.ToString(sealVersion), BrowserVersion.Full, StringComparison.Ordinal))
                throw new Exception("Runtime integrity seal version does not match this presenter build.");
            object[] files = (object[])seal["files"];
            if (files.Length == 0) throw new Exception("Runtime integrity seal contains no files.");
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i;
            for (i = 0; i < files.Length; i++)
            {
                Dictionary<string, object> e = (Dictionary<string, object>)files[i];
                string rel = Convert.ToString(e["path"]);
                if (!seen.Add(rel)) throw new Exception("Duplicate sealed runtime path: " + rel);
                string full = Contained(root, rel);
                if (!File.Exists(full)) throw new Exception("Missing sealed runtime file: " + rel);
                string actual = Sha256(File.ReadAllBytes(full));
                if (!String.Equals(actual, RequireSha256(Convert.ToString(e["sha256"]), "runtime file digest"), StringComparison.Ordinal)) throw new Exception("Runtime integrity failure: " + rel);
            }
        }
        /// <summary>Resolve a manifest-relative path and prove it stays inside the package root.
        /// Rejecting "..", ":" and rooted spellings is necessary but not sufficient, so the
        /// resolved canonical path is compared against the canonical root as well.</summary>
        public static string Contained(string root, string rel)
        {
            if (String.IsNullOrEmpty(rel)) throw new Exception("Empty sealed path.");
            if (System.IO.Path.IsPathRooted(rel) || rel.IndexOf("..", StringComparison.Ordinal) >= 0 || rel.IndexOf(':') >= 0 ||
                rel.StartsWith("/", StringComparison.Ordinal) || rel.StartsWith("\\", StringComparison.Ordinal))
                throw new Exception("Unsafe sealed path: " + rel);
            string baseDir = System.IO.Path.GetFullPath(root);
            if (!baseDir.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                baseDir += System.IO.Path.DirectorySeparatorChar;
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Sealed path escapes the package root: " + rel);
            RejectReparsePoints(baseDir, full);
            return full;
        }

        // A canonical string prefix does not confine a junction or symbolic link. Refuse
        // existing reparse points anywhere from the boundary through the target before I/O.
        public static void RejectReparsePoints(string boundary, string target)
        {
            string root = System.IO.Path.GetFullPath(boundary).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            string full = System.IO.Path.GetFullPath(target);
            string prefix = root + System.IO.Path.DirectorySeparatorChar;
            if (!String.Equals(root, full.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Path escapes its filesystem boundary.");
            string probe = full;
            while (probe != null && probe.Length >= root.Length)
            {
                try
                {
                    if ((File.GetAttributes(probe) & FileAttributes.ReparsePoint) != 0)
                        throw new Exception("Reparse points are outside the JA21 filesystem boundary: " + probe);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (String.Equals(probe.TrimEnd(System.IO.Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) break;
                probe = System.IO.Path.GetDirectoryName(probe.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            }
        }

        /// <summary>Reject anything that is not a 64-character lowercase hex digest, so a
        /// malformed seal can never be compared loosely or used to build a path.</summary>
        public static string RequireSha256(string value, string what)
        {
            if (value == null || value.Length != 64) throw new Exception("Malformed " + what + " (expected a 64-character SHA-256).");
            int i;
            for (i = 0; i < 64; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex) throw new Exception("Malformed " + what + " (expected lowercase hexadecimal).");
            }
            return value;
        }

        public static string Sha256(byte[] data)
        {
            using (SHA256 s = SHA256.Create())
            {
                byte[] h = s.ComputeHash(data); StringBuilder b = new StringBuilder(h.Length * 2); int i;
                for (i = 0; i < h.Length; i++) b.Append(h[i].ToString("x2")); return b.ToString();
            }
        }
    }

    internal sealed class BrowserTab
    {
        public int Id;
        public bool Private;
        public bool Loading;
        public string LogicalUrl;
        public bool ShowingHome;
        public string Title;
        public Grid Surface;
        public Grid PageHost;
        public Button Pill;
        public TextBlock PillText;
        public readonly List<string> History = new List<string>();
        public int HistoryIndex = -1;
        public int Generation;
        /// <summary>Id of the VEC electron bound to this tab, or null when the substrate is
        /// not sealing. A tab is never blocked by the absence of an electron.</summary>
        public string VecId;
        public ILiveWebSurface LiveWeb;
        public bool PreferLiveWeb = true;
        public string RenderMode = "NATIVE";
        public bool CanGoBack { get { return RenderMode == "LIVE" && LiveWeb != null ? LiveWeb.CanGoBack : HistoryIndex > 0; } }
        public bool CanGoForward { get { return RenderMode == "LIVE" && LiveWeb != null ? LiveWeb.CanGoForward : HistoryIndex >= 0 && HistoryIndex + 1 < History.Count; } }
    }

    public sealed class DfSmallHelper
    {
        private readonly string _root;
        private readonly string _helperRoot;
        private readonly Dictionary<string, object> _descriptor;
        private readonly string _staticStatus;

        public DfSmallHelper(string root)
        {
            _root = root;
            _helperRoot = System.IO.Path.Combine(root, "helper", "DF_Small");
            JavaScriptSerializer js = new JavaScriptSerializer(); js.MaxJsonLength = Int32.MaxValue;
            string desc = System.IO.Path.Combine(_helperRoot, "node", "NODE_DESCRIPTOR.json");
            if (!File.Exists(desc)) throw new Exception("DF_Small descriptor is missing.");
            _descriptor = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(desc, Encoding.UTF8));
            _staticStatus = VerifyPayloadDigest();
        }

        public string Summary
        {
            get
            {
                Dictionary<string, object> bounds = (Dictionary<string, object>)_descriptor["bounds"];
                string node = Convert.ToString(_descriptor["node_id"]);
                string network = Convert.ToString(_descriptor["network_policy"]);
                return node + " · " + Convert.ToString(bounds["cli_step_budget"]) + "-step budget · network " + network + " · " + _staticStatus;
            }
        }

        public string VerifyPayloadDigest()
        {
            string digestPath = System.IO.Path.Combine(_helperRoot, "node", "PAYLOAD_DIGEST.json");
            JavaScriptSerializer js = new JavaScriptSerializer(); js.MaxJsonLength = Int32.MaxValue;
            Dictionary<string, object> digest = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(digestPath, Encoding.UTF8));
            string vmRoot = System.IO.Path.Combine(_helperRoot, "vm", "BOTTLE_ROCKET_3.0.0_MODEL_OPERATIONAL_110K");
            object[] files = (object[])digest["files"];
            int i;
            for (i = 0; i < files.Length; i++)
            {
                Dictionary<string, object> e = (Dictionary<string, object>)files[i];
                string rel = Convert.ToString(e["path"]);
                string full = RuntimeSeal.Contained(vmRoot, rel);
                if (!File.Exists(full)) throw new Exception("DF_Small payload file missing: " + rel);
                string actual = RuntimeSeal.Sha256(File.ReadAllBytes(full));
                if (!String.Equals(actual, RuntimeSeal.RequireSha256(Convert.ToString(e["sha256"]), "DF_Small file digest"), StringComparison.Ordinal)) throw new Exception("DF_Small payload integrity failure: " + rel);
            }
            return "payload seal PASS";
        }

        public void RunNativeAsync(string operation, Action<string, bool> done)
        {
            string args;
            // Only these two fixed, package-internal command lines are ever constructed; no
            // caller-supplied text reaches the shell.
            if (operation == "verify") args = "/d /s /c call \"" + System.IO.Path.Combine(_helperRoot, "VERIFY.cmd") + "\"";
            else if (operation == "pulse") args = "/d /s /c call \"" + System.IO.Path.Combine(_helperRoot, "RUN.cmd") + "\" examples\\add42.mssl";
            else { done("That helper operation is not available.", false); return; }
            if (!File.Exists(System.IO.Path.Combine(_helperRoot, operation == "verify" ? "VERIFY.cmd" : "RUN.cmd")))
            { done("The auxiliary DF_Small helper entry point is not present in this package.", false); return; }

            Task.Factory.StartNew(delegate
            {
                bool ok = false; string text = "";
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = Environment.GetEnvironmentVariable("ComSpec");
                    if (String.IsNullOrEmpty(psi.FileName)) psi.FileName = "cmd.exe";
                    psi.Arguments = args;
                    psi.WorkingDirectory = _helperRoot;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    // Both pipes are drained asynchronously. Reading one to the end before the
                    // other deadlocks as soon as the un-read pipe fills its buffer, which also
                    // made the 90-second timeout unreachable.
                    using (Process p = Process.Start(psi))
                    {
                        StringBuilder outBuf = new StringBuilder();
                        StringBuilder errBuf = new StringBuilder();
                        object gate = new object();
                        p.OutputDataReceived += delegate(object src, DataReceivedEventArgs ev) { if (ev.Data != null) lock (gate) AppendBounded(outBuf, ev.Data); };
                        p.ErrorDataReceived += delegate(object src, DataReceivedEventArgs ev) { if (ev.Data != null) lock (gate) AppendBounded(errBuf, ev.Data); };
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        if (!p.WaitForExit(90000))
                        {
                            try { p.Kill(); } catch { }
                            try { p.WaitForExit(5000); } catch { }
                            text = "DF_Small helper did not finish within 90 seconds and was stopped.";
                        }
                        else
                        {
                            ok = p.ExitCode == 0;
                            string stdout, stderr;
                            lock (gate) { stdout = outBuf.ToString(); stderr = errBuf.ToString(); }
                            text = (stdout + Environment.NewLine + stderr).Trim();
                            if (text.Length > 6000) text = text.Substring(text.Length - 6000);
                            if (String.IsNullOrWhiteSpace(text)) text = ok ? "DF_Small helper completed." : "DF_Small helper reported a non-zero exit code.";
                        }
                    }
                }
                catch (Exception ex) { text = "DF_Small helper: " + ex.Message; }
                done(text, ok);
            });
        }

        private static void AppendBounded(StringBuilder output, string line)
        {
            if (line.Length > 6000) line = line.Substring(line.Length - 6000);
            output.AppendLine(line);
            if (output.Length > 12000) output.Remove(0, output.Length - 12000);
        }
    }

    /// <summary>JA21 Optical Instrument Design System 2.2.
    ///
    /// The design system is intentionally expressed as semantic tokens rather than local
    /// literals.  Light, dark and Windows high-contrast variants share the same semantic
    /// names; controls therefore request meaning (glass, hairline, ink, accent) rather than
    /// a theme-specific color.  One accent family is used throughout the shell and every
    /// structural edge is intended to resolve to one physical device pixel.</summary>
    internal static class Ja21Design
    {
        public const string DesignSystemVersion = "2.8.0";

        private static string RequestedTheme
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("JA21_THEME");
                return String.IsNullOrWhiteSpace(v) ? "system" : v.Trim().ToLowerInvariant();
            }
        }

        public static bool HighContrast { get { return SystemParameters.HighContrast; } }
        public static bool Dark
        {
            get
            {
                string v = RequestedTheme;
                if (v == "dark") return true;
                if (v == "light") return false;
                return false; // system mode follows the calm light instrument surface unless explicitly overridden.
            }
        }

        // Windows Spectrum theme: Copilot-like iridescence expressed with Windows/Microsoft color families.
        // Neutral surfaces still dominate; spectrum color is reserved for hierarchy, focus and ambient depth.
        public static string WindowsBlue { get { return Dark ? "#FF60CDFF" : "#FF0078D4"; } }
        public static string WindowsCyan { get { return Dark ? "#FF5DE2E7" : "#FF00B7C3"; } }
        public static string WindowsGreen { get { return Dark ? "#FF6CCB5F" : "#FF107C10"; } }
        public static string WindowsGold { get { return Dark ? "#FFFFC83D" : "#FFFFB900"; } }
        public static string WindowsRed { get { return Dark ? "#FFFF7A67" : "#FFF25022"; } }
        public static string CanvasA { get { return HighContrast ? "#FF000000" : (Dark ? "#FF0B1016" : "#FFF4F8FC"); } }
        public static string CanvasB { get { return HighContrast ? "#FF000000" : (Dark ? "#FF121A23" : "#FFFCFDFF"); } }
        public static string Accent { get { return HighContrast ? "#FFFFFFFF" : WindowsBlue; } }
        public static string AccentSoft { get { return HighContrast ? "#44FFFFFF" : (Dark ? "#3860CDFF" : "#2A0078D4"); } }
        public static string AccentWhisper { get { return HighContrast ? "#24FFFFFF" : (Dark ? "#2060CDFF" : "#160078D4"); } }

        // Glass levels, lightest to most present.  High contrast deliberately becomes opaque.
        public static string Glass0 { get { return HighContrast ? "#FF000000" : (Dark ? "#2A151D25" : "#26FFFFFF"); } }
        public static string Glass1 { get { return HighContrast ? "#FF000000" : (Dark ? "#66182029" : "#5CFFFFFF"); } }
        public static string Glass2 { get { return HighContrast ? "#FF000000" : (Dark ? "#941B252F" : "#90FFFFFF"); } }
        public static string Glass3 { get { return HighContrast ? "#FF000000" : (Dark ? "#CC1D2731" : "#D4FFFFFF"); } }
        public static string Glass4 { get { return HighContrast ? "#FF000000" : (Dark ? "#F21E2934" : "#F2FFFFFF"); } }

        // Hairlines.  Presence is encoded mostly by alpha; geometry stays physically light.
        public static string Hair { get { return HighContrast ? "#FFFFFFFF" : (Dark ? "#42D6E1EA" : "#2493A9BD"); } }
        public static string HairSoft { get { return HighContrast ? "#FFFFFFFF" : (Dark ? "#2AD6E1EA" : "#1693A9BD"); } }
        public static string HairFocus { get { return HighContrast ? "#FFFFFFFF" : Accent; } }

        // Neutral information hierarchy.
        public static string Ink { get { return HighContrast ? "#FFFFFFFF" : (Dark ? "#FFE8EEF3" : "#FF172128"); } }
        public static string InkStrong { get { return HighContrast ? "#FFFFFFFF" : (Dark ? "#FFF7FAFC" : "#FF0E161C"); } }
        public static string InkMuted { get { return HighContrast ? "#FFFFFFFF" : (Dark ? "#FFA8B5C0" : "#FF5F6D77"); } }
        public static string InkFaint { get { return HighContrast ? "#FFFFFFFF" : (Dark ? "#FF7E8C98" : "#FF82909A"); } }
        public static string InkAccent { get { return HighContrast ? "#FFFFFFFF" : (Dark ? "#FF60CDFF" : "#FF005FB8"); } }

        // Semantic states stay emotionally neutral.
        public static string Settled { get { return HighContrast ? "#FFFFFFFF" : (Dark ? "#FF72B8A5" : "#FF3D7768"); } }
        public static string Attention { get { return HighContrast ? "#FFFFFFFF" : (Dark ? "#FFD0AE83" : "#FF8A6A4A"); } }

        // Canonical geometry.
        public const double RadiusPill = 999;
        public const double RadiusControl = 14;
        public const double RadiusPanel = 22;
        public const double RadiusCard = 28;
        public const double Hairline = 1.0; // fallback DIP before a visual is connected to a PresentationSource.

        // Named whitespace scale.
        public const double Space2 = 2;
        public const double Space4 = 4;
        public const double Space6 = 6;
        public const double Space8 = 8;
        public const double Space12 = 12;
        public const double Space16 = 16;
        public const double Space24 = 24;
        public const double Space32 = 32;
        public const double Space48 = 48;
        public const double Space64 = 64;
        public const double Space96 = 96;
        public const double GutterX = 24;
        public const double GutterY = 16;
        public const double Gap = 8;

        // Optical planes.  Website/content stays full-bleed beneath WPF composition chrome.
        public const int ZAmbient = 0;
        public const int ZContent = 100;
        public const int ZChrome = 300;
        public const int ZFloating = 400;
        public const int ZDrawer = 500;
        public const int ZPalette = 600;
        public const int ZCritical = 900;

        public static bool ConsoleMode
        {
            get { return String.Equals(Environment.GetEnvironmentVariable("JA21_CONSOLE_MODE"), "1", StringComparison.Ordinal); }
        }

        public static bool ReducedMotion
        {
            get { return ConsoleMode || String.Equals(Environment.GetEnvironmentVariable("JA21_REDUCED_MOTION"), "1", StringComparison.Ordinal); }
        }

        /// <summary>Return one physical device pixel expressed in WPF DIPs for the supplied
        /// visual.  Before the visual is connected to a source, 1 DIP is used as the safe
        /// fallback.  Horizontal/vertical DPI asymmetry is handled conservatively by using
        /// the larger device scale.</summary>
        public static double DeviceHairline(Visual visual)
        {
            try
            {
                PresentationSource src = visual == null ? null : PresentationSource.FromVisual(visual);
                if (src == null || src.CompositionTarget == null) return 1.0;
                Matrix m = src.CompositionTarget.TransformToDevice;
                double scale = Math.Max(Math.Abs(m.M11), Math.Abs(m.M22));
                return scale <= 0.0 ? 1.0 : 1.0 / scale;
            }
            catch { return 1.0; }
        }

        public static Brush WindowsSpectrum(byte alpha)
        {
            if (HighContrast) return new SolidColorBrush(Colors.White);
            LinearGradientBrush b = new LinearGradientBrush();
            b.StartPoint = new Point(0, 0.5); b.EndPoint = new Point(1, 0.5);
            string[] colors = new string[] { WindowsBlue, WindowsCyan, WindowsGreen, WindowsGold, WindowsRed };
            double[] offsets = new double[] { 0.0, 0.24, 0.48, 0.73, 1.0 };
            for (int i = 0; i < colors.Length; i++)
            {
                Color c = ParseColor(colors[i]); c.A = alpha;
                b.GradientStops.Add(new GradientStop(c, offsets[i]));
            }
            if (b.CanFreeze) b.Freeze();
            return b;
        }

        public static Color ParseColor(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
    }

    /// <summary>Calm motion.
    ///
    /// Two vocabularies only, each with a meaning: cubic-bezier for anything that appears or
    /// resolves (opacity, reveal), and a critically damped spring for anything that follows
    /// the pointer or a selection. Nothing animates on a timer and nothing animates without a
    /// cause, which is the calm-technology rule this shell is held to.</summary>
    internal static class Ja21Motion
    {
        // cubic-bezier(0.22, 1.00, 0.36, 1.00) - a decisive settle with no overshoot.
        public static IEasingFunction Settle() { return new BezierEase(0.22, 1.00, 0.36, 1.00); }
        // cubic-bezier(0.40, 0.00, 0.20, 1.00) - symmetric, for reversible reveals.
        public static IEasingFunction Reveal() { return new BezierEase(0.40, 0.00, 0.20, 1.00); }

        /// <summary>Identity pass-through, kept so call sites read as "ease with this curve".</summary>
        public static IEasingFunction Ease(IEasingFunction f) { return f; }

        public static void Fade(UIElement target, double to, int ms, IEasingFunction ease)
        {
            if (Ja21Design.ReducedMotion)
            {
                target.BeginAnimation(UIElement.OpacityProperty, null);
                target.Opacity = to;
                return;
            }
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = ease;
            target.BeginAnimation(UIElement.OpacityProperty, a);
        }
    }

    /// <summary>A cubic-bezier easing function driven by its four control coordinates, so the
    /// curve named in the design system and the curve on screen are literally the same numbers.
    ///
    /// WPF's KeySpline would express the same curve, but its evaluator (GetSplineProgress) is
    /// internal to PresentationCore, so the inversion is done here: solve x(g) = t for the
    /// bezier parameter g by Newton-Raphson, then return y(g). Six iterations is well past
    /// visual convergence for control points in [0,1], and the slope guard keeps a flat segment
    /// from dividing by zero.</summary>
    internal sealed class BezierEase : EasingFunctionBase
    {
        private readonly double _x1, _y1, _x2, _y2;

        public BezierEase(double x1, double y1, double x2, double y2)
        {
            _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2;
            // EasingFunctionBase would otherwise evaluate the mirror of the authored curve.
            // Fully qualified because EasingMode is also the name of the inherited property.
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn;
        }

        // B(t) = 3(1-t)^2*t*a + 3(1-t)*t^2*b + t^3, for control coordinates a and b.
        private static double Curve(double a, double b, double t)
        {
            double u = 1.0 - t;
            return (3.0 * u * u * t * a) + (3.0 * u * t * t * b) + (t * t * t);
        }

        // dB/dt = 3a(1-t)^2 + 6t(1-t)(b-a) + 3t^2(1-b)
        private static double Slope(double a, double b, double t)
        {
            double u = 1.0 - t;
            return (3.0 * u * u * a) + (6.0 * u * t * (b - a)) + (3.0 * t * t * (1.0 - b));
        }

        protected override double EaseInCore(double t)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            double g = t;
            int i;
            for (i = 0; i < 6; i++)
            {
                double dx = Curve(_x1, _x2, g) - t;
                if (Math.Abs(dx) < 1e-6) break;
                double d = Slope(_x1, _x2, g);
                if (Math.Abs(d) < 1e-9) break;
                g -= dx / d;
                if (g < 0.0) g = 0.0;
                else if (g > 1.0) g = 1.0;
            }
            return Curve(_y1, _y2, g);
        }

        protected override Freezable CreateInstanceCore() { return new BezierEase(_x1, _y1, _x2, _y2); }
    }

    internal sealed class SpringScalar
    {
        private readonly Action<double> _setter;
        private double _value;
        private double _target;
        private double _velocity;
        private bool _running;
        private long _lastTicks;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private readonly bool _snapToPixel;
        public SpringScalar(Action<double> setter) : this(setter, false) { }
        /// <summary>snapToPixel rounds every emitted value to a whole device pixel. Scale
        /// transforms must NOT snap (they are sub-unit); anything that moves a pixel position
        /// must, or the edge of a hairline shimmers as it travels.</summary>
        public SpringScalar(Action<double> setter, bool snapToPixel) { _setter = setter; _snapToPixel = snapToPixel; }
        private void Emit(double v) { _setter(_snapToPixel ? Math.Round(v) : v); }
        public void Snap(double value) { _value = value; _target = value; _velocity = 0; Emit(value); }
        public void AnimateTo(double target)
        {
            if (Ja21Design.ReducedMotion) { Snap(target); return; }
            _target = target;
            if (_running) return;
            _running = true; _lastTicks = _clock.ElapsedTicks;
            CompositionTarget.Rendering += OnFrame;
        }
        private void OnFrame(object sender, EventArgs e)
        {
            long now = _clock.ElapsedTicks;
            double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = now;
            if (dt <= 0) return;
            if (dt > 0.033) dt = 0.033;
            const double stiffness = 420.0;
            const double damping = 40.0;
            double acceleration = stiffness * (_target - _value) - damping * _velocity;
            _velocity += acceleration * dt;
            _value += _velocity * dt;
            if (Math.Abs(_target - _value) < 0.18 && Math.Abs(_velocity) < 0.18)
            {
                _value = _target; _velocity = 0; Emit(_value);
                CompositionTarget.Rendering -= OnFrame; _running = false; return;
            }
            Emit(_value);
        }
    }

    internal static class NativeGlass
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out CursorPoint point);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorPoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfo
        {
            public int cbSize;
            public int left, top, right, bottom;
            public int workLeft, workTop, workRight, workBottom;
            public uint flags;
        }

        public static Point CursorIn(Window window)
        {
            CursorPoint point;
            if (!GetCursorPos(out point)) return new Point(0, 0);
            try { return window.PointFromScreen(new Point(point.X, point.Y)); }
            catch { return new Point(0, 0); }
        }

        public static bool LeftButtonDown()
        {
            try { return (GetAsyncKeyState(0x01) & 0x8000) != 0; }
            catch { return Mouse.LeftButton == MouseButtonState.Pressed; }
        }

        public static void Apply(Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                int corner = 2; DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
                int backdrop = 2; DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));
            }
            catch { }
        }

        public static Rect MonitorBounds(Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                IntPtr monitor = MonitorFromWindow(hwnd, 2);
                MonitorInfo info = new MonitorInfo(); info.cbSize = Marshal.SizeOf(typeof(MonitorInfo));
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                    return new Rect(info.left, info.top, info.right - info.left, info.bottom - info.top);
            }
            catch { }
            return new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        }
    }

    /// <summary>Contextual drawer hosted inside the JA21 optical plane rather than in a
    /// second top-level Window.  WebView2CompositionControl removes the WPF airspace conflict,
    /// so contextual controls can remain visually and semantically inside the omni window.</summary>
    public sealed class ContextDrawerWindow
    {
        private readonly BrowserWindow _owner;
        private readonly Border _shell;
        private readonly TranslateTransform _translate;
        private readonly SpringScalar _slide;
        private readonly TextBlock _helperState;
        private readonly TextBlock _detail;
        private TextBlock _vecState;
        private bool _open;

        public ContextDrawerWindow(BrowserWindow owner, DfSmallHelper helper, VecSubstrate vec)
        {
            _owner = owner;
            _shell = new Border();
            _shell.Width = 408;
            _shell.MinHeight = 420;
            _shell.MaxHeight = 760;
            _shell.HorizontalAlignment = HorizontalAlignment.Right;
            _shell.VerticalAlignment = VerticalAlignment.Stretch;
            _shell.Margin = new Thickness(0, 88, Ja21Design.Space24, Ja21Design.Space24);
            _shell.CornerRadius = new CornerRadius(Ja21Design.RadiusPanel);
            _shell.BorderThickness = new Thickness(Ja21Design.Hairline);
            _shell.BorderBrush = BrowserWindow.Brush(Ja21Design.Hair);
            _shell.Background = BrowserWindow.Brush(Ja21Design.Glass4);
            _shell.Padding = new Thickness(24, 22, 24, 20);
            _shell.SnapsToDevicePixels = true;
            _shell.Effect = new DropShadowEffect { BlurRadius = 52, ShadowDepth = 6, Opacity = .10, Color = Colors.SlateGray };
            _shell.Visibility = Visibility.Collapsed;
            _shell.Opacity = 0;
            _shell.IsHitTestVisible = false;
            _translate = new TranslateTransform(36, 0);
            _shell.RenderTransform = _translate;
            _slide = new SpringScalar(delegate(double x) { _translate.X = x; }, true);
            _slide.Snap(36);

            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            StackPanel stack = new StackPanel();
            scroll.Content = stack;
            _shell.Child = scroll;

            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stack.Children.Add(header);
            StackPanel titleStack = new StackPanel();
            Grid.SetColumn(titleStack, 0); header.Children.Add(titleStack);
            titleStack.Children.Add(BrowserWindow.Label("CONTEXT", 10, FontWeights.Medium, Ja21Design.InkFaint, new Thickness(0, 0, 0, 5)));
            titleStack.Children.Add(BrowserWindow.Label("Instrument context", 21, FontWeights.Medium, Ja21Design.InkStrong, new Thickness(0, 0, 0, 4)));
            Button close = BrowserWindow.GhostButton("×", 36);
            close.Height = 32; close.Padding = new Thickness(0); close.FontSize = 16;
            close.ToolTip = "Close context  (Esc)";
            close.Click += delegate { CloseDrawer(); };
            Grid.SetColumn(close, 1); header.Children.Add(close);

            TextBlock lede = BrowserWindow.Label("State is disclosed only when requested. No panel action changes page content without an explicit command.", 12.5, FontWeights.Normal, Ja21Design.InkMuted, new Thickness(0, 5, 0, 20));
            lede.TextWrapping = TextWrapping.Wrap; lede.LineHeight = 19; stack.Children.Add(lede);

            StackPanel vecBody = new StackPanel();
            _vecState = BrowserWindow.Label(vec == null ? "The VEC1 electron substrate is not present in this package." : vec.LongStatus(),
                11.5, FontWeights.Normal, Ja21Design.InkMuted, new Thickness(0, 2, 0, 10));
            _vecState.TextWrapping = TextWrapping.Wrap; _vecState.LineHeight = 18; vecBody.Children.Add(_vecState);
            TextBlock vecBoundary = BrowserWindow.Label(
                "Readiness, renderer state and evidence availability remain separate from a fabric execution verdict. Strict four-node verification stays explicit and fail-closed.",
                11.5, FontWeights.Normal, Ja21Design.InkFaint, new Thickness(0, 0, 0, 8));
            vecBoundary.TextWrapping = TextWrapping.Wrap; vecBoundary.LineHeight = 17; vecBody.Children.Add(vecBoundary);
            stack.Children.Add(Disclosure("VEC1 substrate", vec == null ? "not bound" : vec.Verdict.ToLowerInvariant(), vecBody, vec != null && vec.Verdict != "BOUND"));

            StackPanel renderBody = new StackPanel();
            TextBlock renderText = BrowserWindow.Label(
                "LIVE uses the installed Microsoft Edge WebView2 Runtime through a WPF composition control. NATIVE preserves the sealed DF_Medium inspection path with site scripts isolated.",
                12, FontWeights.Normal, Ja21Design.InkMuted, new Thickness(0, 2, 0, 6));
            renderText.TextWrapping = TextWrapping.Wrap; renderText.LineHeight = 18; renderBody.Children.Add(renderText);
            stack.Children.Add(Disclosure("Rendering", "explicit mode", renderBody, false));

            StackPanel engineBody = new StackPanel();
            TextBlock engineText = BrowserWindow.Label(
                "JA21 mediates navigation and lifecycle. DF_Medium owns native HTML/CSS/layout semantics. The optical shell presents renderer output and never upgrades presentation state into a substrate verdict.",
                12, FontWeights.Normal, Ja21Design.InkMuted, new Thickness(0, 2, 0, 6));
            engineText.TextWrapping = TextWrapping.Wrap; engineText.LineHeight = 18; engineBody.Children.Add(engineText);
            _detail = BrowserWindow.Label("", 11.5, FontWeights.Normal, Ja21Design.InkMuted, new Thickness(0, 2, 0, 4));
            _detail.TextWrapping = TextWrapping.Wrap; _detail.LineHeight = 18; engineBody.Children.Add(_detail);
            stack.Children.Add(Disclosure("JA21 / DF_Medium", "bounded", engineBody, false));

            StackPanel helperBody = new StackPanel();
            TextBlock hs = BrowserWindow.Label(helper.Summary, 12, FontWeights.Normal, Ja21Design.InkMuted, new Thickness(0, 2, 0, 4));
            hs.TextWrapping = TextWrapping.Wrap; hs.LineHeight = 18; helperBody.Children.Add(hs);
            _helperState = BrowserWindow.Label("Static helper seal verified.", 11.5, FontWeights.Medium, Ja21Design.InkMuted, new Thickness(0, 2, 0, 10));
            _helperState.TextWrapping = TextWrapping.Wrap; helperBody.Children.Add(_helperState);
            WrapPanel helperActions = new WrapPanel();
            Button verify = BrowserWindow.GhostButton("Verify helper", 120); verify.Click += delegate { _owner.DispatchPublic("helper_verify", "", 0); };
            Button pulse = BrowserWindow.GhostButton("Run pulse", 100); pulse.Margin = new Thickness(8, 0, 0, 0); pulse.Click += delegate { _owner.DispatchPublic("helper_pulse", "", 0); };
            helperActions.Children.Add(verify); helperActions.Children.Add(pulse); helperBody.Children.Add(helperActions);
            stack.Children.Add(Disclosure("DF_Small helper", "offline", helperBody, false));

            Border rule = BrowserWindow.HairlineRule(true, Ja21Design.HairSoft);
            rule.Margin = new Thickness(0, 14, 0, 14); stack.Children.Add(rule);
            stack.Children.Add(DrawerAction("New private tab", "private_tab"));
            stack.Children.Add(DrawerAction("Portable desktop", "portable_desktop"));
            stack.Children.Add(DrawerAction("Portable documents", "portable_documents"));
            stack.Children.Add(DrawerAction("Downloads", "downloads"));
            stack.Children.Add(DrawerAction("Command palette  (Ctrl+K)", "command_palette"));
            stack.Children.Add(DrawerAction("Immersive mode  (F11)", "fullscreen"));
            stack.Children.Add(DrawerAction("Runtime details", "about"));

            _owner.AttachOverlay(_shell, Ja21Design.ZDrawer);
        }

        private Button DrawerAction(string label, string kind)
        {
            Button b = BrowserWindow.GhostButton(label, 0);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Padding = new Thickness(16, 0, 16, 0);
            b.Margin = new Thickness(0, 0, 0, 8);
            b.Click += delegate { _owner.DispatchPublic(kind, "", 0); };
            return b;
        }

        private Border Disclosure(string title, string state, UIElement body, bool startOpen)
        {
            Border card = new Border();
            card.CornerRadius = new CornerRadius(Ja21Design.RadiusControl);
            card.BorderThickness = new Thickness(Ja21Design.Hairline);
            card.BorderBrush = BrowserWindow.Brush(Ja21Design.HairSoft);
            card.Background = BrowserWindow.Brush(Ja21Design.Glass1);
            card.Padding = new Thickness(16, 13, 16, 13);
            card.Margin = new Thickness(0, 0, 0, 10);
            card.SnapsToDevicePixels = true;
            card.Loaded += delegate { card.BorderThickness = new Thickness(Ja21Design.DeviceHairline(card)); };
            StackPanel outer = new StackPanel(); card.Child = outer;

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Cursor = Cursors.Hand; head.Background = Brushes.Transparent;
            TextBlock t = BrowserWindow.Label(title, 12.5, FontWeights.Medium, Ja21Design.Ink, new Thickness(0));
            Grid.SetColumn(t, 0); head.Children.Add(t);
            TextBlock st = BrowserWindow.Label(state, 10.5, FontWeights.Normal, Ja21Design.InkFaint, new Thickness(8, 1, 8, 0));
            Grid.SetColumn(st, 1); head.Children.Add(st);
            TextBlock caret = BrowserWindow.Label("›", 14, FontWeights.Normal, Ja21Design.InkFaint, new Thickness(0));
            caret.RenderTransformOrigin = new Point(.5, .5);
            RotateTransform rot = new RotateTransform(startOpen ? 90 : 0); caret.RenderTransform = rot;
            Grid.SetColumn(caret, 2); head.Children.Add(caret); outer.Children.Add(head);

            Border hold = new Border(); hold.Child = body; hold.Margin = new Thickness(0, 10, 0, 0);
            hold.Visibility = startOpen ? Visibility.Visible : Visibility.Collapsed; hold.Opacity = startOpen ? 1 : 0;
            outer.Children.Add(hold);
            bool[] open = new bool[] { startOpen };
            head.MouseLeftButtonUp += delegate
            {
                open[0] = !open[0];
                if (Ja21Design.ReducedMotion)
                {
                    rot.Angle = open[0] ? 90 : 0;
                    hold.Opacity = open[0] ? 1 : 0;
                    hold.Visibility = open[0] ? Visibility.Visible : Visibility.Collapsed;
                    return;
                }
                DoubleAnimation r = new DoubleAnimation(open[0] ? 90 : 0, TimeSpan.FromMilliseconds(170));
                r.EasingFunction = Ja21Motion.Ease(Ja21Motion.Settle()); rot.BeginAnimation(RotateTransform.AngleProperty, r);
                if (open[0])
                {
                    hold.Visibility = Visibility.Visible;
                    Ja21Motion.Fade(hold, 1.0, 180, Ja21Motion.Settle());
                }
                else
                {
                    DoubleAnimation f = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(130));
                    f.EasingFunction = Ja21Motion.Ease(Ja21Motion.Reveal());
                    f.Completed += delegate { if (!open[0]) hold.Visibility = Visibility.Collapsed; };
                    hold.BeginAnimation(UIElement.OpacityProperty, f);
                }
            };
            return card;
        }

        public bool IsOpen { get { return _open; } }
        public void Toggle() { if (_open) CloseDrawer(); else OpenDrawer(); }

        public void OpenDrawer()
        {
            if (_open) return;
            _open = true; _shell.Visibility = Visibility.Visible; _shell.IsHitTestVisible = true;
            _slide.Snap(32); _slide.AnimateTo(0);
            Ja21Motion.Fade(_shell, 1.0, 190, Ja21Motion.Settle());
        }

        public void CloseDrawer()
        {
            if (!_open) return;
            _open = false; _shell.IsHitTestVisible = false; _slide.AnimateTo(32);
            if (Ja21Design.ReducedMotion)
            {
                _shell.Opacity = 0; _shell.Visibility = Visibility.Collapsed; return;
            }
            DoubleAnimation a = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(145));
            a.EasingFunction = Ja21Motion.Ease(Ja21Motion.Reveal());
            a.Completed += delegate { if (!_open) _shell.Visibility = Visibility.Collapsed; };
            _shell.BeginAnimation(UIElement.OpacityProperty, a);
        }

        public void Position(bool snap) { if (snap && _open) _slide.Snap(0); }
        public void Close() { _open = false; _owner.DetachOverlay(_shell); }
        public void SetHelperResult(string text, bool ok)
        {
            _helperState.Text = ok ? "The helper completed." : "The helper reported something to review.";
            _helperState.Foreground = BrowserWindow.Brush(ok ? Ja21Design.Settled : Ja21Design.Attention);
            _detail.Text = text;
        }
        public void SetDetail(string text) { _detail.Text = text; }
        public void SetSubstrate(string text) { if (_vecState != null) _vecState.Text = text; }
    }


    internal sealed class JaMirrorFetchResult
    {
        public Uri FinalUri; public string ContentType; public string Text; public byte[] Data; public int Bytes; public int Redirects; public string Status;
    }

    // 8.0.3 link-target repair. The 8.0.2 DF_Medium DLL returns attribute values without HTML
    // character-reference decoding, so href="/l/?uddg=...&amp;rut=..." was fetched literally and
    // DuckDuckGo answered HTTP 400. The presenter now decodes references before any URL reaches the
    // network, and unwraps DuckDuckGo's /l/ redirector (a script/meta-refresh page the isolated
    // engine cannot follow) to its declared http(s) destination. The engine-side fix is staged in
    // engine/df_medium/patches/8.0.3-html-character-references.patch for the next native rebuild.
    /// <summary>Address text hygiene and honest host display.
    ///
    /// Bidirectional and invisible formatting characters let an address read as one host in
    /// the bar while resolving to another; a Unicode host can also be a homograph of an ASCII
    /// one. Those characters are stripped before an address is used, and the trust line always
    /// shows the resolved ASCII (punycode) host beside the display host.</summary>
    internal static class JaAddressText
    {
        public static string Sanitize(string raw)
        {
            if (String.IsNullOrEmpty(raw)) return "";
            StringBuilder b = new StringBuilder(raw.Length);
            int i;
            for (i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c < (char)0x20 || c == (char)0x7F) continue;                 // C0 + DEL
                if (c >= (char)0x200B && c <= (char)0x200F) continue;            // ZWSP..RLM
                if (c >= (char)0x202A && c <= (char)0x202E) continue;            // bidi embedding/override
                if (c >= (char)0x2066 && c <= (char)0x2069) continue;            // bidi isolates
                if (c == (char)0xFEFF) continue;                                  // BOM / ZWNBSP
                b.Append(c);
            }
            return b.ToString().Trim();
        }

        /// <summary>Display host plus, when they differ, the ASCII form actually resolved.</summary>
        public static string HostLabel(Uri u)
        {
            if (u == null) return "";
            string display = u.Host;
            string ascii = u.DnsSafeHost;
            try { ascii = new System.Globalization.IdnMapping().GetAscii(u.DnsSafeHost); }
            catch { ascii = u.DnsSafeHost; }
            if (String.Equals(display, ascii, StringComparison.OrdinalIgnoreCase)) return display;
            return display + " (" + ascii + ")";
        }

        public static bool IsAscii(string host)
        {
            if (host == null) return true;
            int i;
            for (i = 0; i < host.Length; i++) if (host[i] > (char)0x7E) return false;
            return true;
        }
    }

    internal static class JaLinkTarget
    {
        public static string Resolve(string raw)
        {
            if (String.IsNullOrEmpty(raw)) return raw;
            string url = JaAddressText.Sanitize(raw);
            if (url.Length == 0) return "";
            if (url.IndexOf('&') >= 0) url = JaAddressText.Sanitize(WebUtility.HtmlDecode(url));
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return url;
            string host = u.Host.ToLowerInvariant();
            if ((host == "duckduckgo.com" || host.EndsWith(".duckduckgo.com", StringComparison.Ordinal)) && u.AbsolutePath == "/l/")
            {
                string target = QueryValue(u.Query, "uddg");
                Uri t;
                if (!String.IsNullOrEmpty(target) && Uri.TryCreate(target, UriKind.Absolute, out t) && (t.Scheme == Uri.UriSchemeHttp || t.Scheme == Uri.UriSchemeHttps) && String.IsNullOrEmpty(t.UserInfo)) return t.AbsoluteUri;
            }
            return url;
        }
        internal static void SelfTest()
        {
            Check("https://duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.google.com%2F&amp;rut=4a90d085", "https://www.google.com/");
            Check("https://html.duckduckgo.com/q?a=1&#38;b=2", "https://html.duckduckgo.com/q?a=1&b=2");
            Check("https://example.com/p?x=1&y=2", "https://example.com/p?x=1&y=2");
            Check("https://duckduckgo.com/l/?uddg=javascript%3Aalert(1)", "https://duckduckgo.com/l/?uddg=javascript%3Aalert(1)");
        }
        private static void Check(string input, string expected)
        {
            string got = Resolve(input);
            if (!String.Equals(got, expected, StringComparison.Ordinal)) throw new Exception("JA21 link-target self-test failed: " + input + " -> " + got);
        }
        internal static string QueryValue(string query, string name)
        {
            if (String.IsNullOrEmpty(query)) return null;
            foreach (string part in query.TrimStart('?').Split('&'))
            {
                int eq = part.IndexOf('=');
                string k = eq < 0 ? part : part.Substring(0, eq);
                if (String.Equals(Uri.UnescapeDataString(k.Replace('+', ' ')), name, StringComparison.Ordinal))
                    return eq < 0 ? "" : Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
            }
            return null;
        }
    }

    /// <summary>Address-boundary policy for the DF_Medium Web Device primitive.
    ///
    /// The 8.0.x fetch primitive would follow any http(s) address the engine produced,
    /// including loopback, link-local and RFC1918 addresses. A page can therefore point the
    /// isolated engine at a service that is only reachable from this machine or this LAN and
    /// read the answer back through the scene graph. 8.1.0 refuses those destinations by
    /// default, before a connection is opened and again after every redirect.</summary>
    internal static class JaAddressPolicy
    {
        public static bool AllowPrivateHosts
        {
            get { return !Ja21Design.ConsoleMode && String.Equals(Environment.GetEnvironmentVariable("JA21_ALLOW_PRIVATE_HOSTS"), "1", StringComparison.Ordinal); }
        }

        // Only exact, explicitly granted HTTP(S) origins. No wildcard or private-network bypass.
        private static bool IsConsoleOrigin(Uri target)
        {
            if (!Ja21Design.ConsoleMode || target == null || (target.Scheme != "http" && target.Scheme != "https") || !String.IsNullOrEmpty(target.UserInfo)) return false;
            string configured = Environment.GetEnvironmentVariable("JA21_CONSOLE_ORIGINS");
            if (String.IsNullOrWhiteSpace(configured)) return false;
            string[] origins = configured.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < origins.Length; i++)
            {
                Uri origin;
                if (!Uri.TryCreate(origins[i].Trim(), UriKind.Absolute, out origin)) continue;
                if ((origin.Scheme == "http" || origin.Scheme == "https") && String.IsNullOrEmpty(origin.UserInfo) && origin.AbsolutePath == "/" &&
                    String.IsNullOrEmpty(origin.Query) && String.IsNullOrEmpty(origin.Fragment) && !origin.Host.Contains("*") && origin.Host != "0.0.0.0" && origin.Host != "[::]" &&
                    origin.Scheme == target.Scheme && String.Equals(origin.DnsSafeHost, target.DnsSafeHost, StringComparison.OrdinalIgnoreCase) && origin.Port == target.Port) return true;
            }
            return false;
        }

        public static void Check(Uri u)
        {
            if (u == null) throw new Exception("No network target was supplied.");
            string scheme = u.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https") throw new Exception("The DF_Medium Web Device permits HTTP and HTTPS only.");
            if (!String.IsNullOrEmpty(u.UserInfo)) throw new Exception("Addresses that carry credentials are not opened.");
            if (u.Port <= 0 || u.Port > 65535) throw new Exception("That address has an out-of-range port.");
            if (DesktopSession.Enabled)
            {
                if (DesktopSession.MatchesOrigin(u)) return;
                throw new Exception("The embedded appliance permits only its configured server origin.");
            }
            if (IsConsoleOrigin(u)) return;
            if (AllowPrivateHosts) return;
            string host = u.DnsSafeHost;
            if (String.IsNullOrEmpty(host)) throw new Exception("That address has no host.");
            if (String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Addresses on this machine are outside the JA21 network boundary.");
            System.Net.IPAddress ip;
            if (System.Net.IPAddress.TryParse(host, out ip) && IsInternal(ip))
                throw new Exception("Addresses on this machine or this private network are outside the JA21 network boundary.");
        }

        /// <summary>Applied once the address has been resolved, so a public name that resolves
        /// to an internal address is refused as well.</summary>
        public static void CheckResolved(Uri u)
        {
            Check(u);
            if (DesktopSession.Enabled)
            {
                if (DesktopSession.MatchesOrigin(u)) return;
                throw new Exception("The embedded appliance permits only its configured server origin.");
            }
            if (IsConsoleOrigin(u)) return;
            if (AllowPrivateHosts) return;
            System.Net.IPAddress literal;
            if (System.Net.IPAddress.TryParse(u.DnsSafeHost, out literal)) return; // already checked
            System.Net.IPAddress[] addresses;
            try { addresses = System.Net.Dns.GetHostAddresses(u.DnsSafeHost); }
            catch (Exception ex) { throw new Exception("That host could not be resolved: " + ex.Message); }
            if (addresses == null || addresses.Length == 0) throw new Exception("That host could not be resolved.");
            int i;
            for (i = 0; i < addresses.Length; i++)
                if (IsInternal(addresses[i]))
                    throw new Exception("That host resolves to an address on this machine or this private network, which is outside the JA21 network boundary.");
        }

        public static bool IsInternal(System.Net.IPAddress ip)
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                if (b[0] == 10) return true;                                   // 10/8
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;       // 172.16/12
                if (b[0] == 192 && b[1] == 168) return true;                    // 192.168/16
                if (b[0] == 169 && b[1] == 254) return true;                    // 169.254/16 link-local (incl. cloud metadata)
                if (b[0] == 127) return true;                                   // loopback
                if (b[0] == 0) return true;                                     // this network
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;      // 100.64/10 CGNAT
                if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return true; // protocol assignments + documentation
                if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;   // benchmarking
                if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;     // documentation
                if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;      // documentation
                if (b[0] >= 224) return true;                                   // multicast + reserved
                return false;
            }
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
                byte[] b = ip.GetAddressBytes();
                bool firstTwelveZero = true;
                for (int i = 0; i < 12; i++) if (b[i] != 0) { firstTwelveZero = false; break; }
                // Includes :: (unspecified), ::1 and legacy IPv4-compatible spellings.
                if (firstTwelveZero) return true;
                if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return true; // documentation
                if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
                {
                    if (b[4] == 0 && b[5] == 1) return true;                 // local-use NAT64 prefix
                    bool nat64 = true;
                    for (int i = 4; i < 12; i++) if (b[i] != 0) { nat64 = false; break; }
                    if (nat64)
                    {
                        byte[] v4 = new byte[4]; Array.Copy(b, 12, v4, 0, 4);
                        return IsInternal(new System.Net.IPAddress(v4));
                    }
                }
                if ((b[0] & 0xFE) == 0xFC) return true;                         // fc00::/7 unique local
                if (ip.IsIPv4MappedToIPv6)
                {
                    byte[] v4 = new byte[4];
                    Array.Copy(b, 12, v4, 0, 4);
                    return IsInternal(new System.Net.IPAddress(v4));
                }
                return false;
            }
            return true;
        }

        /// <summary>file: is a development affordance, so it is confined to the package itself
        /// unless the operator opts out explicitly.</summary>
        public static string CheckFile(Uri u, string packageRoot)
        {
            if (u == null || !u.IsFile || u.IsUnc || !String.IsNullOrEmpty(u.Host)) throw new Exception("Only local file addresses are opened.");
            string path = u.LocalPath;
            if (String.Equals(Environment.GetEnvironmentVariable("JA21_ALLOW_ANY_FILE"), "1", StringComparison.Ordinal)) return path;
            string baseDir = System.IO.Path.GetFullPath(packageRoot);
            if (!baseDir.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                baseDir += System.IO.Path.DirectorySeparatorChar;
            string full;
            try { full = System.IO.Path.GetFullPath(path); }
            catch (Exception ex) { throw new Exception("That local path could not be resolved: " + ex.Message); }
            if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Local files are opened only from inside this package.");
            RuntimeSeal.RejectReparsePoints(baseDir, full);
            return full;
        }
    }

    internal sealed class JaMirrorNetworkClient
    {
        public const int MaxDocumentBytes = 8 * 1024 * 1024;
        public const int MaxRedirects = 8;
        public const int MaxNavigationSeconds = 45;
        private static string _packageRoot = "";
        private static bool _tlsConfigured;
        private static readonly object _tlsGate = new object();

        public static void Configure(string packageRoot) { _packageRoot = packageRoot; }
        internal static string PackageRoot { get { return _packageRoot; } }

        /// <summary>8.0.x OR-ed TLS 1.2 onto whatever the framework default was, which left
        /// SSL 3.0 / TLS 1.0 / TLS 1.1 enabled on older defaults. 8.1.0 assigns an explicit
        /// modern set instead, adding TLS 1.3 when the running framework knows it.</summary>
        private static void ConfigureTls()
        {
            lock (_tlsGate)
            {
                if (_tlsConfigured) return;
                SecurityProtocolType wanted = SecurityProtocolType.Tls12;
                try { wanted = wanted | (SecurityProtocolType)12288; } catch { }
                try { ServicePointManager.SecurityProtocol = wanted; }
                catch { try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; } catch { } }
                _tlsConfigured = true;
            }
        }

        public JaMirrorFetchResult Fetch(Uri initial) { return FetchInternal(initial, MaxDocumentBytes, true); }
        public byte[] FetchResource(Uri initial, int maxBytes) { return FetchInternal(initial, maxBytes, false).Data; }

        private JaMirrorFetchResult FetchInternal(Uri initial, int maxBytes, bool textDocument)
        {
            if (initial == null) throw new Exception("No network target was supplied.");
            if (maxBytes <= 0 || maxBytes > MaxDocumentBytes) throw new Exception("Invalid JA21 Web Device byte budget.");
            if (String.Equals(initial.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            {
                string path = JaAddressPolicy.CheckFile(initial, _packageRoot);
                if (!File.Exists(path)) throw new Exception("That local file is not present.");
                FileInfo fi = new FileInfo(path);
                if (fi.Length > maxBytes) throw new Exception("That resource is larger than the JA21 Web Device byte budget.");
                byte[] data;
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (MemoryStream buffer = new MemoryStream())
                {
                    byte[] chunk = new byte[32768]; int read;
                    while ((read = file.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (buffer.Length + read > maxBytes) throw new Exception("That resource is larger than the JA21 Web Device byte budget.");
                        buffer.Write(chunk, 0, read);
                    }
                    data = buffer.ToArray();
                }
                string text = textDocument ? Encoding.UTF8.GetString(data) : "";
                return new JaMirrorFetchResult { FinalUri = initial, ContentType = "text/html; charset=utf-8", Text = text, Data = data, Bytes = data.Length, Redirects = 0, Status = "200" };
            }

            ConfigureTls();
            Stopwatch deadline = Stopwatch.StartNew();
            Uri current = initial;
            int redirects = 0;
            bool startedSecure = String.Equals(initial.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

            while (true)
            {
                if (deadline.Elapsed.TotalSeconds > MaxNavigationSeconds)
                    throw new Exception("This address did not settle within " + MaxNavigationSeconds.ToString(CultureInfo.InvariantCulture) + " seconds.");
                JaAddressPolicy.Check(current);
                JaAddressPolicy.CheckResolved(current);
                if (startedSecure && String.Equals(current.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("This address redirected from HTTPS to plain HTTP, which the JA21 transport boundary does not follow.");

                int remainingMs = (int)Math.Max(1000, (MaxNavigationSeconds * 1000) - deadline.ElapsedMilliseconds);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(current);
                req.Method = "GET";
                req.AllowAutoRedirect = false;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                req.Timeout = Math.Min(20000, remainingMs);
                req.ReadWriteTimeout = Math.Min(20000, remainingMs);
                req.KeepAlive = false;
                req.CookieContainer = null;                 // no ambient cookie store in this profile
                req.UseDefaultCredentials = false;
                req.Credentials = null;
                req.Proxy = null;                           // do not route a confined target through an ambient proxy
                req.Referer = null;                          // no cross-origin referrer is emitted
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DFMedium-JA21-Web/8.1";
                req.Accept = textDocument ? "text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.2" : "*/*";

                HttpWebResponse raw;
                try { raw = (HttpWebResponse)req.GetResponse(); }
                catch (WebException wex)
                {
                    HttpWebResponse er = wex.Response as HttpWebResponse;
                    if (er == null) throw new Exception("This address could not be reached (" + current.Host + "): " + wex.Status + ".");
                    int ec = (int)er.StatusCode;
                    string ed = er.StatusDescription;
                    er.Close();
                    if (ec < 300 || ec > 399) throw new Exception("The server at " + current.Host + " answered HTTP " + ec.ToString(CultureInfo.InvariantCulture) + " " + ed + ".");
                    throw new Exception("The server at " + current.Host + " answered with a redirect that carried no usable response.");
                }

                using (HttpWebResponse resp = raw)
                {
                    int code = (int)resp.StatusCode;
                    if (code >= 300 && code <= 399)
                    {
                        string loc = resp.Headers[HttpResponseHeader.Location];
                        if (String.IsNullOrWhiteSpace(loc)) throw new Exception("The server sent a redirect without a destination.");
                        if (++redirects > MaxRedirects) throw new Exception("This address redirected more times than the JA21 redirect budget allows.");
                        Uri next;
                        if (!Uri.TryCreate(current, loc, out next)) throw new Exception("The server sent a redirect this address boundary cannot resolve.");
                        current = next;
                        continue;
                    }
                    string type = resp.ContentType == null ? "" : resp.ContentType;
                    using (Stream st = resp.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[32768];
                        int total = 0;
                        while (true)
                        {
                            if (deadline.Elapsed.TotalSeconds > MaxNavigationSeconds)
                                throw new Exception("This address did not finish transferring within the JA21 time budget.");
                            int r = st.Read(buf, 0, buf.Length);
                            if (r <= 0) break;
                            total += r;
                            if (total > maxBytes) throw new Exception("That resource is larger than the JA21 Web Device byte budget.");
                            ms.Write(buf, 0, r);
                        }
                        byte[] data = ms.ToArray();
                        Encoding enc = Encoding.UTF8;
                        Match charset = Regex.Match(type, "charset\\s*=\\s*['\"]?([^;'\"\\s]+)", RegexOptions.IgnoreCase);
                        if (charset.Success) { try { enc = Encoding.GetEncoding(charset.Groups[1].Value); } catch { } }
                        return new JaMirrorFetchResult { FinalUri = current, ContentType = type, Text = textDocument ? enc.GetString(data) : "", Data = data, Bytes = total, Redirects = redirects, Status = code.ToString(CultureInfo.InvariantCulture) };
                    }
                }
            }
        }
    }

    /// <summary>Per-document subresource budget.
    ///
    /// 8.0.x started one unbounded task per image scene record and allowed 12 MB each, so a
    /// document that declares hundreds of images could open hundreds of simultaneous
    /// connections. 8.1.0 gives each document a fixed number of slots, a total byte budget
    /// and a count cap, all of which fail quietly into a placeholder rather than an error.</summary>
    internal sealed class JaResourceBudget
    {
        public const int MaxConcurrent = 6;
        public const int MaxImages = 96;
        public const int MaxImageBytes = 8 * 1024 * 1024;
        public const long MaxTotalBytes = 48L * 1024L * 1024L;
        private readonly System.Threading.SemaphoreSlim _slots = new System.Threading.SemaphoreSlim(MaxConcurrent, MaxConcurrent);
        private long _spent;
        private int _started;
        public bool TryStart() { return System.Threading.Interlocked.Increment(ref _started) <= MaxImages; }
        public bool Reserve(int bytes)
        {
            long after = System.Threading.Interlocked.Add(ref _spent, bytes);
            return after <= MaxTotalBytes;
        }
        public IDisposable Slot() { _slots.Wait(); return new Release(_slots); }
        private sealed class Release : IDisposable
        {
            private System.Threading.SemaphoreSlim _s;
            public Release(System.Threading.SemaphoreSlim s) { _s = s; }
            public void Dispose() { if (_s != null) { try { _s.Release(); } catch { } _s = null; } }
        }
    }

    [StructLayout(LayoutKind.Sequential)] internal struct DfWebSceneItem { public UInt32 type; public float x,y,w,h,font_size; public UInt32 fg,bg,flags,text_id,url_id; }
    [StructLayout(LayoutKind.Sequential)] internal struct DfWebResourceItem { public UInt32 kind,url_id; }

    internal static class DfMediumNative
    {
        // Locking on a Type object is process-wide and shared with any other code that
        // happens to lock the same Type; the engine gets a private monitor instead.
        internal static readonly object EngineGate = new object();
        private const string Dll="dfmedium-ja21-web.dll";
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool SetDllDirectory(string path);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern UInt32 dfweb_abi();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern UInt32 dfweb_vm_core_abi();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int dfweb_init();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Ansi)] internal static extern int dfweb_begin(string baseUrl,UInt32 viewportW,UInt32 viewportH);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int dfweb_feed_document(byte[] data,UInt32 len);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Ansi)] internal static extern int dfweb_add_stylesheet(string url,byte[] data,UInt32 len);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int dfweb_commit();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern UInt32 dfweb_scene_count();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int dfweb_scene_get(UInt32 index,out DfWebSceneItem item);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern UInt32 dfweb_resource_count();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int dfweb_resource_get(UInt32 index,out DfWebResourceItem item);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int dfweb_string_get(UInt32 id,[Out] byte[] output,UInt32 cap);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int dfweb_title_get([Out] byte[] output,UInt32 cap);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int dfweb_stats_get(out UInt32 nodes,out UInt32 rules,out UInt32 scripts,out UInt32 vmCalls);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int dfweb_selftest();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int dfweb_profile_hash_get(StringBuilder output,UInt32 cap);
        internal static void Boot(string root){string native=System.IO.Path.Combine(root,"engine","df_medium","native","win-x64");if(!Directory.Exists(native))throw new Exception("DF_Medium native engine directory missing: "+native);if(!SetDllDirectory(native))throw new Exception("Unable to establish DF_Medium native DLL directory.");int r=dfweb_init();if(r!=0)throw new Exception("DF_Medium VM initialization failed: "+r);StringBuilder ph=new StringBuilder(80);if(dfweb_profile_hash_get(ph,(UInt32)ph.Capacity)!=0)throw new Exception("DF_Medium engine did not expose its JA21 profile binding.");string expected=RuntimeSeal.Sha256(File.ReadAllBytes(System.IO.Path.Combine(root,"source","web_engine.ja")));if(!String.Equals(ph.ToString(),expected,StringComparison.OrdinalIgnoreCase))throw new Exception("DF_Medium native engine / JA21 web-profile binding mismatch.");int st=dfweb_selftest();if(st!=0)throw new Exception("DF_Medium JA21 Web Engine self-test failed: "+st);InteropSelfTest();JaLinkTarget.SelfTest();}
        // dfweb_string_get / dfweb_title_get return the number of UTF-8 bytes written.
        // Retry a full buffer before decoding; ANSI StringBuilder marshaling corrupts
        // non-ASCII text and a clipped buffer can split a multi-byte UTF-8 character.
        internal const int StrCap=4096,StrCapMax=262144,TitleCap=1024,TitleCapMax=16384;
        internal static string Str(UInt32 id)
        {
            if(id==0)return "";
            int cap=StrCap;
            while(true)
            {
                byte[] b=new byte[cap];
                int n=dfweb_string_get(id,b,(UInt32)cap);
                if(n<0)return "";
                if(n<cap-1)return Encoding.UTF8.GetString(b,0,n);
                if(cap>=StrCapMax)throw new Exception("DF_Medium string exceeds the UTF-8 interop byte budget.");
                cap=Math.Min(StrCapMax,cap*4);
            }
        }
        internal static string Title()
        {
            int cap=TitleCap;
            while(true)
            {
                byte[] b=new byte[cap];
                int n=dfweb_title_get(b,(UInt32)cap);
                if(n<0)return "";
                if(n<cap-1)return Encoding.UTF8.GetString(b,0,n);
                if(cap>=TitleCapMax)throw new Exception("DF_Medium title exceeds the UTF-8 interop byte budget.");
                cap=Math.Min(TitleCapMax,cap*4);
            }
        }
        internal static void InteropSelfTest()
        {
            if(dfweb_abi()!=0x00080000u)throw new Exception("DF_Medium Web ABI mismatch.");
            if(Marshal.SizeOf(typeof(DfWebSceneItem))!=44)throw new Exception("DF_Medium scene ABI packing mismatch.");
            if(Marshal.SizeOf(typeof(DfWebResourceItem))!=8)throw new Exception("DF_Medium resource ABI packing mismatch.");
            string expectedTitle="JA21 Interop caf\u00e9 \u4e2d\u6587";
            string expectedBody="Visible JA21 caf\u00e9 \u4e2d\u6587";
            string html="<html><head><title>"+expectedTitle+"</title></head><body><h1>"+expectedBody+"</h1><img src='image.png'></body></html>";
            byte[] doc=Encoding.UTF8.GetBytes(html);
            int r=dfweb_begin("https://example.com/interop/index.html",800,600);
            if(r!=0)throw new Exception("DF_Medium interop begin failed: "+r);
            r=dfweb_feed_document(doc,(UInt32)doc.Length);
            if(r!=0)throw new Exception("DF_Medium interop feed failed: "+r);
            r=dfweb_commit();if(r!=0)throw new Exception("DF_Medium interop commit failed: "+r);
            if(Title()!=expectedTitle)throw new Exception("DF_Medium UTF-8 title interop failed.");
            bool text=false,image=false;UInt32 n=dfweb_scene_count();
            for(UInt32 i=0;i<n;i++)
            {
                DfWebSceneItem q;if(dfweb_scene_get(i,out q)!=0)continue;
                if(q.type==1&&Str(q.text_id).IndexOf(expectedBody,StringComparison.Ordinal)>=0)text=true;
                if(q.type==3&&Str(q.url_id).IndexOf("https://example.com/interop/image.png",StringComparison.OrdinalIgnoreCase)>=0)image=true;
            }
            if(!text)throw new Exception("DF_Medium UTF-8 scene text interop failed.");
            if(!image)throw new Exception("DF_Medium scene URL interop failed.");
        }
    }


    internal static class JaHtmlChrome
    {
        // Keep non-document chrome out of the scene, and move <style> into the CSS channel
        // so the web host's designated layout is applied instead of painted as text.
        public static string Prepare(string html, out int selectsCollapsed, out int scriptsRemoved, out string[] inlineStyles)
        {
            int selectCount = 0;
            scriptsRemoved = 0;
            selectsCollapsed = 0;
            System.Collections.Generic.List<string> styles = new System.Collections.Generic.List<string>();
            if (String.IsNullOrEmpty(html)) { inlineStyles = new string[0]; return html ?? ""; }
            string s = html;
            s = Regex.Replace(s, @"<style\b[^>]*>([\s\S]*?)</style>", delegate(Match m) {
                string css = m.Groups[1].Value; if (!String.IsNullOrWhiteSpace(css)) styles.Add(css);
                return "";
            }, RegexOptions.IgnoreCase);
            scriptsRemoved += Count(s, @"<script\b");
            s = Regex.Replace(s, @"<script\b[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<script\b[^>]*/\s*>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<noscript\b[^>]*>[\s\S]*?</noscript>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<svg\b[^>]*>[\s\S]*?</svg>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<template\b[^>]*>[\s\S]*?</template>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<textarea\b[^>]*>[\s\S]*?</textarea>", "<div style=\"display:block;margin:8px 0;padding:10px 14px;background-color:#eef3f7;border-radius:12px\"><p style=\"margin:0;font-size:13px;color:#52677a\">Text entry</p></div>", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<select\b[^>]*>([\s\S]*?)</select>", delegate(Match m) {
                selectCount++;
                string inner = m.Groups[1].Value;
                int opts = Regex.Matches(inner, @"<option\b", RegexOptions.IgnoreCase).Count;
                string label = "";
                Match sel = Regex.Match(inner, @"<option\b[^>]*\bselected\b[^>]*>([\s\S]*?)</option>", RegexOptions.IgnoreCase);
                if (!sel.Success) sel = Regex.Match(inner, @"<option\b[^>]*>([\s\S]*?)</option>", RegexOptions.IgnoreCase);
                if (sel.Success) label = Regex.Replace(sel.Groups[1].Value, @"<[^>]+>", "").Trim();
                label = System.Net.WebUtility.HtmlDecode(label);
                if (String.IsNullOrWhiteSpace(label)) label = "Choice";
                if (label.Length > 64) label = label.Substring(0, 61) + "...";
                string safe = label.Replace("&"," ").Replace("<"," ").Replace(">"," ").Replace("\"","'");
                return "<div style=\"display:block;margin:10px 0;padding:12px 16px;background-color:#e8f1f6;border-radius:14px\"><p style=\"margin:0;font-size:14px;color:#24364a\">☰ " + safe + " · " + opts + " choices</p></div>";
            }, RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<optgroup\b[^>]*>[\s\S]*?</optgroup>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<option\b[^>]*>[\s\S]*?</option>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<option\b[^>]*/\s*>", "", RegexOptions.IgnoreCase);
            selectsCollapsed = selectCount;
            inlineStyles = styles.ToArray();
            return s;
        }
        private static int Count(string s, string pattern) { return Regex.Matches(s, pattern, RegexOptions.IgnoreCase).Count; }
    }

    internal sealed class DfMediumDocumentSurface
    {
        private readonly Action<string> _navigate; public string Title{get;private set;}
        public int ImagesRequested{get;private set;} public UInt32 NodeCount{get;private set;} public UInt32 ScriptCount{get;private set;} public UInt32 VmCalls{get;private set;}
        public DfMediumDocumentSurface(Action<string> navigate){_navigate=navigate;Title="Document";}
        private static System.Windows.Media.Brush Argb(UInt32 c){byte a=(byte)(c>>24),r=(byte)(c>>16),g=(byte)(c>>8),b=(byte)c;return new SolidColorBrush(Color.FromArgb(a,r,g,b));}
        private static void Pos(FrameworkElement e,DfWebSceneItem q,bool textNode){Canvas.SetLeft(e,Math.Max(0,q.x));Canvas.SetTop(e,Math.Max(0,q.y));double w=Math.Max(1,q.w);double h=Math.Max(1,q.h);if(textNode){e.Width=w;e.MaxWidth=w;e.Height=Double.NaN;e.MinHeight=Math.Min(h,64);}else{e.Width=w;e.Height=h;}}
        public int CssLoaded{get;private set;} public int SceneCount{get;private set;} public int LayoutWidth{get;private set;}
        public UIElement Build(string html,Uri baseUri){return Build(html,baseUri,1100);}
        public UIElement Build(string html,Uri baseUri,double hostWidth)
        {
            lock(DfMediumNative.EngineGate){
                int selectsCollapsed=0, scriptsRemoved=0; string[] inlineStyles;
                string prepared=JaHtmlChrome.Prepare(html??"", out selectsCollapsed, out scriptsRemoved, out inlineStyles);
                int vw=(int)Math.Max(640, Math.Min(1920, hostWidth>80?hostWidth:1100));
                int vh=(int)Math.Max(900, vw*1.35);
                LayoutWidth=vw;
                byte[] doc=Encoding.UTF8.GetBytes(prepared);int r=DfMediumNative.dfweb_begin(baseUri.AbsoluteUri,(UInt32)vw,(UInt32)vh);if(r!=0)throw new Exception("DF_Medium begin failed: "+r);r=DfMediumNative.dfweb_feed_document(doc,(UInt32)doc.Length);if(r!=0)throw new Exception("DF_Medium document ingress failed: "+r);
                int cssLoaded=0; int cssFailed=0;
                if(inlineStyles!=null){for(int si=0;si<inlineStyles.Length;si++){byte[] cssBytes=Encoding.UTF8.GetBytes(inlineStyles[si]??"");if(cssBytes.Length==0)continue;if(DfMediumNative.dfweb_add_stylesheet("inline-style-"+si.ToString(CultureInfo.InvariantCulture),cssBytes,(UInt32)cssBytes.Length)==0)cssLoaded++;else cssFailed++;}}
                r=DfMediumNative.dfweb_commit();if(r!=0)throw new Exception("DF_Medium layout commit failed: "+r);
                JaMirrorNetworkClient net=new JaMirrorNetworkClient();JaResourceBudget budget=new JaResourceBudget();
                System.Windows.Threading.Dispatcher dispatcher=Application.Current!=null?Application.Current.Dispatcher:System.Windows.Threading.Dispatcher.CurrentDispatcher;
                UInt32 rc=DfMediumNative.dfweb_resource_count();for(UInt32 i=0;i<rc&&cssLoaded<32;i++){DfWebResourceItem ri;if(DfMediumNative.dfweb_resource_get(i,out ri)!=0||ri.kind!=1)continue;string u=DfMediumNative.Str(ri.url_id);string resolved=JaLinkTarget.Resolve(u);Uri cu;if(!Uri.TryCreate(resolved,UriKind.Absolute,out cu)){if(!Uri.TryCreate(baseUri,resolved,out cu)){cssFailed++;continue;}}try{byte[] css=net.FetchResource(cu,2*1024*1024);if(css!=null&&css.Length>0&&DfMediumNative.dfweb_add_stylesheet(cu.AbsoluteUri,css,(UInt32)css.Length)==0)cssLoaded++;else cssFailed++;}catch{cssFailed++;}}CssLoaded=cssLoaded;if(cssLoaded>0){r=DfMediumNative.dfweb_commit();if(r!=0)throw new Exception("DF_Medium CSS recommit failed: "+r);}
                string nativeTitle=DfMediumNative.Title();if(!String.IsNullOrWhiteSpace(nativeTitle))Title=nativeTitle;UInt32 nodeCount,rules,scripts,calls;DfMediumNative.dfweb_stats_get(out nodeCount,out rules,out scripts,out calls);NodeCount=nodeCount;ScriptCount=scripts;VmCalls=calls;
                Canvas canvas=new Canvas();canvas.Background=BrowserWindow.Brush("#FFFCFDFF");canvas.SnapsToDevicePixels=true;RenderOptions.SetBitmapScalingMode(canvas,BitmapScalingMode.HighQuality);double bottom=500;UInt32 n=DfMediumNative.dfweb_scene_count();SceneCount=(int)n;for(UInt32 i=0;i<n;i++){DfWebSceneItem q;if(DfMediumNative.dfweb_scene_get(i,out q)!=0)continue;bottom=Math.Max(bottom,q.y+q.h+48);string text=DfMediumNative.Str(q.text_id),url=JaLinkTarget.Resolve(DfMediumNative.Str(q.url_id));
                    if(q.type==1){if(String.IsNullOrWhiteSpace(text))continue;TextBlock t=BrowserWindow.Label(text,Math.Max(11,q.font_size),((q.flags&2)!=0)?FontWeights.SemiBold:FontWeights.Normal,"#24364A",new Thickness(0));t.Foreground=Argb(q.fg==0?0xff24364au:q.fg);t.TextWrapping=TextWrapping.Wrap;t.LineHeight=Math.Max(16,q.font_size*1.45);TextOptions.SetTextFormattingMode(t,TextFormattingMode.Display);TextOptions.SetTextRenderingMode(t,TextRenderingMode.ClearType);if((q.flags&4)!=0)t.FontStyle=FontStyles.Italic;if((q.flags&8)!=0)t.TextDecorations=TextDecorations.Underline;if((q.flags&1)!=0&&!String.IsNullOrWhiteSpace(url)){t.Cursor=Cursors.Hand;string link=url;t.MouseLeftButtonUp+=delegate{_navigate(link);};}Pos(t,q,true);canvas.Children.Add(t);}
                    else if(q.type==2){Border b=new Border();b.Background=q.bg==0?BrowserWindow.Brush("#00FFFFFF"):Argb(q.bg);b.CornerRadius=new CornerRadius(10);Pos(b,q,false);canvas.Children.Add(b);}
                    else if(q.type==5){Border line=new Border();line.Background=BrowserWindow.Brush("#3A8197AA");Pos(line,q,false);canvas.Children.Add(line);}
                    else if(q.type==3){Border frame=new Border();frame.Background=BrowserWindow.Brush("#55EEF4F8");frame.BorderBrush=BrowserWindow.Brush("#3B89A0B5");frame.BorderThickness=new Thickness(1);frame.CornerRadius=new CornerRadius(12);frame.SnapsToDevicePixels=true;Image img=new Image();img.Stretch=Stretch.Uniform;RenderOptions.SetBitmapScalingMode(img,BitmapScalingMode.HighQuality);frame.Child=img;Pos(frame,q,false);canvas.Children.Add(frame);
                        Uri iu;
                        if(Uri.TryCreate(url,UriKind.Absolute,out iu)&&budget.TryStart())
                        {
                            Image target=img; Uri source=iu; System.Windows.Threading.Dispatcher ui=dispatcher;
                            Task.Factory.StartNew(delegate{
                                try
                                {
                                    byte[] data;
                                    using(budget.Slot()){ data=net.FetchResource(source,JaResourceBudget.MaxImageBytes); }
                                    if(data==null||data.Length==0||!budget.Reserve(data.Length))return;
                                    ui.BeginInvoke(new Action(delegate{
                                        try{BitmapImage bi=new BitmapImage();bi.BeginInit();bi.CacheOption=BitmapCacheOption.OnLoad;bi.StreamSource=new MemoryStream(data);bi.EndInit();bi.Freeze();target.Source=bi;}catch{}
                                    }));
                                }
                                catch{}
                            });
                        }}
                    else if(q.type==4){
                        // 8.0.x set Source and called Play()/Pause() on load, so every video on a
                        // page opened a connection and started a decoder without being asked.
                        // 8.1.0 attaches the source only on the reader's first activation.
                        Border frame=new Border();frame.Background=BrowserWindow.Brush("#FF121A24");frame.BorderBrush=BrowserWindow.Brush("#4D8EA6B8");frame.BorderThickness=new Thickness(1);frame.CornerRadius=new CornerRadius(12);frame.SnapsToDevicePixels=true;
                        Grid stackMedia=new Grid();frame.Child=stackMedia;
                        MediaElement media=new MediaElement();media.LoadedBehavior=MediaState.Manual;media.UnloadedBehavior=MediaState.Stop;media.Stretch=Stretch.Uniform;stackMedia.Children.Add(media);
                        Border cue=new Border();cue.CornerRadius=new CornerRadius(999);cue.BorderThickness=new Thickness(1);cue.BorderBrush=BrowserWindow.Brush("#66FFFFFF");cue.Background=BrowserWindow.Brush("#2EFFFFFF");cue.Padding=new Thickness(14,7,14,7);cue.HorizontalAlignment=HorizontalAlignment.Center;cue.VerticalAlignment=VerticalAlignment.Center;
                        cue.Child=BrowserWindow.Label("Play",11.5,FontWeights.Medium,"#EAF3F8",new Thickness(0));stackMedia.Children.Add(cue);
                        Pos(frame,q,false);canvas.Children.Add(frame);
                        Uri vu;
                        if(Uri.TryCreate(url,UriKind.Absolute,out vu))
                        {
                            Uri mediaUri=vu;MediaElement me=media;Border overlay=cue;bool[] started=new bool[1];
                            // MediaElement delegates HTTP redirects and address resolution to
                            // an opaque Windows media stack. Keep that path local: live web
                            // provides network media through the checked browser surface.
                            if(!mediaUri.IsFile) { overlay.Child=BrowserWindow.Label("Use live web for network media",11.5,FontWeights.Medium,"#EAF3F8",new Thickness(0)); continue; }
                            frame.Cursor=Cursors.Hand;
                            frame.MouseLeftButtonUp+=delegate{
                                try{
                                    if(!started[0]){ string localMedia=JaAddressPolicy.CheckFile(mediaUri,JaMirrorNetworkClient.PackageRoot); me.Source=new Uri(localMedia,UriKind.Absolute); started[0]=true; overlay.Visibility=Visibility.Collapsed; }
                                    me.Play();
                                }catch{ overlay.Child=BrowserWindow.Label("This media source is outside the JA21 boundary",11.5,FontWeights.Medium,"#EAF3F8",new Thickness(0)); }
                            };
                        }}
                }canvas.Height=Math.Max(bottom,640);canvas.Width=LayoutWidth;
                Border paper=new Border();paper.Background=BrowserWindow.Brush("#FFFCFDFF");paper.Padding=new Thickness(12,10,12,24);paper.Child=canvas;
                Grid pack=new Grid();pack.Children.Add(paper);
                if(selectsCollapsed>0){Border chip=new Border();chip.HorizontalAlignment=HorizontalAlignment.Right;chip.VerticalAlignment=VerticalAlignment.Top;chip.Margin=new Thickness(0,10,18,0);chip.Padding=new Thickness(10,5,10,5);chip.CornerRadius=new CornerRadius(999);chip.Background=BrowserWindow.Brush("#D0E8F2FA");chip.BorderBrush=BrowserWindow.Brush("#4C8EA6B8");chip.BorderThickness=new Thickness(1);chip.Child=BrowserWindow.Label(selectsCollapsed+" choice control"+(selectsCollapsed==1?"":"s")+" collapsed",11,FontWeights.Medium,"#4F647A",new Thickness(0));pack.Children.Add(chip);}
                ScrollViewer scroll=new ScrollViewer();scroll.Content=pack;scroll.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;scroll.HorizontalScrollBarVisibility=ScrollBarVisibility.Auto;scroll.PanningMode=PanningMode.VerticalOnly;scroll.Background=BrowserWindow.Brush("#FFF7F9FC");return scroll;
            }
        }
    }

    public sealed class BrowserWindow : Window
    {
        private bool _photonCloseApproved;
        private bool _photonClosePending;
        private readonly string _root;
        private readonly Ja21Vm _vm;
        private readonly Ja21Vm _rendererVm;
        private readonly DfSmallHelper _helper;
        private readonly VecSubstrate _vec;
        public const int MaxTabs = 64;
        private readonly List<BrowserTab> _tabs = new List<BrowserTab>();
        private readonly StackPanel _tabRail = new StackPanel();
        private readonly Grid _surface = new Grid();
        private readonly TextBox _address = new TextBox();
        private readonly TextBlock _predict = new TextBlock();
        private readonly TextBlock _status = new TextBlock();
        private readonly TextBlock _trust = new TextBlock();
        private ContextDrawerWindow _drawer;
        private int _nextId = 1;
        private bool _syncing;
        private bool _omniUserEditing;
        private bool _omniEditDirty;
        private int _omniEditTabId = -1;
        private string _omniDraftText = "";
        private Button _clearAddressButton;
        private bool _immersive;
        private bool _siteRequestedImmersive;
        private bool _omniBinOpen;
        private readonly Border _focusRing = new Border();
        private readonly Border _intentChip = new Border();
        private readonly TextBlock _intentText = new TextBlock();
        private readonly Border _tabUnderline = new Border();
        private readonly Canvas _tabUnderlineLayer = new Canvas();
        private SpringScalar _underlineX;
        private SpringScalar _underlineW;
        private readonly Border _loadSweep = new Border();
        private bool _loadSweepOn;
        private Border _omniBinPanel;
        private TextBlock _omniBinDetail;
        private SpringScalar _omniBinOpacity;
        private Grid _layout;
        private Grid _overlayRoot;
        private FrameworkElement _commandBand;
        private FrameworkElement _tabBand;
        private FrameworkElement _statusBand;
        private FrameworkElement _omniBar;
        private Border _omniShell;
        private Border _omniGrip;
        private TextBlock _omniGripGlyph;
        private TextBlock _omniLens;
        private Grid _omniInputHost;
        private Button _copilotButton;
        private Popup _omniPopup;
        private bool _omniCollapsed;
        private bool _omniBarDragging;
        private bool _omniDragFrameAttached;
        private Point _omniDragOrigin;
        private double _omniDragStartX;
        private double _omniDragStartY;
        private double _omniNormX = 0.5;
        private double _omniNormY = 0.0;
        private bool _omniPlacementLoaded;
        private Border _contentFrame;
        private Button _omniFab;
        private Border _commandPalettePanel;
        private TextBox _commandPaletteInput;
        private StackPanel _commandPaletteResults;
        private bool _commandPaletteOpen;
        private string _paletteFirstCommand;
        private WindowState _preImmersiveState;
        private Rect _preImmersiveBounds;

        public BrowserWindow(string root, Ja21Vm vm, Ja21Vm rendererVm, DfSmallHelper helper, VecSubstrate vec)
        {
            _root = root; _vm = vm; _rendererVm = rendererVm; _helper = helper; _vec = vec;
            Title = BrowserVersion.Window;
            Width = DesktopSession.Enabled ? 1360 : (Ja21Design.ConsoleMode ? 760 : 1440); Height = DesktopSession.Enabled ? 860 : (Ja21Design.ConsoleMode ? 640 : 920);
            MinWidth = DesktopSession.Enabled ? 520 : (Ja21Design.ConsoleMode ? 420 : 880); MinHeight = DesktopSession.Enabled ? 420 : (Ja21Design.ConsoleMode ? 360 : 620);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; Background = Brush("#F7F8FCFF");
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            string ico = System.IO.Path.Combine(root, "assets", "vb.ico");
            if (File.Exists(ico)) { try { Icon = BitmapFrame.Create(new Uri(ico)); } catch { } }
            BuildUi();
            SourceInitialized += delegate { if (!Ja21Design.ConsoleMode) NativeGlass.Apply(this); };
            Loaded += delegate { if (Ja21Design.ConsoleMode) CreateTab("about:blank", false); else { WindowState = WindowState.Maximized; Dispatch("new_tab", "", 0); }
            try { string startUrl = Environment.GetEnvironmentVariable("JA21_START_URL");
                if (!String.IsNullOrWhiteSpace(startUrl)) {
                    BrowserTab boot = ActiveTab; if (boot == null) { CreateTab("vb://new-tab", false); boot = ActiveTab; }
                    Navigate(boot, startUrl.Trim(), true); Status("Opening " + startUrl.Trim());
                }
            } catch (Exception startEx) { Status("Start URL skipped: " + startEx.Message); } if(_tabs.Count==0)CreateTab(Ja21Design.ConsoleMode ? "about:blank" : "vb://new-tab",false); if(!Ja21Design.ConsoleMode){try{_drawer = new ContextDrawerWindow(this, _helper, _vec);}catch(Exception drawerEx){_drawer=null;Status("DF_Medium JA21 online · optional context drawer unavailable: "+drawerEx.Message);}} if(_tabs.Count>0)Status(DesktopSession.Enabled ? "Photon console surface starting" : "Historical browser mode"); SyncTrust(); };
            LocationChanged += delegate { if (_drawer != null && _drawer.IsOpen) _drawer.Position(false); };
            SizeChanged += delegate { if (_drawer != null && _drawer.IsOpen) _drawer.Position(false); };
            StateChanged += delegate {
                if (_drawer != null && _drawer.IsOpen) _drawer.Position(false);
                if (_omniPopup != null && !_immersive)
                {
                    _omniPopup.IsOpen = WindowState != WindowState.Minimized;
                    if (_omniPopup.IsOpen) LayoutOmniBarFromNormalized();
                }
            };
            Closing += async delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                if (DesktopSession.Enabled && !_photonCloseApproved) {
                    e.Cancel = true;
                    if (_photonClosePending) return;
                    _photonClosePending = true;
                    bool safe = false;
                    try { BrowserTab active = ActiveTab; if (active != null && active.LiveWeb != null) safe = await active.LiveWeb.ShutdownAsync(); } catch { }
                    if (!safe) safe = MessageBox.Show(this, "The Photon engine did not confirm a successful save. Closing can lose unsaved work. Close anyway?", "Persistence not confirmed", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                    _photonClosePending = false;
                    if (safe) { _photonCloseApproved = true; Dispatcher.BeginInvoke(new Action(Close)); }
                    return;
                }
                if (_omniPopup != null) { try { _omniPopup.IsOpen = false; } catch { } }
                if (_drawer != null) { try { _drawer.Close(); } catch { } }
                // Every electron this presenter sealed is retired before the window goes away,
                // so a clean exit never leaves an un-retired electron behind for vecctl to find.
                int ci; for(ci=0;ci<_tabs.Count;ci++) { if(_tabs[ci].LiveWeb!=null) { try { _tabs[ci].LiveWeb.Dispose(); } catch { } _tabs[ci].LiveWeb=null; } }
                if (_vec != null) { try { _vec.EndSession(); } catch { } }
                _tabs.Clear();
            };
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewTextInput += OnPreviewTextInput;
        }

        public static System.Windows.Media.Brush Brush(string hex)
        {
            System.Windows.Media.Brush b = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(hex); if (b.CanFreeze) b.Freeze(); return b;
        }
        public static TextBlock Label(string text, double size, FontWeight weight, string color, Thickness margin)
        {
            TextBlock t = new TextBlock(); t.Text = text; t.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); t.FontSize = size; t.FontWeight = weight; t.Foreground = Brush(color); t.Margin = margin; return t;
        }

        public void AttachOverlay(UIElement element, int z)
        {
            if (element == null || _overlayRoot == null) return;
            Panel.SetZIndex(element, z);
            if (element is FrameworkElement)
            {
                FrameworkElement fe = (FrameworkElement)element;
                if (fe.Parent is Panel) ((Panel)fe.Parent).Children.Remove(element);
            }
            _overlayRoot.Children.Add(element);
        }

        public void DetachOverlay(UIElement element)
        {
            if (element == null || _overlayRoot == null) return;
            if (_overlayRoot.Children.Contains(element)) _overlayRoot.Children.Remove(element);
        }

        public static Border HairlineRule(bool horizontal, string color)
        {
            Border r = new Border();
            r.Background = Brush(color);
            r.SnapsToDevicePixels = true;
            if (horizontal) r.Height = Ja21Design.Hairline; else r.Width = Ja21Design.Hairline;
            r.Loaded += delegate
            {
                double h = Ja21Design.DeviceHairline(r);
                if (horizontal) r.Height = h; else r.Width = h;
            };
            r.IsHitTestVisible = false;
            return r;
        }
        private static ControlTemplate RoundTemplate(Type targetType, double radius)
        {
            ControlTemplate t = new ControlTemplate(targetType);
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            content.SetBinding(ContentPresenter.ContentTemplateProperty, new System.Windows.Data.Binding("ContentTemplate") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.AppendChild(content); t.VisualTree = border; return t;
        }
        /// <summary>The one interactive primitive in this shell: a pill of glass behind a
        /// hairline. A ghost button is the same primitive with its fill dropped to the ambient
        /// wash, so a control that is present but not being asked for recedes rather than
        /// competing with the document.</summary>
        public static Button GhostButton(string text, double width)
        {
            return Pill(text, width, 36, 12.5, Ja21Design.Glass1, Ja21Design.Hair, Ja21Design.Ink);
        }

        public static Button Pill(string text, double width, double height, double size, string fill, string edge, string ink)
        {
            Button b = new Button();
            b.Content = text;
            if (!String.IsNullOrEmpty(text)) AutomationProperties.SetName(b, text);
            if (width > 0) b.Width = width;
            b.Height = height;
            b.Padding = new Thickness(14, 0, 14, 0);
            b.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            b.FontSize = size;
            b.FontWeight = FontWeights.Medium;
            b.Foreground = Brush(ink);
            b.Background = Brush(fill);
            b.BorderBrush = Brush(edge);
            b.BorderThickness = new Thickness(Ja21Design.Hairline);
            b.Template = RoundTemplate(typeof(Button), height / 2.0);
            b.Cursor = Cursors.Hand;
            b.SnapsToDevicePixels = true;
            b.Loaded += delegate { b.BorderThickness = new Thickness(Ja21Design.DeviceHairline(b)); };
            b.Focusable = true;
            WireMotion(b, 1.012);
            WireGlassHover(b, fill, Ja21Design.Glass2);
            return b;
        }

        /// <summary>A floating action bubble. Round, ambient, and the only element in the shell
        /// carrying real elevation, so "this floats above the document" reads without a label.</summary>
        private static Button BubbleButton(string glyph, string tip)
        {
            Button b = Pill(glyph, 38, 38, 15.5, Ja21Design.Glass2, Ja21Design.Hair, Ja21Design.InkAccent);
            b.Padding = new Thickness(0);
            b.ToolTip = tip;
            return b;
        }

        /// <summary>Window and navigation glyphs. No fill at rest at all: pure hairline-light
        /// affordances that only acquire glass when the pointer is actually on them.</summary>
        private static Button GlyphButton(string glyph, string tip, double size)
        {
            Button b = Pill(glyph, 34, 34, size, "#00FFFFFF", "#00FFFFFF", Ja21Design.InkMuted);
            b.Padding = new Thickness(0);
            b.ToolTip = tip;
            AutomationProperties.SetName(b, tip);
            WireGlassHover(b, "#00FFFFFF", Ja21Design.Glass2);
            WireEdgeHover(b, "#00FFFFFF", Ja21Design.Hair);
            return b;
        }

        private static void WireGlassHover(Control c, string rest, string hover)
        {
            System.Windows.Media.Brush r = Brush(rest), h = Brush(hover);
            c.MouseEnter += delegate { c.Background = h; };
            c.MouseLeave += delegate { c.Background = r; };
            c.GotKeyboardFocus += delegate { c.Background = h; };
            c.LostKeyboardFocus += delegate { c.Background = r; };
        }
        private static void WireEdgeHover(Control c, string rest, string hover)
        {
            System.Windows.Media.Brush r = Brush(rest), h = Brush(hover);
            c.MouseEnter += delegate { c.BorderBrush = h; };
            c.MouseLeave += delegate { c.BorderBrush = r; };
        }

        /// <summary>A one-pixel rule. Every division in this shell is one of these.</summary>
        private static Border Rule(bool horizontal, string color)
        {
            return HairlineRule(horizontal, color);
        }

        /// <summary>Hover response. Spring-driven scale, never a timed tween, so the motion
        /// tracks the pointer's own arrival instead of playing back at it.</summary>
        private static void WireMotion(UIElement e, double targetScale)
        {
            ScaleTransform scale = new ScaleTransform(1, 1);
            e.RenderTransform = scale;
            e.RenderTransformOrigin = new Point(.5, .5);
            SpringScalar sx = new SpringScalar(delegate(double v) { scale.ScaleX = v; scale.ScaleY = v; });
            sx.Snap(1.0);
            e.MouseEnter += delegate { sx.AnimateTo(targetScale); };
            e.MouseLeave += delegate { sx.AnimateTo(1.0); };
        }


        private void BuildOmniBinPanel(Grid root)
        {
            _omniBinPanel = new Border();
            _omniBinPanel.Width = 320; _omniBinPanel.MaxHeight = 380;
            _omniBinPanel.HorizontalAlignment = HorizontalAlignment.Right;
            _omniBinPanel.VerticalAlignment = VerticalAlignment.Bottom;
            _omniBinPanel.Margin = new Thickness(0, 0, Ja21Design.GutterX, 60);
            _omniBinPanel.CornerRadius = new CornerRadius(Ja21Design.RadiusPanel);
            _omniBinPanel.BorderThickness = new Thickness(Ja21Design.Hairline);
            _omniBinPanel.BorderBrush = Brush(Ja21Design.Hair);
            _omniBinPanel.Background = Brush(Ja21Design.Glass3);
            _omniBinPanel.Padding = new Thickness(20, 18, 20, 16);
            _omniBinPanel.SnapsToDevicePixels = true;
            _omniBinPanel.Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 4, Opacity = .07, Color = Colors.SlateGray };
            _omniBinPanel.Opacity = 0; _omniBinPanel.IsHitTestVisible = false; _omniBinPanel.Visibility = Visibility.Collapsed;
            StackPanel stack = new StackPanel(); _omniBinPanel.Child = stack;
            stack.Children.Add(Label("OMNI BIN", 10, FontWeights.SemiBold, Ja21Design.InkFaint, new Thickness(0, 0, 0, 5)));
            stack.Children.Add(Label("Scene vessel", 19, FontWeights.SemiBold, Ja21Design.InkStrong, new Thickness(0, 0, 0, 8)));
            _omniBinDetail = Label("Layout records from the current document appear here after it settles.", 12.5, FontWeights.Normal, Ja21Design.InkMuted, new Thickness(0, 0, 0, 12));
            _omniBinDetail.TextWrapping = TextWrapping.Wrap; _omniBinDetail.LineHeight = 20; stack.Children.Add(_omniBinDetail);
            Border sep = Rule(true, Ja21Design.HairSoft); sep.Margin = new Thickness(0, 0, 0, 12); stack.Children.Add(sep);
            WrapPanel pills = new WrapPanel(); stack.Children.Add(pills);
            string[] items = new string[] { "nodes", "scripts", "vm calls", "media" };
            int i;
            for (i = 0; i < items.Length; i++) pills.Children.Add(Chip(items[i]));
            Button close = GhostButton("Close", 92); close.Margin = new Thickness(0, 12, 0, 0); close.Click += delegate { ToggleOmniBin(false); }; stack.Children.Add(close);
            root.Children.Add(_omniBinPanel);
            _omniBinOpacity = new SpringScalar(delegate(double v) { if (_omniBinPanel != null) _omniBinPanel.Opacity = Math.Max(0, Math.Min(1, v)); });
            _omniBinOpacity.Snap(0);
        }

        /// <summary>A read-only pill. Same geometry as an interactive pill so the shell reads as
        /// one material, with no fill and no hover so it never invites a click it cannot answer.</summary>
        private static Border Chip(string text)
        {
            Border p = new Border();
            p.CornerRadius = new CornerRadius(Ja21Design.RadiusPill);
            p.BorderThickness = new Thickness(Ja21Design.Hairline);
            p.BorderBrush = Brush(Ja21Design.HairSoft);
            p.Background = Brush(Ja21Design.Glass0);
            p.Padding = new Thickness(11, 5, 11, 5);
            p.Margin = new Thickness(0, 0, 6, 6);
            p.SnapsToDevicePixels = true;
            p.Child = Label(text, 10.5, FontWeights.Medium, Ja21Design.InkMuted, new Thickness(0));
            return p;
        }

        private void ToggleOmniBin(bool? forceOpen)
        {
            bool open = forceOpen.HasValue ? forceOpen.Value : !_omniBinOpen;
            _omniBinOpen = open;
            if (_omniBinPanel == null) return;
            if (open)
            {
                _omniBinPanel.Visibility = Visibility.Visible; _omniBinPanel.IsHitTestVisible = true;
                RefreshOmniBin();
                _omniBinOpacity.AnimateTo(1.0);
                Status("Scene vessel open");
            }
            else
            {
                _omniBinPanel.IsHitTestVisible = false;
                _omniBinOpacity.AnimateTo(0.0);
                Status("Scene vessel closed");
            }
        }

        private void RefreshOmniBin()
        {
            if (_omniBinDetail == null) return;
            BrowserTab tab = ActiveTab;
            if (tab == null) { _omniBinDetail.Text = "No tab is active."; return; }
            string title = String.IsNullOrWhiteSpace(tab.Title) ? "Untitled" : tab.Title;
            _omniBinDetail.Text = title + "\n" + tab.LogicalUrl +
                (tab.VecId == null ? "" : "\n\nElectron " + tab.VecId) +
                "\n\nThis vessel mirrors the layout records the engine emitted. Document semantics stay in the engine; this window presents pixels.";
        }

        // =====================================================================================
        // Chrome.
        //
        // Floating instruments over one ambient field: a dominant URL/search pill, compact peripheral
        // control clusters, tabs, document and status.  The URL surface is intentionally the
        // longest closed shape on screen so navigation/search reads as the primary interaction.
        // =====================================================================================
        private void BuildUi()
        {
            if (Ja21Design.ConsoleMode)
            {
                // Borderless resizable host: the virtual appliance owns the inner tabs and terminal.
                // Engine backing remains available without constructing any browser chrome.
                Grid console = new Grid(); console.Background = Brush("#FFF7F9FC");
                _layout = console; Content = console;
                _contentFrame = new Border(); _contentFrame.Background = Brush("#FFF7F9FC");
                _contentFrame.Child = _surface; _surface.ClipToBounds = true;
                console.Children.Add(_contentFrame);
                return;
            }
            Grid root = new Grid();
            root.Background = new LinearGradientBrush(
                Ja21Design.ParseColor(Ja21Design.CanvasA),
                Ja21Design.ParseColor(Ja21Design.CanvasB),
                new Point(0, 0), new Point(1, 1));
            Content = root;

            // Plane 0 — ambient field.  One accent family, extremely low energy, no idle motion.
            Ellipse ambientA = new Ellipse();
            ambientA.Width = 980; ambientA.Height = 980;
            ambientA.HorizontalAlignment = HorizontalAlignment.Left;
            ambientA.VerticalAlignment = VerticalAlignment.Top;
            ambientA.Margin = new Thickness(-380, -520, 0, 0);
            ambientA.Opacity = Ja21Design.HighContrast ? 0 : .16;
            ambientA.IsHitTestVisible = false;
            ambientA.Fill = new RadialGradientBrush(Ja21Design.ParseColor(Ja21Design.WindowsBlue), Colors.Transparent);
            Panel.SetZIndex(ambientA, Ja21Design.ZAmbient); root.Children.Add(ambientA);

            Ellipse ambientB = new Ellipse();
            ambientB.Width = 1180; ambientB.Height = 1180;
            ambientB.HorizontalAlignment = HorizontalAlignment.Right;
            ambientB.VerticalAlignment = VerticalAlignment.Bottom;
            ambientB.Margin = new Thickness(0, 0, -520, -680);
            ambientB.Opacity = Ja21Design.HighContrast ? 0 : .08;
            ambientB.IsHitTestVisible = false;
            ambientB.Fill = new RadialGradientBrush(Ja21Design.ParseColor(Ja21Design.WindowsGreen), Colors.Transparent);
            Panel.SetZIndex(ambientB, Ja21Design.ZAmbient); root.Children.Add(ambientB);

            Ellipse ambientC = new Ellipse();
            ambientC.Width = 720; ambientC.Height = 720;
            ambientC.HorizontalAlignment = HorizontalAlignment.Center;
            ambientC.VerticalAlignment = VerticalAlignment.Bottom;
            ambientC.Margin = new Thickness(0, 0, 0, -520);
            ambientC.Opacity = Ja21Design.HighContrast ? 0 : .035;
            ambientC.IsHitTestVisible = false;
            ambientC.Fill = new RadialGradientBrush(Ja21Design.ParseColor(Ja21Design.WindowsGold), Ja21Design.ParseColor("#00F25022"));
            Panel.SetZIndex(ambientC, Ja21Design.ZAmbient); root.Children.Add(ambientC);

            // Plane 1 — full-bleed renderer.  Browser chrome no longer reserves permanent rows.
            _layout = new Grid();
            Panel.SetZIndex(_layout, Ja21Design.ZContent);
            root.Children.Add(_layout);

            _contentFrame = new Border();
            _contentFrame.Margin = new Thickness(0);
            _contentFrame.CornerRadius = new CornerRadius(0);
            _contentFrame.BorderThickness = new Thickness(0);
            _contentFrame.Background = Brush(Ja21Design.Glass0);
            _contentFrame.SnapsToDevicePixels = true;
            _contentFrame.Child = _surface;
            _surface.ClipToBounds = true;
            _surface.PreviewMouseLeftButtonDown += delegate
            {
                // An explicit page click gives keyboard ownership back to page content.  Dirty
                // omni text remains protected by _omniEditDirty, but stale typing ownership must
                // not redirect later page keystrokes into the URL/search field.
                if (!_address.IsKeyboardFocusWithin) _omniUserEditing = false;
            };
            _layout.Children.Add(_contentFrame);

            // Plane 2+ — WPF composition chrome.  With WebView2CompositionControl this layer
            // can genuinely sit above live web content instead of suffering HwndHost airspace.
            _overlayRoot = new Grid();
            _overlayRoot.Background = null;
            Panel.SetZIndex(_overlayRoot, Ja21Design.ZChrome);
            root.Children.Add(_overlayRoot);

            BuildCommandRail(_overlayRoot);
            BuildAddressCapsule(_overlayRoot);
            // 9.8.1: the visible tab rail is intentionally absent. Internal tabs remain available
            // for command and keyboard workflows, but the rail itself is no longer rendered.
            BuildLoadSweep(_overlayRoot);
            BuildStatusBand(_overlayRoot);
            BuildOmniBinPanel(_overlayRoot);
            BuildCommandPalette(_overlayRoot);

            _omniFab = BubbleButton("◎", "JA21 scene vessel  (Ctrl+B)");
            _omniFab.Width = 44; _omniFab.Height = 44; _omniFab.Template = RoundTemplate(typeof(Button), 22);
            _omniFab.HorizontalAlignment = HorizontalAlignment.Right;
            _omniFab.VerticalAlignment = VerticalAlignment.Bottom;
            _omniFab.Margin = new Thickness(0, 0, Ja21Design.Space24, 64);
            _omniFab.Background = Brush(Ja21Design.Glass3);
            _omniFab.BorderBrush = Brush(Ja21Design.Hair);
            _omniFab.Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 4, Opacity = .09, Color = Colors.SlateGray };
            _omniFab.Click += delegate { ToggleOmniBin(null); };
            Panel.SetZIndex(_omniFab, Ja21Design.ZFloating);
            _overlayRoot.Children.Add(_omniFab);
            WireMotion(_omniFab, 1.018);

            Border edge = new Border();
            edge.BorderThickness = new Thickness(Ja21Design.Hairline);
            edge.BorderBrush = Brush(Ja21Design.HairSoft);
            edge.IsHitTestVisible = false;
            edge.Loaded += delegate { edge.BorderThickness = new Thickness(Ja21Design.DeviceHairline(edge)); };
            Panel.SetZIndex(edge, Ja21Design.ZCritical);
            _overlayRoot.Children.Add(edge);
        }

        private void BuildCommandRail(Grid overlay)
        {
            // 9.8.1: command chrome is reduced to a bottom control island. The top edge is reserved
            // for the movable omni search field, while navigation and window lifecycle controls live
            // together in one bottom glass island.
            Border island = new Border(); _commandBand = island;
            island.Height = 50; island.CornerRadius = new CornerRadius(25);
            island.BorderThickness = new Thickness(Ja21Design.Hairline);
            island.BorderBrush = Ja21Design.WindowsSpectrum(82);
            island.Background = Brush(Ja21Design.Glass2);
            island.Padding = new Thickness(10, 6, 10, 6);
            island.HorizontalAlignment = HorizontalAlignment.Center;
            island.VerticalAlignment = VerticalAlignment.Bottom;
            island.Margin = new Thickness(0, 0, 0, 18);
            island.SnapsToDevicePixels = true;
            island.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 4, Opacity = .07, Color = Colors.SlateGray };
            island.Loaded += delegate { island.BorderThickness = new Thickness(Ja21Design.DeviceHairline(island)); };
            Panel.SetZIndex(island, Ja21Design.ZChrome);
            overlay.Children.Add(island);

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            island.Child = row;

            Button back = GlyphButton("←", "Back  (Alt+Left)", 16); back.Click += delegate { Dispatch("back", "", 0); }; row.Children.Add(back);
            Button forward = GlyphButton("→", "Forward  (Alt+Right)", 16); forward.Margin = new Thickness(2, 0, 0, 0); forward.Click += delegate { Dispatch("forward", "", 0); }; row.Children.Add(forward);

            Border sepA = Rule(false, Ja21Design.HairSoft); sepA.Height = 18; sepA.Margin = new Thickness(10, 0, 8, 0); row.Children.Add(sepA);

            Button min = GlyphButton("─", "Minimize", 12); min.Click += delegate { Dispatch("minimize", "", 0); }; row.Children.Add(min);
            Button max = GlyphButton("□", "Enlarge or restore", 12); max.Margin = new Thickness(2, 0, 0, 0); max.Click += delegate { Dispatch("maximize", "", 0); }; row.Children.Add(max);
            Button close = GlyphButton("×", "Exit", 16); close.Margin = new Thickness(2, 0, 0, 0); close.Click += delegate { Dispatch("close_window", "", 0); }; row.Children.Add(close);
        }

        /// <summary>The hairline input bar.
        ///
        /// One capsule, one hairline, and two affordances that only appear when they mean
        /// something: a focus ring that fades in on keyboard focus, and an intent chip that
        /// states what Enter will do. Nothing is predicted at the reader; the chip reports a
        /// decision the policy VM has already made.</summary>
        private void BuildAddressCapsule(Grid overlay)
        {
            // Floating URL and search bar.
            // 9.8.1: the omni search box is hosted by a WPF Popup, giving it its own browser-owned
            // presentation surface above WebView2 and every navigated page.  This prevents
            // page composition/hit-testing from stealing the drag grip after navigation.
            // 9.8.1: RelativePoint coordinates are measured against the full owner window, not the overlay subtree.
            Grid shellGrid = new Grid(); _omniBar = shellGrid;
            shellGrid.Height = 48;
            shellGrid.Width = 620;
            shellGrid.MinWidth = 360;
            shellGrid.MaxWidth = 940;
            shellGrid.HorizontalAlignment = HorizontalAlignment.Left;
            shellGrid.VerticalAlignment = VerticalAlignment.Top;
            shellGrid.Margin = new Thickness(0);
            shellGrid.Focusable = true;
            FocusManager.SetIsFocusScope(shellGrid, true);
            KeyboardNavigation.SetTabNavigation(shellGrid, KeyboardNavigationMode.Cycle);

            _omniPopup = new Popup();
            _omniPopup.PlacementTarget = this;
            _omniPopup.Placement = PlacementMode.RelativePoint;
            _omniPopup.AllowsTransparency = true;
            _omniPopup.StaysOpen = true;
            _omniPopup.Focusable = true;
            _omniPopup.PopupAnimation = PopupAnimation.None;
            _omniPopup.Child = shellGrid;

            overlay.Loaded += delegate
            {
                if (_omniPopup != null && !_immersive) _omniPopup.IsOpen = true;
                RestoreOmniBarPlacement();
            };
            overlay.SizeChanged += delegate { if (_omniPlacementLoaded && !_omniBarDragging) { UpdateOmniFieldShape(); LayoutOmniBarFromNormalized(); } };

            Border capsule = new Border(); _omniShell = capsule;
            capsule.Height = 48;
            capsule.CornerRadius = new CornerRadius(18);
            capsule.BorderThickness = new Thickness(Ja21Design.Hairline);
            capsule.BorderBrush = Ja21Design.WindowsSpectrum(104);
            capsule.Background = Brush("#F6FFFFFF");
            capsule.Padding = new Thickness(12, 0, 10, 0);
            capsule.SnapsToDevicePixels = true;
            capsule.Focusable = true;
            capsule.Effect = new DropShadowEffect { BlurRadius = 42, ShadowDepth = 6, Opacity = .11, Color = Colors.SlateGray };
            capsule.Loaded += delegate { capsule.BorderThickness = new Thickness(Ja21Design.DeviceHairline(capsule)); UpdateOmniFieldShape(); };
            shellGrid.Children.Add(capsule);

            _focusRing.CornerRadius = new CornerRadius(19);
            _focusRing.BorderThickness = new Thickness(Ja21Design.Hairline);
            _focusRing.BorderBrush = Ja21Design.WindowsSpectrum(224);
            _focusRing.Margin = new Thickness(-2);
            _focusRing.Opacity = 0;
            _focusRing.IsHitTestVisible = false;
            _focusRing.SnapsToDevicePixels = true;
            shellGrid.Children.Add(_focusRing);

            Grid inner = new Grid(); capsule.Child = inner;
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border grip = new Border(); _omniGrip = grip;
            grip.Width = 18; grip.Height = 30; grip.CornerRadius = new CornerRadius(9);
            grip.Background = Brushes.Transparent; grip.Cursor = Cursors.SizeAll; grip.VerticalAlignment = VerticalAlignment.Center;
            grip.ToolTip = "Drag anywhere · double-click to collapse or expand · Ctrl+Shift+L to recenter";
            // Compatibility marker: Move floating omni search box.
            AutomationProperties.SetName(grip, "Move or collapse floating omni search box");
            TextBlock gripGlyph = new TextBlock(); _omniGripGlyph = gripGlyph; gripGlyph.Text = "⋮⋮"; gripGlyph.FontSize = 10.5; gripGlyph.FontWeight = FontWeights.Medium;
            gripGlyph.Foreground = Brush(Ja21Design.InkFaint); gripGlyph.Opacity = .72; gripGlyph.HorizontalAlignment = HorizontalAlignment.Center; gripGlyph.VerticalAlignment = VerticalAlignment.Center;
            gripGlyph.IsHitTestVisible = false; grip.Child = gripGlyph;
            grip.MouseLeftButtonDown += OnOmniGripMouseDown; grip.MouseMove += OnOmniGripMouseMove; grip.MouseLeftButtonUp += OnOmniGripMouseUp;
            Grid.SetColumn(grip, 0); inner.Children.Add(grip);

            // Compatibility marker: Copilot button.
            _copilotButton = new Button();
            _copilotButton.Width = 30; _copilotButton.Height = 30;
            _copilotButton.Template = RoundTemplate(typeof(Button), 15);
            _copilotButton.BorderThickness = new Thickness(0);
            _copilotButton.Background = Ja21Design.WindowsSpectrum(255);
            _copilotButton.Cursor = Cursors.Hand;
            _copilotButton.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = .13, Color = Ja21Design.ParseColor(Ja21Design.WindowsBlue) };
            _copilotButton.ToolTip = "Open Copilot in this tab";
            AutomationProperties.SetName(_copilotButton, "Open Copilot");
            TextBlock copilotGlyph = new TextBlock();
            copilotGlyph.Text = "⌘"; copilotGlyph.FontSize = 15; copilotGlyph.FontWeight = FontWeights.SemiBold;
            copilotGlyph.Foreground = Brushes.White; copilotGlyph.HorizontalAlignment = HorizontalAlignment.Center; copilotGlyph.VerticalAlignment = VerticalAlignment.Center;
            _copilotButton.Content = copilotGlyph;
            _copilotButton.Click += delegate { OpenCopilot(); };
            Grid.SetColumn(_copilotButton, 1); inner.Children.Add(_copilotButton);

            TextBlock lens = new TextBlock(); _omniLens = lens;
            lens.Text = "\uE721"; lens.FontFamily = new FontFamily("Segoe MDL2 Assets"); lens.FontSize = 13;
            lens.Foreground = Brush(Ja21Design.InkFaint); lens.Opacity = .82;
            lens.VerticalAlignment = VerticalAlignment.Center; lens.HorizontalAlignment = HorizontalAlignment.Left;
            lens.IsHitTestVisible = false; Grid.SetColumn(lens, 2); inner.Children.Add(lens);

            Grid inputHost = new Grid(); _omniInputHost = inputHost; Grid.SetColumn(inputHost, 3); inner.Children.Add(inputHost);
            _address.BorderThickness = new Thickness(0);
            _address.Background = Brushes.Transparent;
            _address.Foreground = Brush(Ja21Design.Ink);
            _address.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            _address.FontSize = 14.5;
            _address.VerticalContentAlignment = VerticalAlignment.Center;
            _address.Padding = new Thickness(0, 0, 0, 1);
            _address.CaretBrush = Brush(Ja21Design.InkAccent);
            _address.SelectionBrush = Brush("#D00078D4");
            _address.SelectionOpacity = 1.0;
            AutomationProperties.SetName(_address, "Movable floating omni search box with Copilot");
            AutomationProperties.SetHelpText(_address, "Enter a URL, search query, or JA21 command. Drag the leading grip to move this omni search box. Press Ctrl+Shift+L to recenter. Double-click the move grip to collapse or expand the search box. Use the Copilot button to open Copilot in the active tab. Selected text is shown with a Windows-blue highlight.");
            _address.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                BeginOmniEditSession();
                // 9.8.7: WebView2 can remain the native keyboard-focus owner after navigation.
                // Reclaim focus synchronously before the TextBox handles the mouse event, then
                // verify the claim again on the dispatcher.  Do not mark the mouse event handled: 
                // the TextBox must still place/extend its caret and selection normally.
                ClaimOmniKeyboardFocus(false);
                QueueOmniKeyboardFocusRecovery(false);
            };
            _address.TextChanged += OnAddressChanged;
            _address.KeyDown += OnAddressKeyDown;
            _address.GotKeyboardFocus += delegate
            {
                BeginOmniEditSession();
                Ja21Motion.Fade(_focusRing, 1.0, 160, Ja21Motion.Settle());
                _predict.Opacity = String.IsNullOrWhiteSpace(_address.Text) ? .50 : 0.0;
                UpdateOmniFieldShape();
            };
            _address.LostKeyboardFocus += delegate
            {
                // Losing keyboard focus ends *active typing ownership* even when a dirty draft is
                // intentionally preserved.  This prevents a stale _omniUserEditing flag from
                // hijacking later keystrokes intended for a web-page input.
                _omniUserEditing = false;
                Ja21Motion.Fade(_focusRing, 0.0, 130, Ja21Motion.Reveal());
                _predict.Opacity = String.IsNullOrWhiteSpace(_address.Text) ? .62 : 0.0;
                UpdateOmniFieldShape();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(delegate
                {
                    if (_omniBar != null && !_omniBar.IsKeyboardFocusWithin && !_omniEditDirty)
                    {
                        EndOmniEditSession(false);
                        SyncChrome();
                    }
                }));
            };
            TextOptions.SetTextFormattingMode(_address, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(_address, TextRenderingMode.ClearType);
            inputHost.Children.Add(_address);

            _predict.Text = "Search the web or enter address";
            _predict.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            _predict.FontSize = 13.5;
            _predict.Foreground = Brush(Ja21Design.InkFaint);
            _predict.Opacity = .62;
            _predict.VerticalAlignment = VerticalAlignment.Center;
            _predict.Margin = new Thickness(1, 0, 0, 1);
            _predict.IsHitTestVisible = false;
            inputHost.Children.Add(_predict);

            _intentChip.CornerRadius = new CornerRadius(12);
            _intentChip.BorderThickness = new Thickness(Ja21Design.Hairline);
            _intentChip.BorderBrush = Ja21Design.WindowsSpectrum(58);
            _intentChip.Background = Brush(Ja21Design.Glass1);
            _intentChip.Padding = new Thickness(11, 4, 11, 4);
            _intentChip.VerticalAlignment = VerticalAlignment.Center;
            _intentChip.Margin = new Thickness(10, 0, 2, 0);
            _intentChip.Opacity = 0;
            _intentChip.SnapsToDevicePixels = true;
            _intentText.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            _intentText.FontSize = 10.5;
            _intentText.FontWeight = FontWeights.Medium;
            _intentText.Foreground = Brush(Ja21Design.InkAccent);
            _intentChip.Child = _intentText;
            Grid.SetColumn(_intentChip, 4); inner.Children.Add(_intentChip);

            _clearAddressButton = GlyphButton("×", "Clear omni text", 12);
            _clearAddressButton.Width = 28; _clearAddressButton.Height = 28;
            _clearAddressButton.Margin = new Thickness(4, 0, 0, 0);
            _clearAddressButton.Visibility = Visibility.Collapsed;
            _clearAddressButton.Click += delegate
            {
                BeginOmniEditSession();
                _omniEditDirty = true;
                _omniDraftText = "";
                _syncing = true; _address.Text = ""; _syncing = false;
                SetIntent(""); UpdateOmniFieldShape(); UpdateClearAddressButton();
                ClaimOmniKeyboardFocus(false);
                QueueOmniKeyboardFocusRecovery(false);
            };
            Grid.SetColumn(_clearAddressButton, 5); inner.Children.Add(_clearAddressButton);
        }

        private void BeginOmniEditSession()
        {
            BrowserTab t = ActiveTab;
            if (t == null) return;
            if (_omniEditTabId != t.Id)
            {
                _omniEditTabId = t.Id;
                _omniEditDirty = false;
                _omniDraftText = _address.Text ?? "";
            }
            _omniUserEditing = true;
        }

        private void EndOmniEditSession(bool preserveCurrentText)
        {
            if (preserveCurrentText) _omniDraftText = _address.Text ?? "";
            _omniUserEditing = false;
            _omniEditDirty = false;
            _omniEditTabId = -1;
            _omniDraftText = "";
            UpdateClearAddressButton();
        }

        private bool HasProtectedOmniDraft(BrowserTab t)
        {
            return t != null && _omniEditTabId == t.Id && (_omniUserEditing || _omniEditDirty || _address.IsKeyboardFocusWithin);
        }

        private bool OmniKeyboardRecoveryEligible()
        {
            BrowserTab t = ActiveTab;
            return t != null && _omniUserEditing && _omniEditTabId == t.Id && !_address.IsKeyboardFocusWithin;
        }

        private bool ClaimOmniKeyboardFocus(bool selectAll)
        {
            BrowserTab t = ActiveTab;
            if (t == null) return false;
            BeginOmniEditSession();
            if (_omniCollapsed) { _omniCollapsed = false; ApplyOmniExpandedVisual(); LayoutOmniBarFromNormalized(); }
            try { Activate(); } catch { }
            try { if (_omniPopup != null && !_immersive && !_omniPopup.IsOpen) _omniPopup.IsOpen = true; } catch { }
            try { if (_omniBar != null) FocusManager.SetFocusedElement(_omniBar, _address); } catch { }
            bool focused = false;
            try { focused = _address.Focus(); } catch { }
            try
            {
                if (!_address.IsKeyboardFocusWithin)
                {
                    Keyboard.Focus(_address);
                    focused = _address.IsKeyboardFocusWithin || focused;
                }
            }
            catch { }
            if (selectAll && _address.IsKeyboardFocusWithin) _address.SelectAll();
            return _address.IsKeyboardFocusWithin || focused;
        }

        private void QueueOmniKeyboardFocusRecovery(bool selectAll)
        {
            BrowserTab requested = ActiveTab;
            int requestedTabId = requested == null ? -1 : requested.Id;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(delegate
            {
                BrowserTab active = ActiveTab;
                if (active == null || active.Id != requestedTabId || _omniEditTabId != requestedTabId) return;
                if (!_address.IsKeyboardFocusWithin) ClaimOmniKeyboardFocus(selectAll);
                else if (selectAll) _address.SelectAll();
            }));
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(delegate
            {
                BrowserTab active = ActiveTab;
                if (active != null && active.Id == requestedTabId && _omniEditTabId == requestedTabId && !_address.IsKeyboardFocusWithin)
                    ClaimOmniKeyboardFocus(selectAll);
            }));
        }

        private void ReplaceOmniSelection(string text)
        {
            if (text == null) text = "";
            BeginOmniEditSession();
            int start = Math.Max(0, Math.Min(_address.SelectionStart, (_address.Text ?? "").Length));
            int length = Math.Max(0, Math.Min(_address.SelectionLength, (_address.Text ?? "").Length - start));
            string current = _address.Text ?? "";
            _address.Text = current.Substring(0, start) + text + current.Substring(start + length);
            _address.CaretIndex = start + text.Length;
            _address.SelectionLength = 0;
        }

        private void DeleteOmniText(bool backward)
        {
            BeginOmniEditSession();
            string current = _address.Text ?? "";
            int start = Math.Max(0, Math.Min(_address.SelectionStart, current.Length));
            int length = Math.Max(0, Math.Min(_address.SelectionLength, current.Length - start));
            if (length > 0)
            {
                _address.Text = current.Remove(start, length);
                _address.CaretIndex = start;
                _address.SelectionLength = 0;
                return;
            }
            if (backward && start > 0)
            {
                _address.Text = current.Remove(start - 1, 1);
                _address.CaretIndex = start - 1;
            }
            else if (!backward && start < current.Length)
            {
                _address.Text = current.Remove(start, 1);
                _address.CaretIndex = start;
            }
        }

        private void UpdateClearAddressButton()
        {
            if (_clearAddressButton == null) return;
            _clearAddressButton.Visibility = (!_omniCollapsed && !String.IsNullOrEmpty(_address.Text)) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OpenCopilot()
        {
            BrowserTab t = ActiveTab;
            if (t == null)
            {
                Dispatch("new_tab", "", 0);
                t = ActiveTab;
            }
            if (t == null) return;
            Status("Copilot · opening in the active tab");
            Navigate(t, "https://copilot.microsoft.com/", true);
        }

        private void UpdateOmniFieldShape()
        {
            if (_omniBar == null || _omniShell == null) return;
            if (_omniCollapsed)
            {
                ApplyOmniCollapsedVisual();
                if (_omniPlacementLoaded && !_omniBarDragging) LayoutOmniBarFromNormalized();
                return;
            }
            string txt = JaAddressText.Sanitize(_address.Text);
            bool active = _address.IsKeyboardFocusWithin;
            double max = OmniBarWidthForViewport();
            double min = 360;
            double target = String.IsNullOrWhiteSpace(txt) ? 620 : 420 + Math.Min(420, txt.Length * 6.0);
            if (active) target += 34;
            target = Math.Max(min, Math.Min(max, target));
            _omniBar.Width = target;
            double radius = active ? 13 : (String.IsNullOrWhiteSpace(txt) ? 20 : 15);
            _omniShell.CornerRadius = new CornerRadius(radius);
            _focusRing.CornerRadius = new CornerRadius(radius + 1);
            _intentChip.CornerRadius = new CornerRadius(Math.Max(11, radius - 2));
            if (_omniPlacementLoaded && !_omniBarDragging) LayoutOmniBarFromNormalized();
        }

        private void ApplyOmniCollapsedVisual()
        {
            if (_omniBar == null || _omniShell == null) return;
            _omniBar.Width = 30; _omniBar.Height = 30;
            _omniShell.Width = 30; _omniShell.Height = 30;
            _omniShell.Padding = new Thickness(0); _omniShell.CornerRadius = new CornerRadius(15);
            _omniShell.ClipToBounds = true;
            _focusRing.CornerRadius = new CornerRadius(15); _focusRing.Opacity = 0;
            if (_omniGrip != null) { _omniGrip.Width = 30; _omniGrip.Height = 30; _omniGrip.CornerRadius = new CornerRadius(15); }
            if (_omniGripGlyph != null) { _omniGripGlyph.Text = "✦"; _omniGripGlyph.FontSize = 12; _omniGripGlyph.Foreground = Brush(Ja21Design.WindowsBlue); _omniGripGlyph.Opacity = .92; }
            if (_copilotButton != null) _copilotButton.Visibility = Visibility.Collapsed;
            if (_omniLens != null) _omniLens.Visibility = Visibility.Collapsed;
            if (_omniInputHost != null) _omniInputHost.Visibility = Visibility.Collapsed;
            if (_clearAddressButton != null) _clearAddressButton.Visibility = Visibility.Collapsed;
            _intentChip.Visibility = Visibility.Collapsed;
        }

        private void ApplyOmniExpandedVisual()
        {
            if (_omniBar == null || _omniShell == null) return;
            _omniBar.Height = 48;
            _omniShell.Width = Double.NaN; _omniShell.Height = 48;
            _omniShell.Padding = new Thickness(12, 0, 10, 0); _omniShell.ClipToBounds = false;
            if (_omniGrip != null) { _omniGrip.Width = 18; _omniGrip.Height = 30; _omniGrip.CornerRadius = new CornerRadius(9); }
            if (_omniGripGlyph != null) { _omniGripGlyph.Text = "⋮⋮"; _omniGripGlyph.FontSize = 10.5; _omniGripGlyph.Foreground = Brush(Ja21Design.InkFaint); _omniGripGlyph.Opacity = .72; }
            if (_copilotButton != null) _copilotButton.Visibility = Visibility.Visible;
            if (_omniLens != null) _omniLens.Visibility = Visibility.Visible;
            if (_omniInputHost != null) _omniInputHost.Visibility = Visibility.Visible;
            _intentChip.Visibility = String.IsNullOrWhiteSpace(_intentText.Text) ? Visibility.Collapsed : Visibility.Visible;
            UpdateClearAddressButton();
            UpdateOmniFieldShape();
        }

        private void ToggleOmniCollapsed()
        {
            if (_omniBarDragging) FinishOmniDrag(null);
            _omniCollapsed = !_omniCollapsed;
            if (_omniCollapsed)
            {
                Keyboard.ClearFocus();
                ApplyOmniCollapsedVisual();
                Status("Omni search collapsed · double-click to expand");
                PortableFabric.Record("workspace.state", "omni-collapse", "30x30 compact floating search control");
            }
            else
            {
                ApplyOmniExpandedVisual();
                Status("Omni search expanded");
                PortableFabric.Record("workspace.state", "omni-expand", "floating search control restored");
            }
            LayoutOmniBarFromNormalized();
        }

        private static double Clamp01(double v) { return v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v); }

        private void RestoreOmniBarPlacement()
        {
            if (_omniPlacementLoaded) return;
            double x, y;
            if (PortableUiState.TryLoadOmniBarPlacement(_root, out x, out y))
            {
                _omniNormX = Clamp01(x); _omniNormY = Clamp01(y);
                PortableFabric.Record("workspace.state", "omni-bar-load", "normalized portable placement restored");
            }
            _omniPlacementLoaded = true;
            LayoutOmniBarFromNormalized();
        }

        private double OmniBarWidthForViewport()
        {
            double viewportWidth = ActualWidth > 0 ? ActualWidth : (_overlayRoot == null ? 0 : _overlayRoot.ActualWidth);
            if (viewportWidth <= 0) return 760;
            return Math.Max(360, Math.Min(940, viewportWidth * 0.68));
        }

        private void LayoutOmniBarFromNormalized()
        {
            if (_omniBar == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            double inset = 0.0;
            double width = _omniBar.ActualWidth > 0 ? _omniBar.ActualWidth : OmniBarWidthForViewport();
            if (_omniBar.ActualWidth <= 0) _omniBar.Width = width;
            double height = _omniBar.ActualHeight > 0 ? _omniBar.ActualHeight : 48;
            double spanX = Math.Max(0, ActualWidth - width - (inset * 2));
            double spanY = Math.Max(0, ActualHeight - height - (inset * 2));
            if (_omniPopup == null) return;
            _omniPopup.HorizontalOffset = inset + (Clamp01(_omniNormX) * spanX);
            _omniPopup.VerticalOffset = inset + (Clamp01(_omniNormY) * spanY);
        }

        private void SetOmniBarPixels(double x, double y)
        {
            if (_omniBar == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            double inset = 0.0;
            double width = _omniBar.ActualWidth > 0 ? _omniBar.ActualWidth : OmniBarWidthForViewport();
            double height = _omniBar.ActualHeight > 0 ? _omniBar.ActualHeight : 48;
            double spanX = Math.Max(0, ActualWidth - width - (inset * 2));
            double spanY = Math.Max(0, ActualHeight - height - (inset * 2));
            double px = Math.Max(inset, Math.Min(inset + spanX, x));
            double py = Math.Max(inset, Math.Min(inset + spanY, y));
            if (_omniPopup == null) return;
            _omniPopup.HorizontalOffset = px; _omniPopup.VerticalOffset = py;
            _omniNormX = spanX <= 0 ? 0.5 : Clamp01((px - inset) / spanX);
            _omniNormY = spanY <= 0 ? 0.0 : Clamp01((py - inset) / spanY);
        }

        private static double SnapOmniNorm(double v)
        {
            v = Clamp01(v);
            if (Math.Abs(v) < .035) return 0.0;
            if (Math.Abs(v - .5) < .035) return .5;
            if (Math.Abs(v - 1.0) < .035) return 1.0;
            return v;
        }

        private void PersistOmniBarPlacement(string operation)
        {
            _omniNormX = SnapOmniNorm(_omniNormX); _omniNormY = SnapOmniNorm(_omniNormY);
            LayoutOmniBarFromNormalized();
            try
            {
                PortableUiState.SaveOmniBarPlacement(_root, _omniNormX, _omniNormY);
                PortableFabric.Record("workspace.state", operation, "omni-bar normalized placement persisted");
            }
            catch (Exception ex) { Status("Omni bar position was not saved · " + ex.Message); }
        }

        private void RecenterOmniBar(bool persist)
        {
            _omniNormX = .5; _omniNormY = 0.0; LayoutOmniBarFromNormalized();
            if (persist) PersistOmniBarPlacement("omni-bar-recenter");
            Status("Omni bar recentered");
        }

        private void OnOmniGripMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (e.ClickCount >= 2) { ToggleOmniCollapsed(); e.Handled = true; return; }
            _omniBarDragging = true; _omniDragOrigin = NativeGlass.CursorIn(this);
            _omniDragStartX = _omniPopup == null ? 0 : _omniPopup.HorizontalOffset;
            _omniDragStartY = _omniPopup == null ? 0 : _omniPopup.VerticalOffset;
            StartOmniDragFrame();
            PortableFabric.Record("workspace.state", "omni-bar-drag-start", "global pointer-polled movable omni instrument");
            e.Handled = true;
        }

        private void StartOmniDragFrame()
        {
            if (_omniDragFrameAttached) return;
            CompositionTarget.Rendering += OnOmniDragFrame;
            _omniDragFrameAttached = true;
        }

        private void StopOmniDragFrame()
        {
            if (!_omniDragFrameAttached) return;
            CompositionTarget.Rendering -= OnOmniDragFrame;
            _omniDragFrameAttached = false;
        }

        private void OnOmniDragFrame(object sender, EventArgs e)
        {
            if (!_omniBarDragging) { StopOmniDragFrame(); return; }
            if (!NativeGlass.LeftButtonDown()) { FinishOmniDrag(null); return; }
            Point now = NativeGlass.CursorIn(this);
            SetOmniBarPixels(_omniDragStartX + (now.X - _omniDragOrigin.X), _omniDragStartY + (now.Y - _omniDragOrigin.Y));
        }

        private void OnOmniGripMouseMove(object sender, MouseEventArgs e)
        {
            // Movement is intentionally polled from the owner window instead of relying on popup-local
            // MouseMove delivery. That keeps dragging alive while the pointer crosses WebView2/video.
            if (_omniBarDragging) e.Handled = true;
        }

        private void OnOmniGripMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_omniBarDragging) return;
            FinishOmniDrag(sender as UIElement);
            e.Handled = true;
        }

        private void FinishOmniDrag(UIElement element)
        {
            if (!_omniBarDragging) return;
            _omniBarDragging = false;
            StopOmniDragFrame();
            if (element != null && element.IsMouseCaptured) element.ReleaseMouseCapture();
            PersistOmniBarPlacement("omni-bar-move");
        }

        private void BuildTabRail(Grid overlay)
        {
            Border host = new Border(); _tabBand = host;
            host.Height = 34;
            host.HorizontalAlignment = HorizontalAlignment.Stretch;
            host.VerticalAlignment = VerticalAlignment.Top;
            host.Margin = new Thickness(58, 78, 58, 0);
            host.CornerRadius = new CornerRadius(17);
            host.BorderThickness = new Thickness(Ja21Design.Hairline);
            host.BorderBrush = Brush(Ja21Design.HairSoft);
            host.Background = Brush(Ja21Design.Glass1);
            host.Padding = new Thickness(7, 1, 7, 0);
            host.SnapsToDevicePixels = true;
            host.Loaded += delegate { host.BorderThickness = new Thickness(Ja21Design.DeviceHairline(host)); };
            Panel.SetZIndex(host, Ja21Design.ZChrome);
            overlay.Children.Add(host);

            Grid band = new Grid(); host.Child = band;
            ScrollViewer tabsScroll = new ScrollViewer();
            tabsScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            tabsScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            tabsScroll.Padding = new Thickness(0);
            tabsScroll.VerticalAlignment = VerticalAlignment.Center;
            band.Children.Add(tabsScroll);
            _tabRail.Orientation = Orientation.Horizontal;
            _tabRail.LayoutUpdated += delegate { PositionUnderline(false); };
            tabsScroll.Content = _tabRail;

            _tabUnderlineLayer.IsHitTestVisible = false;
            _tabUnderlineLayer.VerticalAlignment = VerticalAlignment.Bottom;
            _tabUnderlineLayer.Height = 2;
            band.Children.Add(_tabUnderlineLayer);
            _tabUnderline.Height = 2; _tabUnderline.Width = 0;
            _tabUnderline.CornerRadius = new CornerRadius(1);
            _tabUnderline.Background = Brush(Ja21Design.Accent);
            _tabUnderline.Opacity = .74;
            _tabUnderline.SnapsToDevicePixels = true;
            _tabUnderlineLayer.Children.Add(_tabUnderline);
            Canvas.SetLeft(_tabUnderline, 0); Canvas.SetTop(_tabUnderline, 0);
            _underlineX = new SpringScalar(delegate(double v) { Canvas.SetLeft(_tabUnderline, v); }, true);
            _underlineW = new SpringScalar(delegate(double v) { _tabUnderline.Width = Math.Max(0, v); }, true);
        }

        private void PositionUnderline(bool snap)
        {
            BrowserTab t = ActiveTab;
            if (t == null || t.Pill == null || _underlineX == null) { if (_underlineW != null) _underlineW.AnimateTo(0); return; }
            try
            {
                if (!t.Pill.IsVisible || t.Pill.ActualWidth <= 0) return;
                GeneralTransform gt = t.Pill.TransformToAncestor(_tabUnderlineLayer.Parent as UIElement);
                Point p = gt.Transform(new Point(0, 0));
                double x = p.X + 10;
                double w = Math.Max(0, t.Pill.ActualWidth - 20);
                if (snap) { _underlineX.Snap(x); _underlineW.Snap(w); }
                else { _underlineX.AnimateTo(x); _underlineW.AnimateTo(w); }
            }
            catch { }
        }

        /// <summary>A single low-contrast sweep, running only while a document is in flight.
        /// There is no idle animation anywhere in this shell.</summary>
        private void BuildLoadSweep(Grid overlay)
        {
            Grid host = new Grid();
            host.HorizontalAlignment = HorizontalAlignment.Stretch;
            host.VerticalAlignment = VerticalAlignment.Top;
            host.Height = 2;
            host.Margin = new Thickness(74, 114, 74, 0);
            host.ClipToBounds = true;
            Panel.SetZIndex(host, Ja21Design.ZFloating);
            overlay.Children.Add(host);

            _loadSweep.Height = 2; _loadSweep.Width = 180;
            _loadSweep.HorizontalAlignment = HorizontalAlignment.Left;
            _loadSweep.CornerRadius = new CornerRadius(1);
            _loadSweep.Opacity = 0; _loadSweep.IsHitTestVisible = false;
            _loadSweep.Background = new LinearGradientBrush(
                new GradientStopCollection(new GradientStop[] {
                    new GradientStop(Colors.Transparent, 0.0),
                    new GradientStop(Ja21Design.ParseColor(Ja21Design.Accent), 0.5),
                    new GradientStop(Colors.Transparent, 1.0) }),
                new Point(0, 0), new Point(1, 0));
            TranslateTransform tt = new TranslateTransform();
            _loadSweep.RenderTransform = tt; host.Children.Add(_loadSweep);
        }

        private void SetLoading(bool on)
        {
            if (_loadSweepOn == on) return;
            _loadSweepOn = on;
            TranslateTransform tt = _loadSweep.RenderTransform as TranslateTransform;
            if (tt == null) return;
            if (on)
            {
                Ja21Motion.Fade(_loadSweep, 1.0, 140, Ja21Motion.Settle());
                if (Ja21Design.ReducedMotion)
                {
                    tt.BeginAnimation(TranslateTransform.XProperty, null);
                    tt.X = 0;
                    return;
                }
                double span = Math.Max(300, ActualWidth - 148);
                DoubleAnimation a = new DoubleAnimation(-200, span, TimeSpan.FromMilliseconds(1150));
                a.RepeatBehavior = RepeatBehavior.Forever;
                a.EasingFunction = Ja21Motion.Ease(Ja21Motion.Reveal());
                tt.BeginAnimation(TranslateTransform.XProperty, a);
            }
            else
            {
                Ja21Motion.Fade(_loadSweep, 0.0, 200, Ja21Motion.Reveal());
                tt.BeginAnimation(TranslateTransform.XProperty, null);
            }
        }

        private void BuildStatusBand(Grid overlay)
        {
            Grid statusGrid = new Grid(); _statusBand = statusGrid;
            statusGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
            statusGrid.VerticalAlignment = VerticalAlignment.Bottom;
            statusGrid.Margin = new Thickness(Ja21Design.Space24, 0, Ja21Design.Space24, Ja21Design.Space16);
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Panel.SetZIndex(statusGrid, Ja21Design.ZFloating); overlay.Children.Add(statusGrid);

            Border statusPill = new Border();
            statusPill.MaxWidth = 620; statusPill.HorizontalAlignment = HorizontalAlignment.Left;
            statusPill.CornerRadius = new CornerRadius(14);
            statusPill.BorderThickness = new Thickness(Ja21Design.Hairline);
            statusPill.BorderBrush = Brush(Ja21Design.HairSoft);
            statusPill.Background = Brush(Ja21Design.Glass2);
            statusPill.Padding = new Thickness(12, 6, 12, 6);
            statusPill.SnapsToDevicePixels = true;
            statusPill.Loaded += delegate { statusPill.BorderThickness = new Thickness(Ja21Design.DeviceHairline(statusPill)); };
            Grid.SetColumn(statusPill, 0); statusGrid.Children.Add(statusPill);
            _status.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            _status.FontSize = 10.5; _status.Foreground = Brush(Ja21Design.InkFaint);
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            _status.Text = "Ready";
            statusPill.Child = _status;

            Border trustPill = new Border();
            trustPill.HorizontalAlignment = HorizontalAlignment.Right; trustPill.Cursor = Cursors.Hand;
            trustPill.CornerRadius = new CornerRadius(14);
            trustPill.BorderThickness = new Thickness(Ja21Design.Hairline);
            trustPill.BorderBrush = Brush(Ja21Design.HairSoft);
            trustPill.Background = Brush(Ja21Design.Glass2);
            trustPill.Padding = new Thickness(12, 6, 12, 6);
            trustPill.SnapsToDevicePixels = true;
            trustPill.Loaded += delegate { trustPill.BorderThickness = new Thickness(Ja21Design.DeviceHairline(trustPill)); };
            trustPill.MouseLeftButtonUp += delegate { if (_drawer != null) _drawer.OpenDrawer(); };
            Grid.SetColumn(trustPill, 1); statusGrid.Children.Add(trustPill);
            _trust.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            _trust.FontSize = 10.5; _trust.Foreground = Brush(Ja21Design.InkFaint);
            _trust.VerticalAlignment = VerticalAlignment.Center;
            _trust.Text = "◇  Local surface · NATIVE";
            trustPill.Child = _trust;
        }

        private void BuildCommandPalette(Grid overlay)
        {
            _commandPalettePanel = new Border();
            _commandPalettePanel.Width = 640;
            _commandPalettePanel.MaxHeight = 520;
            _commandPalettePanel.HorizontalAlignment = HorizontalAlignment.Center;
            _commandPalettePanel.VerticalAlignment = VerticalAlignment.Top;
            _commandPalettePanel.Margin = new Thickness(0, 126, 0, 0);
            _commandPalettePanel.CornerRadius = new CornerRadius(Ja21Design.RadiusPanel);
            _commandPalettePanel.BorderThickness = new Thickness(Ja21Design.Hairline);
            _commandPalettePanel.BorderBrush = Brush(Ja21Design.Hair);
            _commandPalettePanel.Background = Brush(Ja21Design.Glass4);
            _commandPalettePanel.Padding = new Thickness(18, 16, 18, 18);
            _commandPalettePanel.Effect = new DropShadowEffect { BlurRadius = 56, ShadowDepth = 7, Opacity = .12, Color = Colors.SlateGray };
            _commandPalettePanel.Visibility = Visibility.Collapsed;
            _commandPalettePanel.Opacity = 0;
            _commandPalettePanel.IsHitTestVisible = false;
            _commandPalettePanel.SnapsToDevicePixels = true;
            _commandPalettePanel.Loaded += delegate { _commandPalettePanel.BorderThickness = new Thickness(Ja21Design.DeviceHairline(_commandPalettePanel)); };
            Panel.SetZIndex(_commandPalettePanel, Ja21Design.ZPalette);
            overlay.Children.Add(_commandPalettePanel);

            StackPanel outer = new StackPanel(); _commandPalettePanel.Child = outer;
            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            outer.Children.Add(top);
            TextBlock title = Label("COMMAND PALETTE", 10, FontWeights.Medium, Ja21Design.InkFaint, new Thickness(2, 0, 0, 8));
            Grid.SetColumn(title, 0); top.Children.Add(title);
            TextBlock shortcut = Label("Ctrl+K", 10, FontWeights.Normal, Ja21Design.InkFaint, new Thickness(0, 0, 2, 8));
            Grid.SetColumn(shortcut, 1); top.Children.Add(shortcut);

            Border inputShell = new Border();
            inputShell.CornerRadius = new CornerRadius(18);
            inputShell.BorderThickness = new Thickness(Ja21Design.Hairline);
            inputShell.BorderBrush = Brush(Ja21Design.HairFocus);
            inputShell.Background = Brush(Ja21Design.Glass2);
            inputShell.Padding = new Thickness(14, 7, 14, 7);
            inputShell.SnapsToDevicePixels = true;
            inputShell.Loaded += delegate { inputShell.BorderThickness = new Thickness(Ja21Design.DeviceHairline(inputShell)); };
            outer.Children.Add(inputShell);
            _commandPaletteInput = new TextBox();
            _commandPaletteInput.BorderThickness = new Thickness(0);
            _commandPaletteInput.Background = Brushes.Transparent;
            _commandPaletteInput.Foreground = Brush(Ja21Design.Ink);
            _commandPaletteInput.CaretBrush = Brush(Ja21Design.Accent);
            _commandPaletteInput.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            _commandPaletteInput.FontSize = 14;
            _commandPaletteInput.Padding = new Thickness(0);
            AutomationProperties.SetName(_commandPaletteInput, "JA21 command palette");
            _commandPaletteInput.TextChanged += delegate { RefreshCommandPalette(); };
            _commandPaletteInput.KeyDown += OnPaletteKeyDown;
            inputShell.Child = _commandPaletteInput;

            Border sep = Rule(true, Ja21Design.HairSoft); sep.Margin = new Thickness(0, 14, 0, 10); outer.Children.Add(sep);
            _commandPaletteResults = new StackPanel(); outer.Children.Add(_commandPaletteResults);
            RefreshCommandPalette();
        }

        private void ToggleCommandPalette(bool? force)
        {
            bool open = force.HasValue ? force.Value : !_commandPaletteOpen;
            _commandPaletteOpen = open;
            if (_commandPalettePanel == null) return;
            if (open)
            {
                if (_drawer != null && _drawer.IsOpen) _drawer.CloseDrawer();
                _commandPalettePanel.Visibility = Visibility.Visible;
                _commandPalettePanel.IsHitTestVisible = true;
                _commandPaletteInput.Text = "";
                RefreshCommandPalette();
                Ja21Motion.Fade(_commandPalettePanel, 1.0, 170, Ja21Motion.Settle());
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(delegate { _commandPaletteInput.Focus(); }));
            }
            else
            {
                _commandPalettePanel.IsHitTestVisible = false;
                if (Ja21Design.ReducedMotion) { _commandPalettePanel.Opacity = 0; _commandPalettePanel.Visibility = Visibility.Collapsed; return; }
                DoubleAnimation a = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(130));
                a.EasingFunction = Ja21Motion.Ease(Ja21Motion.Reveal());
                a.Completed += delegate { if (!_commandPaletteOpen) _commandPalettePanel.Visibility = Visibility.Collapsed; };
                _commandPalettePanel.BeginAnimation(UIElement.OpacityProperty, a);
            }
        }

        private void OnPaletteKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { ToggleCommandPalette(false); e.Handled = true; return; }
            if (e.Key == Key.Enter && !String.IsNullOrEmpty(_paletteFirstCommand))
            {
                string command = _paletteFirstCommand; ToggleCommandPalette(false); RunPaletteCommand(command); e.Handled = true;
            }
        }

        private void RefreshCommandPalette()
        {
            if (_commandPaletteResults == null) return;
            _commandPaletteResults.Children.Clear(); _paletteFirstCommand = null;
            string q = _commandPaletteInput == null ? "" : (_commandPaletteInput.Text ?? "").Trim().ToLowerInvariant();
            AddPaletteAction("New tab", "Ctrl+T", "new_tab", q);
            AddPaletteAction("New private tab", "Ctrl+Shift+N", "private_tab", q);
            AddPaletteAction("Open context and VEC1 diagnostics", "", "drawer_toggle", q);
            AddPaletteAction("Open portable desktop", "", "portable_desktop", q);
            AddPaletteAction("Open portable documents", "", "portable_documents", q);
            AddPaletteAction("Open downloads", "", "downloads", q);
            AddPaletteAction("Enter immersive mode", "F11", "fullscreen", q);
            AddPaletteAction("Focus omni input", "Ctrl+L", "focus_address", q);
            AddPaletteAction("Recenter floating omni bar", "Ctrl+Shift+L", "recenter_omni", q);
        }

        private void AddPaletteAction(string label, string shortcut, string command, string query)
        {
            string hay = (label + " " + command).ToLowerInvariant();
            if (!String.IsNullOrEmpty(query) && hay.IndexOf(query, StringComparison.Ordinal) < 0) return;
            if (_paletteFirstCommand == null) _paletteFirstCommand = command;
            Button b = GhostButton("", 0);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            b.Height = 42; b.Margin = new Thickness(0, 0, 0, 6);
            Grid g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock t = Label(label, 12.5, FontWeights.Normal, Ja21Design.Ink, new Thickness(4, 0, 0, 0)); t.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(t, 0); g.Children.Add(t);
            TextBlock s = Label(shortcut, 10.5, FontWeights.Normal, Ja21Design.InkFaint, new Thickness(12, 0, 4, 0)); s.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(s, 1); g.Children.Add(s);
            b.Content = g; b.Click += delegate { ToggleCommandPalette(false); RunPaletteCommand(command); };
            _commandPaletteResults.Children.Add(b);
        }

        private void RunPaletteCommand(string command)
        {
            if (command == "portable_desktop") { OpenPortableFolder(PortablePaths.Desktop(_root), "Portable desktop"); return; }
            if (command == "portable_documents") { OpenPortableFolder(PortablePaths.Documents(_root), "Portable documents"); return; }
            if (command == "recenter_omni") { RecenterOmniBar(true); return; }
            Dispatch(command, "", 0);
        }

        private void OpenPortableFolder(string path, string label)
        {
            try { Directory.CreateDirectory(path); PortableFabric.Record("device.io", "open-folder", label); Process.Start("explorer.exe", path); Status(label + " · " + path); }
            catch(Exception ex) { Status(label + " unavailable · " + ex.Message); }
        }

        private void OnBrandMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { Dispatch("maximize","",0); e.Handled = true; return; }
            if (e.LeftButton == MouseButtonState.Pressed && WindowState == WindowState.Normal && !_immersive) { try { DragMove(); } catch { } }
        }
        private void OnAddressKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string pending = _address.Text ?? "";
                e.Handled = true;
                if (String.IsNullOrWhiteSpace(pending))
                {
                    BeginOmniEditSession();
                    _omniEditDirty = true;
                    _omniDraftText = "";
                    Status("Omni field cleared");
                    UpdateClearAddressButton();
                    return;
                }
                EndOmniEditSession(false);
                Dispatch("navigate", pending, 0);
            }
            else if (e.Key == Key.Escape)
            {
                EndOmniEditSession(false);
                SyncChrome(); Keyboard.ClearFocus();
            }
        }
        private void OnAddressChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncing) { UpdateClearAddressButton(); return; }
            BeginOmniEditSession();
            _omniEditDirty = true;
            _omniDraftText = _address.Text ?? "";
            SetIntent(_address.Text);
            UpdateOmniFieldShape();
            UpdateClearAddressButton();
        }

        /// <summary>Report what Enter will do, in the fewest neutral words that are true.
        /// The decision itself comes from the JA21 policy VM, so the chip cannot claim an
        /// outcome the policy would not actually produce.</summary>
        private void SetIntent(string raw)
        {
            string clean = JaAddressText.Sanitize(raw);
            if (String.IsNullOrWhiteSpace(clean))
            {
                _predict.Text = "Search the web or enter address";
                _predict.Opacity = _address.IsKeyboardFocusWithin ? .50 : .62;
                _address.ToolTip = "Search the web, open an address, or enter a JA21 command.";
                ShowIntent(null, Ja21Design.InkAccent);
                return;
            }

            // Once text exists the watermark withdraws completely; the compact trailing chip
            // carries the primary action while the full neutral explanation remains available
            // as the field tooltip.
            _predict.Opacity = 0.0;
            if (!String.Equals(clean, raw == null ? "" : raw.Trim(), StringComparison.Ordinal))
                _address.ToolTip = "Formatting characters were removed from this address.";
            try
            {
                string normalized = Convert.ToString(_vm.Run("normalize", clean));
                if (String.IsNullOrEmpty(normalized))
                {
                    _address.ToolTip = "JA21 policy does not open this address.";
                    ShowIntent("Not opened", Ja21Design.Attention);
                    return;
                }
                if (normalized.StartsWith("https://html.duckduckgo.com/html/?q=", StringComparison.OrdinalIgnoreCase))
                {
                    _address.ToolTip = "Enter searches the web.";
                    ShowIntent("Search", Ja21Design.InkAccent);
                    return;
                }
                if (normalized == "vb://new-tab")
                {
                    _address.ToolTip = "Enter opens a new tab.";
                    ShowIntent("New tab", Ja21Design.InkAccent);
                    return;
                }
                Uri u;
                if (Uri.TryCreate(normalized, UriKind.Absolute, out u))
                {
                    _address.ToolTip = "Enter opens " + JaAddressText.HostLabel(u) + ".";
                    ShowIntent(u.Scheme == "https" ? "Open · encrypted" : "Open", Ja21Design.InkAccent);
                    return;
                }
                _address.ToolTip = "Enter opens this address.";
                ShowIntent("Open", Ja21Design.InkAccent);
            }
            catch
            {
                _address.ToolTip = "This address is not complete yet.";
                ShowIntent(null, Ja21Design.InkAccent);
            }
        }

        private void ShowIntent(string text, string ink)
        {
            if (text == null)
            {
                if (_intentChip.Opacity > 0) Ja21Motion.Fade(_intentChip, 0.0, 120, Ja21Motion.Reveal());
                return;
            }
            _intentText.Text = text;
            _intentText.Foreground = Brush(ink);
            _intentChip.BorderBrush = Brush(ink == Ja21Design.Attention ? "#4C8A6A4A" : Ja21Design.HairSoft);
            if (_intentChip.Opacity < 1) Ja21Motion.Fade(_intentChip, 1.0, 150, Ja21Motion.Settle());
        }

        private BrowserTab ActiveTab
        {
            get { int i; for (i=0;i<_tabs.Count;i++) if (_tabs[i].Surface.Visibility==Visibility.Visible) return _tabs[i]; return null; }
        }
        private Dictionary<string, object> Model()
        {
            BrowserTab t=ActiveTab; Dictionary<string,object> m=new Dictionary<string,object>(StringComparer.Ordinal);
            m["active"]=t==null?0:t.Id; m["count"]=_tabs.Count; m["can_back"]=t!=null&&t.CanGoBack; m["can_forward"]=t!=null&&t.CanGoForward; m["loading"]=t!=null&&t.Loading; return m;
        }
        public void DispatchPublic(string kind, string input, int id)
        {
            if (kind == "command_palette") { ToggleCommandPalette(null); return; }
            if (kind == "portable_desktop") { OpenPortableFolder(PortablePaths.Desktop(_root), "Portable desktop"); return; }
            if (kind == "portable_documents") { OpenPortableFolder(PortablePaths.Documents(_root), "Portable documents"); return; }
            Dispatch(kind, input, id);
        }
        private void Dispatch(string kind, string input, int id)
        {
            try
            {
                Dictionary<string,object> ev=new Dictionary<string,object>(StringComparer.Ordinal); ev["kind"]=kind; ev["id"]=id; ev["input"]=input==null?"":input;
                Dictionary<string,object> action=(Dictionary<string,object>)_vm.Run("on_event",Model(),ev); Execute(action);
            }
            catch(Exception ex){ Status("JA21 · "+ex.Message); }
        }
        private void Execute(Dictionary<string, object> a)
        {
            string kind=Convert.ToString(a["kind"]); int id=Convert.ToInt32(a["id"]); string value=Convert.ToString(a["value"]);
            if(kind=="create"){CreateTab(value,false);return;} if(kind=="create_private"){CreateTab(value,true);Status("Private control tab · history remains in memory for this process only.");return;}
            if(kind=="load"){BrowserTab t=FindTab(id);if(t!=null)Navigate(t,value);return;} if(kind=="select"){SelectTab(id);return;} if(kind=="close"){CloseTab(id);return;}
            BrowserTab active=ActiveTab;
            if(kind=="back"&&active!=null&&active.CanGoBack){if(active.RenderMode=="LIVE"&&active.LiveWeb!=null){active.LiveWeb.GoBack();return;}active.HistoryIndex--;Navigate(active,active.History[active.HistoryIndex],false);return;} if(kind=="forward"&&active!=null&&active.CanGoForward){if(active.RenderMode=="LIVE"&&active.LiveWeb!=null){active.LiveWeb.GoForward();return;}active.HistoryIndex++;Navigate(active,active.History[active.HistoryIndex],false);return;}
            if(kind=="reload"&&active!=null){if(active.RenderMode=="LIVE"&&active.LiveWeb!=null){active.LiveWeb.Reload();return;}Navigate(active,active.LogicalUrl,false);return;} if(kind=="stop"&&active!=null){active.Generation++;active.Loading=false;SetLoading(false);if(active.RenderMode=="LIVE"&&active.LiveWeb!=null)active.LiveWeb.Stop();Status("Stopped.");SyncChrome();return;}
            if(kind=="downloads"){OpenPortableFolder(PortablePaths.Downloads(_root), "Portable downloads");return;}
            if(kind=="clear_data"){if(active!=null&&active.LiveWeb!=null){active.LiveWeb.ClearBrowsingData();Status("Live web browsing data clear requested for this JA21 profile.");}else Status("Native DF_Medium mode has no persistent cookie/local-storage engine to clear.");return;}
            if(kind=="about"){ShowAbout();return;} if(kind=="omni_bin"){ToggleOmniBin(null);return;} if(kind=="drawer_toggle"){if(_drawer!=null)_drawer.Toggle();return;} if(kind=="drawer_close"){if(_drawer!=null)_drawer.CloseDrawer();return;}
            if(kind=="helper_status"){if(_drawer!=null){_drawer.OpenDrawer();_drawer.SetDetail(_helper.Summary);}Status("Auxiliary DF_Small · "+_helper.Summary);return;}
            if(kind=="helper_verify"){RunHelper("verify");return;} if(kind=="helper_pulse"){RunHelper("pulse");return;}
            if(kind=="minimize"){WindowState=WindowState.Minimized;return;} if(kind=="maximize"){ToggleMaximize();return;} if(kind=="fullscreen"){ToggleImmersive();return;} if(kind=="close_window"){Close();return;}
            if(kind=="focus_address")
            {
                BeginOmniEditSession();
                ClaimOmniKeyboardFocus(true);
                QueueOmniKeyboardFocusRecovery(true);
                return;
            } if(kind=="status"){Status(value);return;}
        }
        private void RunHelper(string operation)
        {
            Status("Auxiliary DF_Small · running "+operation+"…"); if(_drawer!=null)_drawer.OpenDrawer();
            _helper.RunNativeAsync(operation,delegate(string text,bool ok){Dispatcher.BeginInvoke(new Action(delegate{if(_drawer!=null)_drawer.SetHelperResult(text,ok);Status(ok?"Auxiliary DF_Small · completed":"Auxiliary DF_Small · host toolchain or gate needs attention");}));});
        }

        private void CreateTab(string url, bool isPrivate)
        {
            if (Ja21Design.ConsoleMode && ActiveTab != null) { Navigate(ActiveTab, url, true); return; }
            if(_tabs.Count>=MaxTabs){Status("This window is holding its maximum of "+MaxTabs.ToString(CultureInfo.InvariantCulture)+" tabs. Close one to open another.");return;}
            BrowserTab t=new BrowserTab();t.Id=_nextId++;
            if(_vec!=null)t.VecId=_vec.BindTab(isPrivate?"private tab":"tab");t.Private=isPrivate;t.LogicalUrl="vb://new-tab";t.Title=isPrivate?"Private":"New tab";
            Grid surface=new Grid();surface.Visibility=Visibility.Collapsed;surface.Background=Brush("#00FFFFFF");t.Surface=surface;Grid pageHost=new Grid();t.PageHost=pageHost;surface.Children.Add(pageHost);_surface.Children.Add(surface);
            if (Ja21Design.ConsoleMode) { _tabs.Add(t); SelectTab(t.Id); Navigate(t,url,true); return; }
            TextBlock label=Label(t.Title,11.5,FontWeights.Medium,Ja21Design.InkMuted,new Thickness(0));label.TextTrimming=TextTrimming.CharacterEllipsis;t.PillText=label;
            Button pill=Pill("",132,30,11.5,"#00FFFFFF","#00FFFFFF",Ja21Design.InkMuted);
            pill.Margin=new Thickness(4,0,4,4);pill.Content=label;pill.Tag=t.Id;
            pill.ToolTip="Click to open \u00b7 right-click to close";
            pill.Click+=delegate{Dispatch("select_tab","",t.Id);};pill.MouseRightButtonUp+=delegate{Dispatch("close_tab","",t.Id);};
            t.Pill=pill;_tabRail.Children.Add(pill);
            _tabs.Add(t);SelectTab(t.Id);Navigate(t,url,true);
        }
        private void UpdateTabTitle(BrowserTab t)
        {
            if (t.PillText == null) return;
            string title=String.IsNullOrWhiteSpace(t.Title)?(t.Private?"Private":"New tab"):t.Title;if(title.Length>24)title=title.Substring(0,23)+"…";t.PillText.Text=(t.Private?"◌ ":"")+title;
        }
        private bool LiveWebAvailable(){return LiveWebFactory.IsAvailable(_root);}
        private bool EnsureLiveSurface(BrowserTab t)
        {
            if(t.LiveWeb==null)
            {
                try
                {
                    t.LiveWeb=LiveWebFactory.Create(_root,t.Id,t.Private);
                    ILiveWebSurface createdSurface = t.LiveWeb;
                    t.LiveWeb.NavigationCommitted+=delegate(string src,string title)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if(t.LiveWeb!=createdSurface||t.RenderMode!="LIVE")return;
                            Uri nu=null;if(!String.IsNullOrWhiteSpace(src)&&Uri.TryCreate(src,UriKind.Absolute,out nu)&&(nu.Scheme=="http"||nu.Scheme=="https"))
                            {
                                t.LogicalUrl=nu.AbsoluteUri;ReplaceCurrentHistory(t,t.LogicalUrl);
                                if(!String.IsNullOrWhiteSpace(title))t.Title=title;else t.Title=nu.Host;
                                UpdateTabTitle(t);
                            }
                            t.Loading=false;SetLoading(false);SyncChrome();
                            if(_vec!=null&&t.VecId!=null)_vec.RecordPresentation(t.VecId,nu==null?"":nu.Host,"ok",0,0,0,null,"webview2-live");
                        }));
                    };
                    t.LiveWeb.StatusChanged+=delegate(string text){Dispatcher.BeginInvoke(new Action(delegate{if(t.LiveWeb==createdSurface&&t.RenderMode=="LIVE")Status(text);}));};
                    t.LiveWeb.NavigationFailed+=delegate(string reason){Dispatcher.BeginInvoke(new Action(delegate{
                        if(t.LiveWeb!=createdSurface||t.RenderMode!="LIVE")return;
                        t.Loading=false;SetLoading(false);Status(reason);SyncChrome();
                        if (DesktopSession.Enabled)
                        {
                            ILiveWebSurface failedSurface = t.LiveWeb; t.LiveWeb = null;
                            try { failedSurface.Dispose(); } catch { }
                            ShowError(t,"Virtual console",reason + " Check the certificate, server origin and WebView2 installation; no native-renderer fallback was selected.");
                        }
                        Uri failed;Uri.TryCreate(t.LogicalUrl,UriKind.Absolute,out failed);
                        if(_vec!=null&&t.VecId!=null)_vec.RecordPresentation(t.VecId,failed==null?"":failed.Host,"failed",0,0,0,reason,"webview2-live");
                    }));};
                    t.LiveWeb.FullScreenChanged+=delegate(bool full){Dispatcher.BeginInvoke(new Action(delegate{
                        if(t.LiveWeb!=createdSurface)return;
                        if(full){if(!_immersive){_siteRequestedImmersive=true;ToggleImmersive();}}
                        else if(_siteRequestedImmersive){_siteRequestedImmersive=false;if(_immersive)ToggleImmersive();}
                    }));};
                }
                catch(Exception ex){Status("Live web bridge failed · "+ex.Message);t.LiveWeb=null;return false;}
            }
            t.PageHost.Children.Clear();FrameworkElement view=t.LiveWeb.View;if(view.Parent is Panel)((Panel)view.Parent).Children.Remove(view);t.PageHost.Children.Add(view);t.RenderMode="LIVE";SyncTrust();return true;
        }
        private void NavigateLive(BrowserTab t,Uri u,bool pushHistory)
        {
            int generation=++t.Generation;t.Loading=true;t.ShowingHome=false;t.LogicalUrl=u.AbsoluteUri;t.Title=u.Host;UpdateTabTitle(t);if(pushHistory)PushHistory(t,t.LogicalUrl);SetLoading(true);SyncChrome();Status("Web · "+u.Host);
            if(!EnsureLiveSurface(t)){
                if(DesktopSession.Enabled){t.Loading=false;ShowError(t,"Virtual console","WebView2 could not create the application surface. No blank/native fallback was selected.");return;}
                t.PreferLiveWeb=false;t.RenderMode="NATIVE";Navigate(t,u.AbsoluteUri,false);return;
            }
            try{t.LiveWeb.Navigate(u.AbsoluteUri);}catch(Exception ex){if(generation!=t.Generation)return;t.Loading=false;ShowError(t,"Live web",ex.Message);Status(ex.Message);}
        }

        private void Navigate(BrowserTab t,string url){Navigate(t,url,true);}
        private void Navigate(BrowserTab t,string url,bool pushHistory)
        {
            if(t==null)return;if(String.IsNullOrEmpty(url)||url=="vb://new-tab"){ShowHome(t);if(pushHistory)PushHistory(t,Ja21Design.ConsoleMode ? "about:blank" : "vb://new-tab");return;}if(url=="about:blank"){ShowBlank(t);if(pushHistory)PushHistory(t,"about:blank");return;}
            url=JaLinkTarget.Resolve(JaAddressText.Sanitize(url));Uri u;if(!Uri.TryCreate(url,UriKind.Absolute,out u)){Status("That address could not be read as a web address.");return;}string scheme=u.Scheme.ToLowerInvariant();bool isFile=scheme=="file";bool allowed=isFile;if(!isFile){try{allowed=(bool)_vm.Run("page_allowed",scheme,false);}catch{allowed=false;}}if(!allowed||(scheme!="http"&&scheme!="https"&&!isFile)){Status("JA21 policy does not open that kind of address.");return;}if(!String.IsNullOrEmpty(u.UserInfo)){Status("Addresses that carry credentials are not opened.");return;}
            if(DesktopSession.Enabled && isFile){ShowError(t,"Address","The integrated console opens only its configured server, not native file URLs.");return;}
            if(!isFile){try{JaAddressPolicy.Check(u);}catch(Exception policyEx){Status(policyEx.Message);ShowError(t,"Address",policyEx.Message);return;}}
            if(!isFile&&t.PreferLiveWeb&&LiveWebAvailable()){NavigateLive(t,u,pushHistory);return;}
            if(DesktopSession.Enabled){ShowError(t,"Virtual console","The JavaScript/WebSocket appliance requires WebView2. No native-renderer fallback was selected.");return;}
            t.RenderMode="NATIVE";SyncTrust();
            int generation=++t.Generation;t.Loading=true;t.ShowingHome=false;t.LogicalUrl=u.AbsoluteUri;t.Title=u.Host;UpdateTabTitle(t);if(pushHistory)PushHistory(t,t.LogicalUrl);ShowLoading(t,u);SyncChrome();Status("Inspection fallback · "+u.Host);
            Task.Factory.StartNew(delegate
            {
                try
                {
                    JaMirrorNetworkClient net=new JaMirrorNetworkClient();JaMirrorFetchResult result=net.Fetch(u);
                    if(!isFile){Dictionary<string,object> meta=new Dictionary<string,object>(StringComparer.Ordinal);meta["scheme"]=result.FinalUri.Scheme.ToLowerInvariant();meta["mime"]=MimeOnly(result.ContentType);meta["bytes"]=result.Bytes;meta["redirects"]=result.Redirects;if(!(bool)_rendererVm.Run("fetch_allowed",meta))throw new Exception("JA renderer policy rejected the fetched document budget.");Dictionary<string,object> docPolicy=(Dictionary<string,object>)_rendererVm.Run("document_policy",MimeOnly(result.ContentType));if(!Convert.ToBoolean(docPolicy["allow"]))throw new Exception(Convert.ToString(docPolicy["reason"]));}
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if(generation!=t.Generation)return;try{DfMediumDocumentSurface renderer=new DfMediumDocumentSurface(delegate(string link){Navigate(t,JaLinkTarget.Resolve(link),true);});double hostW=t.PageHost.ActualWidth;if(hostW<400)hostW=_surface.ActualWidth;if(hostW<400)hostW=Math.Max(880,ActualWidth-56);UIElement view=renderer.Build(result.Text,result.FinalUri,hostW);t.PageHost.Children.Clear();view.Opacity=0;t.PageHost.Children.Add(view);Ja21Motion.Fade(view,1.0,200,Ja21Motion.Settle());SetLoading(false);t.Loading=false;t.LogicalUrl=result.FinalUri.AbsoluteUri;t.Title=renderer.Title;UpdateTabTitle(t);ReplaceCurrentHistory(t,t.LogicalUrl);SyncChrome();SyncTrust();
                        if(_vec!=null&&t.VecId!=null)_vec.RecordPresentation(t.VecId,result.FinalUri.Host,"ok",result.Bytes,renderer.SceneCount,renderer.ScriptCount,null);
                        Status(renderer.SceneCount+" scene records · "+renderer.CssLoaded+" style sheets · "+renderer.LayoutWidth+"px · "+result.Bytes+" bytes · "+renderer.ScriptCount+" scripts isolated");}catch(Exception renderEx){t.Loading=false;ShowError(t,"Layout",renderEx.Message);SyncChrome();if(_vec!=null&&t.VecId!=null)_vec.RecordPresentation(t.VecId,u.Host,"failed",0,0,0,renderEx.Message);}
                    }));
                }
                catch(Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(delegate{if(generation!=t.Generation)return;t.Loading=false;ShowError(t,"Transport",ex.Message);SyncChrome();Status(ex.Message);if(_vec!=null&&t.VecId!=null)_vec.RecordPresentation(t.VecId,u.Host,"failed",0,0,0,ex.Message);}));
                }
            });
        }
        private static string MimeOnly(string contentType){if(String.IsNullOrWhiteSpace(contentType))return "";int semi=contentType.IndexOf(';');return (semi<0?contentType:contentType.Substring(0,semi)).Trim().ToLowerInvariant();}
        private void PushHistory(BrowserTab t,string url){if(t.HistoryIndex>=0&&t.HistoryIndex<t.History.Count&&t.History[t.HistoryIndex]==url)return;if(t.HistoryIndex+1<t.History.Count)t.History.RemoveRange(t.HistoryIndex+1,t.History.Count-t.HistoryIndex-1);t.History.Add(url);t.HistoryIndex=t.History.Count-1;}
        private void ReplaceCurrentHistory(BrowserTab t,string url){if(t.HistoryIndex>=0&&t.HistoryIndex<t.History.Count)t.History[t.HistoryIndex]=url;}
        private void ShowLoading(BrowserTab t,Uri u)
        {
            SetLoading(true);
            t.PageHost.Children.Clear();
            Grid g=new Grid();g.Background=Brush("#00FFFFFF");
            StackPanel s=new StackPanel();s.Width=560;s.HorizontalAlignment=HorizontalAlignment.Center;s.VerticalAlignment=VerticalAlignment.Center;g.Children.Add(s);
            TextBlock h=Label("Reconstructing this document",21,FontWeights.SemiBold,Ja21Design.InkStrong,new Thickness(0,0,0,8));
            h.HorizontalAlignment=HorizontalAlignment.Center;s.Children.Add(h);
            TextBlock p=Label(JaAddressText.HostLabel(u),12.5,FontWeights.Normal,Ja21Design.InkMuted,new Thickness(0,0,0,18));
            p.HorizontalAlignment=HorizontalAlignment.Center;s.Children.Add(p);
            Border rule=Rule(true,Ja21Design.HairSoft);rule.Width=200;rule.HorizontalAlignment=HorizontalAlignment.Center;s.Children.Add(rule);
            g.Opacity=0;t.PageHost.Children.Add(g);
            Ja21Motion.Fade(g,1.0,220,Ja21Motion.Settle());
        }

        private void ShowError(BrowserTab t,string stage,string message)
        {
            SetLoading(false);
            t.PageHost.Children.Clear();
            Grid g=new Grid();g.Background=Brush("#00FFFFFF");
            Border card=new Border();card.MaxWidth=680;card.Padding=new Thickness(36,32,36,32);
            card.CornerRadius=new CornerRadius(Ja21Design.RadiusCard);card.BorderThickness=new Thickness(Ja21Design.Hairline);
            card.BorderBrush=Brush(Ja21Design.Hair);card.Background=Brush(Ja21Design.Glass4);card.SnapsToDevicePixels=true;
            card.HorizontalAlignment=HorizontalAlignment.Center;card.VerticalAlignment=VerticalAlignment.Center;
            StackPanel s=new StackPanel();card.Child=s;
            s.Children.Add(Label(stage.ToUpperInvariant(),10,FontWeights.SemiBold,Ja21Design.InkFaint,new Thickness(0,0,0,8)));
            s.Children.Add(Label("This document did not finish.",22,FontWeights.SemiBold,Ja21Design.InkStrong,new Thickness(0,0,0,10)));
            TextBlock p=Label(message,13,FontWeights.Normal,Ja21Design.InkMuted,new Thickness(0,0,0,18));p.TextWrapping=TextWrapping.Wrap;p.LineHeight=21;s.Children.Add(p);
            Border rule=Rule(true,Ja21Design.HairSoft);rule.Margin=new Thickness(0,0,0,16);s.Children.Add(rule);
            WrapPanel acts=new WrapPanel();s.Children.Add(acts);
            Button retry=GhostButton("Try again",104);retry.Click+=delegate{if(DesktopSession.Enabled){t.PreferLiveWeb=true;Navigate(t,DesktopSession.Origin+"/#ja21-desktop",false);}else Dispatch("refresh","",0);};acts.Children.Add(retry);
            Button home=GhostButton("New tab",98);home.Margin=new Thickness(8,0,0,0);home.Click+=delegate{Dispatch("new_tab","",0);};if(!DesktopSession.Enabled)acts.Children.Add(home);
            g.Children.Add(card);g.Opacity=0;t.PageHost.Children.Add(g);
            Ja21Motion.Fade(g,1.0,200,Ja21Motion.Settle());
        }

        private void ShowBlank(BrowserTab t)
        {
            SetLoading(false); t.Generation++;
            if (Ja21Design.ConsoleMode)
            {
                // Remove live content and its process ownership when this box becomes blank.
                ILiveWebSurface previous = t.LiveWeb; t.LiveWeb = null;
                if (previous != null) { try { previous.Stop(); } catch { } try { previous.Dispose(); } catch { } }
                t.RenderMode = "NATIVE"; t.PreferLiveWeb = true; t.ShowingHome = false;
            }
            t.LogicalUrl="about:blank";t.Loading=false;t.Title="Blank";t.PageHost.Children.Clear();t.PageHost.Background=Brush("#00FFFFFF");UpdateTabTitle(t);SyncChrome();
        }
        private static System.Windows.Shapes.Path LandingRibbon(string geometry, string start, string mid, string end, double opacity)
        {
            System.Windows.Shapes.Path p = new System.Windows.Shapes.Path();
            p.Data = Geometry.Parse(geometry);
            LinearGradientBrush b = new LinearGradientBrush();
            b.StartPoint = new Point(0, .5); b.EndPoint = new Point(1, .5);
            b.GradientStops.Add(new GradientStop(Ja21Design.ParseColor(start), 0.0));
            b.GradientStops.Add(new GradientStop(Ja21Design.ParseColor(mid), .52));
            b.GradientStops.Add(new GradientStop(Ja21Design.ParseColor(end), 1.0));
            p.Fill = b; p.Opacity = opacity; p.IsHitTestVisible = false; p.SnapsToDevicePixels = true;
            return p;
        }

        private static System.Windows.Shapes.Path LandingHairline(string geometry, string color, double thickness, double opacity)
        {
            System.Windows.Shapes.Path p = new System.Windows.Shapes.Path();
            p.Data = Geometry.Parse(geometry); p.Fill = Brushes.Transparent;
            p.Stroke = Brush(color); p.StrokeThickness = thickness; p.Opacity = opacity;
            p.IsHitTestVisible = false; p.SnapsToDevicePixels = true;
            return p;
        }

        private static System.Windows.Shapes.Path LandingPane(string geometry, double opacity)
        {
            System.Windows.Shapes.Path p = new System.Windows.Shapes.Path();
            p.Data = Geometry.Parse(geometry);
            LinearGradientBrush fill = new LinearGradientBrush();
            fill.StartPoint = new Point(0,0); fill.EndPoint = new Point(1,1);
            fill.GradientStops.Add(new GradientStop(Ja21Design.ParseColor("#2479C7F5"),0));
            fill.GradientStops.Add(new GradientStop(Ja21Design.ParseColor("#08FFFFFF"),1));
            p.Fill = fill; p.Stroke = Brush("#BFFFFFFF"); p.StrokeThickness = 1.35;
            p.Opacity = opacity; p.IsHitTestVisible = false;
            return p;
        }

        private void ShowHome(BrowserTab t)
        {
            if (Ja21Design.ConsoleMode) { ShowBlank(t); return; }
            SetLoading(false);
            t.Generation++; t.LogicalUrl = "vb://new-tab"; t.Loading = false; t.ShowingHome = true;
            t.Title = t.Private ? "Private" : "New tab"; t.PreferLiveWeb = true;
            UpdateTabTitle(t); t.PageHost.Children.Clear(); t.PageHost.Background = Brush("#00FFFFFF");

            Grid g = new Grid();
            g.Background = Brush(Ja21Design.HighContrast ? Ja21Design.CanvasA : "#FFF7FBFF");
            g.IsHitTestVisible = true; g.ClipToBounds = true;

            // 9.8.3 native landing scene: hard-coded from the approved text-free reference.
            // No bitmap/OCR/text layer is required at runtime. The design is native WPF geometry.
            Viewbox viewport = new Viewbox();
            viewport.Stretch = Stretch.UniformToFill; viewport.StretchDirection = StretchDirection.Both;
            viewport.HorizontalAlignment = HorizontalAlignment.Stretch; viewport.VerticalAlignment = VerticalAlignment.Stretch;
            viewport.IsHitTestVisible = false;
            Canvas scene = new Canvas(); scene.Width = 1600; scene.Height = 900; scene.ClipToBounds = true;
            viewport.Child = scene; g.Children.Add(viewport);

            Rectangle sky = new Rectangle(); sky.Width = 1600; sky.Height = 900;
            LinearGradientBrush skyBrush = new LinearGradientBrush(); skyBrush.StartPoint = new Point(0,0); skyBrush.EndPoint = new Point(1,1);
            skyBrush.GradientStops.Add(new GradientStop(Ja21Design.ParseColor("#FFF8FCFF"),0));
            skyBrush.GradientStops.Add(new GradientStop(Ja21Design.ParseColor("#FFE7F3FF"),.48));
            skyBrush.GradientStops.Add(new GradientStop(Ja21Design.ParseColor("#FFD9E9F7"),1));
            sky.Fill = skyBrush; scene.Children.Add(sky);

            // Atmospheric haze fields.
            Ellipse hazeBlue = new Ellipse(); hazeBlue.Width = 760; hazeBlue.Height = 760; hazeBlue.Opacity = .22;
            hazeBlue.Fill = new RadialGradientBrush(Ja21Design.ParseColor("#900078D4"), Ja21Design.ParseColor("#000078D4")); hazeBlue.Effect = new BlurEffect { Radius = 54 };
            Canvas.SetLeft(hazeBlue,-280); Canvas.SetTop(hazeBlue,-120); scene.Children.Add(hazeBlue);
            Ellipse hazeCyan = new Ellipse(); hazeCyan.Width = 760; hazeCyan.Height = 650; hazeCyan.Opacity = .17;
            hazeCyan.Fill = new RadialGradientBrush(Ja21Design.ParseColor("#7000B7C3"), Ja21Design.ParseColor("#0000B7C3")); hazeCyan.Effect = new BlurEffect { Radius = 62 };
            Canvas.SetLeft(hazeCyan,410); Canvas.SetTop(hazeCyan,140); scene.Children.Add(hazeCyan);
            Ellipse hazeWarm = new Ellipse(); hazeWarm.Width = 650; hazeWarm.Height = 620; hazeWarm.Opacity = .16;
            hazeWarm.Fill = new RadialGradientBrush(Ja21Design.ParseColor("#70FFB900"), Ja21Design.ParseColor("#00F25022")); hazeWarm.Effect = new BlurEffect { Radius = 66 };
            Canvas.SetLeft(hazeWarm,1080); Canvas.SetTop(hazeWarm,280); scene.Children.Add(hazeWarm);

            // Windows-pane geometry in the upper-right, deliberately translucent and architectural.
            scene.Children.Add(LandingPane("M 952,248 L 1134,202 L 1134,352 L 952,377 Z", .56));
            scene.Children.Add(LandingPane("M 1148,198 L 1405,142 L 1405,319 L 1148,350 Z", .58));
            scene.Children.Add(LandingPane("M 952,391 L 1134,367 L 1134,508 L 952,505 Z", .46));
            scene.Children.Add(LandingPane("M 1148,365 L 1405,335 L 1405,520 L 1148,509 Z", .49));

            // Major ribbon bodies. Shapes intentionally overlap to create translucent glass-smoke volume.
            scene.Children.Add(LandingRibbon("M -90,175 C 40,28 176,4 315,122 C 428,217 446,365 596,437 C 424,398 318,277 203,304 C 84,333 -2,278 -90,232 Z", "#D80078D4", "#A60078D4", "#4800B7C3", .86));
            scene.Children.Add(LandingRibbon("M -60,315 C 102,182 223,161 362,287 C 509,420 626,458 826,462 C 643,500 489,522 337,456 C 207,398 70,443 -60,389 Z", "#B80A64D8", "#B400A4EF", "#5500C4CC", .82));
            scene.Children.Add(LandingRibbon("M -75,416 C 84,302 231,306 385,397 C 532,485 667,561 869,525 C 703,612 522,625 367,553 C 214,484 83,562 -75,510 Z", "#920078D4", "#8F00B7C3", "#4500D6C9", .64));
            scene.Children.Add(LandingRibbon("M 305,219 C 466,137 610,201 744,360 C 852,488 957,544 1125,514 C 989,592 824,603 689,533 C 560,466 418,425 305,344 Z", "#280078D4", "#8A00B7C3", "#60107C10", .64));
            scene.Children.Add(LandingRibbon("M 578,389 C 727,276 846,340 972,462 C 1081,568 1198,594 1322,554 C 1205,635 1071,664 924,610 C 800,565 680,521 578,474 Z", "#5600B7C3", "#8A107C10", "#56FFB900", .70));
            scene.Children.Add(LandingRibbon("M 792,456 C 932,374 1038,412 1159,517 C 1266,609 1370,623 1608,545 L 1608,708 C 1414,736 1284,713 1138,648 C 1015,594 884,566 792,540 Z", "#34107C10", "#94FFB900", "#7AF25022", .76));
            scene.Children.Add(LandingRibbon("M 1038,492 C 1184,423 1324,459 1433,531 C 1512,583 1572,587 1640,548 L 1640,744 C 1539,784 1431,765 1323,703 C 1213,639 1113,594 1038,576 Z", "#36FFB900", "#A0F7630C", "#B8F25022", .78));

            // Translucent white silk overlays that give the waves the airy, layered look of the reference.
            scene.Children.Add(LandingRibbon("M -80,77 C 149,-22 310,54 460,254 C 533,353 653,421 806,421 C 643,447 487,403 353,310 C 209,211 80,221 -80,206 Z", "#BFFFFFFF", "#54FFFFFF", "#08FFFFFF", .70));
            scene.Children.Add(LandingRibbon("M 147,284 C 333,179 485,241 630,385 C 764,518 902,533 1058,480 C 912,596 733,603 578,510 C 428,421 290,379 147,402 Z", "#35FFFFFF", "#70FFFFFF", "#08FFFFFF", .62));
            scene.Children.Add(LandingRibbon("M 706,368 C 870,300 1008,379 1124,473 C 1244,570 1409,568 1637,432 L 1637,554 C 1444,660 1262,671 1098,594 C 948,524 831,464 706,449 Z", "#08FFFFFF", "#62FFFFFF", "#1EFFFFFF", .66));

            // Luminous white/cyan hairlines follow the crests; these stay hairline-light and static.
            string[] curves = new string[] {
                "M -10,137 C 173,91 246,169 388,278 C 531,388 622,405 784,398",
                "M -16,290 C 147,231 270,254 407,358 C 564,477 690,489 846,451",
                "M 63,397 C 231,340 348,369 492,456 C 626,536 751,552 905,505",
                "M 527,394 C 690,325 809,371 930,471 C 1041,563 1175,582 1324,536",
                "M 807,520 C 952,455 1088,488 1205,577 C 1322,666 1445,676 1606,596",
                "M 1004,626 C 1160,549 1272,566 1395,633 C 1488,684 1554,681 1610,653"
            };
            for(int ci=0;ci<curves.Length;ci++) scene.Children.Add(LandingHairline(curves[ci], ci<3?"#D8FFFFFF":"#C8F7FDFF", ci==0?1.8:1.2, ci==0?.84:.68));

            // Pixel/particle accents, concentrated where the reference has sparkling bokeh.
            double[,] particles = new double[,] {
                {88,620,5,.32},{122,544,9,.20},{164,663,4,.38},{246,595,6,.34},{300,687,4,.26},
                {365,541,3,.42},{442,614,5,.25},{526,552,4,.28},{612,633,7,.20},{708,577,4,.34},
                {811,515,6,.25},{916,582,4,.32},{1031,512,7,.28},{1112,578,4,.26},{1210,526,6,.31},
                {1288,612,4,.38},{1372,560,8,.23},{1468,622,4,.34},{1520,502,6,.28},{1568,446,3,.38}
            };
            for(int pi=0;pi<particles.GetLength(0);pi++) {
                Rectangle px = new Rectangle(); double ps=particles[pi,2]; px.Width=ps; px.Height=ps; px.RadiusX=ps*.28; px.RadiusY=ps*.28;
                px.Fill=Brush(pi<8?"#D8FFFFFF":(pi<13?"#A0DFFFFF":"#B0FFF2D0")); px.Opacity=particles[pi,3];
                Canvas.SetLeft(px,particles[pi,0]); Canvas.SetTop(px,particles[pi,1]); scene.Children.Add(px);
            }

            // Glass floor and reflected color wash.
            Rectangle floor = new Rectangle(); floor.Width = 1600; floor.Height = 170;
            LinearGradientBrush floorBrush = new LinearGradientBrush(); floorBrush.StartPoint = new Point(0,0); floorBrush.EndPoint = new Point(0,1);
            floorBrush.GradientStops.Add(new GradientStop(Ja21Design.ParseColor("#72FFFFFF"),0));
            floorBrush.GradientStops.Add(new GradientStop(Ja21Design.ParseColor("#C6F7FBFF"),.52));
            floorBrush.GradientStops.Add(new GradientStop(Ja21Design.ParseColor("#FFF8FBFE"),1));
            floor.Fill=floorBrush; floor.Opacity=.82; Canvas.SetLeft(floor,0); Canvas.SetTop(floor,730); scene.Children.Add(floor);
            scene.Children.Add(LandingHairline("M 0,736 L 1600,736","#B8FFFFFF",1.0,.78));
            scene.Children.Add(LandingHairline("M 0,770 C 460,720 1020,716 1600,754","#5EFFFFFF",1.0,.46));

            if (Ja21Design.HighContrast)
            {
                scene.Opacity = .12;
            }

            g.Opacity = 0; t.PageHost.Children.Add(g);
            Ja21Motion.Fade(g, 1.0, 220, Ja21Motion.Settle());
            SyncChrome();
        }

        private BrowserTab FindTab(int id){int i;for(i=0;i<_tabs.Count;i++)if(_tabs[i].Id==id)return _tabs[i];return null;}
        private void SelectTab(int id)
        {
            BrowserTab t=FindTab(id);if(t==null)return;int i;
            EndOmniEditSession(false);
            for(i=0;i<_tabs.Count;i++){
                bool active=_tabs[i].Id==id;
                _tabs[i].Surface.Visibility=active?Visibility.Visible:Visibility.Collapsed;
                if (_tabs[i].Pill != null)
                {
                    _tabs[i].Pill.Background=Brush(active?Ja21Design.Glass2:"#00FFFFFF");
                    _tabs[i].Pill.BorderBrush=Brush(active?Ja21Design.HairSoft:"#00FFFFFF");
                    _tabs[i].PillText.Foreground=Brush(active?Ja21Design.Ink:Ja21Design.InkFaint);
                }
            }
            SyncChrome();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(delegate{PositionUnderline(false);}));
        }
        private void CloseTab(int id)
        {
            BrowserTab t=FindTab(id);if(t==null)return;bool was=t.Surface.Visibility==Visibility.Visible;_surface.Children.Remove(t.Surface);if(t.Pill!=null)_tabRail.Children.Remove(t.Pill);_tabs.Remove(t);t.Generation++;
            if(t.LiveWeb!=null){try{t.LiveWeb.Dispose();}catch{}t.LiveWeb=null;}
            if(_vec!=null&&t.VecId!=null)_vec.RetireTab(t.VecId);
            if(_tabs.Count==0)Dispatch("new_tab","",0);else if(was)SelectTab(_tabs[Math.Max(0,_tabs.Count-1)].Id);else SyncChrome();
        }
        private void SyncChrome()
        {
            BrowserTab t=ActiveTab;if(t==null)return;
            if (Ja21Design.ConsoleMode) { Title = DesktopSession.Enabled ? "JA21 Photon Console 3.0.1" : "JA21 Virtual Console"; return; }
            // 9.8.7 edit/focus-state hardening: a dirty omni draft belongs to the active tab and wins
            // over all page-driven URL/title refreshes until the user explicitly commits, cancels,
            // or switches tabs. This includes an intentionally empty string.
            if (HasProtectedOmniDraft(t))
            {
                if (_omniEditDirty && !String.Equals(_address.Text ?? "", _omniDraftText ?? "", StringComparison.Ordinal))
                {
                    _syncing = true; _address.Text = _omniDraftText ?? ""; _syncing = false;
                }
                SetIntent(_address.Text); UpdateOmniFieldShape(); UpdateClearAddressButton();
            }
            else
            {
                _syncing=true;_address.Text=t.LogicalUrl=="vb://new-tab"?"":t.LogicalUrl;_syncing=false;
                SetIntent(_address.Text);UpdateOmniFieldShape();UpdateClearAddressButton();
            }
            Title=BrowserVersion.Window+(t.Private?" · Private control":"");SyncTrust();
        }

        /// <summary>The trust line states only what is actually true of the current tab:
        /// transport, script isolation, host spelling and whether the VEC1 substrate is
        /// really sealing state. No claim is made that the substrate did not earn.</summary>
        private void SyncTrust()
        {
            BrowserTab t = ActiveTab;
            Uri u = null;
            if (t != null && t.LogicalUrl != null) Uri.TryCreate(t.LogicalUrl, UriKind.Absolute, out u);

            string host = "local";
            string transport = "Local surface";
            if (u != null && (u.Scheme == "http" || u.Scheme == "https"))
            {
                host = JaAddressText.HostLabel(u);
                transport = u.Scheme == "https" ? "Encrypted transport" : "Plain transport";
            }

            bool live = t != null && t.RenderMode == "LIVE" && t.LiveWeb != null;
            string vecShort = _vec == null ? "VEC1 —" : _vec.ShortStatus();
            _trust.Text = "◇  " + host + " · " + vecShort;

            StringBuilder detail = new StringBuilder();
            detail.Append(transport).Append(" · ").Append(host);
            if (u != null && !JaAddressText.IsAscii(u.Host)) detail.Append(" · non-ASCII host");
            detail.Append(live ? " · modern web renderer active" : " · DF_Medium inspection fallback active");
            detail.Append(" · renderer selection automatic");
            detail.Append(" · ").Append(_vec == null ? "VEC1 substrate not bound" : _vec.LongStatus());
            _trust.ToolTip = detail.ToString();
        }
        private void Status(string text){_status.Text=text;}
        private void ShowAbout()
        {
            if(_drawer!=null){_drawer.OpenDrawer();_drawer.SetDetail(
                BrowserVersion.Window+"\n\n"+
                "Role: VEC1-managed JA21 omni-bin application shell.\n"+
                "Modern web surface: Microsoft Edge WebView2 runtime when available\n"+
                "Native inspection surface: DF_Medium / Bottle Rocket JA21 engine\n"+
                "Native shell adapter: Windows WPF presenter only\n"+
                "Application lifecycle substrate: VEC1 electron substitute\n"+
                "Electron / Node dependency: none\n\n"+
                "Renderer selection is automatic. JA21 uses the WebView2 composition surface for modern HTTP/HTTPS sites when it is available and falls back to the sealed DF_Medium inspection path when live rendering cannot initialize or when a local inspection surface is required. No user renderer switch is exposed. WebView2 is not Electron and it does not replace VEC1 lifecycle/state ownership.\n\n"+
                (_vec==null?"The VEC1 electron substrate is not present in this package.":_vec.LongStatus()));}
        }
        private void ToggleMaximize(){if(_immersive){ToggleImmersive();return;}WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;}
        private void ToggleImmersive()
        {
            if (!_immersive)
            {
                _immersive = true;
                _preImmersiveState = WindowState;
                _preImmersiveBounds = new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
                if (_drawer != null && _drawer.IsOpen) _drawer.CloseDrawer();
                if (_commandPaletteOpen) ToggleCommandPalette(false);
                if (_omniBinOpen) ToggleOmniBin(false);

                Rect monitor = NativeGlass.MonitorBounds(this);
                WindowState = WindowState.Normal;
                Topmost = true;
                Left = monitor.Left; Top = monitor.Top; Width = monitor.Width; Height = monitor.Height;

                if (_commandBand != null) _commandBand.Visibility = Visibility.Collapsed;
                if (_omniPopup != null) _omniPopup.IsOpen = false;
                if (_tabBand != null) _tabBand.Visibility = Visibility.Collapsed;
                if (_statusBand != null) _statusBand.Visibility = Visibility.Collapsed;
                if (_omniFab != null) _omniFab.Visibility = Visibility.Collapsed;
                _loadSweep.Visibility = Visibility.Collapsed;
                if (_contentFrame != null)
                {
                    _contentFrame.Margin = new Thickness(0);
                    _contentFrame.CornerRadius = new CornerRadius(0);
                    _contentFrame.BorderThickness = new Thickness(0);
                    _contentFrame.Effect = null;
                }
                Status("Immersive mode · full-bleed content · F11 to return");
            }
            else
            {
                _immersive = false;
                Topmost = false;
                WindowState = WindowState.Normal;
                Left = _preImmersiveBounds.Left; Top = _preImmersiveBounds.Top;
                Width = Math.Max(MinWidth, _preImmersiveBounds.Width); Height = Math.Max(MinHeight, _preImmersiveBounds.Height);
                if (_preImmersiveState == WindowState.Maximized)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(delegate { WindowState = WindowState.Maximized; }));

                if (_commandBand != null) _commandBand.Visibility = Visibility.Visible;
                if (_omniPopup != null) { _omniPopup.IsOpen = true; LayoutOmniBarFromNormalized(); }
                if (_tabBand != null) _tabBand.Visibility = Visibility.Visible;
                if (_statusBand != null) _statusBand.Visibility = Visibility.Visible;
                if (_omniFab != null) _omniFab.Visibility = Visibility.Visible;
                _loadSweep.Visibility = Visibility.Visible;
                Status("JA21 optical window");
            }
        }

        private void SelectRelativeTab(int delta)
        {
            if (_tabs.Count < 2) return;
            BrowserTab current = ActiveTab; int index = current == null ? 0 : _tabs.IndexOf(current);
            int next = (index + delta + _tabs.Count) % _tabs.Count;
            SelectTab(_tabs[next].Id);
        }

        private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (Ja21Design.ConsoleMode) return;
            // Defensive first-keystroke recovery.  If WebView2 won the focus race after the user
            // clicked the omni field, preserve the first typed character while reclaiming focus.
            if (!OmniKeyboardRecoveryEligible() || e == null || String.IsNullOrEmpty(e.Text)) return;
            if (!ClaimOmniKeyboardFocus(false)) return;
            ReplaceOmniSelection(e.Text);
            e.Handled = true;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Console mode has no tabs, navigation instruments, drawer or command palette.
            // Preserve native window close and page input without invoking hidden chrome.
            if (Ja21Design.ConsoleMode)
            {
                if (e.Key == Key.F11) { ToggleImmersive(); e.Handled = true; }
                ModifierKeys consoleMods = Keyboard.Modifiers;
                bool consoleControl = (consoleMods & ModifierKeys.Control) != 0;
                if (e.Key == Key.F6 || (consoleControl && (e.Key == Key.T || e.Key == Key.N ||
                    e.Key == Key.W || e.Key == Key.L || e.Key == Key.K || e.Key == Key.B || e.Key == Key.Tab)))
                    e.Handled = true;
                return;
            }
            ModifierKeys mods = Keyboard.Modifiers;
            if (OmniKeyboardRecoveryEligible() && mods == ModifierKeys.None && (e.Key == Key.Back || e.Key == Key.Delete))
            {
                ClaimOmniKeyboardFocus(false);
                DeleteOmniText(e.Key == Key.Back);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F11) { Dispatch("fullscreen", "", 0); e.Handled = true; return; }

            if (e.Key == Key.Escape)
            {
                if (_commandPaletteOpen) { ToggleCommandPalette(false); e.Handled = true; return; }
                if (_drawer != null && _drawer.IsOpen) { _drawer.CloseDrawer(); e.Handled = true; return; }
                if (_omniBinOpen) { ToggleOmniBin(false); e.Handled = true; return; }
                if (_immersive && !_siteRequestedImmersive) { ToggleImmersive(); e.Handled = true; return; }
            }

            if (mods == ModifierKeys.Control && e.Key == Key.K) { ToggleCommandPalette(null); e.Handled = true; return; }
            if (mods == ModifierKeys.Control && e.Key == Key.B) { ToggleOmniBin(null); e.Handled = true; return; }
            if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.L) { RecenterOmniBar(true); e.Handled = true; return; }
            if (mods == ModifierKeys.Control && e.Key == Key.L) { Dispatch("focus_address", "", 0); e.Handled = true; return; }
            if (mods == ModifierKeys.Control && e.Key == Key.T) { Dispatch("new_tab", "", 0); e.Handled = true; return; }
            if (mods == ModifierKeys.Control && e.Key == Key.W) { Dispatch("close_active", "", 0); e.Handled = true; return; }
            if (mods == ModifierKeys.Control && e.Key == Key.R) { Dispatch("refresh", "", 0); e.Handled = true; return; }
            if (mods == ModifierKeys.Control && e.Key == Key.Tab) { SelectRelativeTab(1); e.Handled = true; return; }
            if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Tab) { SelectRelativeTab(-1); e.Handled = true; return; }
            if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N) { Dispatch("private_tab", "", 0); e.Handled = true; return; }
            if (mods == ModifierKeys.Alt && e.Key == Key.Left) { Dispatch("back", "", 0); e.Handled = true; return; }
            if (mods == ModifierKeys.Alt && e.Key == Key.Right) { Dispatch("forward", "", 0); e.Handled = true; return; }
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Run(string root)
        {
            PortablePaths.EnsureLayout(root);
            PortableFabric.Configure(root);
            PortableFabric.Record("boot.guard", "runtime-seal", "verify sealed application before mutable workspace use");
            RuntimeSeal.Verify(root);
            string source=File.ReadAllText(System.IO.Path.Combine(root,"source","browser.ja"),Encoding.UTF8); string sourceHash=RuntimeSeal.Sha256(Encoding.UTF8.GetBytes(source));
            JavaScriptSerializer js=new JavaScriptSerializer();js.MaxJsonLength=Int32.MaxValue;
            Dictionary<string,object> unit=(Dictionary<string,object>)js.DeserializeObject(File.ReadAllText(System.IO.Path.Combine(root,"engine","ja21","browser.ir.json"),Encoding.UTF8));
            if(!String.Equals(sourceHash,Convert.ToString(unit["sourceHash"]),StringComparison.OrdinalIgnoreCase))throw new Exception("JA21 source/IR identity mismatch.");
            Dictionary<string,object> corpus=(Dictionary<string,object>)js.DeserializeObject(File.ReadAllText(System.IO.Path.Combine(root,"engine","ja21","corpus.lock.json"),Encoding.UTF8)); if(Convert.ToInt32(corpus["suite_count"])!=20)throw new Exception("JA21 corpus lock is incomplete.");
            Dictionary<string,object> profile=(Dictionary<string,object>)js.DeserializeObject(File.ReadAllText(System.IO.Path.Combine(root,"engine","ja21","profile.json"),Encoding.UTF8));
            string[] forbidden=new string[]{"electron","bundled_chromium","mshtml","node_runtime_required"};
            int fi;for(fi=0;fi<forbidden.Length;fi++){object fv;if(profile.TryGetValue(forbidden[fi],out fv)&&Convert.ToBoolean(fv))throw new Exception("JA21 profile unexpectedly enables "+forbidden[fi]+".");}
            if(!String.Equals(Convert.ToString(profile["version"]),BrowserVersion.Full,StringComparison.Ordinal))throw new Exception("JA21 profile version does not match this presenter build.");
            string rendererSource=File.ReadAllText(System.IO.Path.Combine(root,"source","renderer.ja"),Encoding.UTF8);string rendererSourceHash=RuntimeSeal.Sha256(Encoding.UTF8.GetBytes(rendererSource));Dictionary<string,object> rendererUnit=(Dictionary<string,object>)js.DeserializeObject(File.ReadAllText(System.IO.Path.Combine(root,"engine","ja21","renderer.ir.json"),Encoding.UTF8));if(!String.Equals(rendererSourceHash,Convert.ToString(rendererUnit["sourceHash"]),StringComparison.OrdinalIgnoreCase))throw new Exception("JA Mirror renderer source/IR identity mismatch.");
            string webSource=File.ReadAllText(System.IO.Path.Combine(root,"source","web_engine.ja"),Encoding.UTF8);string webHash=RuntimeSeal.Sha256(Encoding.UTF8.GetBytes(webSource));Dictionary<string,object> webUnit=(Dictionary<string,object>)js.DeserializeObject(File.ReadAllText(System.IO.Path.Combine(root,"engine","ja21","web_engine.ir.json"),Encoding.UTF8));if(!String.Equals(webHash,Convert.ToString(webUnit["sourceHash"]),StringComparison.OrdinalIgnoreCase))throw new Exception("DF_Medium JA21 web-engine source/IR identity mismatch.");
            JaMirrorNetworkClient.Configure(root);
            PortableFabric.Record("semantic.command", "policy-ready", "JA21 source/IR identity verified");
            DfSmallHelper helper = null;
            VecSubstrate vec = null;
            if (!DesktopSession.Enabled)
            {
                throw new Exception("This compact Photon release starts through BOOT.cmd. The historical standalone browser/backends are preserved in history/JP250.zip.");
            }
            // The compatible Photon control plane is owned by the compiled native host. The modern
            // integrated surface has no native DF_Medium fallback and must not initialize
            // the dormant DF_Small/VEC1 research tree just to open the console.
            PortableFabric.Record("host.bridge", "photon-presenter", "Photon application surface; historical native backends not initialized");
            System.Windows.Application app=new System.Windows.Application();
            app.ShutdownMode=ShutdownMode.OnMainWindowClose;
            BrowserWindow window=new BrowserWindow(root,new Ja21Vm(unit),new Ja21Vm(rendererUnit),helper,vec);
            window.Loaded += delegate { PortableFabric.Record("native.render", "window-loaded", "native console window loaded"); };
            window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { PortableFabric.Record("native.render", "window-closing", "cancelled=" + e.Cancel); };
            window.Closed += delegate { PortableFabric.Record("native.render", "window-closed", "native console window closed"); };
            app.Dispatcher.ShutdownStarted += delegate { PortableFabric.Record("native.render", "dispatcher-shutdown", "dispatcher shutdown started"); };
            app.Exit += delegate(object sender, ExitEventArgs e) { PortableFabric.Record("native.render", "application-exit", "exit=" + e.ApplicationExitCode); };
            app.Run(window);
        }
    }
}
