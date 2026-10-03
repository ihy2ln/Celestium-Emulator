using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DroidLauncher;

/// <summary>App settings, fleet tools (start/stop all, arrange windows, always on top) and the header "more" menu.</summary>
partial class MainForm
{
    readonly HashSet<string> _onTop = new();

    void ShowMoreMenu(Control anchor)
    {
        var m = new ContextMenuStrip();
        int running = _views.Values.Count(v => v.Running);
        m.Items.Add("Start all devices", null, (_, _) => { foreach (var v in _views.Values.Where(v => !v.Running && !v.Starting)) StartWith(v.Avd, ""); });
        m.Items.Add($"Stop all devices ({running})", null, async (_, _) =>
        {
            if (running == 0) return;
            if (MessageBox.Show(this, $"Stop all {running} running device(s)?", "Stop all", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var v in _views.Values.Where(v => v.Running).ToList()) await ToggleStart(v.Avd);
        }).Enabled = running > 0;
        m.Items.Add("Arrange device windows", null, (_, _) => ArrangeWindows()).Enabled = running > 0;
        m.Items.Add("Free up memory…", null, async (_, _) => await ShowFreeMemory());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Settings…", null, (_, _) => ShowAppSettings());
        m.Items.Add("Help (F1)", null, (_, _) => HelpWindow.Open(this));
        m.Items.Add("Check for updates", null, async (_, _) => await CheckForUpdate(manual: true));
        m.Items.Add("Keyboard shortcuts", null, (_, _) => MessageBox.Show(this,
            "Ctrl+1 … Ctrl+6   switch tabs\nCtrl+N   new device\nCtrl+G   game mode for the selected device\nCtrl+S   screenshot\nF5   refresh\n\nIn game mode: mapped keys tap the screen, other keys type, Esc = Back.",
            "Keyboard shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Information));
        m.Items.Add($"About Celestium {AppInfo.Version}", null, (_, _) => MessageBox.Show(this,
            $"Celestium Emulator {AppInfo.Version}\nA modern launcher for the official Android Emulator.\n\nhttps://github.com/ihy2ln/Celestium-Emulator",
            "About", MessageBoxButtons.OK, MessageBoxIcon.Information));
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(anchor, new Point(0, anchor.Height));
    }

    // ── Free up memory ──────────────────────────────────────────────────────

    async Task ShowFreeMemory()
    {
        SetStatus("Measuring memory…");
        var daemons = await Task.Run(MemoryGuard.BuildDaemons);
        var comfy = await MemoryGuard.ComfyUiMb();
        var (total, available) = MemoryGuard.SystemMemory();
        var devices = _views.Values.Where(v => v.Running).ToList();
        long deviceMb = 0;
        foreach (var v in devices)
            if (Sdk.QemuPid(v.Avd) is { } pid) try { using var p = Process.GetProcessById(pid); deviceMb += p.PrivateMemorySize64 / 1048576; } catch { }
        long daemonMb = daemons.Sum(d => d.Mb);

        using var f = DarkDialog("Free up memory", 520, 360);
        int y = 20;
        void Line(string text, string? sub = null)
        {
            f.Controls.Add(new Label { Text = text, Location = new Point(22, y), AutoSize = true, ForeColor = Ui.T.Text, Font = Theme.BodyBold });
            if (sub != null) f.Controls.Add(new Label { Text = sub, Location = new Point(22, y + 22), AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Ui.T.SubText, Font = Theme.Small, Tag = "sub" });
            y += sub != null ? 58 : 32;
        }
        Line($"Windows: {MemoryGuard.Gb(total - available)} of {MemoryGuard.Gb(total)} GB in use");
        Line($"Celestium devices: {MemoryGuard.Gb(deviceMb)} GB ({devices.Count} running)", devices.Count > 0 ? string.Join(", ", devices.Select(d => d.Avd)) : "None running.");
        Line($"Android build daemons: {MemoryGuard.Gb(daemonMb)} GB ({daemons.Count})", "Gradle/Kotlin servers left running after builds for up to 3 hours. Stopping idle ones is safe; the next build starts them again.");
        if (comfy != null) Line($"ComfyUI: {MemoryGuard.Gb(comfy.Value)} GB", "Keeps AI models loaded after each job. Unloading is safe; they reload on the next job.");

        var row = new FlowLayoutPanel { Location = new Point(18, f.ClientSize.Height - 60), Size = new Size(490, 46), BackColor = Color.Transparent };
        f.Controls.Add(row);
        var stopDaemons = new PillButton("Stop idle build daemons", PillStyle.Primary) { Margin = new Padding(0, 0, 8, 0), Enabled = daemons.Count > 0 };
        stopDaemons.Click += async (_, _) => { stopDaemons.Enabled = false; var n = await MemoryGuard.StopIdleBuildDaemons(); SetStatus($"Stopped {n} idle build daemon(s)."); f.Close(); };
        row.Controls.Add(stopDaemons);
        if (comfy != null)
        {
            var unload = new PillButton("Unload AI models") { Margin = new Padding(0, 0, 8, 0) };
            unload.Click += async (_, _) => { unload.Enabled = false; SetStatus(await MemoryGuard.UnloadComfyUiModels() ? "ComfyUI released its models." : "ComfyUI didn't respond."); f.Close(); };
            row.Controls.Add(unload);
        }
        var stopAll = new PillButton("Stop all devices", PillStyle.Danger) { Enabled = devices.Count > 0 };
        stopAll.Click += async (_, _) => { f.Close(); foreach (var v in devices) await ToggleStart(v.Avd); };
        row.Controls.Add(stopAll);
        SetStatus("");
        f.ShowDialog(this);
    }

    // ── Window tools ────────────────────────────────────────────────────────

    IntPtr EmulatorWindow(string avd)
    {
        if (Sdk.QemuPid(avd) is not { } pid) return IntPtr.Zero;
        try { using var p = Process.GetProcessById(pid); return p.MainWindowHandle; } catch { return IntPtr.Zero; }
    }

    /// <summary>Device windows whose mouse acts as a finger (the wheel then scrolls with a swipe), by window → serial.</summary>
    Dictionary<IntPtr, string> FingerModeWindows()
    {
        var map = new Dictionary<IntPtr, string>();
        foreach (var v in _views.Values.Where(v => v.Running && v.Serial != null && !v.Info.Headless && !v.Info.MouseAsPointer))
        {
            var h = EmulatorWindow(v.Avd);
            if (h != IntPtr.Zero) map[h] = v.Serial!;
        }
        return map;
    }

    /// <summary>Whether the window in front belongs to a device (the user is using it right now).</summary>
    bool DeviceWindowInFront()
    {
        var fg = GetForegroundWindow();
        return fg != IntPtr.Zero && _views.Values.Any(v => v.Running && !v.Info.Headless && EmulatorWindow(v.Avd) == fg);
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    /// <summary>Tile every visible device window in a grid on the monitor Celestium is on, keeping their proportions.</summary>
    void ArrangeWindows()
    {
        var windows = _views.Values.Where(v => v.Running && !v.Info.Headless)
            .Select(v => EmulatorWindow(v.Avd)).Where(h => h != IntPtr.Zero).ToList();
        if (windows.Count == 0) { SetStatus("No device windows to arrange."); return; }
        var area = Screen.FromControl(this).WorkingArea;
        int cols = (int)Math.Ceiling(Math.Sqrt(windows.Count * (double)area.Width / area.Height / 2.2));
        cols = Math.Clamp(cols, 1, windows.Count);
        int rows = (int)Math.Ceiling(windows.Count / (double)cols);
        int cellW = area.Width / cols, cellH = area.Height / rows;
        for (int i = 0; i < windows.Count; i++)
        {
            var h = windows[i];
            if (IsIconic(h)) ShowWindow(h, 9);
            GetWindowRect(h, out var r);
            double ratio = (double)(r.Bottom - r.Top) / Math.Max(1, r.Right - r.Left);
            int height = cellH - 12, width = (int)(height / ratio);
            if (width > cellW - 12) { width = cellW - 12; height = (int)(width * ratio); }
            int x = area.Left + (i % cols) * cellW + (cellW - width) / 2;
            int y = area.Top + (i / cols) * cellH + (cellH - height) / 2;
            SetWindowPos(h, IntPtr.Zero, x, y, width, height, 0x14);
        }
        SetStatus($"Arranged {windows.Count} device window(s).");
    }

    void ToggleOnTop(string avd)
    {
        var h = EmulatorWindow(avd);
        if (h == IntPtr.Zero) { SetStatus("That device has no window."); return; }
        bool on = !_onTop.Contains(avd);
        SetWindowPos(h, on ? new IntPtr(-1) : new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST / NOTOPMOST
        if (on) _onTop.Add(avd); else _onTop.Remove(avd);
        SetStatus(on ? $"{avd} stays on top of other windows." : $"{avd} no longer stays on top.");
        if (_selected == avd) BuildPage();
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);

    // ── App settings ────────────────────────────────────────────────────────

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string EmulatorSettingsKey = @"Software\Android Open Source Project\Emulator";

    /// <summary>
    /// The emulator keeps Ctrl shortcuts (Ctrl+Left/Right rotate, Ctrl+Backspace = back, Ctrl+H/S/P/O…) for its own
    /// controls by default, which breaks normal PC text editing in apps. Its "send keyboard shortcuts to the virtual
    /// device" setting fixes that; it's read when a device window opens.
    /// </summary>
    void ApplyEmulatorKeyboardSetting()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(EmulatorSettingsKey);
            k.SetValue("set/forwardShortcutsToDevice", _state.ShortcutsToApps ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { }
    }

    static bool StartsWithWindows()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("CelestiumEmulator") is string;
    }

    static void SetStartWithWindows(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("CelestiumEmulator", $"\"{Application.ExecutablePath}\" --tray");
        else k.DeleteValue("CelestiumEmulator", false);
    }

    void ShowAppSettings()
    {
        using var f = DarkDialog("Celestium settings", 520, 650);
        int y = 20;
        Control Row(string label, Control right, string? hint = null)
        {
            f.Controls.Add(new Label { Text = label, Location = new Point(22, y + 4), AutoSize = true, ForeColor = Ui.T.Text, Font = Theme.Body });
            right.Location = new Point(500 - 22 - right.Width, y);
            f.Controls.Add(right);
            if (hint != null) f.Controls.Add(new Label { Text = hint, Location = new Point(22, y + 26), AutoSize = true, ForeColor = Ui.T.SubText, Font = Theme.Small, Tag = "sub" });
            y += hint != null ? 58 : 44;
            return right;
        }

        var theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 170 };
        theme.Items.AddRange(new object[] { "Follow Windows", "Light", "Dark" });
        theme.SelectedIndex = _state.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        Row("Theme", theme);

        var startWin = new Toggle(); startWin.SetQuiet(StartsWithWindows());
        Row("Start Celestium when Windows starts", startWin, "Opens hidden in the tray, ready to go.");
        var hidden = new Toggle(); hidden.SetQuiet(_state.StartHidden);
        Row("Start hidden in the tray", hidden);
        var reopen = new Toggle(); reopen.SetQuiet(_state.RestoreInstances);
        Row("Reopen devices that were running", reopen, "After a reboot, devices you left running start again.");
        var budget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 170 };
        var budgets = new[] { 25, 35, 50, 75, 0 };
        long totalGb = MemoryGuard.SystemMemory().totalMb / 1024;
        budget.Items.AddRange(budgets.Select(b => (object)(b == 0 ? "No limit" : $"{b}% ({totalGb * b / 100} GB)")).ToArray());
        budget.SelectedIndex = Math.Max(0, Array.IndexOf(budgets, _state.MemoryBudgetPercent));
        Row("Memory devices may use in total", budget, "Starting a device that would go over this is blocked.");
        var backs = new Toggle(); backs.SetQuiet(_state.PcBackButtons);
        Row("Right-click, Esc and mouse Back = Android Back", backs);
        var shortcuts = new Toggle(); shortcuts.SetQuiet(_state.ShortcutsToApps);
        Row("Ctrl shortcuts go to apps", shortcuts, "Ctrl+C/V/A/Z, Ctrl+arrows… work in apps. Applies to newly opened device windows.");

        var media = new TextBox { Width = 250, Text = MediaFolder, BorderStyle = BorderStyle.FixedSingle };
        Row("Screenshots & recordings", media);
        var browse = new PillButton("Browse…") { Location = new Point(22, y - 8), Height = 30 };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = media.Text, UseDescriptionForTitle = true, Description = "Save screenshots and recordings to…" };
            if (d.ShowDialog(f) == DialogResult.OK) media.Text = d.SelectedPath;
        };
        f.Controls.Add(browse);
        y += 34;

        f.Controls.Add(new Label
        {
            Text = $"Celestium {AppInfo.Version}  ·  Android SDK: {Path.GetDirectoryName(Path.GetDirectoryName(_sdk.Emulator))}\nDevices: {Paths.AvdHome}",
            Location = new Point(22, y + 6), AutoSize = true, ForeColor = Ui.T.SubText, Font = Theme.Small, Tag = "sub",
        });

        var save = new PillButton("Save", PillStyle.Primary) { Location = new Point(390, 590), Width = 108 };
        save.Click += (_, _) => { f.DialogResult = DialogResult.OK; f.Close(); };
        var data = new PillButton("Open data folder") { Location = new Point(22, 590) };
        data.Click += (_, _) => Sdk.Launch("explorer.exe", $"\"{Paths.DataDir}\"");
        f.Controls.AddRange(new Control[] { save, data });
        if (f.ShowDialog(this) != DialogResult.OK) return;

        _state.Theme = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        _state.StartHidden = hidden.On;
        _state.RestoreInstances = reopen.On;
        _state.PcBackButtons = backs.On;
        _state.MemoryBudgetPercent = budgets[budget.SelectedIndex];
        _state.ShortcutsToApps = shortcuts.On;
        ApplyEmulatorKeyboardSetting();
        _state.MediaFolder = media.Text.Trim().Length > 0 && media.Text.Trim() != MediaFolder ? media.Text.Trim() : _state.MediaFolder;
        try { if (startWin.On != StartsWithWindows()) SetStartWithWindows(startWin.On); }
        catch (Exception ex) { SetStatus("Couldn't change the startup setting: " + ex.Message); }
        _state.Save();
        ApplyTheme();
        SetStatus("Settings saved.");
    }
}
