using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OwaToOracle
{
    public class MainForm : Form
    {
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
        private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

        private readonly Settings _s = Settings.Load();
        private readonly bool _startHidden;
        private bool _exiting, _running, _paused, _failing;

        private readonly ListView list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        private readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 24, Text = "Starting...", Padding = new Padding(4, 4, 0, 0) };
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly Timer timer = new Timer();
        private ToolStripButton btnPause;

        public MainForm(bool startHidden)
        {
            _startHidden = startHidden;
            Text = "Mail -> Oracle (24/7)";
            Width = 1100; Height = 600;
            if (startHidden) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }

            var bar = new ToolStrip { Dock = DockStyle.Top };
            bar.Items.Add(new ToolStripButton("Settings...", null, (s, e) => OpenSettings()));
            bar.Items.Add(new ToolStripButton("Check now", null, async (s, e) => await RunCycle(true)));
            btnPause = new ToolStripButton("Pause", null, (s, e) => TogglePause());
            bar.Items.Add(btnPause);
            bar.Items.Add(new ToolStripButton("Open log", null, (s, e) => OpenLog()));

            list.Columns.Add("Time", 140);
            list.Columns.Add("From", 220);
            list.Columns.Add("A  (name1)", 150);
            list.Columns.Add("D  (name2)", 150);
            list.Columns.Add("Status", 80);
            list.Columns.Add("Info", 330);

            Controls.Add(list);
            Controls.Add(bar);
            Controls.Add(status);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (s, e) => ShowWindow());
            menu.Items.Add("Check now", null, async (s, e) => await RunCycle(true));
            menu.Items.Add("Exit", null, (s, e) => { _exiting = true; Close(); });
            tray.Icon = SystemIcons.Application;
            tray.Text = "Mail -> Oracle";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowWindow();
            tray.Visible = true;

            timer.Tick += async (s, e) => await RunCycle(false);

            Shown += (s, e) =>
            {
                if (_startHidden) Hide();
                foreach (Row r in Store.Last(300).AsEnumerable().Reverse()) AddRow(r);   // oldest first, newest ends on top
                ApplySettings();
                if (!_s.IsConfigured()) { if (!_startHidden) OpenSettings(); }
                else { var t = RunCycle(false); }
            };
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }   // X = hide to tray
            SetThreadExecutionState(ES_CONTINUOUS);
            tray.Visible = false;
            _s.Save();
            base.OnFormClosing(e);
        }

        private void ShowWindow() { ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate(); }
        private void SetStatus(string t) { status.Text = t; }

        // ---------------- settings ----------------
        private void OpenSettings()
        {
            using (var f = new SettingsForm(_s))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    _s.Save();
                    ApplySettings();
                    var t = RunCycle(true);
                }
            }
        }

        private void ApplySettings()
        {
            SetThreadExecutionState(_s.KeepAwake ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED) : ES_CONTINUOUS);
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (_s.AutoStart) key.SetValue("OwaToOracle", "\"" + Application.ExecutablePath + "\" --tray");
                    else key.DeleteValue("OwaToOracle", false);
                }
            }
            catch (Exception ex) { Log.Write("AutoStart: " + ex.Message); }

            timer.Stop();
            timer.Interval = Math.Max(5, _s.IntervalSeconds) * 1000;
            timer.Enabled = !_paused;
        }

        private void TogglePause()
        {
            _paused = !_paused;
            btnPause.Text = _paused ? "Resume" : "Pause";
            timer.Enabled = !_paused;
            SetStatus(_paused ? "Paused - no mail is read." : "Resumed.");
        }

        private void OpenLog()
        {
            try { if (File.Exists(Log.FilePath)) Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); else MessageBox.Show("No log yet."); }
            catch { }
        }

        // ---------------- the 24/7 cycle ----------------
        private async Task RunCycle(bool manual)
        {
            if (_running) return;
            if (!_s.IsConfigured()) { if (manual) OpenSettings(); return; }
            if (_paused && !manual) return;

            _running = true;
            SetStatus("Checking mail...");
            try
            {
                Settings snap = _s;
                int n = await Task.Run(() => MailProcessor.Run(snap, r => BeginInvoke(new Action(() => AddRow(r)))));
                _s.Save();
                SetStatus(DateTime.Now.ToString("HH:mm:ss") + "  OK - " + n + " row(s) added/updated in last check.");
                if (n > 0) tray.ShowBalloonTip(3000, "Oracle", n + " row(s) added/updated", ToolTipIcon.Info);
                if (_failing) { _failing = false; Log.Write("Recovered."); }
            }
            catch (Exception ex)
            {
                Log.Write("Cycle error: " + ex.Message);
                SetStatus(DateTime.Now.ToString("HH:mm:ss") + "  PROBLEM (will retry): " + ex.Message);
                if (!_failing)
                {
                    _failing = true;
                    tray.ShowBalloonTip(5000, "Mail -> Oracle problem", ex.Message, ToolTipIcon.Warning);
                }
            }
            finally { _running = false; }
        }

        private void AddRow(Row r)
        {
            var li = new ListViewItem(r.Time);
            li.SubItems.Add(r.From);
            li.SubItems.Add(r.A);
            li.SubItems.Add(r.D);
            li.SubItems.Add(r.Status);
            li.SubItems.Add(r.Info);
            if (r.Status == "ADDED") li.BackColor = Color.FromArgb(220, 245, 220);
            else if (r.Status == "DELETED") li.BackColor = Color.FromArgb(255, 236, 200);
            else if (r.Status == "SEARCHED") li.BackColor = Color.FromArgb(215, 235, 255);
            else if (r.Status == "NOTFOUND") li.BackColor = Color.FromArgb(255, 250, 205);
            else if (r.Status == "FAILED") li.BackColor = Color.FromArgb(250, 215, 215);
            else li.ForeColor = Color.Gray;
            list.Items.Insert(0, li);
            while (list.Items.Count > 1000) list.Items.RemoveAt(list.Items.Count - 1);
        }
    }
}
