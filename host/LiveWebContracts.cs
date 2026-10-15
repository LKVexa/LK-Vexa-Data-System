using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Text.RegularExpressions;
using System.Windows;

namespace VBJA21
{

    /// <summary>One origin, one in-memory credential and one bootstrap response. No native command API.</summary>
    public static class DesktopSession
    {
        private static Uri _origin;
        private static string _token;
        private static string _engineToken;
        public static bool Enabled { get { return _origin != null; } }
        public static string Origin { get { return _origin == null ? "" : _origin.GetLeftPart(UriPartial.Authority); } }

        public static void Configure(string json)
        {
            if (Enabled) throw new Exception("Native session is already configured.");
            try
            {
                if (String.IsNullOrEmpty(json) || json.Length > 2048) throw new Exception();
                JavaScriptSerializer serializer = new JavaScriptSerializer(); serializer.MaxJsonLength = 2048; serializer.RecursionLimit = 4;
                Dictionary<string, object> value = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (value == null || value.Count != 4 || !value.ContainsKey("protocol") || !(value["protocol"] is int) || (int)value["protocol"] != 2 ||
                    !value.ContainsKey("origin") || !(value["origin"] is string) || !value.ContainsKey("token") || !(value["token"] is string) || !value.ContainsKey("engineToken") || !(value["engineToken"] is string)) throw new Exception();
                string origin = (string)value["origin"], token = (string)value["token"], engineToken = (string)value["engineToken"]; Uri parsed;
                if (engineToken == token || engineToken.Length != 64 || !Regex.IsMatch(engineToken, "^[a-f0-9]{64}$") || token.Length != 64 || !Regex.IsMatch(token, "^[a-f0-9]{64}$") || !Uri.TryCreate(origin, UriKind.Absolute, out parsed) ||
                    (parsed.Scheme != "http" && parsed.Scheme != "https") || !String.IsNullOrEmpty(parsed.UserInfo) || parsed.AbsolutePath != "/" ||
                    !String.IsNullOrEmpty(parsed.Query) || !String.IsNullOrEmpty(parsed.Fragment) || origin != parsed.GetLeftPart(UriPartial.Authority) ||
                    parsed.Host == "0.0.0.0" || parsed.Host == "[::]" || parsed.Host == "::" || parsed.Host.Contains("*")) throw new Exception();
                _token = token; _engineToken = engineToken; _origin = parsed;
            }
            catch { throw new Exception("Invalid native bootstrap envelope; credentials were not logged or saved."); }
        }

        public static bool MatchesOrigin(Uri target)
        {
            return Enabled && target != null && String.IsNullOrEmpty(target.UserInfo) && target.Scheme == _origin.Scheme &&
                String.Equals(target.DnsSafeHost, _origin.DnsSafeHost, StringComparison.OrdinalIgnoreCase) && target.Port == _origin.Port;
        }
        public static bool AllowsSocket(Uri target)
        {
            if (!Enabled || target == null || !String.IsNullOrEmpty(target.UserInfo) || !String.IsNullOrEmpty(target.Query) || !String.IsNullOrEmpty(target.Fragment)) return false;
            if (target.AbsolutePath != "/socket" && target.AbsolutePath != "/engine") return false;
            string expected = _origin.Scheme == "https" ? "wss" : "ws";
            return target.Scheme == expected && target.Port == _origin.Port && String.Equals(target.DnsSafeHost, _origin.DnsSafeHost, StringComparison.OrdinalIgnoreCase);
        }
        public static bool IsAppDocument(string raw)
        {
            Uri uri; return Uri.TryCreate(raw, UriKind.Absolute, out uri) && MatchesOrigin(uri) && uri.AbsolutePath == "/" &&
                String.IsNullOrEmpty(uri.Query) && uri.Fragment == "#ja21-desktop";
        }
        public static string Reply(string sender, string currentDocument, string request)
        {
            if (!Enabled || !IsAppDocument(sender) || !IsAppDocument(currentDocument) || String.IsNullOrEmpty(request) || request.Length > 512) return null;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer(); serializer.MaxJsonLength = 2048; serializer.RecursionLimit = 4;
                Dictionary<string, object> value = serializer.DeserializeObject(request) as Dictionary<string, object>;
                if (value == null || value.Count != 3 || !value.ContainsKey("type") || !(value["type"] is string) || (string)value["type"] != "ja21.session.request" ||
                    !value.ContainsKey("protocol") || !(value["protocol"] is int) || (int)value["protocol"] != 2 || !value.ContainsKey("nonce") || !(value["nonce"] is string) ||
                    ((string)value["nonce"]).Length != 32 || !Regex.IsMatch((string)value["nonce"], "^[a-f0-9]{32}$")) return null;
                Dictionary<string, object> response = new Dictionary<string, object>();
                response["type"] = "ja21.session.bootstrap"; response["protocol"] = 2; response["nonce"] = value["nonce"];
                response["origin"] = Origin; response["endpoint"] = (Origin.StartsWith("https:", StringComparison.Ordinal) ? "wss:" + Origin.Substring(6) : "ws:" + Origin.Substring(5)) + "/socket";
                response["token"] = _token; response["engineToken"] = _engineToken;
                return serializer.Serialize(response);
            }
            catch { return null; }
        }
        public static void Clear() { _token = null; _engineToken = null; _origin = null; }
    }

    internal interface ILiveWebSurface : IDisposable
    {
        FrameworkElement View { get; }
        bool Ready { get; }
        string EngineLabel { get; }
        string Source { get; }
        string DocumentTitle { get; }
        bool CanGoBack { get; }
        bool CanGoForward { get; }
        event Action<string, string> NavigationCommitted;
        event Action<string> NavigationFailed;
        event Action<string> StatusChanged;
        event Action<bool> FullScreenChanged;
        void Navigate(string url);
        void GoBack();
        void GoForward();
        void Reload();
        void Stop();
        void ClearBrowsingData();
        System.Threading.Tasks.Task<bool> ShutdownAsync();
    }
}
