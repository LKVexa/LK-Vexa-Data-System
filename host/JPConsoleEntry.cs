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
            try
            {
                bool developmentRenderer = args.Length == 1 && args[0] == "--development-installed-renderer";
                if (args.Length != 0 && !developmentRenderer) throw new Exception("The native console accepts only the broker's private bootstrap pipe.");
                if (!Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem) throw new Exception("Windows x64 is required.");
                if (!Console.IsInputRedirected) throw new Exception("Use Start Console.cmd to supply the private broker session.");
                RuntimeSeal.RejectReparsePoints(root, root);
                foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
                {
                    string key = Convert.ToString(item.Key);
                    if (key.StartsWith("JA21_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase)) Environment.SetEnvironmentVariable(key, null);
                }
                string workspace = Path.Combine(root, "workspace");
                Environment.SetEnvironmentVariable("JA21_PORTABLE_ROOT", workspace);
                Environment.SetEnvironmentVariable("JA21_WEB_DATA_ROOT", Path.Combine(workspace, "wv"));
                Environment.SetEnvironmentVariable("JA21_VEC1_STATE_ROOT", Path.Combine(workspace, "state", "vec1"));
                Environment.SetEnvironmentVariable("JA21_CONSOLE_MODE", "1");
                Environment.SetEnvironmentVariable("JA21_REDUCED_MOTION", "1");
                Environment.SetEnvironmentVariable("JA21_ALLOW_PRIVATE_HOSTS", "0");
                PortablePaths.EnsureLayout(root);
                Environment.SetEnvironmentVariable("TEMP", Path.Combine(workspace, "temp"));
                Environment.SetEnvironmentVariable("TMP", Path.Combine(workspace, "temp"));
                string fixedRenderer = Path.Combine(root, "runtime", "webview2-fixed");
                if (!developmentRenderer)
                {
                    RuntimeSeal.RejectReparsePoints(root, fixedRenderer);
                    if (!File.Exists(Path.Combine(fixedRenderer, "msedgewebview2.exe"))) throw new Exception("The app-local Fixed Version WebView2 runtime is missing. This candidate cannot yet launch offline. Complete renderer packaging; no download or Evergreen fallback was attempted.");
                    Environment.SetEnvironmentVariable("JA21_WEBVIEW2_FIXED_ROOT", fixedRenderer);
                    Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", fixedRenderer);
                }
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
                if (session == null || session.Count != 4 || !session.ContainsKey("origin") || !(session["origin"] is string)) throw new Exception("Invalid private bootstrap fields.");
                string origin = (string)session["origin"];
                Environment.SetEnvironmentVariable("JA21_CONSOLE_ORIGINS", origin);
                Environment.SetEnvironmentVariable("JA21_START_URL", origin + "/#ja21-desktop");
                DesktopSession.Configure(envelope.ToString());
                envelope.Clear(); session = null;
                LiveWebFactory.RuntimeVersion(root);
                Program.Run(root);
                return 0;
            }
            catch (Exception ex)
            {
                string failure = "JP300 native console startup failed: " + ex.Message;
                try
                {
                    string log = Path.Combine(root, "workspace", "logs", "console-error.txt");
                    RuntimeSeal.RejectReparsePoints(root, log);
                    Directory.CreateDirectory(Path.GetDirectoryName(log));
                    File.WriteAllText(log, DateTime.UtcNow.ToString("o") + Environment.NewLine + failure, new UTF8Encoding(false));
                }
                catch { }
                Console.Error.WriteLine(failure);
                return 1;
            }
            finally { DesktopSession.Clear(); }
        }
    }
}
