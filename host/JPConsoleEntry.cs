using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;

namespace VBJA21
{
    internal static class JPConsoleEntry
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            string workspace = Path.Combine(root, "workspace");
            bool probe = args.Length == 1 && args[0] == "--probe";
            try
            {
                Console.InputEncoding = new UTF8Encoding(false);
                Console.OutputEncoding = new UTF8Encoding(false);
                bool developmentRenderer = args.Length == 1 && args[0] == "--development-installed-renderer";
                if (args.Length != 0 && !developmentRenderer && !probe) throw new Exception("The native console accepts only BOOT.cmd's private broker session or its offline --probe.");
                if (!Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem) throw new Exception("Windows x64 is required.");
                if (!probe && !Console.IsInputRedirected) throw new Exception("Use BOOT.cmd to supply the private broker session.");
                RuntimeSeal.RejectReparsePoints(root, root);
                foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
                {
                    string key = Convert.ToString(item.Key);
                    if (key.StartsWith("JA21_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase)) Environment.SetEnvironmentVariable(key, null);
                }
                if (!probe)
                {
                    StringBuilder envelope = new StringBuilder();
                    for (int index = 0; index <= 2048; index++)
                    {
                        int ch = Console.In.Read();
                        if (ch < 0 || ch == 10) break;
                        if (ch == 13) continue;
                        if (index == 2048) throw new Exception("Native bootstrap exceeds its size limit.");
                        envelope.Append((char)ch);
                    }
                    System.Web.Script.Serialization.JavaScriptSerializer json = new System.Web.Script.Serialization.JavaScriptSerializer();
                    json.MaxJsonLength = 2048; json.RecursionLimit = 4;
                    Dictionary<string, object> session = json.DeserializeObject(envelope.ToString()) as Dictionary<string, object>;
                    if (session == null || (session.Count != 4 && session.Count != 5)) throw new Exception("Invalid private bootstrap fields.");
                    object qualification;
                    string selectedWorkspace = workspace;
                    if (session.TryGetValue("qualificationWorkspace", out qualification))
                    {
                        if (!(qualification is string)) throw new Exception("Invalid private qualification workspace.");
                        selectedWorkspace = QualificationWorkspace(root, (string)qualification);
                        session.Remove("qualificationWorkspace");
                    }
                    // The optional host-only field never reaches the browser bootstrap.
                    // DesktopSession retains its exact four-field protocol and credential checks.
                    workspace = selectedWorkspace;
                    DesktopSession.Configure(json.Serialize(session));
                    string origin = DesktopSession.Origin;
                    Environment.SetEnvironmentVariable("JA21_CONSOLE_ORIGINS", origin);
                    Environment.SetEnvironmentVariable("JA21_START_URL", origin + "/#ja21-desktop");
                    envelope.Clear(); session = null;
                }
                Environment.SetEnvironmentVariable("JA21_PORTABLE_ROOT", workspace);
                Environment.SetEnvironmentVariable("JA21_WEB_DATA_ROOT", Path.Combine(workspace, "wv"));
                Environment.SetEnvironmentVariable("JA21_VEC1_STATE_ROOT", Path.Combine(workspace, "state", "vec1"));
                Environment.SetEnvironmentVariable("JA21_CONSOLE_MODE", "1");
                Environment.SetEnvironmentVariable("JA21_REDUCED_MOTION", "1");
                Environment.SetEnvironmentVariable("JA21_ALLOW_PRIVATE_HOSTS", "0");
                string fixedRenderer = Path.Combine(root, "runtime", "webview2-fixed");
                if (!developmentRenderer)
                {
                    RuntimeSeal.RejectReparsePoints(root, fixedRenderer);
                    if (!File.Exists(Path.Combine(fixedRenderer, "msedgewebview2.exe"))) throw new Exception("The bundled browser files are missing. Extract the complete JP300 ZIP before running BOOT.cmd; no download or installed-renderer fallback was attempted.");
                    Environment.SetEnvironmentVariable("JA21_WEBVIEW2_FIXED_ROOT", fixedRenderer);
                    Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", fixedRenderer);
                }
                if (probe)
                {
                    Dictionary<string, object> result = new Dictionary<string, object>();
                    result["schema"] = "JP300-MANAGED-PROBE/1";
                    result["ok"] = true;
                    result["clrVersion"] = Environment.Version.ToString();
                    result["wpfAssembly"] = typeof(Window).Assembly.Location;
                    result["adapterAssembly"] = typeof(Microsoft.Web.WebView2.Core.CoreWebView2Environment).Assembly.Location;
                    result["rendererFolder"] = fixedRenderer;
                    result["rendererVersion"] = LiveWebFactory.RuntimeVersion(root);
                    result["process64"] = Environment.Is64BitProcess;
                    result["installedRendererFallback"] = false;
                    Console.WriteLine(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(result));
                    return 0;
                }
                PortablePaths.EnsureLayout(root);
                Environment.SetEnvironmentVariable("TEMP", Path.Combine(workspace, "temp"));
                Environment.SetEnvironmentVariable("TMP", Path.Combine(workspace, "temp"));
                LiveWebFactory.RuntimeVersion(root);
                Program.Run(root);
                return 0;
            }
            catch (Exception ex)
            {
                string failure = "JP300 native console startup failed: " + ex.Message;
                try
                {
                    if (!probe)
                    {
                        string log = Path.Combine(workspace, "logs", "console-error.txt");
                        RuntimeSeal.RejectReparsePoints(root, log);
                        Directory.CreateDirectory(Path.GetDirectoryName(log));
                        File.WriteAllText(log, DateTime.UtcNow.ToString("o") + Environment.NewLine + failure, new UTF8Encoding(false));
                    }
                }
                catch { }
                Console.Error.WriteLine(failure);
                return 1;
            }
            finally { DesktopSession.Clear(); }
        }

        private static string QualificationWorkspace(string root, string value)
        {
            if (String.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value)) throw new Exception("Invalid private qualification workspace.");
            string full = Path.GetFullPath(value);
            string boundary = Path.GetFullPath(Path.Combine(root, "workspace", "qualification"));
            string prefix = boundary.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(full, value, StringComparison.OrdinalIgnoreCase)) throw new Exception("Private qualification workspace escapes its boundary.");
            string relative = full.Substring(prefix.Length);
            foreach (string part in relative.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new Exception("Invalid private qualification workspace component.");
            }
            RuntimeSeal.RejectReparsePoints(root, full);
            return full;
        }
    }
}
