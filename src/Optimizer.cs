using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace SteamOptimizer
{
    public class ProcInfo
    {
        public int Pid, Ppid;
        public string Name = "", Path = "", Cmd = "", Title = "";
        public long WS, StartTicks;
        public bool HasWindow;
        public List<IntPtr> Windows = new List<IntPtr>();
    }

    public class HeldApp
    {
        public string Name = "", Action = "", Exe = "", Args = "", WorkDir = "";
        public List<int> Pids = new List<int>();
        public List<long> Starts = new List<long>();
        public int MB;
        public bool Relaunch;   // closed and eligible for relaunch
    }

    public class Journal
    {
        public DateTime Start;
        public string Game = "", Mode = "";
        public string PrevPlan = "";
        public bool PowerSet;
        public bool ToastSet;
        public bool PrevToastExisted;
        public int PrevToastValue;
        public List<HeldApp> Held = new List<HeldApp>();
        public List<string> Services = new List<string>();
        public bool ExtrasLocal, ExtrasStreaming;
        public int Closed, Suspended, FreedMB;
    }

    public class Optimizer
    {
        public static readonly string[] HardProtected = {
            "system","idle","registry","smss","csrss","wininit","winlogon","services","lsass","svchost","dwm","explorer","fontdrvhost",
            "sihost","taskhostw","ctfmon","runtimebroker","searchhost","searchindexer","startmenuexperiencehost","shellexperiencehost",
            "textinputhost","applicationframehost","audiodg","conhost","wudfhost","securityhealthsystray","securityhealthservice","msmpeng","nissrv",
            "dllhost","backgroundtaskhost","lockapp","systemsettings","widgets","steam","steamwebhelper","steamservice","gameoverlayui","gameoverlayui64",
            "streaming_client","steam_monitor","steamerrorreporter","steamoptimizer","nvdisplay.container","nvcontainer","wlanext","spoolsv","usocoreworker"
        };
        static readonly HashSet<string> hard = new HashSet<string>(HardProtected);

        public AppConfig Cfg;
        public Journal Current;           // null when idle
        public bool Busy { get; private set; }
        public event Action Changed;

        Thread awakeThread; ManualResetEvent awakeStop;
        readonly HashSet<int> prioritized = new HashSet<int>();
        DateTime lastPrioTry = DateTime.MinValue, sessionStart;
        readonly object gate = new object();

        public Optimizer(AppConfig cfg)
        {
            Cfg = cfg;
            Current = Store.Load<Journal>(Store.JournalPath, null);
        }

        void Fire() { Action a = Changed; if (a != null) a(); }
        static void Log(string m) { Store.Log(m); }

        public static bool IsElevated()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        // ---------- process snapshot ----------
        public static List<ProcInfo> Snapshot()
        {
            int mySession = Process.GetCurrentProcess().SessionId;
            Dictionary<int, string> titles = new Dictionary<int, string>();
            Dictionary<int, List<IntPtr>> wins = Native.VisibleWindows(titles);
            List<ProcInfo> list = new List<ProcInfo>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine,SessionId,WorkingSetSize,CreationDate FROM Win32_Process"))
                using (ManagementObjectCollection col = s.Get())
                    foreach (ManagementObject o in col)
                    {
                        using (o)
                        {
                            if (Convert.ToInt32(o["SessionId"] ?? 0) != mySession) continue;
                            ProcInfo p = new ProcInfo();
                            p.Pid = Convert.ToInt32(o["ProcessId"]);
                            p.Ppid = Convert.ToInt32(o["ParentProcessId"] ?? 0);
                            string n = (o["Name"] ?? "").ToString();
                            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                            p.Name = n.ToLowerInvariant();
                            p.Path = (o["ExecutablePath"] ?? "").ToString();
                            p.Cmd = (o["CommandLine"] ?? "").ToString();
                            p.WS = Convert.ToInt64(o["WorkingSetSize"] ?? 0);
                            try { p.StartTicks = ManagementDateTimeConverter.ToDateTime(o["CreationDate"].ToString()).ToUniversalTime().Ticks; } catch { }
                            List<IntPtr> w; if (wins.TryGetValue(p.Pid, out w)) { p.Windows = w; p.HasWindow = true; }
                            string t; if (titles.TryGetValue(p.Pid, out t)) p.Title = t;
                            list.Add(p);
                        }
                    }
            }
            catch (Exception ex) { Log("Snapshot failed: " + ex.Message); }
            return list;
        }

        static HashSet<int> Ancestors()
        {
            HashSet<int> set = new HashSet<int>();
            int me = Process.GetCurrentProcess().Id; set.Add(me);
            try
            {
                Dictionary<int, int> parent = new Dictionary<int, int>();
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId FROM Win32_Process"))
                    foreach (ManagementObject o in s.Get()) { using (o) parent[Convert.ToInt32(o["ProcessId"])] = Convert.ToInt32(o["ParentProcessId"] ?? 0); }
                int cur = me, guard = 0;
                while (parent.ContainsKey(cur) && guard++ < 32) { cur = parent[cur]; if (cur == 0 || !set.Add(cur)) break; }
            }
            catch { }
            return set;
        }

        static string WinDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }
        static bool UnderWindows(string path) { return path.Length > 0 && path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase); }

        public class Target { public string Name, Action; public List<ProcInfo> Procs = new List<ProcInfo>(); public long WS; public bool Explicit; }

        /// <summary>Works out which running apps would be touched, and how, for the current rules.</summary>
        public List<Target> PlanTargets(GameState game, List<ProcInfo> snap)
        {
            HashSet<int> anc = Ancestors();
            Dictionary<string, Target> groups = new Dictionary<string, Target>();
            foreach (ProcInfo p in snap)
            {
                if (hard.Contains(p.Name) || anc.Contains(p.Pid)) continue;
                if (game != null && game.InstallDir.Length > 0 && p.Path.StartsWith(game.InstallDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (game != null && game.TriggerExe.Length > 0 && p.Name == game.TriggerExe.ToLowerInvariant()) continue;
                Target t; if (!groups.TryGetValue(p.Name, out t)) { t = new Target(); t.Name = p.Name; groups[p.Name] = t; }
                t.Procs.Add(p); t.WS += p.WS;
            }
            List<Target> result = new List<Target>();
            foreach (Target t in groups.Values)
            {
                string rule; bool windowed = t.Procs.Exists(delegate(ProcInfo p) { return p.HasWindow; });
                if (Cfg.Rules.TryGetValue(t.Name, out rule)) t.Explicit = true;
                else
                {
                    if (t.Procs.Exists(delegate(ProcInfo p) { return UnderWindows(p.Path) || p.Path.Length == 0; })) continue; // never default-touch Windows components
                    rule = windowed ? Cfg.DefaultWindowed : Cfg.DefaultBackground;
                }
                if (rule == Act.Keep) continue;
                t.Action = rule; result.Add(t);
            }
            result.Sort(delegate(Target a, Target b) { return b.WS.CompareTo(a.WS); });
            return result;
        }

        // ---------- apply ----------
        public void BeginSession(GameState game)
        {
            lock (gate) { if (Busy || Current != null) return; Busy = true; }
            Thread t = new Thread(delegate() { try { DoApply(game); } catch (Exception ex) { Log("Apply error: " + ex); } finally { Busy = false; Fire(); } });
            t.IsBackground = true; t.Start();
        }

        public void EndSession(string reason)
        {
            lock (gate) { if (Busy || Current == null) return; Busy = true; }
            Thread t = new Thread(delegate() { try { DoRestore(reason); } catch (Exception ex) { Log("Restore error: " + ex); } finally { Busy = false; Fire(); } });
            t.IsBackground = true; t.Start();
        }

        void Save() { try { Store.Save(Store.JournalPath, Current); } catch (Exception ex) { Log("Journal save failed: " + ex.Message); } }

        void DoApply(GameState game)
        {
            ModeProfile prof = game.Streaming ? Cfg.Streaming : Cfg.Local;
            if (!prof.Enabled) { Log("Profile for " + (game.Streaming ? "streaming" : "local") + " play is disabled; skipping."); return; }
            Journal j = new Journal();
            j.Start = DateTime.Now; j.Game = game.Name; j.Mode = game.Streaming ? "Streaming" : "Local";
            sessionStart = DateTime.Now; prioritized.Clear();
            Log("=== Session start: " + game.Name + " [" + j.Mode + "] " + (Cfg.DryRun ? "(DRY RUN)" : "") + " ===");
            if (game.Streaming) Log("Streaming detected: " + game.StreamReason);
            Current = j;
            if (!Cfg.DryRun) Save();
            Fire();

            if (prof.CloseApps) ApplyApps(game, j, prof.TrimMemory);
            ApplyExtras(prof, j, game.Streaming);
            if (!Cfg.DryRun) Save();
            Log("Optimization complete: " + j.Closed + " closed, " + j.Suspended + " suspended, ~" + j.FreedMB + " MB released.");
        }

        /// <summary>Called when streaming begins after the session already started in local mode.</summary>
        public void UpgradeToStreaming(GameState game)
        {
            if (Current == null || Busy || Current.Mode == "Streaming") return;
            Current.Mode = "Streaming";
            Log("Streaming began mid-session (" + game.StreamReason + "); applying streaming profile extras.");
            ApplyExtras(Cfg.Streaming, Current, true);
            if (!Cfg.DryRun) Save();
            Fire();
        }

        void ApplyApps(GameState game, Journal j, bool trim)
        {
            List<ProcInfo> snap = Snapshot();
            List<Target> targets = PlanTargets(game, snap);
            Log("Found " + targets.Count + " app group(s) to handle.");
            Dictionary<int, ProcInfo> byPid = new Dictionary<int, ProcInfo>();
            foreach (ProcInfo p in snap) byPid[p.Pid] = p;

            foreach (Target t in targets)
            {
                int mb = (int)(t.WS / (1024 * 1024));
                HeldApp h = new HeldApp(); h.Name = t.Name; h.Action = t.Action; h.MB = mb;
                if (t.Action == Act.Suspend)
                {
                    if (Cfg.DryRun) { Log("[dry] would suspend " + t.Name + " (" + t.Procs.Count + " proc, " + mb + " MB)"); continue; }
                    foreach (ProcInfo p in t.Procs)
                        if (Native.Suspend(p.Pid))
                        {
                            h.Pids.Add(p.Pid); h.Starts.Add(p.StartTicks);
                            if (trim) try { using (Process pr = Process.GetProcessById(p.Pid)) Native.EmptyWorkingSet(pr.Handle); } catch { }
                        }
                    if (h.Pids.Count > 0) { j.Held.Add(h); j.Suspended++; j.FreedMB += mb; Log("Suspended " + t.Name + " (" + h.Pids.Count + " proc, " + mb + " MB)"); }
                    else Log("Could not suspend " + t.Name + " (access denied - is it elevated?)");
                }
                else // Close
                {
                    ProcInfo root = null;
                    foreach (ProcInfo p in t.Procs)
                        if (!byPid.ContainsKey(p.Ppid) || byPid[p.Ppid].Name != t.Name) { if (root == null || (p.HasWindow && !root.HasWindow)) root = p; }
                    if (root == null) root = t.Procs[0];
                    h.Exe = root.Path; h.WorkDir = root.Path.Length > 0 ? Path.GetDirectoryName(root.Path) : "";
                    h.Args = ExtractArgs(root.Cmd, root.Path);
                    if (Cfg.DryRun) { Log("[dry] would close " + t.Name + " (" + t.Procs.Count + " proc, " + mb + " MB)"); continue; }
                    foreach (ProcInfo p in t.Procs) foreach (IntPtr w in p.Windows) Native.PostMessage(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    if (!WaitGone(t.Procs, Cfg.CloseTimeoutSec * 1000))
                    {
                        if (Cfg.ForceKillAfterTimeout) { foreach (ProcInfo p in t.Procs) KillQuiet(p); WaitGone(t.Procs, 3000); Log("Force-killed " + t.Name); }
                    }
                    List<ProcInfo> alive = t.Procs.FindAll(delegate(ProcInfo p) { return IsAlive(p); });
                    if (alive.Count == 0 || (!IsAlive(root) && alive.TrueForAll(delegate(ProcInfo p) { return !p.HasWindow; })))
                    {
                        h.Relaunch = h.Exe.Length > 0; j.Held.Add(h); j.Closed++; j.FreedMB += mb;
                        Log("Closed " + t.Name + " (" + t.Procs.Count + " proc, " + mb + " MB)" + (h.Relaunch ? "" : " - cannot relaunch automatically"));
                    }
                    else Log("Left " + t.Name + " running - it did not close (unsaved work or a prompt?).");
                }
                if (!Cfg.DryRun) Save();
            }
        }

        static string ExtractArgs(string cmd, string exe)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            cmd = cmd.Trim();
            if (cmd.StartsWith("\"")) { int i = cmd.IndexOf('"', 1); return i < 0 ? "" : cmd.Substring(i + 1).Trim(); }
            if (!string.IsNullOrEmpty(exe) && cmd.StartsWith(exe, StringComparison.OrdinalIgnoreCase)) return cmd.Substring(exe.Length).Trim();
            int sp = cmd.IndexOf(' '); return sp < 0 ? "" : cmd.Substring(sp + 1).Trim();
        }

        static bool IsAlive(ProcInfo p)
        {
            try { using (Process pr = Process.GetProcessById(p.Pid)) return !pr.HasExited && pr.StartTime.ToUniversalTime().Ticks - p.StartTicks < TimeSpan.TicksPerSecond * 2; }
            catch { return false; }
        }
        static bool IsAlive(int pid, long startTicks)
        {
            try { using (Process pr = Process.GetProcessById(pid)) return !pr.HasExited && Math.Abs(pr.StartTime.ToUniversalTime().Ticks - startTicks) < TimeSpan.TicksPerSecond * 2; }
            catch { return false; }
        }
        static void KillQuiet(ProcInfo p) { try { using (Process pr = Process.GetProcessById(p.Pid)) pr.Kill(); } catch { } }
        static bool WaitGone(List<ProcInfo> procs, int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end)
            {
                if (!procs.Exists(delegate(ProcInfo p) { return IsAlive(p); })) return true;
                Thread.Sleep(250);
            }
            return !procs.Exists(delegate(ProcInfo p) { return IsAlive(p); });
        }

        // ---------- extras (power, toasts, services, keep-awake) ----------
        void ApplyExtras(ModeProfile prof, Journal j, bool streaming)
        {
            if (streaming ? j.ExtrasStreaming : j.ExtrasLocal) return;
            if (streaming) j.ExtrasStreaming = true; else j.ExtrasLocal = true;

            if (prof.PowerPlan.Length > 0 && !j.PowerSet)
            {
                string cur = ActivePlan(); string target = ResolvePlan(prof.PowerPlan);
                if (target.Length > 0 && !string.Equals(cur, target, StringComparison.OrdinalIgnoreCase))
                {
                    if (Cfg.DryRun) Log("[dry] would switch power plan " + cur + " -> " + target);
                    else if (Run("powercfg", "/setactive " + target) == 0) { j.PrevPlan = cur; j.PowerSet = true; Log("Power plan -> " + target); }
                    else Log("Could not switch power plan.");
                }
            }
            if (prof.SuppressToasts && !j.ToastSet)
            {
                if (Cfg.DryRun) Log("[dry] would disable toast notifications");
                else try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications"))
                    {
                        object v = k.GetValue("ToastEnabled");
                        j.PrevToastExisted = v != null; j.PrevToastValue = v is int ? (int)v : 1;
                        k.SetValue("ToastEnabled", 0, RegistryValueKind.DWord); j.ToastSet = true; Log("Notification toasts suppressed.");
                    }
                }
                catch (Exception ex) { Log("Toast suppression failed: " + ex.Message); }
            }
            foreach (string svc in prof.StopServices)
            {
                if (j.Services.Contains(svc)) continue;
                if (Cfg.DryRun) { Log("[dry] would stop service " + svc); continue; }
                try
                {
                    using (ServiceController sc = new ServiceController(svc))
                        if (sc.Status == ServiceControllerStatus.Running && sc.CanStop)
                        { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)); j.Services.Add(svc); Log("Stopped service " + svc); }
                }
                catch (Exception ex) { Log("Service " + svc + " not stopped (" + ex.Message.Split('\n')[0] + ") - needs admin."); }
            }
            if (prof.KeepAwake && awakeThread == null && !Cfg.DryRun) StartAwake();
        }

        void StartAwake()
        {
            awakeStop = new ManualResetEvent(false);
            ManualResetEvent stop = awakeStop;
            awakeThread = new Thread(delegate()
            {
                Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);
                stop.WaitOne();
                Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
            });
            awakeThread.IsBackground = true; awakeThread.Start();
            Log("Sleep / screen-off blocked for the session.");
        }
        void StopAwake()
        {
            if (awakeThread == null) return;
            awakeStop.Set(); awakeThread.Join(1000); awakeThread = null;
        }

        /// <summary>Periodic work while a session is active (raising game priority once its process appears).</summary>
        public void Tick(GameState game)
        {
            if (Current == null || Busy || Cfg.DryRun) return;
            ModeProfile prof = Current.Mode == "Streaming" ? Cfg.Streaming : Cfg.Local;
            if (!prof.HighPriority) return;
            double age = (DateTime.Now - sessionStart).TotalSeconds;
            if ((DateTime.Now - lastPrioTry).TotalSeconds < (age < 90 ? 4 : 30)) return;
            lastPrioTry = DateTime.Now;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (prioritized.Contains(p.Id)) continue;
                    bool match = false;
                    if (game.TriggerExe.Length > 0) match = string.Equals(p.ProcessName, game.TriggerExe, StringComparison.OrdinalIgnoreCase);
                    else if (game.InstallDir.Length > 0) match = p.MainModule.FileName.StartsWith(game.InstallDir, StringComparison.OrdinalIgnoreCase);
                    if (match && p.PriorityClass != ProcessPriorityClass.High && p.PriorityClass != ProcessPriorityClass.RealTime)
                    { p.PriorityClass = ProcessPriorityClass.High; Log("Raised priority of " + p.ProcessName + " to High."); }
                    if (match) prioritized.Add(p.Id);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        // ---------- restore ----------
        void DoRestore(string reason)
        {
            Journal j = Current; if (j == null) return;
            Log("=== Restoring (" + reason + ") ===");
            int resumed = 0, relaunched = 0;
            foreach (HeldApp h in j.Held)
            {
                if (h.Action == Act.Suspend)
                {
                    for (int i = 0; i < h.Pids.Count; i++)
                        if (IsAlive(h.Pids[i], h.Starts[i]) && Native.Resume(h.Pids[i])) resumed++;
                }
            }
            foreach (HeldApp h in j.Held)
            {
                if (h.Action != Act.Close || !h.Relaunch) continue;
                if (Process.GetProcessesByName(h.Name).Length > 0) { Log(h.Name + " is already running again; not relaunching."); continue; }
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    if (IsElevated() && h.Args.Length == 0) { psi.FileName = "explorer.exe"; psi.Arguments = "\"" + h.Exe + "\""; }   // de-elevated launch
                    else { psi.FileName = h.Exe; psi.Arguments = h.Args; psi.UseShellExecute = true; }
                    if (h.WorkDir.Length > 0 && Directory.Exists(h.WorkDir) && psi.FileName != "explorer.exe") psi.WorkingDirectory = h.WorkDir;
                    if (Cfg.RelaunchMinimized) psi.WindowStyle = ProcessWindowStyle.Minimized;
                    Process.Start(psi); relaunched++; Log("Relaunched " + h.Name);
                    Thread.Sleep(Cfg.RelaunchStaggerMs);
                }
                catch (Exception ex) { Log("Could not relaunch " + h.Name + ": " + ex.Message); }
            }
            if (j.PowerSet && j.PrevPlan.Length > 0) { Run("powercfg", "/setactive " + j.PrevPlan); Log("Power plan restored."); }
            if (j.ToastSet)
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications"))
                    { if (j.PrevToastExisted) k.SetValue("ToastEnabled", j.PrevToastValue, RegistryValueKind.DWord); else k.DeleteValue("ToastEnabled", false); }
                    Log("Notification toasts restored.");
                }
                catch (Exception ex) { Log("Could not restore toasts: " + ex.Message); }
            foreach (string svc in j.Services)
                try { using (ServiceController sc = new ServiceController(svc)) { sc.Start(); Log("Restarted service " + svc); } }
                catch (Exception ex) { Log("Could not restart " + svc + ": " + ex.Message); }
            StopAwake();

            HistoryEntry he = new HistoryEntry();
            he.Start = j.Start; he.End = DateTime.Now; he.Game = j.Game; he.Mode = j.Mode;
            he.Closed = j.Closed; he.Suspended = j.Suspended; he.FreedMB = j.FreedMB; he.Note = reason;
            List<HistoryEntry> hist = Store.Load<List<HistoryEntry>>(Store.HistoryPath, new List<HistoryEntry>());
            hist.Add(he); if (hist.Count > 200) hist.RemoveRange(0, hist.Count - 200);
            try { Store.Save(Store.HistoryPath, hist); } catch { }

            Current = null;
            try { File.Delete(Store.JournalPath); } catch { }
            Log("Restore complete: " + resumed + " resumed, " + relaunched + " relaunched.");
        }

        // ---------- power helpers ----------
        static string ActivePlan()
        {
            Match m = Regex.Match(Capture("powercfg", "/getactivescheme"), "([0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})");
            return m.Success ? m.Groups[1].Value : "";
        }
        public static List<KeyValuePair<string, string>> PowerPlans()
        {
            List<KeyValuePair<string, string>> r = new List<KeyValuePair<string, string>>();
            foreach (Match m in Regex.Matches(Capture("powercfg", "/list"), "([0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\\s+\\(([^)]*)\\)"))
                r.Add(new KeyValuePair<string, string>(m.Groups[1].Value, m.Groups[3].Value));
            return r;
        }
        static string ResolvePlan(string spec)
        {
            if (spec != "high") return spec;
            string ultimate = "", high = "";
            foreach (KeyValuePair<string, string> p in PowerPlans())
            {
                if (p.Key.StartsWith("e9a42b02", StringComparison.OrdinalIgnoreCase)) ultimate = p.Key;
                if (p.Key.StartsWith("8c5e7fda", StringComparison.OrdinalIgnoreCase)) high = p.Key;
            }
            if (ultimate.Length > 0) return ultimate;
            if (high.Length > 0) return high;
            Capture("powercfg", "/duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
            foreach (KeyValuePair<string, string> p in PowerPlans()) if (p.Value.IndexOf("High performance", StringComparison.OrdinalIgnoreCase) >= 0) return p.Key;
            return "";
        }
        static int Run(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args); psi.CreateNoWindow = true; psi.UseShellExecute = false;
                using (Process p = Process.Start(psi)) { p.WaitForExit(10000); return p.ExitCode; }
            }
            catch { return -1; }
        }
        static string Capture(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi)) { string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(10000); return o; }
            }
            catch { return ""; }
        }
    }
}
