using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Steam Game Optimizer")]
[assembly: System.Reflection.AssemblyVersion("0.1.0.0")]

namespace SteamOptimizer
{
    static class Program
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        [STAThread]
        static int Main(string[] args)
        {
            List<string> a = new List<string>(args);
            if (a.Contains("--selftest") || a.Contains("--dry-run"))
            {
                AttachConsole(-1);
                return a.Contains("--selftest") ? SelfTest.Run() : SelfTest.DryRun();
            }

            bool created;
            using (Mutex m = new Mutex(true, "SteamGameOptimizer.Single", out created))
            {
                if (!created && a.Contains("--elevated"))
                {
                    try { created = m.WaitOne(10000); } catch (AbandonedMutexException) { created = true; }
                }
                if (!created) { MessageBox.Show("Steam Game Optimizer is already running (check the system tray).", "Steam Game Optimizer"); return 0; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(a.Contains("--tray")));
            }
            return 0;
        }
    }

    /// <summary>Headless checks: --dry-run lists what would happen, --selftest exercises close/relaunch/suspend on Notepad.</summary>
    static class SelfTest
    {
        public static int DryRun()
        {
            AppConfig cfg = Store.LoadConfig();
            Optimizer opt = new Optimizer(cfg);
            GameState g = new GameState(); g.Active = true; g.Name = "(simulated)";
            Console.WriteLine("Apps that would be handled when a game starts:");
            foreach (Optimizer.Target t in opt.PlanTargets(g, Optimizer.Snapshot()))
                Console.WriteLine("  {0,-9} {1,-28} {2,3} proc  {3,6} MB{4}", t.Action, t.Name, t.Procs.Count, t.WS / 1048576, t.Explicit ? "  (rule)" : "");
            return 0;
        }

        static int fails;
        static void Check(string what, bool ok) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) fails++; }

        static bool IsSuspended(Process p)
        {
            try
            {
                p.Refresh();
                foreach (ProcessThread t in p.Threads)
                    if (t.ThreadState != System.Diagnostics.ThreadState.Wait || t.WaitReason != ThreadWaitReason.Suspended) return false;
                return p.Threads.Count > 0;
            }
            catch { return false; }
        }

        public static int Run()
        {
            Store.LogLine += delegate(string l) { Console.WriteLine("      " + l); };
            AppConfig cfg = new AppConfig();
            cfg.Rules["notepad"] = Act.Close;
            cfg.Rules["so_sleeper"] = Act.Suspend;
            cfg.DefaultWindowed = Act.Keep; cfg.DefaultBackground = Act.Keep;
            cfg.Local.PowerPlan = ""; cfg.Local.SuppressToasts = false; cfg.Local.KeepAwake = false; cfg.Local.HighPriority = false;

            string tmp = Path.Combine(Path.GetTempPath(), "so_selftest.txt");
            File.WriteAllText(tmp, "selftest");
            string sleeper = Path.Combine(Path.GetTempPath(), "so_sleeper.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), sleeper, true);
            Process np = Process.Start("notepad.exe", "\"" + tmp + "\"");
            ProcessStartInfo sp = new ProcessStartInfo(sleeper, "-n 60 127.0.0.1"); sp.CreateNoWindow = true; sp.UseShellExecute = false; sp.RedirectStandardOutput = true;
            Process sus = Process.Start(sp);
            Thread.Sleep(2500);

            Optimizer opt = new Optimizer(cfg);
            if (opt.Current != null) { Console.WriteLine("(stale journal found - ignoring for test)"); opt.Current = null; }
            GameState g = new GameState(); g.Active = true; g.Name = "SelfTest Game";

            // cmd is an ancestor of nothing here but protect-by-ancestor must not hit our own parent chain
            opt.BeginSession(g);
            while (opt.Busy || opt.Current == null) { Thread.Sleep(200); if (opt.Current == null && !opt.Busy) break; }
            Thread.Sleep(500);
            Check("journal written", File.Exists(Store.JournalPath));
            bool npGone = np.HasExited || Process.GetProcessesByName("notepad").Length == 0;
            Check("notepad closed by rule", npGone);
            bool held = opt.Current != null && opt.Current.Held.Exists(delegate(HeldApp h) { return h.Name == "notepad" && h.Relaunch; });
            Check("notepad recorded for relaunch", held);
            Check("so_sleeper suspended by rule", IsSuspended(sus));

            opt.EndSession("selftest");
            while (opt.Busy || opt.Current != null) Thread.Sleep(200);
            Thread.Sleep(1500);
            Check("notepad relaunched", Process.GetProcessesByName("notepad").Length > 0);
            Check("so_sleeper resumed", !IsSuspended(sus));
            Check("journal cleared", !File.Exists(Store.JournalPath));

            foreach (Process p in Process.GetProcessesByName("notepad")) try { p.Kill(); } catch { }
            try { sus.Kill(); } catch { }
            Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
            return fails;
        }
    }
}
