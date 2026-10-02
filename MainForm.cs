using System.Diagnostics;

namespace DroidLauncher;

/// <summary>Main window: header, device sidebar, detail panel (MainForm.Device.cs), status bar, tray.</summary>
partial class MainForm : Form
{
    public static string? StartSelect;
    public static int StartTab;
    public static bool ForceEdges;
    public static bool CheckUpdateOnStart;
    public static bool GameOnStart;
    public static bool SyncOnStart;
    public static bool StartInTray;
    public static bool MacroSelfTest;
    public static string? HelpOnStart;

    readonly Sdk _sdk;
    readonly AppState _state;
    /// <summary>The first window owns the tray, session restore, edge resizing and saved position; extra windows are plain.</summary>
    readonly bool _primary;

    readonly Panel _header = new() { Dock = DockStyle.Top, Height = 68, Padding = new Padding(20, 0, 16, 0) };
    readonly Panel _sidebar = new() { Dock = DockStyle.Left, Width = 300 };
    readonly FlowLayoutPanel _sideList = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        Padding = new Padding(12, 12, 4, 12), BackColor = Color.Transparent,
    };
    readonly Label _subtitle = Ui.Text("", Theme.Small, sub: true);
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(16, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Small };
    readonly ToolTip _tips = new();
    readonly NotifyIcon _tray = new();
    readonly PillButton _updateButton = new("⬆  Update", PillStyle.Success) { Visible = false, Margin = new Padding(0, 0, 8, 0) };
    Updater.Release? _update;

    const int ActivePollMs = 3000, HiddenPollMs = 15000, StartTimeoutSeconds = 120;
    readonly System.Windows.Forms.Timer _poll = new() { Interval = ActivePollMs };
    readonly System.Windows.Forms.Timer _live = new() { Interval = 1500 };

    readonly Dictionary<string, DeviceTile> _tiles = new();
    readonly Dictionary<string, Image> _thumbs = new();
    Dictionary<string, DeviceView> _views = new();
    Dictionary<string, (string serial, string state)> _running = new();
    readonly Dictionary<string, bool> _booted = new();
    readonly Dictionary<string, DateTime> _startingSince = new();
    readonly HashSet<string> _busy = new();
    List<string> _avds = new();
    int _thumbRound;
    bool _refreshing, _quitting, _restoredInstances, _loaded, _sessionEnding;
    EdgeResizer? _edges;
    WheelScroller? _wheel;
    readonly Microsoft.Win32.SessionEndingEventHandler _onSessionEnding;
    readonly Microsoft.Win32.UserPreferenceChangedEventHandler _onPrefs;

    public MainForm(Config cfg, AppState state, bool primary)
    {
        _sdk = new Sdk(cfg);
        _state = state;
        _primary = primary;
        Text = primary ? "Celestium Emulator" : "Celestium Emulator — extra window";
        Font = Theme.Body;
        MinimumSize = new Size(960, 620);
        AllowDrop = true;
        DoubleBuffered = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        RestoreBoundsFromState();

        BuildHeader();
        _sidebar.Controls.Add(_sideList);
        _sideList.Resize += (_, _) => FitTiles();
        BuildDetail();
        Controls.Add(_detail);
        Controls.Add(_sidebar);
        Controls.Add(_header);
        Controls.Add(_status);
        SetStatus("Tip: drag APKs or any files onto this window to send them to the selected device.");

        if (_primary)
        {
            BuildTray();
            VisibleChanged += (_, _) => { if (Visible) Program.PrimaryVisible?.Set(); else Program.PrimaryVisible?.Reset(); };
        }

        // SystemEvents is static: unhook on close or the handlers keep this form alive.
        _onSessionEnding = (_, _) => _sessionEnding = true;
        _onPrefs = (_, e) => { if (e.Category == Microsoft.Win32.UserPreferenceCategory.General && _state.Theme == "system") BeginInvoke(ApplyTheme); };
        Microsoft.Win32.SystemEvents.SessionEnding += _onSessionEnding;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += _onPrefs;
        FormClosed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.SessionEnding -= _onSessionEnding;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= _onPrefs;
            _poll.Stop(); _live.Stop();
            _edges?.Dispose();
            _wheel?.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        };

        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;
            var target = await PickRunningDevice();
            if (target == null) return;
            var apks = files.Where(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var apk in apks) await InstallApk(target, apk);
            var others = files.Except(apks).Where(File.Exists).ToList();
            if (others.Count > 0) await SendFiles(target, others);
        };

        _poll.Tick += async (_, _) => await RefreshAll();
        _live.Tick += async (_, _) =>
        {
            if (_tabs.SelectedIndex == 0) await RefreshPreview();
            await RefreshSideThumb();
        };
        VisibleChanged += async (_, _) =>
        {
            _poll.Interval = Visible ? ActivePollMs : HiddenPollMs;
            _live.Enabled = Visible && _loaded;
            if (Visible && _loaded) await RefreshAll();
        };
        HandleCreated += (_, _) => ApplyTheme();
        Shown += async (_, _) =>
        {
            if (!File.Exists(_sdk.Emulator)) { SetStatus($"emulator.exe not found at {_sdk.Emulator} — check launcher.json"); return; }
            if (_loaded) return;
            await Reload();
            _poll.Start(); _live.Start();
            if (_primary)
            {
                Updater.CleanupOldFiles();
                if (_state.LastUpdateCheck is not { } last || DateTime.UtcNow - last > TimeSpan.FromHours(12)) _ = CheckForUpdate(manual: false);
            }
            if (CheckUpdateOnStart) await CheckForUpdate(manual: true);
            if (HelpOnStart != null) HelpWindow.Open(this, HelpOnStart);
            if (GameOnStart && _selected != null)
            {
                StartGame(_selected);
                if (SyncOnStart && _games.TryGetValue(_selected, out var g)) g.ToggleSync();
                if (MacroSelfTest && _games.TryGetValue(_selected, out var mg)) await mg.SelfTest(_sdk);
            }
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
        if (_primary || ForceEdges)
        {
            _edges = new EdgeResizer();
            _wheel = new WheelScroller(this, FingerModeWindows);
        }
        KeyPreview = true;
        KeyDown += async (_, e) =>
        {
            if (e.Control && e.KeyCode is >= Keys.D1 and <= Keys.D6) { _tabs.Select(e.KeyCode - Keys.D1); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.N) { e.Handled = true; await NewInstance(null); }
            else if (e.KeyCode == Keys.F5) { e.Handled = true; await Reload(); }
            else if (e.KeyCode == Keys.F1) { e.Handled = true; HelpWindow.Open(this, HelpTopicForTab()); }
            else if (e.Control && e.KeyCode == Keys.G && Selected is { Running: true } gs) { e.Handled = true; StartGame(gs.Avd); }
            else if (e.Control && e.KeyCode == Keys.S && Selected is { Running: true } s) { e.Handled = true; try { await TakeScreenshot(s.Avd); } catch (Exception ex) { SetStatus(ex.Message); } }
        };
    }

    // ── Layout pieces ──────────────────────────────────────────────────────

    void BuildHeader()
    {
        var logo = new PictureBox { Image = Icon?.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(30, 30), Location = new Point(20, 19), BackColor = Color.Transparent };
        var title = Ui.Text("Celestium", Theme.Title);
        title.Location = new Point(58, 12);
        _subtitle.Location = new Point(60, 41);
        _header.Controls.AddRange(new Control[] { logo, title, _subtitle });

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 16, 0, 0) };
        var theme = new PillButton("◐", PillStyle.Ghost) { Width = 40, Margin = new Padding(0, 0, 4, 0) };
        _tips.SetToolTip(theme, "Theme: system / light / dark");
        theme.Click += (_, _) =>
        {
            _state.Theme = _state.Theme switch { "system" => "light", "light" => "dark", _ => "system" };
            _state.Save();
            ApplyTheme();
            SetStatus($"Theme: {_state.Theme}.");
        };
        var help = new PillButton("?", PillStyle.Ghost) { Width = 40, Margin = new Padding(0, 0, 4, 0) };
        _tips.SetToolTip(help, "Help (F1)");
        help.Click += (_, _) => HelpWindow.Open(this, HelpTopicForTab());
        var more = new PillButton("⋯", PillStyle.Ghost) { Width = 40, Margin = new Padding(0, 0, 4, 0) };
        _tips.SetToolTip(more, "More: start/stop all, arrange windows, settings");
        more.Click += (_, _) => ShowMoreMenu(more);
        var window = new PillButton("⧉", PillStyle.Ghost) { Width = 40, Margin = new Padding(0, 0, 8, 0) };
        _tips.SetToolTip(window, "Open another window");
        window.Click += (_, _) => OpenNewWindow();
        var apk = new PillButton("Install APK…") { Margin = new Padding(0, 0, 8, 0) };
        apk.Click += async (_, _) => { var t = await PickRunningDevice(); if (t != null) await PickAndInstall(t); };
        var add = new PillButton("+  New device", PillStyle.Primary) { Margin = new Padding(0) };
        add.Click += async (_, _) => await NewInstance(null);
        _updateButton.Click += async (_, _) => await OfferUpdate(manual: true);
        buttons.Controls.AddRange(new Control[] { _updateButton, help, more, theme, window, apk, add });
        _header.Controls.Add(buttons);
    }

    void FitTiles()
    {
        int w = _sideList.ClientSize.Width - _sideList.Padding.Horizontal;
        foreach (Control c in _sideList.Controls) c.Width = Math.Max(160, w);
    }

    void ApplyTheme()
    {
        Ui.T = Theme.Resolve(_state.Theme);
        var t = Ui.T;
        BackColor = t.Window;
        _header.BackColor = t.Window;
        _sidebar.BackColor = t.Sidebar;
        _detail.BackColor = t.Window;
        Ui.Recolor(this);
        _status.BackColor = t.Sidebar;
        _status.ForeColor = t.SubText;
        t.ApplyToWindow(this);
        Invalidate(true);
    }

    void SetStatus(string s) => _status.Text = s;

    /// <summary>F1 opens the help page for the tab you're on.</summary>
    string HelpTopicForTab() => _tabs.SelectedIndex switch
    {
        1 => "Phone controls",
        2 or 3 => "Apps and files",
        4 or 5 => "Devices",
        _ => "Getting started",
    };

    // ── Window state ────────────────────────────────────────────────────────

    void RestoreBoundsFromState()
    {
        if (!_primary)
        {
            StartPosition = FormStartPosition.WindowsDefaultLocation;
            Size = new Size(Math.Max(_state.Width, 1120), Math.Max(_state.Height, 740));
            return;
        }
        var bounds = new Rectangle(_state.X, _state.Y, Math.Max(_state.Width, 1120), Math.Max(_state.Height, 740));
        bool onScreen = _state.X != int.MinValue && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(
            new Rectangle(bounds.X, bounds.Y, Math.Min(bounds.Width, 200), 40)));
        if (onScreen) { StartPosition = FormStartPosition.Manual; Bounds = bounds; }
        else { StartPosition = FormStartPosition.CenterScreen; Size = new Size(1180, 780); }
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
        if (_primary && !IsHandleCreated && (_state.StartHidden || StartInTray) && value)
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
        menu.Items.Add(new ToolStripMenuItem("Open Celestium Emulator", null, (_, _) => ShowFromTray()) { Font = Theme.BodyBold });
        menu.Items.Add(new ToolStripSeparator());
        foreach (var avd in _avds)
        {
            var running = _running.ContainsKey(avd);
            var item = new ToolStripMenuItem(avd);
            item.DropDownItems.Add(running ? "Stop" : "Start", null, async (_, _) => await ToggleStart(avd));
            if (running)
            {
                item.DropDownItems.Add("Screenshot", null, async (_, _) => { try { await TakeScreenshot(avd); } catch (Exception ex) { SetStatus(ex.Message); } });
                item.DropDownItems.Add(_recording.Contains(avd) ? "Stop recording" : "Record screen", null, async (_, _) => { try { await ToggleRecording(avd); } catch (Exception ex) { SetStatus(ex.Message); } });
                item.DropDownItems.Add("Show window", null, (_, _) => ShowEmulatorWindow(avd));
                item.DropDownItems.Add(_games.ContainsKey(avd) ? "Exit game mode" : "Game mode", null, (_, _) =>
                {
                    if (_games.TryGetValue(avd, out var g)) g.Dispose(); else StartGame(avd);
                });
            }
            menu.Items.Add(item);
        }
        if (_avds.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("New device…", null, async (_, _) => { ShowFromTray(); await NewInstance(null); });
        menu.Items.Add("New window", null, (_, _) => OpenNewWindow());
        menu.Items.Add("Help", null, (_, _) => { ShowFromTray(); HelpWindow.Open(this); });
        menu.Items.Add("Open media folder", null, (_, _) => { Directory.CreateDirectory(MediaFolder); Sdk.Launch("explorer.exe", $"\"{MediaFolder}\""); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Reopen running devices on launch", null, (_, _) => { _state.RestoreInstances = !_state.RestoreInstances; _state.Save(); }) { Checked = _state.RestoreInstances });
        menu.Items.Add(new ToolStripMenuItem("Start hidden in tray", null, (_, _) => { _state.StartHidden = !_state.StartHidden; _state.Save(); }) { Checked = _state.StartHidden });
        menu.Items.Add(_update != null ? $"Install update {_update.Version}…" : "Check for updates…", null, async (_, _) =>
        {
            ShowFromTray();
            if (_update != null) await OfferUpdate(manual: true); else await CheckForUpdate(manual: true);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    static extern bool EmptyWorkingSet(IntPtr process);

    void HideToTray()
    {
        Hide();
        // Nothing is on screen now: drop cached pages so the tray app sits at a few MB.
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

    static void OpenNewWindow()
    {
        using var _ = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--new-window") { UseShellExecute = false });
    }

    // ── Devices: list, state, start/stop ────────────────────────────────────

    async Task Reload()
    {
        var avds = await _sdk.ListAvds();
        _avds = avds;
        _loaded = true;
        _sideList.SuspendLayout();
        foreach (var gone in _tiles.Keys.Where(a => !avds.Contains(a)).ToList())
        {
            _tiles[gone].Dispose();
            _tiles.Remove(gone);
        }
        foreach (var avd in avds.Where(a => !_tiles.ContainsKey(a)))
        {
            var tile = new DeviceTile(avd);
            if (_thumbs.TryGetValue(avd, out var th)) tile.Thumb = th;
            tile.Click += (_, _) => SelectDevice(avd);
            tile.DoubleClick += async (_, _) => { if (!_running.ContainsKey(avd)) await ToggleStart(avd); else ShowEmulatorWindow(avd); };
            _tiles[avd] = tile;
            _sideList.Controls.Add(tile);
        }
        _sideList.ResumeLayout();
        FitTiles();
        await RefreshAll();
        if (StartSelect != null && avds.Contains(StartSelect)) { SelectDevice(StartSelect); StartSelect = null; }
        if (_selected == null || !avds.Contains(_selected))
            SelectDevice(avds.Contains(_state.SelectedAvd ?? "") ? _state.SelectedAvd : avds.FirstOrDefault());
        if (StartTab > 0) { _tabs.Select(StartTab); StartTab = 0; }
        RestoreInstances();
    }

    /// <summary>Once per launch: start the devices that were running last time and aren't anymore (e.g. after a reboot).</summary>
    void RestoreInstances()
    {
        if (_restoredInstances) return;
        _restoredInstances = true;
        if (!_primary || !_state.RestoreInstances) return;
        var toStart = _state.RunningInstances.Where(a => _avds.Contains(a) && !_running.ContainsKey(a)).ToList();
        foreach (var avd in toStart) StartWith(avd, "");
        if (toStart.Count > 0) SetStatus($"Reopening {string.Join(", ", toStart)} from last session…");
    }

    async Task RefreshAll()
    {
        if (_refreshing || !_loaded) return;
        _refreshing = true;
        try
        {
            _running = await _sdk.RunningAvds();
            if (_primary && _restoredInstances && !_sessionEnding)
            {
                var now = _running.Keys.Where(_avds.Contains).OrderBy(a => a).ToList();
                if (!now.SequenceEqual(_state.RunningInstances.OrderBy(a => a))) { _state.RunningInstances = now; _state.Save(); }
            }
            var store = InstanceStore.Load();
            var views = new Dictionary<string, DeviceView>();
            foreach (var avd in _avds)
            {
                var info = store.TryGetValue(avd, out var i) ? i : new InstanceInfo();
                if (_running.TryGetValue(avd, out var r))
                {
                    _startingSince.Remove(avd);
                    if (!_booted.GetValueOrDefault(avd) && r.state == "device") _booted[avd] = await _sdk.IsBooted(r.serial);
                    views[avd] = new DeviceView(avd, info, r.serial, true, _booted.GetValueOrDefault(avd), false);
                }
                else
                {
                    _booted.Remove(avd);
                    _recording.Remove(avd);
                    if (_games.TryGetValue(avd, out var game)) game.Dispose();
                    // A start that never shows up in adb (bad args, port clash, crash) shouldn't stay "Starting…" forever.
                    if (_startingSince.TryGetValue(avd, out var since) && (DateTime.UtcNow - since).TotalSeconds > StartTimeoutSeconds)
                    {
                        _startingSince.Remove(avd);
                        SetStatus($"{avd} didn't start. Try Settings › Cold boot, or check the emulator window for errors.");
                    }
                    views[avd] = new DeviceView(avd, info, null, false, false, _startingSince.ContainsKey(avd));
                }
            }

            var old = _views;
            _views = views;
            foreach (var (avd, v) in views)
            {
                if (!_tiles.TryGetValue(avd, out var tile)) continue;
                tile.Project = v.Info.Project ?? "";
                tile.Status = v.StatusText;
                tile.StatusColor = v.Running ? (v.Booted ? Ui.T.Good : Ui.T.Warn) : v.Starting ? Ui.T.Warn : Ui.T.SubText;
                var ram = v.Info.RamMb ?? AvdFactory.ConfiguredRamMb(avd);
                tile.Specs = string.Join(" · ", new[]
                {
                    v.Info.Profile ?? "",
                    ram > 0 ? $"{ram / 1024.0:0.#} GB" : "",
                    v.Info.Preset ?? "",
                    v.Info.Headless ? "background" : "",
                }.Where(s => s.Length > 0));
                if (!v.Running && tile.Thumb != null) { tile.Thumb = null; if (_thumbs.Remove(avd, out var th)) th.Dispose(); }
                tile.Invalidate();
            }
            int running = views.Values.Count(v => v.Running);
            _subtitle.Text = $"{views.Count} device{(views.Count == 1 ? "" : "s")} · {running} running";

            UpdateDetailHeader();
            // Rebuild the page only when something it shows changed (running/booted state or settings).
            if (_selected != null && views.TryGetValue(_selected, out var nv) &&
                (!old.TryGetValue(_selected, out var ov) || ov.Running != nv.Running || ov.Booted != nv.Booted || !SameInfo(ov.Info, nv.Info)))
                BuildPage();
        }
        catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message); }
        finally { _refreshing = false; }
    }

    static bool SameInfo(InstanceInfo a, InstanceInfo b) =>
        System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);

    /// <summary>Round-robin thumbnail refresh for running devices that aren't the big preview.</summary>
    async Task RefreshSideThumb()
    {
        if (!Visible || WindowState == FormWindowState.Minimized) return;
        if (++_thumbRound % 8 != 0) return; // roughly every 12 s
        // The selected device's thumbnail comes from the big preview while the Overview tab is open.
        var others = _views.Values.Where(v => v.Running && v.Booted && v.Serial != null && (v.Avd != _selected || _tabs.SelectedIndex != 0)).ToList();
        if (others.Count == 0) return;
        var v = others[(_thumbRound / 8) % others.Count];
        try
        {
            if (DeviceWindowInFront()) return;
            using var img = await CaptureImage(v);
            if (img != null) SetThumb(v.Avd, img);
        }
        catch { }
    }

    void StartWith(string avd, string args)
    {
        if (_running.ContainsKey(avd) || _startingSince.ContainsKey(avd)) { SetStatus($"{avd} is already running."); return; }
        try
        {
            var serial = _sdk.Start(avd, args);
            SetStatus($"Starting {avd} on {serial}…");
        }
        catch (Exception ex) { SetStatus($"Couldn't start {avd}: {ex.Message}"); return; }
        _startingSince[avd] = DateTime.UtcNow;
        _ = RefreshAll();
    }

    async Task ToggleStart(string avd)
    {
        if (!_busy.Add(avd)) return; // ignore double-clicks while a stop is in flight
        try
        {
            if (_running.TryGetValue(avd, out var r))
            {
                SetStatus($"Stopping {avd}…");
                if (_recording.Contains(avd)) { try { await ToggleRecording(avd); } catch { } }
                await _sdk.Stop(r.serial);
                EmulatorConsole.Forget(r.serial);
                await Task.Delay(1500);
                await RefreshAll();
                SetStatus($"{avd} stopped.");
            }
            else StartWith(avd, "");
        }
        finally { _busy.Remove(avd); }
    }

    void WithSerial(string avd, Action<string> act)
    {
        if (_running.TryGetValue(avd, out var r)) act(r.serial);
        else SetStatus($"{avd} isn't running.");
    }

    /// <summary>The selected device if it's running, otherwise ask which running device to use.</summary>
    async Task<string?> PickRunningDevice()
    {
        await RefreshAll();
        var running = _running.Where(kv => kv.Value.state == "device").Select(kv => kv.Key).ToList();
        if (_selected != null && running.Contains(_selected)) return _selected;
        if (running.Count == 0) { SetStatus("Start a device first."); return null; }
        if (running.Count == 1) return running[0];
        return PickTarget(running);
    }

    async Task PickAndInstall(string avd)
    {
        using var dlg = new OpenFileDialog { Filter = "Android packages (*.apk)|*.apk", Multiselect = true, Title = "Install APK" };
        if (Directory.Exists(_state.LastApkFolder)) dlg.InitialDirectory = _state.LastApkFolder;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _state.LastApkFolder = Path.GetDirectoryName(dlg.FileNames[0]);
        _state.Save();
        foreach (var f in dlg.FileNames) await InstallApk(avd, f);
    }

    async Task InstallApk(string avd, string apk)
    {
        if (!_running.TryGetValue(avd, out var r)) { SetStatus($"{avd} isn't running."); return; }
        SetStatus($"Installing {Path.GetFileName(apk)} on {avd}…");
        var (code, output) = await _sdk.Install(r.serial, apk);
        if (code == 0 && output.Contains("Success")) SetStatus($"Installed {Path.GetFileName(apk)} on {avd}.");
        else
        {
            SetStatus($"Install failed on {avd}.");
            MessageBox.Show(this, output.Trim(), "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── Create / delete ─────────────────────────────────────────────────────

    async Task NewInstance(string? template)
    {
        if (_avds.Count == 0) { SetStatus("Create a first AVD with avdmanager, then add more here."); return; }
        using var f = DarkDialog("New device", 460, 360);
        ComboBox Combo(IEnumerable<string> items, int top)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Bounds = new Rectangle(170, top, 265, 28) };
            c.Items.AddRange(items.Cast<object>().ToArray());
            c.SelectedIndex = 0;
            f.Controls.Add(c);
            return c;
        }
        var nameBox = new TextBox { Text = SuggestName(template ?? _avds[0]), Bounds = new Rectangle(170, 22, 265, 28), BorderStyle = BorderStyle.FixedSingle };
        f.Controls.Add(nameBox);
        var fromBox = Combo(_avds, 64);
        fromBox.SelectedItem = template ?? _avds[0];
        fromBox.SelectedIndexChanged += (_, _) => nameBox.Text = SuggestName((string)fromBox.SelectedItem!);
        var profileBox = Combo(new[] { "Same screen as source" }.Concat(DeviceProfile.All.Select(p => $"{p.Name} — {p.Description}")), 106);
        var presetBox = Combo(PerfPreset.All.Select(p => $"{p.Name} — {p.Description}"), 148);
        presetBox.SelectedIndex = 1;
        var projBox = new TextBox { Bounds = new Rectangle(170, 190, 265, 28), PlaceholderText = "e.g. Weaverse (optional)", BorderStyle = BorderStyle.FixedSingle };
        f.Controls.Add(projBox);
        foreach (var (text, top) in new[] { ("Name", 25), ("Android version from", 67), ("Device profile", 109), ("Performance", 151), ("Project label", 193) })
            f.Controls.Add(new Label { Text = text, Location = new Point(22, top), AutoSize = true, ForeColor = Ui.T.Text });
        f.Controls.Add(new Label
        {
            Text = "Uses the same Android version as the source device. It starts empty, so apps aren't copied.",
            Bounds = new Rectangle(22, 232, 415, 40), ForeColor = Ui.T.SubText, Font = Theme.Small, Tag = "sub",
        });
        var ok = new PillButton("Create device", PillStyle.Primary) { Location = new Point(300, 296), Width = 135 };
        ok.Click += (_, _) => { f.DialogResult = DialogResult.OK; f.Close(); };
        f.Controls.Add(ok);
        if (f.ShowDialog(this) != DialogResult.OK) return;

        var name = nameBox.Text.Trim();
        try
        {
            var profile = profileBox.SelectedIndex > 0 ? DeviceProfile.All[profileBox.SelectedIndex - 1] : null;
            AvdFactory.CreateFrom((string)fromBox.SelectedItem!, name, profile);
            var preset = PerfPreset.All[presetBox.SelectedIndex];
            InstanceStore.Edit(name, i =>
            {
                i.Preset = preset.Name; i.RamMb = preset.RamMb; i.Cores = preset.EffectiveCores;
                if (projBox.Text.Trim().Length > 0) i.Project = projBox.Text.Trim();
            });
            InstanceStore.EnsurePort(name);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't create device", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await Reload();
        SelectDevice(name);
        SetStatus($"Created {name} (adb serial emulator-{InstanceStore.Get(name).Port}). Press Start to boot it.");
    }

    string SuggestName(string from)
    {
        var stem = System.Text.RegularExpressions.Regex.Replace(from, @"_?\d+$", "");
        for (int i = 2; ; i++)
            if (!_avds.Contains(stem + "_" + i, StringComparer.OrdinalIgnoreCase)) return stem + "_" + i;
    }

    async Task DeleteInstance(string avd)
    {
        if (_running.ContainsKey(avd)) { SetStatus("Stop the device before deleting it."); return; }
        if (MessageBox.Show(this, $"Permanently delete '{avd}' and all of its apps and data?\n\nThis cannot be undone.",
                "Delete device", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try { AvdFactory.Delete(avd); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't delete", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        _selected = null;
        await Reload();
        SetStatus($"Deleted {avd}.");
    }

    // ── Updates ─────────────────────────────────────────────────────────────

    async Task CheckForUpdate(bool manual)
    {
        try
        {
            if (manual) SetStatus("Checking for updates…");
            _update = await Updater.CheckAsync();
            _state.LastUpdateCheck = DateTime.UtcNow;
            _state.Save();
            _updateButton.Visible = _update != null;
            if (_update != null)
            {
                _updateButton.Text = $"⬆  Update to {_update.Version}";
                _updateButton.Width = TextRenderer.MeasureText(_updateButton.Text, _updateButton.Font).Width + 32;
                SetStatus($"Celestium {_update.Version} is available. Click Update in the top bar.");
                if (!manual && !Visible) _tray.ShowBalloonTip(4000, "Update available", $"Celestium {_update.Version} is ready to install.", ToolTipIcon.Info);
                if (manual) await OfferUpdate(manual: true);
            }
            else if (manual) SetStatus($"You're up to date (Celestium {AppInfo.Version}).");
        }
        catch (Exception ex) { if (manual) SetStatus("Couldn't check for updates: " + ex.Message); }
    }

    async Task OfferUpdate(bool manual)
    {
        if (_update is not { } u) return;
        var notes = u.Notes.Length > 900 ? u.Notes[..900] + "…" : u.Notes;
        if (MessageBox.Show(this, $"Install Celestium {u.Version}? (You have {AppInfo.Version}.)\n\n{notes}\n\nThe app restarts afterwards; your Android devices keep running.",
                "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
        _updateButton.Enabled = false;
        try
        {
            var exeChanged = await Updater.InstallAsync(u, new Progress<string>(SetStatus));
            if (exeChanged)
                MessageBox.Show(this, "This update includes a new .exe, so your antivirus may check it once on the next start.", "Update installed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Updater.RestartIntoNewVersion();
            Quit();
        }
        catch (Exception ex)
        {
            _updateButton.Enabled = true;
            MessageBox.Show(this, ex.Message, "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── Small dialogs ───────────────────────────────────────────────────────

    string? Prompt(string title, string message, string value)
    {
        using var f = DarkDialog(title, 400, 190);
        f.Controls.Add(new Label { Text = message, Bounds = new Rectangle(20, 16, 360, 44), ForeColor = Ui.T.Text });
        var box = new TextBox { Text = value, Bounds = new Rectangle(20, 70, 355, 28), BorderStyle = BorderStyle.FixedSingle };
        var ok = new PillButton("Save", PillStyle.Primary) { Location = new Point(275, 126), Width = 100 };
        ok.Click += (_, _) => { f.DialogResult = DialogResult.OK; f.Close(); };
        box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { f.DialogResult = DialogResult.OK; f.Close(); } };
        f.Controls.AddRange(new Control[] { box, ok });
        return f.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    Form DarkDialog(string title, int w, int h)
    {
        var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(w, h), MaximizeBox = false, MinimizeBox = false, BackColor = Ui.T.Window, ForeColor = Ui.T.Text,
            Font = Theme.Body, ShowInTaskbar = false,
        };
        f.HandleCreated += (_, _) => Ui.T.ApplyToWindow(f);
        f.Shown += (_, _) => Ui.Recolor(f);
        return f;
    }

    string? PickTarget(List<string> avds)
    {
        using var f = DarkDialog("Which device?", 300, 40 + avds.Count * 46);
        string? chosen = null;
        for (int i = 0; i < avds.Count; i++)
        {
            var name = avds[i];
            var b = new PillButton(name) { Bounds = new Rectangle(20, 20 + i * 46, 260, 38) };
            b.Click += (_, _) => { chosen = name; f.Close(); };
            f.Controls.Add(b);
        }
        f.ShowDialog(this);
        return chosen;
    }
}
