using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace SteamOptimizer
{
    public static class Act
    {
        public const string Keep = "Keep";
        public const string Suspend = "Suspend";
        public const string Close = "Close";
    }

    public class ModeProfile
    {
        public bool Enabled = true;
        public bool CloseApps = true;
        public string PowerPlan = "high";       // "" = leave alone, "high" = High/Ultimate performance, or a GUID
        public bool HighPriority = true;
        public bool SuppressToasts = true;
        public bool KeepAwake = true;
        public bool TrimMemory = true;
        public List<string> StopServices = new List<string>();
    }

    public class AppConfig
    {
        public bool Enabled = true;
        public Dictionary<string, string> Rules = new Dictionary<string, string>();
        public string DefaultWindowed = Act.Close;      // apps that have a visible window and no rule
        public string DefaultBackground = Act.Keep;     // everything else with no rule
        public ModeProfile Local = new ModeProfile();
        public ModeProfile Streaming = new ModeProfile();
        public List<string> ExtraTriggers = new List<string>();        // non-Steam game exes
        public List<string> StreamIndicators = new List<string>(new string[] { "streaming_client", "sunshine", "parsec", "moonlight" });
        public bool OptimizeForStreamingClient = true;
        public bool StartWithWindows = false;
        public bool StartMinimized = true;
        public bool CloseToTray = true;
        public bool ShowToasts = true;
        public bool DryRun = false;
        public bool ForceKillAfterTimeout = false;
        public bool RelaunchMinimized = false;
        public int CloseTimeoutSec = 8;
        public int StartDelaySec = 5;
        public int EndGraceSec = 8;
        public int RelaunchStaggerMs = 400;

        public static AppConfig CreateDefault()
        {
            AppConfig c = new AppConfig();
            // Strict by default: only Claude plus remote-access/streaming plumbing survive. Every other windowed app is closed.
            string[] keep = { "claude", "tailscale-ipn", "tailscale", "sunshine", "parsecd", "anydesk", "teamviewer", "rustdesk" };
            foreach (string k in keep) c.Rules[k] = Act.Keep;
            c.Streaming.KeepAwake = true;
            c.Local.KeepAwake = false;
            return c;
        }
    }

    public class HistoryEntry
    {
        public DateTime Start;
        public DateTime End;
        public string Game = "";
        public string Mode = "";
        public int Closed;
        public int Suspended;
        public int FreedMB;
        public string Note = "";
    }

    public static class Store
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamOptimizer");
        public static string ConfigPath { get { return Path.Combine(Dir, "config.json"); } }
        public static string JournalPath { get { return Path.Combine(Dir, "session.json"); } }
        public static string HistoryPath { get { return Path.Combine(Dir, "history.json"); } }
        public static string LogPath { get { return Path.Combine(Dir, "log.txt"); } }

        static JavaScriptSerializer Ser() { JavaScriptSerializer s = new JavaScriptSerializer(); s.MaxJsonLength = int.MaxValue; return s; }

        public static T Load<T>(string path, T fallback)
        {
            try { if (File.Exists(path)) return Ser().Deserialize<T>(File.ReadAllText(path)); } catch { }
            return fallback;
        }

        public static void Save(string path, object o)
        {
            Directory.CreateDirectory(Dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Pretty(Ser().Serialize(o)));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static string Pretty(string json)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(); int ind = 0; bool q = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) q = !q;
                if (q) { sb.Append(c); continue; }
                switch (c)
                {
                    case '{': case '[': sb.Append(c).Append('\n').Append(' ', ++ind * 2); break;
                    case '}': case ']': sb.Append('\n').Append(' ', --ind * 2).Append(c); break;
                    case ',': sb.Append(c).Append('\n').Append(' ', ind * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public static AppConfig LoadConfig()
        {
            AppConfig c = Load<AppConfig>(ConfigPath, null);
            if (c == null) { c = AppConfig.CreateDefault(); try { Save(ConfigPath, c); } catch { } }
            if (c.Rules == null) c.Rules = new Dictionary<string, string>();
            if (c.Local == null) c.Local = new ModeProfile();
            if (c.Streaming == null) c.Streaming = new ModeProfile();
            if (c.ExtraTriggers == null) c.ExtraTriggers = new List<string>();
            if (c.StreamIndicators == null) c.StreamIndicators = new List<string>();
            Dictionary<string, string> norm = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kv in c.Rules) norm[kv.Key.ToLowerInvariant()] = kv.Value;
            c.Rules = norm;
            return c;
        }

        static readonly object logLock = new object();
        public static event Action<string> LogLine;
        public static void Log(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
            lock (logLock)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    FileInfo fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 1024 * 1024) File.Move(LogPath, LogPath + ".old");
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
                catch { }
            }
            Action<string> h = LogLine; if (h != null) h(line);
        }
    }
}
