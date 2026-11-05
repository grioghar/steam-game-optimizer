using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SteamOptimizer
{
    public class GameState
    {
        public bool Active;
        public int AppId;
        public string Name = "";
        public string InstallDir = "";
        public string TriggerExe = "";   // set for non-Steam triggers
        public bool Streaming;
        public string StreamReason = "";

        public string Key { get { return Active ? (AppId + "|" + TriggerExe) : ""; } }
    }

    /// <summary>Detects a running Steam game (local or Remote Play hosted) via Steam's own registry key.</summary>
    public class SteamMonitor
    {
        string steamPath = "";
        List<string> libs = new List<string>();
        DateTime libsLoaded = DateTime.MinValue;
        Dictionary<int, string[]> nameCache = new Dictionary<int, string[]>();
        public bool ForceStreaming;   // manual override from the UI

        public SteamMonitor()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", "");
                if (v != null && v.ToString().Length > 0) steamPath = Path.GetFullPath(v.ToString());
            }
            catch { }
        }

        public string SteamPath { get { return steamPath; } }

        static int ReadRunningAppId()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0);
                if (v is int) return (int)v;
                if (v != null) { int r; if (int.TryParse(v.ToString(), out r)) return r; }
            }
            catch { }
            return 0;
        }

        void LoadLibs()
        {
            if ((DateTime.Now - libsLoaded).TotalMinutes < 5 && libs.Count > 0) return;
            libsLoaded = DateTime.Now;
            List<string> l = new List<string>();
            if (steamPath.Length > 0)
            {
                l.Add(steamPath);
                try
                {
                    string vdf = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        {
                            string p = m.Groups[1].Value.Replace("\\\\", "\\");
                            if (!l.Exists(delegate(string x) { return string.Equals(x, p, StringComparison.OrdinalIgnoreCase); })) l.Add(p);
                        }
                }
                catch { }
            }
            libs = l;
        }

        /// <summary>Returns {name, installDir} for an app id by reading its appmanifest.</summary>
        string[] Lookup(int appId)
        {
            string[] r;
            if (nameCache.TryGetValue(appId, out r)) return r;
            LoadLibs();
            r = new string[] { "Steam app " + appId, "" };
            foreach (string lib in libs)
            {
                try
                {
                    string acf = Path.Combine(lib, @"steamapps\appmanifest_" + appId + ".acf");
                    if (!File.Exists(acf)) continue;
                    string t = File.ReadAllText(acf);
                    Match n = Regex.Match(t, "\"name\"\\s+\"([^\"]*)\"");
                    Match d = Regex.Match(t, "\"installdir\"\\s+\"([^\"]*)\"");
                    if (n.Success) r[0] = n.Groups[1].Value;
                    if (d.Success) r[1] = Path.Combine(lib, @"steamapps\common\" + d.Groups[1].Value);
                    break;
                }
                catch { }
            }
            if (appId > 0x7FFFFF && r[1].Length == 0) r[0] = "Non-Steam game (shortcut " + appId + ")";
            nameCache[appId] = r;
            return r;
        }

        static bool AnyRunning(IEnumerable<string> names, out string found)
        {
            found = "";
            foreach (string n in names)
            {
                string clean = n.Trim();
                if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(0, clean.Length - 4);
                if (clean.Length == 0) continue;
                Process[] ps = Process.GetProcessesByName(clean);
                bool any = ps.Length > 0;
                foreach (Process p in ps) p.Dispose();
                if (any) { found = clean; return true; }
            }
            return false;
        }

        /// <summary>Best-effort Remote Play detection (host: recent streaming log activity; client: streaming_client.exe).</summary>
        string StreamingReason(AppConfig cfg)
        {
            if (ForceStreaming) return "manual override";
            string hit;
            if (AnyRunning(cfg.StreamIndicators, out hit)) return hit + ".exe running";
            try
            {
                string log = Path.Combine(steamPath, @"logs\streaming_log.txt");
                if (File.Exists(log) && (DateTime.Now - File.GetLastWriteTime(log)).TotalSeconds < 30) return "Steam streaming log active";
            }
            catch { }
            return "";
        }

        public GameState Poll(AppConfig cfg)
        {
            GameState g = new GameState();
            int id = ReadRunningAppId();
            if (id > 0)
            {
                string[] info = Lookup(id);
                g.Active = true; g.AppId = id; g.Name = info[0]; g.InstallDir = info[1];
            }
            else
            {
                string trig;
                if (cfg.ExtraTriggers.Count > 0 && AnyRunning(cfg.ExtraTriggers, out trig))
                {
                    g.Active = true; g.TriggerExe = trig; g.Name = trig + " (non-Steam)";
                }
                else if (cfg.OptimizeForStreamingClient)
                {
                    Process[] ps = Process.GetProcessesByName("streaming_client");
                    bool any = ps.Length > 0; foreach (Process p in ps) p.Dispose();
                    if (any) { g.Active = true; g.TriggerExe = "streaming_client"; g.Name = "Steam Remote Play (this PC is the client)"; }
                }
            }
            if (g.Active) { g.StreamReason = StreamingReason(cfg); g.Streaming = g.StreamReason.Length > 0; }
            return g;
        }
    }
}
