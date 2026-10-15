// JA21 9.8.7 portable desktop path and DF-tier placement layer.
// The Windows/WPF shell remains the presenter. Node assignments below are host-mediated
// service placement/evidence records; they do not claim that WPF or WebView2 executes
// inside a DF VM and they never create a fabric execution verdict.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VBJA21
{
    internal static class PortablePaths
    {
        private static string _root;
        private static readonly object Gate = new object();

        // The presenter deliberately stays compatible with default Windows path
        // handling. These are UTF-16 String.Length counts, excluding the NUL.
        internal const int PackageRootLimit = 100;
        internal const int FilePathLimit = 259;
        internal const int DirectoryPathLimit = 247;

        internal static string RequireWindowsPath(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > FilePathLimit)
                throw new IOException("Windows file path exceeds 259 UTF-16 code units or is empty; use a shorter path.");
            // Legacy Framework normalization can trim trailing dots/spaces, so
            // reject those aliases before asking it for the canonical path.
            CheckWindowsPathComponents(value);
            string full = System.IO.Path.GetFullPath(value);
            string volume = System.IO.Path.GetPathRoot(full);
            if (full.Length > FilePathLimit || volume == null || volume.Length != 3 || volume[1] != ':')
                throw new IOException("Use a local absolute Windows drive path of at most 259 UTF-16 code units.");
            string parent = System.IO.Path.GetDirectoryName(full);
            if (parent != null && parent.Length > DirectoryPathLimit)
                throw new IOException("Windows parent directory exceeds 247 UTF-16 code units; use a shorter path.");
            CheckWindowsPathComponents(full.Substring(volume.Length));
            return full;
        }

        private static void CheckWindowsPathComponents(string value)
        {
            string[] parts = value.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < parts.Length; index++)
            {
                string part = parts[index];
                if (part == "." || part == "..") continue;
                if (index == 0 && part.Length == 2 && part[1] == ':' && Char.IsLetter(part[0])) continue;
                string device = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                bool reserved = device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" || device == "CONIN$" || device == "CONOUT$" ||
                    (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                    ((device[3] >= '0' && device[3] <= '9') || device[3] == '\u00b9' || device[3] == '\u00b2' || device[3] == '\u00b3'));
                if (part.Length > 255 || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || reserved)
                    throw new IOException("Invalid, reserved, or aliased Windows path component.");
            }
        }

        internal static string RequirePackageRoot(string value)
        {
            string full = RequireWindowsPath(value);
            if (full.TrimEnd('\\', '/').Length > PackageRootLimit)
                throw new IOException("Package root exceeds 100 UTF-16 code units; extract the complete package to a short local folder such as C:\\LKVS.");
            return full;
        }

        public static string Workspace(string packageRoot)
        {
            lock (Gate)
            {
                packageRoot = RequirePackageRoot(packageRoot);
                if (!String.IsNullOrWhiteSpace(_root))
                {
                    RuntimeSeal.RejectReparsePoints(_root, _root);
                    return _root;
                }
                string p = Environment.GetEnvironmentVariable("JA21_PORTABLE_ROOT");
                if (String.IsNullOrWhiteSpace(p)) p = System.IO.Path.Combine(packageRoot, "workspace");
                p = System.IO.Path.GetFullPath(p);
                if (!String.Equals(Environment.GetEnvironmentVariable("JA21_ALLOW_EXTERNAL_PORTABLE_ROOT"), "1", StringComparison.Ordinal))
                {
                    string baseDir = System.IO.Path.GetFullPath(packageRoot);
                    if (!baseDir.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)) baseDir += System.IO.Path.DirectorySeparatorChar;
                    string probe = p;
                    if (!probe.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)) probe += System.IO.Path.DirectorySeparatorChar;
                    if (!probe.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("Portable workspace must remain inside the JA21 package unless JA21_ALLOW_EXTERNAL_PORTABLE_ROOT=1 is explicitly set.");
                    RuntimeSeal.RejectReparsePoints(baseDir, p);
                }
                else RuntimeSeal.RejectReparsePoints(p, p);
                _root = p;
                return _root;
            }
        }

        public static string EnsureLayout(string packageRoot)
        {
            string w = Workspace(packageRoot);
            string[] dirs = new string[] {
                "state", "wv", "state\\vec1", "state\\ui", "desktop", "documents", "downloads", "uploads",
                "logs", "temp", "cache", "home", "host-env\\LocalAppData", "host-env\\RoamingAppData",
                "runtime", "evidence", "snapshots"
            };
            int i;
            for (i = 0; i < dirs.Length; i++) Directory.CreateDirectory(WithinWorkspace(packageRoot, dirs[i]));
            return w;
        }

        private static string WithinWorkspace(string packageRoot, string relative)
        {
            string w = Workspace(packageRoot);
            string path = System.IO.Path.Combine(w, relative);
            RuntimeSeal.RejectReparsePoints(w, path);
            return path;
        }

        public static string State(string packageRoot) { return WithinWorkspace(packageRoot, "state"); }
        public static string Vec1State(string packageRoot) { return WithinWorkspace(packageRoot, "state\\vec1"); }
        public static string LiveWebState(string packageRoot) { return WithinWorkspace(packageRoot, "wv"); }
        public static string Downloads(string packageRoot) { return WithinWorkspace(packageRoot, "downloads"); }
        public static string Desktop(string packageRoot) { return WithinWorkspace(packageRoot, "desktop"); }
        public static string Documents(string packageRoot) { return WithinWorkspace(packageRoot, "documents"); }
        public static string Temp(string packageRoot) { return WithinWorkspace(packageRoot, "temp"); }
        public static string Logs(string packageRoot) { return WithinWorkspace(packageRoot, "logs"); }
        public static string Evidence(string packageRoot) { return WithinWorkspace(packageRoot, "evidence"); }
        public static string MutableRuntime(string packageRoot) { return WithinWorkspace(packageRoot, "runtime"); }

        public static string WebView2SdkRoot(string packageRoot)
        {
            string sealedRoot = System.IO.Path.Combine(packageRoot, "runtime", "webview2-sdk");
            if (Directory.Exists(sealedRoot)) return sealedRoot;
            return System.IO.Path.Combine(MutableRuntime(packageRoot), "webview2-sdk");
        }

        public static string FixedWebView2Root(string packageRoot)
        {
            string p = Environment.GetEnvironmentVariable("JA21_WEBVIEW2_FIXED_ROOT");
            if (String.IsNullOrWhiteSpace(p)) p = System.IO.Path.Combine(packageRoot, "runtime", "webview2-fixed");
            if (!Directory.Exists(p))
            {
                string mutable = System.IO.Path.Combine(MutableRuntime(packageRoot), "webview2-fixed");
                if (Directory.Exists(mutable)) p = mutable;
            }
            return Directory.Exists(p) ? System.IO.Path.GetFullPath(p) : "";
        }

        public static string UniqueDownloadPath(string packageRoot, string suggestedPath)
        {
            string dir = Downloads(packageRoot); Directory.CreateDirectory(dir);
            string name = System.IO.Path.GetFileName(suggestedPath);
            if (String.IsNullOrWhiteSpace(name)) name = "download.bin";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.TrimEnd(' ', '.');
            if (name.Length == 0) name = "download.bin";
            if (name.Length > 180) name = name.Substring(0, 180).TrimEnd(' ', '.');
            string stem = System.IO.Path.GetFileNameWithoutExtension(name); string ext = System.IO.Path.GetExtension(name);
            string device = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" || device == "CONIN$" || device == "CONOUT$" ||
                (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                ((device[3] >= '0' && device[3] <= '9') || device[3] == '\u00b9' || device[3] == '\u00b2' || device[3] == '\u00b3')))
            { stem = "_" + stem; name = stem + ext; }
            // The full path, not just its basename, must fit the presenter's Win32 API budget.
            // Reserve space for a duplicate suffix without forcing a short installation root.
            int budget = 259 - dir.Length - 1 - 12;
            if (budget < 24) throw new IOException("Actual download directory leaves too little filename space: " + dir);
            if (ext.Length > 16) ext = ext.Substring(0, 16);
            int stemBudget = Math.Max(1, budget - ext.Length);
            if (stem.Length > stemBudget) stem = stem.Substring(0, stemBudget);
            if (stem.Length > 0 && Char.IsHighSurrogate(stem[stem.Length - 1])) stem = stem.Substring(0, stem.Length - 1);
            if (stem.Length == 0) stem = "download";
            name = stem + ext;
            string candidate = System.IO.Path.Combine(dir, name); int i = 1;
            while (true)
            {
                if (i > 1000) throw new IOException("Too many duplicate download names; choose a new filename.");
                RuntimeSeal.RejectReparsePoints(Workspace(packageRoot), candidate);
                try
                {
                    // Reserve atomically, including against simultaneous WebView2 tabs.
                    using (FileStream reservation = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    return candidate;
                }
                catch (IOException)
                {
                    if (!File.Exists(candidate) && !Directory.Exists(candidate)) throw;
                    candidate = System.IO.Path.Combine(dir, stem + " (" + i.ToString(CultureInfo.InvariantCulture) + ")" + ext); i++;
                }
            }
        }
    }

    internal static class PortableUiState
    {
        private static double Clamp01(double v) { return Double.IsNaN(v) || Double.IsInfinity(v) ? 0.5 : (v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v)); }

        public static string OmniBarPlacementPath(string packageRoot)
        {
            string dir = System.IO.Path.Combine(PortablePaths.State(packageRoot), "ui");
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "omni-bar.position");
        }

        public static bool TryLoadOmniBarPlacement(string packageRoot, out double x, out double y)
        {
            x = 0.5; y = 0.0;
            try
            {
                string path = OmniBarPlacementPath(packageRoot);
                if (!File.Exists(path)) return false;
                string[] parts = File.ReadAllText(path, Encoding.UTF8).Trim().Split(',');
                double px, py;
                if (parts.Length != 2 || !Double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out px) ||
                    !Double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out py) ||
                    Double.IsNaN(px) || Double.IsInfinity(px) || Double.IsNaN(py) || Double.IsInfinity(py)) return false;
                x = Clamp01(px); y = Clamp01(py); return true;
            }
            catch { return false; }
        }

        public static void SaveOmniBarPlacement(string packageRoot, double x, double y)
        {
            string path = OmniBarPlacementPath(packageRoot);
            string text = Clamp01(x).ToString("0.000000", CultureInfo.InvariantCulture) + "," +
                Clamp01(y).ToString("0.000000", CultureInfo.InvariantCulture) + Environment.NewLine;
            VecIo.WriteAtomic(path, text);
        }
    }

    internal static class PortableFabric
    {
        private static readonly object Gate = new object();
        private static string _packageRoot = "";
        private static string _journal = "";
        private static long _sequence;
        private static readonly Dictionary<string,string> Placement = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            {"boot.guard", "N_SMALL"}, {"security.policy", "N_SMALL"}, {"manifest.verify", "N_SMALL"}, {"heartbeat", "N_SMALL"},
            {"semantic.command", "N_MEDIUM"}, {"native.render", "N_MEDIUM"}, {"native.verify", "N_MEDIUM"}, {"document.inspect", "N_MEDIUM"},
            {"live.web", "N_LARGE"}, {"device.io", "N_LARGE"}, {"downloads", "N_LARGE"}, {"permissions", "N_LARGE"}, {"host.bridge", "N_LARGE"},
            {"workspace.state", "N_XLARGE"}, {"session.snapshot", "N_XLARGE"}, {"history.replay", "N_XLARGE"}, {"long.running", "N_XLARGE"},
            {"fabric.route", "DF0"}, {"fabric.replay", "DF0"}, {"fabric.diff", "DF0"}, {"fabric.registry", "DF0"}
        };

        public static void Configure(string packageRoot)
        {
            lock (Gate)
            {
                _packageRoot = packageRoot;
                PortablePaths.EnsureLayout(packageRoot);
                _journal = System.IO.Path.Combine(PortablePaths.Evidence(packageRoot), "fabric-placement.jsonl");
                _sequence = 0;
                Record("fabric.registry", "portable-session-start", "host-mediated placement registry active; no fabric verdict implied");
            }
        }

        public static string NodeFor(string service)
        {
            string n; return Placement.TryGetValue(service ?? "", out n) ? n : "DF0";
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            StringBuilder b = new StringBuilder();
            VecCanonical.WriteString(b, s);
            return b.ToString().Substring(1, b.Length - 2);
        }

        public static void Record(string service, string operation, string detail)
        {
            try
            {
                lock (Gate)
                {
                    if (String.IsNullOrWhiteSpace(_packageRoot)) return;
                    long seq = ++_sequence; string node = NodeFor(service);
                    string semantic = "{\"schema\":\"JA21/PORTABLE_FABRIC_EVENT/1\",\"sequence\":" + seq.ToString(CultureInfo.InvariantCulture) +
                        ",\"service\":\"" + Esc(service) + "\",\"node\":\"" + Esc(node) + "\",\"operation\":\"" + Esc(operation) +
                        "\",\"detail\":\"" + Esc(detail) + "\",\"execution_claim\":\"HOST_MEDIATED_PLACEMENT_ONLY\",\"fabric_verdict\":null}";
                    string hash = RuntimeSeal.Sha256(Encoding.UTF8.GetBytes(semantic));
                    string line = semantic.Substring(0, semantic.Length - 1) + ",\"semantic_sha256\":\"" + hash + "\",\"observed_utc\":\"" +
                        DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\"}" + Environment.NewLine;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_journal));
                    File.AppendAllText(_journal, line, new UTF8Encoding(false));
                }
            }
            catch { /* evidence logging cannot take down the browser presenter */ }
        }
    }
}
