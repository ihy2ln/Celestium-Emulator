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

    // ── Window tools ────────────────────────────────────────────────────────

    IntPtr EmulatorWindow(string avd)
    {
        if (Sdk.QemuPid(avd) is not { } pid) return IntPtr.Zero;
        try { using var p = Process.GetProcessById(pid); return p.MainWindowHandle; } catch { return IntPtr.Zero; }
    }

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
        using var f = DarkDialog("Celestium settings", 520, 470);
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

        var save = new PillButton("Save", PillStyle.Primary) { Location = new Point(390, 410), Width = 108 };
        save.Click += (_, _) => { f.DialogResult = DialogResult.OK; f.Close(); };
        var data = new PillButton("Open data folder") { Location = new Point(22, 410) };
        data.Click += (_, _) => Sdk.Launch("explorer.exe", $"\"{Paths.DataDir}\"");
        f.Controls.AddRange(new Control[] { save, data });
        if (f.ShowDialog(this) != DialogResult.OK) return;

        _state.Theme = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        _state.StartHidden = hidden.On;
        _state.RestoreInstances = reopen.On;
        _state.MediaFolder = media.Text.Trim().Length > 0 && media.Text.Trim() != MediaFolder ? media.Text.Trim() : _state.MediaFolder;
        try { if (startWin.On != StartsWithWindows()) SetStartWithWindows(startWin.On); }
        catch (Exception ex) { SetStatus("Couldn't change the startup setting: " + ex.Message); }
        _state.Save();
        ApplyTheme();
        SetStatus("Settings saved.");
    }
}
