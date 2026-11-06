using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Steam Game Optimizer Setup")]
[assembly: AssemblyVersion("0.2.0.0")]

namespace SteamOptimizerSetup
{
    static class Setup
    {
        const string Version = "0.2.0";
        const string AppName = "Steam Game Optimizer";
        const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SteamGameOptimizer";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static string DefaultDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\" + AppName); } }
        static string StartMenu { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
        static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool silent = Array.IndexOf(args, "/S") >= 0;
            if (Array.IndexOf(args, "/uninstall") >= 0) return Uninstall(silent);
            if (silent) { try { Install(DefaultDir, true, Array.IndexOf(args, "/autostart") >= 0, false); return 0; } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; } }
            Application.Run(new SetupForm());
            return 0;
        }

        static void StopApp()
        {
            foreach (Process p in Process.GetProcessesByName("SteamOptimizer"))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
                p.Dispose();
            }
        }

        public static void Install(string dir, bool startMenu, bool autostart, bool desktop)
        {
            StopApp();
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "SteamOptimizer.exe");
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.exe"))
            using (FileStream f = File.Create(exe)) s.CopyTo(f);
            string un = Path.Combine(dir, "Uninstall.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, un, true);

            if (startMenu) Shortcut(StartMenu, exe, dir);
            if (desktop) Shortcut(DesktopLnk, exe, dir);
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            { if (autostart) k.SetValue("SteamGameOptimizer", "\"" + exe + "\" --tray"); else k.DeleteValue("SteamGameOptimizer", false); }
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "grioghar");
                k.SetValue("InstallLocation", dir);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("UninstallString", "\"" + un + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + un + "\" /uninstall /S");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        static void Shortcut(string lnk, string target, string dir)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { dir });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { AppName });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        static int Uninstall(bool silent)
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            bool wipe = false;
            if (!silent)
            {
                if (MessageBox.Show("Remove " + AppName + " from this PC?", AppName + " Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
                wipe = MessageBox.Show("Also delete your settings and history?\r\n(" + SettingsDir + ")\r\n\r\nChoose No to keep them for a future reinstall.", AppName + " Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            }
            // make sure nothing is left held: the app restores held apps on exit, so ask it nicely first
            foreach (Process p in Process.GetProcessesByName("SteamOptimizer"))
            {
                try { p.CloseMainWindow(); } catch { }
                p.Dispose();
            }
            Thread.Sleep(1500);
            StopApp();
            try { File.Delete(StartMenu); } catch { }
            try { File.Delete(DesktopLnk); } catch { }
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true)) k.DeleteValue("SteamGameOptimizer", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
            if (wipe) try { Directory.Delete(SettingsDir, true); } catch { }
            // this exe is running from the install dir, so delete the folder from a detached helper
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + dir + "\"");
            psi.CreateNoWindow = true; psi.UseShellExecute = false;
            Process.Start(psi);
            if (!silent) MessageBox.Show(AppName + " was removed.", AppName + " Uninstall");
            return 0;
        }

        static string SettingsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamOptimizer"); } }

        class SetupForm : Form
        {
            TextBox path; CheckBox cStart, cDesk, cAuto, cLaunch;
            public SetupForm()
            {
                Text = AppName + " Setup"; ClientSize = new Size(500, 330); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
                StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 9.5f);
                Controls.Add(new Label { Text = AppName, Font = new Font("Segoe UI Semibold", 16f), AutoSize = true, Location = new Point(20, 16), ForeColor = Color.FromArgb(0, 122, 204) });
                Controls.Add(new Label { Text = "Version " + Version + " - closes distractions while you play or stream Steam games, then restores them.", Location = new Point(22, 54), Size = new Size(460, 38), ForeColor = Color.DimGray });
                Controls.Add(new Label { Text = "Install location (no administrator needed):", AutoSize = true, Location = new Point(22, 104) });
                path = new TextBox { Text = DefaultDir, Location = new Point(22, 128), Width = 366 };
                Button browse = new Button { Text = "Browse...", Location = new Point(396, 126), Size = new Size(84, 28) };
                browse.Click += delegate { using (FolderBrowserDialog d = new FolderBrowserDialog()) if (d.ShowDialog() == DialogResult.OK) path.Text = Path.Combine(d.SelectedPath, AppName); };
                cStart = new CheckBox { Text = "Start Menu shortcut", Checked = true, AutoSize = true, Location = new Point(24, 172) };
                cDesk = new CheckBox { Text = "Desktop shortcut", AutoSize = true, Location = new Point(24, 198) };
                cAuto = new CheckBox { Text = "Start with Windows (runs in the tray)", Checked = true, AutoSize = true, Location = new Point(24, 224) };
                cLaunch = new CheckBox { Text = "Launch when setup finishes", Checked = true, AutoSize = true, Location = new Point(24, 250) };
                Button go = new Button { Text = "Install", Location = new Point(300, 286), Size = new Size(88, 30) };
                Button cancel = new Button { Text = "Cancel", Location = new Point(396, 286), Size = new Size(84, 30) };
                cancel.Click += delegate { Close(); };
                go.Click += delegate
                {
                    try
                    {
                        Install(path.Text.Trim(), cStart.Checked, cAuto.Checked, cDesk.Checked);
                        if (cLaunch.Checked) Process.Start(Path.Combine(path.Text.Trim(), "SteamOptimizer.exe"), "--tray");
                        MessageBox.Show(AppName + " is installed." + (cLaunch.Checked ? "\r\nLook for its icon in the system tray." : ""), AppName + " Setup");
                        Close();
                    }
                    catch (Exception ex) { MessageBox.Show("Install failed: " + ex.Message, AppName + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                };
                Controls.AddRange(new Control[] { path, browse, cStart, cDesk, cAuto, cLaunch, go, cancel });
                AcceptButton = go; CancelButton = cancel;
            }
        }
    }
}
