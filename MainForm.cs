using System.Diagnostics;

namespace DroidLauncher;

class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(24, 26, 31);
    static readonly Color Panel = Color.FromArgb(36, 39, 46);
    static readonly Color Accent = Color.FromArgb(61, 220, 132);
    static readonly Color Fg = Color.FromArgb(230, 232, 236);
    static readonly Color Muted = Color.FromArgb(150, 155, 165);

    readonly Sdk _sdk;
    readonly FlowLayoutPanel _list = new();
    readonly Label _status = new();
    readonly System.Windows.Forms.Timer _poll = new() { Interval = 3000 };
    Dictionary<string, (string serial, string state)> _running = new();
    readonly Dictionary<string, bool> _booted = new();
    bool _refreshing;

    public MainForm(Config cfg)
    {
        _sdk = new Sdk(cfg);
        Text = "Celestium Emulator";
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(620, 520);
        MinimumSize = new Size(560, 360);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Panel, Padding = new Padding(16, 0, 12, 0) };
        var title = new Label
        {
            Text = "Android Instances", Dock = DockStyle.Left, AutoSize = false, Width = 260,
            TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 13f), ForeColor = Fg,
        };
        var apkBtn = MakeButton("Install APK…", Accent, Bg);
        apkBtn.Dock = DockStyle.Right;
        apkBtn.Width = 130;
        apkBtn.Click += async (_, _) => await PickAndInstall();
        var refreshBtn = MakeButton("⟳", Panel, Fg);
        refreshBtn.Dock = DockStyle.Right;
        refreshBtn.Width = 44;
        refreshBtn.Click += async (_, _) => await Reload();
        header.Controls.Add(title);
        header.Controls.Add(refreshBtn);
        header.Controls.Add(apkBtn);

        _list.Dock = DockStyle.Fill;
        _list.FlowDirection = FlowDirection.TopDown;
        _list.WrapContents = false;
        _list.AutoScroll = true;
        _list.Padding = new Padding(12);
        _list.Resize += (_, _) => { foreach (Control c in _list.Controls) c.Width = _list.ClientSize.Width - 30; };

        _status.Dock = DockStyle.Bottom;
        _status.Height = 30;
        _status.Padding = new Padding(14, 0, 0, 0);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Muted;
        _status.BackColor = Panel;
        _status.Text = "Drag an .apk onto this window to install it on the running instance.";

        Controls.Add(_list);
        Controls.Add(header);
        Controls.Add(_status);

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] f && f.Any(p => p.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var apk in files.Where(p => p.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
                    await InstallApk(apk);
        };

        _poll.Tick += async (_, _) => await RefreshState();
        Shown += async (_, _) =>
        {
            if (!File.Exists(_sdk.Emulator))
            {
                SetStatus($"emulator.exe not found at {_sdk.Emulator} — check launcher.json");
                return;
            }
            await Reload();
            _poll.Start();
        };
    }

    static Button MakeButton(string text, Color back, Color fore)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore,
            Height = 34, Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 9.5f),
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    void SetStatus(string s) => _status.Text = s;

    async Task Reload()
    {
        SetStatus("Loading instances…");
        var avds = await _sdk.ListAvds();
        _list.Controls.Clear();
        foreach (var avd in avds) _list.Controls.Add(BuildCard(avd));
        if (avds.Count == 0) SetStatus("No AVDs found. Create one with avdmanager.");
        await RefreshState();
        if (avds.Count > 0) SetStatus("Drag an .apk onto this window to install it on the running instance.");
    }

    Control BuildCard(string avd)
    {
        var card = new Panel { Width = _list.ClientSize.Width - 30, Height = 96, BackColor = Panel, Margin = new Padding(0, 0, 0, 10), Tag = avd };
        var dot = new Label { Name = "dot", Text = "●", ForeColor = Muted, Location = new Point(14, 14), AutoSize = true, Font = new Font("Segoe UI", 12f) };
        var name = new Label { Text = avd.Replace('_', ' '), Location = new Point(38, 14), AutoSize = true, Font = new Font("Segoe UI Semibold", 12f), ForeColor = Fg };
        var state = new Label { Name = "state", Text = "Stopped", Location = new Point(40, 40), AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 9f) };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 0, 8, 6), BackColor = Panel,
        };
        var start = MakeButton("Start", Accent, Bg); start.Name = "start"; start.Width = 90;
        var menu = MakeButton("More ▾", Bg, Fg); menu.Width = 90;
        start.Click += async (_, _) => await ToggleStart(avd);

        var cm = new ContextMenuStrip();
        cm.Items.Add("Cold boot (ignore snapshot)", null, (_, _) => StartWith(avd, "-no-snapshot-load"));
        cm.Items.Add("Start with writable system (root/remount)", null, (_, _) => StartWith(avd, "-writable-system -no-snapshot-load"));
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add("Open logcat", null, (_, _) => WithSerial(avd, _sdk.Logcat));
        cm.Items.Add("Open adb shell", null, (_, _) => WithSerial(avd, _sdk.Shell));
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add("Wipe data && start fresh…", null, (_, _) =>
        {
            if (_running.ContainsKey(avd)) { SetStatus("Stop the instance before wiping it."); return; }
            if (MessageBox.Show(this, $"Erase all apps and data on '{avd}'? This cannot be undone.", "Wipe data",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                StartWith(avd, "-wipe-data");
        });
        cm.Items.Add("Open instance folder", null, (_, _) =>
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd", avd + ".avd");
            if (Directory.Exists(dir)) Process.Start("explorer.exe", dir);
        });
        menu.Click += (_, _) => cm.Show(menu, new Point(0, menu.Height));

        buttons.Controls.Add(start);
        buttons.Controls.Add(menu);
        card.Controls.Add(dot);
        card.Controls.Add(name);
        card.Controls.Add(state);
        card.Controls.Add(buttons);
        return card;
    }

    void StartWith(string avd, string args)
    {
        if (_running.ContainsKey(avd)) { SetStatus($"{avd} is already running."); return; }
        _sdk.Start(avd, args);
        SetStatus($"Starting {avd} {args}…");
        MarkStarting(avd);
    }

    async Task ToggleStart(string avd)
    {
        if (_running.TryGetValue(avd, out var r))
        {
            SetStatus($"Stopping {avd}…");
            await _sdk.Stop(r.serial);
            await Task.Delay(1500);
            await RefreshState();
            SetStatus($"{avd} stopped.");
        }
        else StartWith(avd, "");
    }

    void MarkStarting(string avd)
    {
        var card = _list.Controls.Cast<Control>().FirstOrDefault(c => (string?)c.Tag == avd);
        if (card == null) return;
        card.Controls.Find("state", true)[0].Text = "Starting…";
        card.Controls.Find("dot", true)[0].ForeColor = Color.Gold;
    }

    void WithSerial(string avd, Action<string> act)
    {
        if (_running.TryGetValue(avd, out var r)) act(r.serial);
        else SetStatus($"{avd} isn't running.");
    }

    async Task RefreshState()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _running = await _sdk.RunningAvds();
            foreach (Control card in _list.Controls)
            {
                var avd = (string)card.Tag!;
                var dot = card.Controls.Find("dot", true)[0];
                var state = card.Controls.Find("state", true)[0];
                var start = (Button)card.Controls.Find("start", true)[0];
                if (_running.TryGetValue(avd, out var r))
                {
                    if (!_booted.GetValueOrDefault(avd) && r.state == "device") _booted[avd] = await _sdk.IsBooted(r.serial);
                    var booted = _booted.GetValueOrDefault(avd);
                    state.Text = booted ? $"Running · {r.serial}" : $"Booting · {r.serial}";
                    dot.ForeColor = booted ? Accent : Color.Gold;
                    start.Text = "Stop";
                    start.BackColor = Color.FromArgb(220, 80, 80);
                    start.ForeColor = Color.White;
                }
                else
                {
                    _booted.Remove(avd);
                    if (state.Text != "Starting…") { state.Text = "Stopped"; dot.ForeColor = Muted; }
                    start.Text = "Start";
                    start.BackColor = Accent;
                    start.ForeColor = Bg;
                }
            }
        }
        catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message); }
        finally { _refreshing = false; }
    }

    async Task PickAndInstall()
    {
        using var dlg = new OpenFileDialog { Filter = "Android packages (*.apk)|*.apk", Multiselect = true, Title = "Install APK" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var f in dlg.FileNames) await InstallApk(f);
    }

    async Task InstallApk(string apk)
    {
        await RefreshState();
        var targets = _running.Where(kv => kv.Value.state == "device").ToList();
        if (targets.Count == 0) { SetStatus("Start an instance first, then install the APK."); return; }

        var (avd, (serial, _)) = targets[0];
        if (targets.Count > 1)
        {
            var pick = PickTarget(targets.Select(t => t.Key).ToList());
            if (pick == null) return;
            (avd, (serial, _)) = targets.First(t => t.Key == pick);
        }

        SetStatus($"Installing {Path.GetFileName(apk)} on {avd}…");
        var (code, output) = await _sdk.Install(serial, apk);
        if (code == 0 && output.Contains("Success")) SetStatus($"Installed {Path.GetFileName(apk)} on {avd}.");
        else
        {
            SetStatus($"Install failed on {avd}.");
            MessageBox.Show(this, output.Trim(), "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    string? PickTarget(List<string> avds)
    {
        using var f = new Form
        {
            Text = "Install on…", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(300, 60 + avds.Count * 42), MaximizeBox = false, MinimizeBox = false, BackColor = Bg,
        };
        string? chosen = null;
        for (int i = 0; i < avds.Count; i++)
        {
            var name = avds[i];
            var b = MakeButton(name, Panel, Fg);
            b.SetBounds(20, 20 + i * 42, 260, 34);
            b.Click += (_, _) => { chosen = name; f.Close(); };
            f.Controls.Add(b);
        }
        f.ShowDialog(this);
        return chosen;
    }
}
