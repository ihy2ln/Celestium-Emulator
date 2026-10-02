using System.Diagnostics;

namespace DroidLauncher;

class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(24, 26, 31);
    static readonly Color Panel = Color.FromArgb(36, 39, 46);
    static readonly Color Accent = Color.FromArgb(61, 220, 132);
    static readonly Color Fg = Color.FromArgb(230, 232, 236);
    static readonly Color Muted = Color.FromArgb(150, 155, 165);

    // Shared GDI objects. Creating a new Font per control leaked handles on every list reload.
    static readonly Font UiFont = new("Segoe UI", 10f);
    static readonly Font ButtonFont = new("Segoe UI Semibold", 9.5f);
    static readonly Font TitleFont = new("Segoe UI Semibold", 13f);
    static readonly Font NameFont = new("Segoe UI Semibold", 12f);
    static readonly Font DotFont = new("Segoe UI", 12f);
    static readonly Font SmallFont = new("Segoe UI", 9f);
    static readonly Font SmallBoldFont = new("Segoe UI Semibold", 9f);
    static readonly Font HintFont = new("Segoe UI", 8.5f);
    static readonly int[] RamChoicesMb = { 2048, 3072, 4096, 6144, 8192 };

    readonly Sdk _sdk;
    readonly FlowLayoutPanel _list = new();
    readonly Label _status = new();
    readonly System.Windows.Forms.Timer _poll = new() { Interval = ActivePollMs };
    const int ActivePollMs = 3000, HiddenPollMs = 15000, StartTimeoutSeconds = 120;
    readonly ToolTip _tips = new();
    readonly Dictionary<string, DateTime> _startingSince = new();
    readonly HashSet<string> _busy = new();
    readonly Microsoft.Win32.SessionEndingEventHandler _onSessionEnding;
    Dictionary<string, (string serial, string state)> _running = new();
    readonly Dictionary<string, bool> _booted = new();
    readonly AppState _state;
    readonly NotifyIcon _tray = new();
    List<string> _avds = new();
    bool _refreshing;
    bool _quitting;
    bool _restoredInstances;
    bool _loaded;
    bool _sessionEnding;
    /// <summary>The first window owns the tray, session restore and saved position; extra windows are plain.</summary>
    readonly bool _primary;

    public MainForm(Config cfg, AppState state, bool primary)
    {
        _sdk = new Sdk(cfg);
        _state = state;
        _primary = primary;
        Text = primary ? "Celestium Emulator" : "Celestium Emulator — extra window";
        BackColor = Bg;
        ForeColor = Fg;
        Font = UiFont;
        MinimumSize = new Size(560, 360);
        AllowDrop = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        RestoreBoundsFromState();
        if (_primary)
        {
            BuildTray();
            VisibleChanged += (_, _) => { if (Visible) Program.PrimaryVisible?.Set(); else Program.PrimaryVisible?.Reset(); };
        }
        // SystemEvents is static: unhook on close or the handler keeps this form alive.
        _onSessionEnding = (_, _) => _sessionEnding = true;
        Microsoft.Win32.SystemEvents.SessionEnding += _onSessionEnding;
        FormClosed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.SessionEnding -= _onSessionEnding;
            _poll.Stop();
            _tray.Visible = false;
            _tray.Dispose();
        };
        // Poll slowly while nobody is looking, and refresh at once when the window comes back.
        VisibleChanged += async (_, _) =>
        {
            _poll.Interval = Visible ? ActivePollMs : HiddenPollMs;
            if (Visible && _loaded) await RefreshState();
        };

        var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Panel, Padding = new Padding(16, 0, 12, 0) };
        var title = new Label
        {
            Text = "Android Instances", Dock = DockStyle.Left, AutoSize = false, Width = 260,
            TextAlign = ContentAlignment.MiddleLeft, Font = TitleFont, ForeColor = Fg,
        };
        var apkBtn = MakeButton("Install APK…", Accent, Bg);
        apkBtn.Dock = DockStyle.Right;
        apkBtn.Width = 130;
        apkBtn.Click += async (_, _) => await PickAndInstall();
        var refreshBtn = MakeButton("⟳", Panel, Fg);
        refreshBtn.Dock = DockStyle.Right;
        refreshBtn.Width = 44;
        refreshBtn.Click += async (_, _) => await Reload();
        var newBtn = MakeButton("+ New", Panel, Fg);
        newBtn.Dock = DockStyle.Right;
        newBtn.Width = 76;
        newBtn.Click += async (_, _) => await NewInstance(null);
        var windowBtn = MakeButton("⧉", Panel, Fg);
        windowBtn.Dock = DockStyle.Right;
        windowBtn.Width = 44;
        windowBtn.Click += (_, _) => OpenNewWindow();
        _tips.SetToolTip(windowBtn, "Open another Celestium Emulator window");
        _tips.SetToolTip(newBtn, "Create a new Android instance");
        _tips.SetToolTip(refreshBtn, "Refresh");
        header.Controls.Add(title);
        header.Controls.Add(windowBtn);
        header.Controls.Add(refreshBtn);
        header.Controls.Add(newBtn);
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
            if (_loaded) return;
            await Reload();
            _poll.Start();
        };

        FormClosing += (_, e) =>
        {
            SaveBounds();
            // The X button hides to the tray; only Quit (tray menu), Windows shutdown or Task Manager really exit.
            if (_primary && e.CloseReason == CloseReason.UserClosing && !_quitting)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            _state.Save();
        };
        ResizeEnd += (_, _) => SaveBounds();
    }

    // ── Window state ────────────────────────────────────────────────────────

    void RestoreBoundsFromState()
    {
        if (!_primary)
        {
            StartPosition = FormStartPosition.WindowsDefaultLocation;
            Size = new Size(Math.Max(_state.Width, 560), Math.Max(_state.Height, 360));
            return;
        }
        var bounds = new Rectangle(_state.X, _state.Y, Math.Max(_state.Width, 560), Math.Max(_state.Height, 360));
        // Only reuse the saved spot if it's still visible on a connected monitor.
        bool onScreen = _state.X != int.MinValue && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(
            new Rectangle(bounds.X, bounds.Y, Math.Min(bounds.Width, 200), 40)));
        if (onScreen)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 520);
        }
        if (_state.Maximized) WindowState = FormWindowState.Maximized;
    }

    void SaveBounds()
    {
        if (!_primary || !Visible || WindowState == FormWindowState.Minimized) return;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _state.X = b.X; _state.Y = b.Y; _state.Width = b.Width; _state.Height = b.Height;
        _state.Maximized = WindowState == FormWindowState.Maximized;
        _state.Save();
    }

    protected override void SetVisibleCore(bool value)
    {
        // Launched with "start in tray": create the window but keep it hidden.
        if (_primary && !IsHandleCreated && _state.StartHidden && value)
        {
            CreateHandle();
            value = false;
            BeginInvoke(new Action(async () =>
            {
                if (File.Exists(_sdk.Emulator)) { await Reload(); _poll.Start(); }
            }));
        }
        base.SetVisibleCore(value);
    }

    // ── Tray ────────────────────────────────────────────────────────────────

    void BuildTray()
    {
        _tray.Icon = Icon;
        _tray.Text = "Celestium Emulator " + AppInfo.Version;
        _tray.Visible = true;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => FillTrayMenu(menu);
        FillTrayMenu(menu);
        _tray.ContextMenuStrip = menu;
    }

    void FillTrayMenu(ContextMenuStrip menu)
    {
        foreach (ToolStripItem old in menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        menu.Items.Clear();
        var open = new ToolStripMenuItem("Open Celestium Emulator", null, (_, _) => ShowFromTray()) { Font = ButtonFont };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        foreach (var avd in _avds)
        {
            var running = _running.ContainsKey(avd);
            menu.Items.Add(running ? $"Stop {avd}" : $"Start {avd}", null, async (_, _) => await ToggleStart(avd));
        }
        if (_avds.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Install APK…", null, async (_, _) => { ShowFromTray(); await PickAndInstall(); });
        menu.Items.Add("New instance…", null, async (_, _) => { ShowFromTray(); await NewInstance(null); });
        menu.Items.Add("New window", null, (_, _) => OpenNewWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Reopen running instances on launch", null, (_, _) =>
        {
            _state.RestoreInstances = !_state.RestoreInstances;
            _state.Save();
        }) { Checked = _state.RestoreInstances });
        menu.Items.Add(new ToolStripMenuItem("Start hidden in tray", null, (_, _) =>
        {
            _state.StartHidden = !_state.StartHidden;
            _state.Save();
        }) { Checked = _state.StartHidden });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    static extern bool EmptyWorkingSet(IntPtr process);

    void HideToTray()
    {
        Hide();
        // Nothing is on screen now: drop cached pages so the tray app sits at a few MB instead of ~60.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using (var self = Process.GetCurrentProcess()) EmptyWorkingSet(self.Handle);
        if (!_state.TrayHintShown)
        {
            _tray.ShowBalloonTip(4000, "Celestium Emulator is still running",
                "It's in the system tray. Right-click the tray icon and choose Quit to exit.", ToolTipIcon.Info);
            _state.TrayHintShown = true;
            _state.Save();
        }
    }

    public void ShowFromTray()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = _state.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    void Quit()
    {
        _quitting = true;
        SaveBounds();
        Close();
        Application.Exit();
    }

    static Button MakeButton(string text, Color back, Color fore)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore,
            Height = 34, Cursor = Cursors.Hand, Font = ButtonFont,
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    void SetStatus(string s) => _status.Text = s;

    async Task Reload()
    {
        SetStatus("Loading instances…");
        var avds = await _sdk.ListAvds();
        _avds = avds;
        _loaded = true;
        _list.SuspendLayout();
        // Controls.Clear() doesn't dispose; old cards and their menus would pile up on every reload.
        foreach (Control old in _list.Controls.Cast<Control>().ToList()) old.Dispose();
        foreach (var avd in avds) _list.Controls.Add(BuildCard(avd));
        _list.ResumeLayout();
        if (avds.Count == 0) SetStatus("No AVDs found. Create one with avdmanager.");
        await RefreshState();
        if (avds.Count > 0) SetStatus("Drag an .apk onto this window to install it on the running instance.");
        RestoreInstances();
    }

    /// <summary>Once per launch: start the instances that were running last time and aren't anymore (e.g. after a reboot).</summary>
    void RestoreInstances()
    {
        if (_restoredInstances) return;
        _restoredInstances = true;
        if (!_primary || !_state.RestoreInstances) return;
        var toStart = _state.RunningInstances.Where(a => _avds.Contains(a) && !_running.ContainsKey(a)).ToList();
        foreach (var avd in toStart) StartWith(avd, "");
        if (toStart.Count > 0) SetStatus($"Reopening {string.Join(", ", toStart)} from last session…");
    }

    Control BuildCard(string avd)
    {
        var card = new Panel { Width = _list.ClientSize.Width - 30, Height = 104, BackColor = Panel, Margin = new Padding(0, 0, 0, 10), Tag = avd };
        var dot = new Label { Name = "dot", Text = "●", ForeColor = Muted, Location = new Point(14, 14), AutoSize = true, Font = DotFont };
        var name = new Label { Text = avd.Replace('_', ' '), Location = new Point(38, 14), AutoSize = true, Font = NameFont, ForeColor = Fg };
        var state = new Label { Name = "state", Text = "Stopped", Location = new Point(40, 40), AutoSize = true, ForeColor = Muted, Font = SmallFont };
        var info = InstanceStore.Get(avd);
        var project = new Label
        {
            Name = "project", AutoSize = true, Location = new Point(name.Left + 4, 16), ForeColor = Accent,
            Font = SmallBoldFont, Text = info.Project is { } pr ? "  " + pr : "",
        };
        name.SizeChanged += (_, _) => project.Left = name.Right + 4;
        var ramMb = info.RamMb ?? AvdFactory.ConfiguredRamMb(avd);
        var specs = new Label
        {
            AutoSize = true, Location = new Point(40, 58), ForeColor = Muted, Font = SmallFont,
            Text = (ramMb > 0 ? $"{ramMb / 1024.0} GB RAM" : "") + (info.Headless ? " · background, no window" : ""),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 0, 8, 6), BackColor = Panel,
        };
        var start = MakeButton("Start", Accent, Bg); start.Name = "start"; start.Width = 90;
        var menu = MakeButton("More ▾", Bg, Fg); menu.Width = 90;
        start.Click += async (_, _) => await ToggleStart(avd);

        var cm = new ContextMenuStrip();
        card.Disposed += (_, _) => cm.Dispose();
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
        cm.Items.Add(new ToolStripSeparator());
        var ram = new ToolStripMenuItem("Memory");
        var configured = AvdFactory.ConfiguredRamMb(avd);
        ram.DropDownItems.Add(new ToolStripMenuItem($"Default ({(configured > 0 ? configured / 1024.0 + " GB" : "AVD setting")})", null,
            (_, _) => SetRam(avd, null)) { Checked = info.RamMb == null });
        foreach (var mb in RamChoicesMb)
            ram.DropDownItems.Add(new ToolStripMenuItem($"{mb / 1024.0} GB", null, (_, _) => SetRam(avd, mb)) { Checked = info.RamMb == mb });
        cm.Items.Add(ram);
        cm.Items.Add(new ToolStripMenuItem("Run in background (no window)", null, (_, _) =>
        {
            InstanceStore.SetHeadless(avd, !InstanceStore.Get(avd).Headless);
            _ = Reload();
            SetStatus($"{avd}: takes effect next time it starts.");
        }) { Checked = info.Headless });
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add("Set project label…", null, (_, _) =>
        {
            var current = InstanceStore.Get(avd).Project ?? "";
            var label = Prompt("Project label", $"Which project is '{avd}' for? Leave empty to clear.\nTools can target it by this label too.", current);
            if (label == null) return;
            InstanceStore.SetProject(avd, label);
            _ = Reload();
        });
        cm.Items.Add("Duplicate as new instance…", null, async (_, _) => await NewInstance(avd));
        cm.Items.Add("Copy adb serial", null, (_, _) =>
        {
            var serial = _running.TryGetValue(avd, out var r) ? r.serial : "emulator-" + InstanceStore.EnsurePort(avd);
            Clipboard.SetText(serial);
            SetStatus($"Copied {serial}.");
        });
        cm.Items.Add("Delete instance…", null, async (_, _) => await DeleteInstance(avd));
        cm.Items.Add("Open instance folder", null, (_, _) =>
        {
            var dir = Path.Combine(Paths.AvdHome, avd + ".avd");
            if (Directory.Exists(dir)) Sdk.Launch("explorer.exe", $"\"{dir}\"");
        });
        menu.Click += (_, _) => cm.Show(menu, new Point(0, menu.Height));

        buttons.Controls.Add(start);
        buttons.Controls.Add(menu);
        card.Controls.Add(dot);
        card.Controls.Add(name);
        card.Controls.Add(project);
        card.Controls.Add(state);
        card.Controls.Add(specs);
        card.Controls.Add(buttons);
        return card;
    }

    void SetRam(string avd, int? mb)
    {
        InstanceStore.SetRam(avd, mb);
        _ = Reload();
        SetStatus(_running.ContainsKey(avd) ? $"{avd}: memory change takes effect next time it starts." : $"{avd} memory updated.");
    }

    void StartWith(string avd, string args)
    {
        if (_running.ContainsKey(avd) || _startingSince.ContainsKey(avd)) { SetStatus($"{avd} is already running."); return; }
        try
        {
            var serial = _sdk.Start(avd, args);
            SetStatus($"Starting {avd} on {serial} {args}…");
        }
        catch (Exception ex) { SetStatus($"Couldn't start {avd}: {ex.Message}"); return; }
        _startingSince[avd] = DateTime.UtcNow;
        MarkStarting(avd);
    }

    async Task ToggleStart(string avd)
    {
        if (!_busy.Add(avd)) return; // ignore double-clicks while a stop is in flight
        try
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
        finally { _busy.Remove(avd); }
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
            if (_primary && _restoredInstances && !_sessionEnding)
            {
                var now = _running.Keys.Where(_avds.Contains).OrderBy(a => a).ToList();
                if (!now.SequenceEqual(_state.RunningInstances.OrderBy(a => a)))
                {
                    _state.RunningInstances = now;
                    _state.Save();
                }
            }
            var store = InstanceStore.Load();
            foreach (Control card in _list.Controls)
            {
                var avd = (string)card.Tag!;
                var dot = card.Controls.Find("dot", true)[0];
                var state = card.Controls.Find("state", true)[0];
                var start = (Button)card.Controls.Find("start", true)[0];
                if (_running.TryGetValue(avd, out var r))
                {
                    _startingSince.Remove(avd);
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
                    var port = store.TryGetValue(avd, out var si) ? si.Port : 0;
                    // A start that never shows up in adb (bad args, port clash, crash) shouldn't stay "Starting…" forever.
                    if (_startingSince.TryGetValue(avd, out var since) && (DateTime.UtcNow - since).TotalSeconds > StartTimeoutSeconds)
                    {
                        _startingSince.Remove(avd);
                        SetStatus($"{avd} didn't start. Try More ▾ → Cold boot, or check the emulator window for errors.");
                    }
                    if (!_startingSince.ContainsKey(avd)) { state.Text = port > 0 ? $"Stopped · emulator-{port}" : "Stopped"; dot.ForeColor = Muted; }
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
        if (Directory.Exists(_state.LastApkFolder)) dlg.InitialDirectory = _state.LastApkFolder;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _state.LastApkFolder = Path.GetDirectoryName(dlg.FileNames[0]);
        _state.Save();
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

    // ── Instances & windows ─────────────────────────────────────────────────

    static void OpenNewWindow()
    {
        using var _ = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--new-window") { UseShellExecute = false });
    }

    async Task NewInstance(string? template)
    {
        if (_avds.Count == 0) { SetStatus("Create a first AVD with avdmanager, then add more here."); return; }
        using var f = DarkDialog("New instance", 380, 250);
        var nameBox = new TextBox { Text = SuggestName(template ?? _avds[0]), Bounds = new Rectangle(130, 20, 225, 26) };
        var fromBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(130, 62, 225, 26) };
        fromBox.Items.AddRange(_avds.Cast<object>().ToArray());
        fromBox.SelectedItem = template ?? _avds[0];
        fromBox.SelectedIndexChanged += (_, _) => nameBox.Text = SuggestName((string)fromBox.SelectedItem!);
        var projBox = new TextBox { Bounds = new Rectangle(130, 104, 225, 26), PlaceholderText = "e.g. Weaverse (optional)" };
        f.Controls.Add(DialogLabel("Name", 20, 23));
        f.Controls.Add(DialogLabel("Copy settings of", 20, 65));
        f.Controls.Add(DialogLabel("Project label", 20, 107));
        f.Controls.Add(new Label
        {
            Text = "Same Android version and hardware as the source. It starts empty, so apps aren't copied.",
            Bounds = new Rectangle(20, 140, 340, 36), ForeColor = Muted, Font = HintFont,
        });
        f.Controls.AddRange(new Control[] { nameBox, fromBox, projBox });
        var ok = MakeButton("Create", Accent, Bg);
        ok.Bounds = new Rectangle(255, 190, 100, 34);
        ok.Click += (_, _) => { f.DialogResult = DialogResult.OK; f.Close(); };
        f.AcceptButton = ok;
        f.Controls.Add(ok);
        if (f.ShowDialog(this) != DialogResult.OK) return;

        var name = nameBox.Text.Trim();
        try
        {
            AvdFactory.CreateFrom((string)fromBox.SelectedItem!, name);
            if (projBox.Text.Trim().Length > 0) InstanceStore.SetProject(name, projBox.Text);
            InstanceStore.EnsurePort(name);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't create instance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await Reload();
        SetStatus($"Created {name} (adb serial emulator-{InstanceStore.Get(name).Port}).");
    }

    string SuggestName(string from)
    {
        var stem = System.Text.RegularExpressions.Regex.Replace(from, @"_?\d+$", "");
        for (int i = 2; ; i++)
            if (!_avds.Contains(stem + "_" + i, StringComparer.OrdinalIgnoreCase)) return stem + "_" + i;
    }

    async Task DeleteInstance(string avd)
    {
        if (_running.ContainsKey(avd)) { SetStatus("Stop the instance before deleting it."); return; }
        if (MessageBox.Show(this, $"Permanently delete '{avd}' and all of its apps and data?\n\nThis cannot be undone.",
                "Delete instance", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try { AvdFactory.Delete(avd); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't delete", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        await Reload();
        SetStatus($"Deleted {avd}.");
    }

    string? Prompt(string title, string message, string value)
    {
        using var f = DarkDialog(title, 380, 190);
        f.Controls.Add(new Label { Text = message, Bounds = new Rectangle(20, 16, 340, 48), ForeColor = Fg });
        var box = new TextBox { Text = value, Bounds = new Rectangle(20, 74, 335, 26) };
        var ok = MakeButton("Save", Accent, Bg);
        ok.Bounds = new Rectangle(255, 126, 100, 34);
        ok.Click += (_, _) => { f.DialogResult = DialogResult.OK; f.Close(); };
        f.AcceptButton = ok;
        f.Controls.AddRange(new Control[] { box, ok });
        return f.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    static Form DarkDialog(string title, int w, int h) => new()
    {
        Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
        ClientSize = new Size(w, h), MaximizeBox = false, MinimizeBox = false, BackColor = Bg, ForeColor = Fg,
        Font = UiFont, ShowInTaskbar = false,
    };

    static Label DialogLabel(string text, int x, int y) =>
        new() { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Fg };

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
