using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SteamOptimizer
{
    public class MainForm : Form
    {
        AppConfig cfg;
        Optimizer opt;
        SteamMonitor monitor = new SteamMonitor();
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        NotifyIcon tray = new NotifyIcon();
        Icon appIcon;
        bool startHidden, reallyExit, paused, manualSession;
        DateTime? firstSeen, goneSince;
        string attemptedKey = "";
        GameState lastGame = new GameState();

        // dashboard
        Label lblState, lblSub, lblStats;
        ListView lvHeld;
        TextBox txtLog;
        Button btnOptimize, btnRestore;
        CheckBox chkPause, chkForceStream;
        // apps
        DataGridView grid;
        CheckBox chkBackground;
        bool loadingGrid;
        // history
        DataGridView histGrid; Label lblTotals;

        static readonly Color Accent = Color.FromArgb(0, 122, 204);

        public MainForm(bool tray)
        {
            startHidden = tray;
            cfg = Store.LoadConfig();
            opt = new Optimizer(cfg);
            appIcon = MakeIcon();
            Icon = appIcon;
            Text = "Steam Game Optimizer";
            Font = new Font("Segoe UI", 9.5f);
            Size = new Size(980, 700); MinimumSize = new Size(860, 560);
            StartPosition = FormStartPosition.CenterScreen;

            TabControl tabs = new TabControl(); tabs.Dock = DockStyle.Fill; tabs.Padding = new Point(14, 6);
            tabs.TabPages.Add(BuildDashboard());
            tabs.TabPages.Add(BuildApps());
            tabs.TabPages.Add(BuildProfiles());
            tabs.TabPages.Add(BuildSettings());
            tabs.TabPages.Add(BuildHistory());
            Controls.Add(tabs);
            tabs.SelectedIndexChanged += delegate { if (tabs.SelectedTab.Text == "Apps") RefreshApps(); if (tabs.SelectedTab.Text == "History") LoadHistory(); };

            BuildTray();
            Store.LogLine += OnLog;
            opt.Changed += delegate { SafeInvoke(delegate { UpdateDashboard(); if (opt.Current == null) LoadHistory(); }); };
            timer.Interval = 1500; timer.Tick += OnTick; timer.Start();
            FormClosing += OnFormClosing;
            Shown += delegate { if (startHidden) { Hide(); } else RefreshApps(); };
            Load += delegate { if (startHidden) { ShowInTaskbar = false; BeginInvoke(new Action(Hide)); } };
            if (opt.Current != null) Store.Log("Found an unfinished session from a previous run (" + opt.Current.Game + ") - will restore once no game is running.");
            UpdateDashboard();
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(value); }

        void SafeInvoke(Action a) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { } }

        // ================= tray / icon =================
        static Icon MakeIcon()
        {
            Bitmap b = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (LinearGradientBrush br = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(0, 150, 230), Color.FromArgb(20, 60, 160), 90f))
                    g.FillEllipse(br, 1, 1, 30, 30);
                PointF[] bolt = { new PointF(18, 4), new PointF(8, 18), new PointF(15, 18), new PointF(13, 28), new PointF(24, 13), new PointF(17, 13) };
                g.FillPolygon(Brushes.White, bolt);
            }
            return Icon.FromHandle(b.GetHicon());
        }

        void BuildTray()
        {
            tray.Icon = appIcon; tray.Text = "Steam Game Optimizer"; tray.Visible = true;
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("Open", null, delegate { ShowWindow(); });
            m.Items.Add("Optimize now", null, delegate { OptimizeNow(); });
            m.Items.Add("Restore now", null, delegate { RestoreNow(); });
            ToolStripMenuItem pz = new ToolStripMenuItem("Pause automation"); pz.CheckOnClick = true;
            pz.CheckedChanged += delegate { paused = pz.Checked; chkPause.Checked = paused; };
            m.Items.Add(pz);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Exit (restores everything)", null, delegate { reallyExit = true; Close(); });
            tray.ContextMenuStrip = m;
            tray.DoubleClick += delegate { ShowWindow(); };
        }

        void ShowWindow() { ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate(); RefreshApps(); }

        void Toast(string title, string text)
        {
            if (cfg.ShowToasts) try { tray.ShowBalloonTip(4000, title, text, ToolTipIcon.Info); } catch { }
        }

        void OnFormClosing(object s, FormClosingEventArgs e)
        {
            if (!reallyExit && cfg.CloseToTray && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            if (opt.Current != null)
            {
                timer.Stop();
                Store.Log("Exiting - restoring held apps first.");
                opt.EndSession("app exit");
                DateTime end = DateTime.Now.AddSeconds(60);
                while ((opt.Busy || opt.Current != null) && DateTime.Now < end) { Thread.Sleep(150); Application.DoEvents(); }
            }
            tray.Visible = false; tray.Dispose();
        }

        // ================= automation loop =================
        void OnTick(object s, EventArgs e)
        {
            try { Tick(); } catch (Exception ex) { Store.Log("Monitor error: " + ex.Message); }
        }

        void Tick()
        {
            monitor.ForceStreaming = chkForceStream != null && chkForceStream.Checked;
            if (opt.Busy) { UpdateDashboard(); return; }
            if (!cfg.Enabled || paused) { lastGame = new GameState(); UpdateDashboard(); return; }
            GameState g = monitor.Poll(cfg);
            lastGame = g;
            if (g.Active)
            {
                goneSince = null;
                if (opt.Current == null)
                {
                    if (attemptedKey == g.Key) { UpdateDashboard(); return; }
                    if (firstSeen == null) { firstSeen = DateTime.Now; Toast("Game detected", g.Name + (g.Streaming ? " (streaming)" : "") + " - optimizing in " + cfg.StartDelaySec + "s"); }
                    if ((DateTime.Now - firstSeen.Value).TotalSeconds >= cfg.StartDelaySec) { attemptedKey = g.Key; manualSession = false; opt.BeginSession(g); }
                }
                else
                {
                    opt.Tick(g);
                    if (g.Streaming && opt.Current.Mode == "Local") opt.UpgradeToStreaming(g);
                }
            }
            else
            {
                firstSeen = null; attemptedKey = "";
                if (opt.Current != null && !manualSession)
                {
                    if (goneSince == null) goneSince = DateTime.Now;
                    if ((DateTime.Now - goneSince.Value).TotalSeconds >= cfg.EndGraceSec)
                    {
                        goneSince = null; opt.EndSession("game exited");
                        Toast("Session ended", "Restoring your apps...");
                    }
                }
            }
            UpdateDashboard();
        }

        void OptimizeNow()
        {
            if (opt.Busy || opt.Current != null) return;
            GameState g = monitor.Poll(cfg);
            if (!g.Active) { g.Active = true; g.Name = "Manual session"; g.Streaming = chkForceStream != null && chkForceStream.Checked; }
            manualSession = true; opt.BeginSession(g);
        }
        void RestoreNow() { manualSession = false; opt.EndSession("manual restore"); }

        // ================= dashboard =================
        TabPage BuildDashboard()
        {
            TabPage p = new TabPage("Dashboard"); p.Padding = new Padding(16);
            lblState = new Label { Text = "Idle", Font = new Font("Segoe UI Semibold", 22f), AutoSize = true, Location = new Point(16, 12), ForeColor = Accent };
            lblSub = new Label { Text = "Waiting for a Steam game...", AutoSize = true, Location = new Point(20, 58), ForeColor = Color.DimGray };
            lblStats = new Label { Text = "", AutoSize = true, Location = new Point(20, 80), ForeColor = Color.DimGray };
            btnOptimize = new Button { Text = "Optimize now", Location = new Point(20, 112), Size = new Size(130, 32) };
            btnRestore = new Button { Text = "Restore now", Location = new Point(158, 112), Size = new Size(130, 32) };
            btnOptimize.Click += delegate { OptimizeNow(); };
            btnRestore.Click += delegate { RestoreNow(); };
            chkPause = new CheckBox { Text = "Pause automation", Location = new Point(310, 118), AutoSize = true };
            chkPause.CheckedChanged += delegate { paused = chkPause.Checked; };
            chkForceStream = new CheckBox { Text = "Treat next session as streaming", Location = new Point(460, 118), AutoSize = true };
            lvHeld = new ListView { View = View.Details, FullRowSelect = true, GridLines = true, Location = new Point(20, 160), Size = new Size(900, 220), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            lvHeld.Columns.Add("Held app", 240); lvHeld.Columns.Add("Action", 130); lvHeld.Columns.Add("Memory (MB)", 100); lvHeld.Columns.Add("Will be restored by", 300);
            txtLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Location = new Point(20, 392), Size = new Size(900, 220), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Font = new Font("Consolas", 9f), BackColor = Color.White };
            p.Controls.AddRange(new Control[] { lblState, lblSub, lblStats, btnOptimize, btnRestore, chkPause, chkForceStream, lvHeld, txtLog });
            return p;
        }

        void OnLog(string line)
        {
            SafeInvoke(delegate { if (txtLog.TextLength > 60000) txtLog.Text = txtLog.Text.Substring(30000); txtLog.AppendText(line + "\r\n"); });
        }

        void UpdateDashboard()
        {
            if (lblState == null) return;
            Journal j = opt.Current;
            string state, sub, stats = "";
            if (opt.Busy) { state = j == null ? "Optimizing..." : "Restoring..."; sub = lastGame.Active ? lastGame.Name : ""; }
            else if (j != null) { state = "Optimized"; sub = j.Game + "  -  " + j.Mode + " mode  -  running for " + Fmt(DateTime.Now - j.Start); stats = j.Closed + " closed, " + j.Suspended + " suspended, ~" + j.FreedMB + " MB released"; }
            else if (!cfg.Enabled || paused) { state = "Paused"; sub = "Automation is off. Games will not be optimized."; }
            else if (lastGame.Active) { state = "Game detected"; sub = lastGame.Name + (lastGame.Streaming ? "  (streaming: " + lastGame.StreamReason + ")" : ""); }
            else { state = "Idle"; sub = "Waiting for a Steam game" + (monitor.SteamPath.Length == 0 ? "  (Steam install not found!)" : "") + "..."; }
            lblState.Text = state; lblSub.Text = sub; lblStats.Text = stats;
            lblState.ForeColor = j != null ? Color.FromArgb(16, 137, 62) : (state == "Paused" ? Color.DarkOrange : Accent);
            btnOptimize.Enabled = !opt.Busy && j == null; btnRestore.Enabled = !opt.Busy && j != null;
            tray.Text = "Steam Game Optimizer - " + (state.Length > 40 ? state.Substring(0, 40) : state);

            lvHeld.BeginUpdate(); lvHeld.Items.Clear();
            if (j != null)
                foreach (HeldApp h in new List<HeldApp>(j.Held))
                {
                    ListViewItem it = new ListViewItem(h.Name);
                    it.SubItems.Add(h.Action == Act.Suspend ? "Suspended" : "Closed");
                    it.SubItems.Add(h.MB.ToString());
                    it.SubItems.Add(h.Action == Act.Suspend ? "Resume in place" : (h.Relaunch ? "Relaunch: " + Path.GetFileName(h.Exe) : "Cannot relaunch"));
                    lvHeld.Items.Add(it);
                }
            lvHeld.EndUpdate();
        }

        static string Fmt(TimeSpan t) { return t.TotalHours >= 1 ? ((int)t.TotalHours + "h " + t.Minutes + "m") : (t.Minutes + "m " + t.Seconds + "s"); }

        void SaveCfg() { try { Store.Save(Store.ConfigPath, cfg); } catch (Exception ex) { MessageBox.Show("Could not save settings: " + ex.Message); } }

        // ================= apps tab =================
        TabPage BuildApps()
        {
            TabPage p = new TabPage("Apps"); p.Padding = new Padding(10);
            Panel top = new Panel { Dock = DockStyle.Top, Height = 74 };
            Label l1 = new Label { Text = "Apps with a window and no rule:", AutoSize = true, Location = new Point(4, 10) };
            ComboBox c1 = ActionCombo(false, cfg.DefaultWindowed, delegate(string v) { cfg.DefaultWindowed = v; SaveCfg(); RecomputeEffective(); }); c1.Location = new Point(200, 6);
            Label l2 = new Label { Text = "Background / tray apps with no rule:", AutoSize = true, Location = new Point(330, 10) };
            ComboBox c2 = ActionCombo(false, cfg.DefaultBackground, delegate(string v) { cfg.DefaultBackground = v; SaveCfg(); RecomputeEffective(); }); c2.Location = new Point(560, 6);
            chkBackground = new CheckBox { Text = "Show background processes", AutoSize = true, Location = new Point(4, 44) };
            chkBackground.CheckedChanged += delegate { RefreshApps(); };
            Button refresh = new Button { Text = "Refresh", Location = new Point(220, 40), Size = new Size(80, 28) }; refresh.Click += delegate { RefreshApps(); };
            Label l3 = new Label { Text = "Add by name:", AutoSize = true, Location = new Point(330, 46) };
            TextBox add = new TextBox { Location = new Point(410, 42), Width = 150 };
            Button addBtn = new Button { Text = "Add rule", Location = new Point(566, 40), Size = new Size(80, 28) };
            addBtn.Click += delegate
            {
                string n = add.Text.Trim().ToLowerInvariant(); if (n.EndsWith(".exe")) n = n.Substring(0, n.Length - 4);
                if (n.Length == 0) return; if (!cfg.Rules.ContainsKey(n)) cfg.Rules[n] = Act.Keep; SaveCfg(); add.Text = ""; RefreshApps();
            };
            top.Controls.AddRange(new Control[] { l1, c1, l2, c2, chkBackground, refresh, l3, add, addBtn });

            grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "App", HeaderText = "App", ReadOnly = true, FillWeight = 22 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Title", HeaderText = "Window", ReadOnly = true, FillWeight = 34 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mem", HeaderText = "MB", ReadOnly = true, FillWeight = 8 });
            DataGridViewComboBoxColumn rc = new DataGridViewComboBoxColumn { Name = "Rule", HeaderText = "Rule", FlatStyle = FlatStyle.Flat, FillWeight = 16 };
            rc.Items.AddRange(new object[] { "(auto)", Act.Keep, Act.Suspend, Act.Close });
            grid.Columns.Add(rc);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Effective", HeaderText = "During a game", ReadOnly = true, FillWeight = 20 });
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += OnRuleChanged;
            grid.DataError += delegate { };
            Label hint = new Label { Dock = DockStyle.Bottom, Height = 44, Text = "Keep = never touch.  Suspend = freeze it (instant resume, exact state, frees CPU and trims RAM).  Close = ask it to quit (saves work prompts are respected), then relaunch afterward.\r\nCore Windows, Steam and this app are always protected.", ForeColor = Color.DimGray };
            p.Controls.Add(grid); p.Controls.Add(top); p.Controls.Add(hint);
            return p;
        }

        ComboBox ActionCombo(bool withAuto, string cur, Action<string> onChange)
        {
            ComboBox c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            c.Items.AddRange(new object[] { Act.Keep, Act.Suspend, Act.Close }); c.SelectedItem = cur;
            c.SelectedIndexChanged += delegate { onChange((string)c.SelectedItem); };
            return c;
        }

        string Effective(string name, bool windowed, bool underWin)
        {
            string r; if (cfg.Rules.TryGetValue(name, out r)) return r == Act.Keep ? "Keep" : (r == Act.Suspend ? "Suspend" : "Close + relaunch");
            if (underWin) return "Keep (Windows component)";
            r = windowed ? cfg.DefaultWindowed : cfg.DefaultBackground;
            return r == Act.Keep ? "Keep" : (r == Act.Suspend ? "Suspend" : "Close + relaunch");
        }

        void RecomputeEffective()
        {
            if (grid == null) return;
            loadingGrid = true;
            foreach (DataGridViewRow r in grid.Rows) { bool[] f = (bool[])r.Tag; r.Cells["Effective"].Value = Effective((string)r.Cells["App"].Value, f[0], f[1]); }
            loadingGrid = false;
        }

        void OnRuleChanged(object s, DataGridViewCellEventArgs e)
        {
            if (loadingGrid || e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Rule") return;
            DataGridViewRow r = grid.Rows[e.RowIndex]; string name = (string)r.Cells["App"].Value; string v = (string)r.Cells["Rule"].Value;
            if (v == "(auto)" || v == null) cfg.Rules.Remove(name); else cfg.Rules[name] = v;
            SaveCfg(); bool[] f = (bool[])r.Tag; r.Cells["Effective"].Value = Effective(name, f[0], f[1]);
        }

        void RefreshApps()
        {
            if (grid == null) return;
            bool all = chkBackground.Checked;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<ProcInfo> snap = Optimizer.Snapshot();
                SafeInvoke(delegate { FillGrid(snap, all); });
            });
        }

        void FillGrid(List<ProcInfo> snap, bool all)
        {
            Dictionary<string, List<ProcInfo>> g = new Dictionary<string, List<ProcInfo>>();
            HashSet<string> hard = new HashSet<string>(Optimizer.HardProtected);
            foreach (ProcInfo p in snap)
            {
                if (hard.Contains(p.Name)) continue;
                List<ProcInfo> l; if (!g.TryGetValue(p.Name, out l)) { l = new List<ProcInfo>(); g[p.Name] = l; }
                l.Add(p);
            }
            loadingGrid = true; grid.Rows.Clear();
            List<string> names = new List<string>(g.Keys);
            foreach (string k in cfg.Rules.Keys) if (!g.ContainsKey(k)) names.Add(k);
            names.Sort(delegate(string a, string b)
            {
                long wa = g.ContainsKey(a) ? g[a].Count : 0, wb = g.ContainsKey(b) ? g[b].Count : 0;
                bool ha = g.ContainsKey(a) && g[a].Exists(delegate(ProcInfo p) { return p.HasWindow; }), hb = g.ContainsKey(b) && g[b].Exists(delegate(ProcInfo p) { return p.HasWindow; });
                if (ha != hb) return hb.CompareTo(ha);
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (string n in names)
            {
                List<ProcInfo> l; bool running = g.TryGetValue(n, out l);
                bool windowed = running && l.Exists(delegate(ProcInfo p) { return p.HasWindow; });
                bool inRules = cfg.Rules.ContainsKey(n);
                if (!all && !windowed && !inRules) continue;
                bool underWin = running && l.Exists(delegate(ProcInfo p) { return p.Path.StartsWith(win, StringComparison.OrdinalIgnoreCase) || p.Path.Length == 0; });
                string title = ""; long ws = 0;
                if (running) { foreach (ProcInfo p in l) { ws += p.WS; if (title.Length == 0) title = p.Title; } }
                int idx = grid.Rows.Add(n, running ? title : "(not running)", running ? (ws / 1048576).ToString() : "", inRules ? cfg.Rules[n] : "(auto)", "");
                DataGridViewRow row = grid.Rows[idx]; row.Tag = new bool[] { windowed, underWin };
                row.Cells["Effective"].Value = Effective(n, windowed, underWin);
                if (!running) row.DefaultCellStyle.ForeColor = Color.Gray;
            }
            loadingGrid = false;
        }

        // ================= profiles tab =================
        TabPage BuildProfiles()
        {
            TabPage p = new TabPage("Profiles"); p.Padding = new Padding(10);
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t.Controls.Add(ProfileBox("Local play", cfg.Local), 0, 0);
            t.Controls.Add(ProfileBox("Streaming (Remote Play host)", cfg.Streaming), 1, 0);
            p.Controls.Add(t);
            return p;
        }

        GroupBox ProfileBox(string title, ModeProfile m)
        {
            GroupBox gb = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(10) };
            FlowLayoutPanel f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            f.Controls.Add(Chk("Enable this profile", m.Enabled, delegate(bool v) { m.Enabled = v; }));
            f.Controls.Add(Chk("Close / suspend non-essential apps (per Apps tab)", m.CloseApps, delegate(bool v) { m.CloseApps = v; }));
            f.Controls.Add(Chk("Trim memory of suspended apps", m.TrimMemory, delegate(bool v) { m.TrimMemory = v; }));
            f.Controls.Add(Chk("Raise the game's CPU priority to High", m.HighPriority, delegate(bool v) { m.HighPriority = v; }));
            f.Controls.Add(Chk("Suppress notification pop-ups", m.SuppressToasts, delegate(bool v) { m.SuppressToasts = v; }));
            f.Controls.Add(Chk("Keep PC and display awake during the session", m.KeepAwake, delegate(bool v) { m.KeepAwake = v; }));
            f.Controls.Add(new Label { Text = "Power plan", AutoSize = true, Margin = new Padding(3, 10, 3, 0) });
            ComboBox pc = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
            pc.Items.Add(new PlanItem("", "Don't change")); pc.Items.Add(new PlanItem("high", "Best performance (Ultimate / High)"));
            foreach (KeyValuePair<string, string> kv in Optimizer.PowerPlans()) pc.Items.Add(new PlanItem(kv.Key, kv.Value));
            pc.SelectedIndex = 0;
            for (int i = 0; i < pc.Items.Count; i++) if (((PlanItem)pc.Items[i]).Id == m.PowerPlan) pc.SelectedIndex = i;
            pc.SelectedIndexChanged += delegate { m.PowerPlan = ((PlanItem)pc.SelectedItem).Id; SaveCfg(); };
            f.Controls.Add(pc);
            f.Controls.Add(new Label { Text = Optimizer.IsElevated() ? "Windows services to stop during the session:" : "Windows services to stop (needs admin - see Settings):", AutoSize = true, Margin = new Padding(3, 10, 3, 0) });
            CheckedListBox cl = new CheckedListBox { Width = 330, Height = 160, CheckOnClick = true };
            try
            {
                List<ServiceController> svcs = new List<ServiceController>(ServiceController.GetServices());
                svcs.Sort(delegate(ServiceController a, ServiceController b) { return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase); });
                foreach (ServiceController sc in svcs)
                    if (sc.Status == ServiceControllerStatus.Running && sc.CanStop) cl.Items.Add(new SvcItem(sc.ServiceName, sc.DisplayName), m.StopServices.Contains(sc.ServiceName));
            }
            catch { }
            cl.ItemCheck += delegate(object s, ItemCheckEventArgs e)
            {
                string n = ((SvcItem)cl.Items[e.Index]).Name;
                if (e.NewValue == CheckState.Checked) { if (!m.StopServices.Contains(n)) m.StopServices.Add(n); } else m.StopServices.Remove(n);
                SaveCfg();
            };
            f.Controls.Add(cl);
            f.Controls.Add(new Label { Text = "Suggestions: SysMain (Superfetch), Windows Search, Print Spooler.\r\nStopped services are restarted when the session ends.", AutoSize = true, ForeColor = Color.DimGray });
            gb.Controls.Add(f);
            return gb;
        }

        class PlanItem { public string Id, Label; public PlanItem(string i, string l) { Id = i; Label = l; } public override string ToString() { return Label; } }
        class SvcItem { public string Name, Display; public SvcItem(string n, string d) { Name = n; Display = d; } public override string ToString() { return Display + "  [" + Name + "]"; } }

        CheckBox Chk(string text, bool val, Action<bool> set)
        {
            CheckBox c = new CheckBox { Text = text, Checked = val, AutoSize = true };
            c.CheckedChanged += delegate { set(c.Checked); SaveCfg(); };
            return c;
        }

        // ================= settings tab =================
        TabPage BuildSettings()
        {
            TabPage p = new TabPage("Settings"); p.Padding = new Padding(12);
            FlowLayoutPanel f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            f.Controls.Add(Head("General"));
            f.Controls.Add(Chk("Start with Windows (in the tray)", cfg.StartWithWindows, delegate(bool v) { cfg.StartWithWindows = v; SetAutoStart(v); }));
            f.Controls.Add(Chk("Closing the window keeps running in the tray", cfg.CloseToTray, delegate(bool v) { cfg.CloseToTray = v; }));
            f.Controls.Add(Chk("Show tray notifications", cfg.ShowToasts, delegate(bool v) { cfg.ShowToasts = v; }));
            f.Controls.Add(Chk("Automation enabled", cfg.Enabled, delegate(bool v) { cfg.Enabled = v; }));
            f.Controls.Add(Chk("Dry run (log what would happen, change nothing)", cfg.DryRun, delegate(bool v) { cfg.DryRun = v; }));

            f.Controls.Add(Head("Closing apps"));
            f.Controls.Add(Chk("Force-kill apps that don't close in time (can lose unsaved work)", cfg.ForceKillAfterTimeout, delegate(bool v) { cfg.ForceKillAfterTimeout = v; }));
            f.Controls.Add(Chk("Relaunch restored apps minimized", cfg.RelaunchMinimized, delegate(bool v) { cfg.RelaunchMinimized = v; }));
            f.Controls.Add(Num("Wait for an app to close (seconds)", cfg.CloseTimeoutSec, 1, 60, delegate(int v) { cfg.CloseTimeoutSec = v; }));
            f.Controls.Add(Num("Delay between relaunches (ms)", cfg.RelaunchStaggerMs, 0, 5000, delegate(int v) { cfg.RelaunchStaggerMs = v; }));

            f.Controls.Add(Head("Detection"));
            f.Controls.Add(Num("Wait after a game starts before optimizing (seconds)", cfg.StartDelaySec, 0, 120, delegate(int v) { cfg.StartDelaySec = v; }));
            f.Controls.Add(Num("Wait after a game exits before restoring (seconds)", cfg.EndGraceSec, 1, 300, delegate(int v) { cfg.EndGraceSec = v; }));
            f.Controls.Add(Chk("Also optimize when this PC is the Remote Play client", cfg.OptimizeForStreamingClient, delegate(bool v) { cfg.OptimizeForStreamingClient = v; }));
            f.Controls.Add(Lines("Non-Steam game executables that should also trigger optimization (one per line, e.g. eldenring.exe):", cfg.ExtraTriggers));
            f.Controls.Add(Lines("Processes that mean \"a stream is active\" (one per line):", cfg.StreamIndicators));

            f.Controls.Add(Head("Elevation & files"));
            Label adm = new Label { AutoSize = true, Text = "Running as administrator: " + (Optimizer.IsElevated() ? "yes" : "no  (only needed to stop Windows services; apps you restore will also run elevated)") };
            f.Controls.Add(adm);
            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true };
            Button b1 = new Button { Text = "Restart as administrator", AutoSize = true, Enabled = !Optimizer.IsElevated() }; b1.Click += delegate { RestartElevated(); };
            Button b2 = new Button { Text = "Open settings folder", AutoSize = true }; b2.Click += delegate { Process.Start("explorer.exe", "\"" + Store.Dir + "\""); };
            row.Controls.Add(b1); row.Controls.Add(b2); f.Controls.Add(row);
            p.Controls.Add(f);
            return p;
        }

        Label Head(string t) { return new Label { Text = t, Font = new Font("Segoe UI Semibold", 11f), AutoSize = true, ForeColor = Accent, Margin = new Padding(3, 14, 3, 4) }; }

        Control Num(string label, int val, int min, int max, Action<int> set)
        {
            FlowLayoutPanel r = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            NumericUpDown n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, val)), Width = 70 };
            n.ValueChanged += delegate { set((int)n.Value); SaveCfg(); };
            r.Controls.Add(n); r.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 5, 3, 0) });
            return r;
        }

        Control Lines(string label, List<string> list)
        {
            FlowLayoutPanel r = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            TextBox t = new TextBox { Multiline = true, Width = 420, Height = 56, ScrollBars = ScrollBars.Vertical, Text = string.Join("\r\n", list.ToArray()) };
            t.Leave += delegate
            {
                list.Clear();
                foreach (string s in t.Text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) if (s.Trim().Length > 0) list.Add(s.Trim());
                SaveCfg();
            };
            r.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 6, 3, 2) }); r.Controls.Add(t);
            return r;
        }

        void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                { if (on) k.SetValue("SteamGameOptimizer", "\"" + Application.ExecutablePath + "\" --tray"); else k.DeleteValue("SteamGameOptimizer", false); }
            }
            catch (Exception ex) { MessageBox.Show("Could not change startup setting: " + ex.Message); }
        }

        void RestartElevated()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--elevated") { Verb = "runas", UseShellExecute = true };
                Process.Start(psi); reallyExit = true; Close();
            }
            catch { /* user declined UAC */ }
        }

        // ================= history tab =================
        TabPage BuildHistory()
        {
            TabPage p = new TabPage("History"); p.Padding = new Padding(10);
            histGrid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, BackgroundColor = Color.White, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            foreach (string c in new string[] { "Started", "Game", "Mode", "Length", "Closed", "Suspended", "MB released", "Ended by" }) histGrid.Columns.Add(c, c);
            lblTotals = new Label { Dock = DockStyle.Bottom, Height = 28, ForeColor = Color.DimGray };
            p.Controls.Add(histGrid); p.Controls.Add(lblTotals);
            LoadHistory();
            return p;
        }

        void LoadHistory()
        {
            if (histGrid == null) return;
            List<HistoryEntry> h = Store.Load<List<HistoryEntry>>(Store.HistoryPath, new List<HistoryEntry>());
            histGrid.Rows.Clear(); TimeSpan total = TimeSpan.Zero; long mb = 0;
            for (int i = h.Count - 1; i >= 0; i--)
            {
                HistoryEntry e = h[i]; TimeSpan len = e.End - e.Start; total += len; mb += e.FreedMB;
                histGrid.Rows.Add(e.Start.ToString("yyyy-MM-dd HH:mm"), e.Game, e.Mode, Fmt(len), e.Closed, e.Suspended, e.FreedMB, e.Note);
            }
            lblTotals.Text = h.Count + " sessions  -  " + Fmt(total) + " optimized play time  -  ~" + mb + " MB released in total";
        }
    }
}
