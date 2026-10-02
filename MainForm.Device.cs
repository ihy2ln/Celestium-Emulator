using System.Diagnostics;

namespace DroidLauncher;

/// <summary>The right-hand detail panel: Overview, Controls, Snapshots and Settings for the selected device.</summary>
partial class MainForm
{
    readonly Panel _detail = new() { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 12) };
    readonly Label _dName = Ui.Text("", Theme.Huge);
    readonly Label _dStatus = Ui.Text("", Theme.Body, sub: true);
    readonly PillButton _dStart = new("Start", PillStyle.Primary) { Width = 110 };
    readonly PillButton _dShow = new("Show window") { Width = 130 };
    readonly TabStrip _tabs = new("Overview", "Controls", "Apps", "Files", "Snapshots", "Settings");
    readonly FlowLayoutPanel _page = new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight,
        Padding = new Padding(0, 6, 0, 12), BackColor = Color.Transparent,
    };
    readonly Label _empty = Ui.Text("Create a device with  + New device  to get started.", Theme.Heading, sub: true);

    string? _selected;
    // Live widgets of the current page (null when that page isn't showing).
    PictureBox? _preview;
    Label? _usage;
    Label? _recBadge;
    PillButton? _recButton;
    ListView? _snapList;
    readonly Dictionary<string, (TimeSpan cpu, DateTime at)> _cpuSamples = new();
    readonly HashSet<string> _recording = new();
    bool _previewBusy;

    void BuildDetail()
    {
        var head = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Color.Transparent };
        _dName.Location = new Point(0, 0);
        _dStatus.Location = new Point(2, 42);
        _dStart.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _dShow.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        head.Controls.AddRange(new Control[] { _dName, _dStatus, _dStart, _dShow });
        head.Resize += (_, _) =>
        {
            _dStart.Location = new Point(head.Width - _dStart.Width, 6);
            _dShow.Location = new Point(_dStart.Left - _dShow.Width - 8, 6);
        };
        _dStart.Click += async (_, _) => { if (_selected != null) await ToggleStart(_selected); };
        _dShow.Click += (_, _) => { if (_selected != null) ShowEmulatorWindow(_selected); };

        var tabsRow = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.Transparent };
        tabsRow.Controls.Add(_tabs);
        _tabs.SelectedChanged += _ => BuildPage();

        _empty.Location = new Point(28, 120);
        _detail.Controls.Add(_page);
        _detail.Controls.Add(tabsRow);
        _detail.Controls.Add(head);
        _detail.Controls.Add(_empty);
        _page.Resize += (_, _) => FitCards();
    }

    DeviceView? Selected => _selected != null && _views.TryGetValue(_selected, out var v) ? v : null;

    void SelectDevice(string? avd)
    {
        if (avd != null && _selected == avd) return;
        _selected = avd;
        _state.SelectedAvd = avd;
        foreach (var tile in _tiles.Values) { tile.Selected = tile.Avd == avd; tile.Invalidate(); }
        UpdateDetailHeader();
        BuildPage();
    }

    void UpdateDetailHeader()
    {
        var v = Selected;
        foreach (Control c in _detail.Controls) c.Visible = v != null || c == _empty;
        _empty.Visible = v == null;
        if (v == null) return;
        _dName.Text = v.Avd.Replace('_', ' ');
        _dStatus.Text = v.StatusText + (v.Info.Project is { } p ? $"  ·  {p}" : "");
        _dStart.Text = v.Running ? "Stop" : v.Starting ? "Starting…" : "Start";
        _dStart.Style = v.Running ? PillStyle.Danger : PillStyle.Primary;
        _dStart.Enabled = !v.Starting || v.Running;
        _dShow.Visible = v.Running && !v.Info.Headless;
        _dStart.Invalidate();
    }

    // ── Pages ───────────────────────────────────────────────────────────────

    void BuildPage()
    {
        _preview = null; _usage = null; _recBadge = null; _recButton = null; _snapList = null;
        _page.SuspendLayout();
        foreach (Control c in _page.Controls.Cast<Control>().ToList()) c.Dispose();
        var v = Selected;
        if (v != null)
        {
            switch (_tabs.SelectedIndex)
            {
                case 0: BuildOverview(v); break;
                case 1: BuildControls(v); break;
                case 2: BuildApps(v); break;
                case 3: BuildFiles(v); break;
                case 4: BuildSnapshots(v); break;
                case 5: BuildSettings(v); break;
            }
        }
        _page.ResumeLayout();
        FitCards();
        Ui.Recolor(_page);
        if (v != null && _tabs.SelectedIndex == 0) _ = RefreshPreview();
        if (v != null && _tabs.SelectedIndex == 4) _ = LoadSnapshots(v);
        if (v != null && _tabs.SelectedIndex == 2) _ = LoadApps(v);
        if (v != null && _tabs.SelectedIndex == 3) _ = LoadFiles(v);
        if (v != null && _tabs.SelectedIndex == 1) _ = LoadControlStates(v);
    }

    /// <summary>Usable page width, leaving room for the vertical scrollbar so a horizontal one never appears.</summary>
    int PageWidth => _page.ClientSize.Width - _page.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 16;

    /// <summary>"half" cards sit two per row when there's room, otherwise full width.</summary>
    int HalfWidth => PageWidth >= 2 * 340 + 14 ? (PageWidth - 14) / 2 : Math.Max(300, PageWidth);

    /// <summary>"wide" cards stretch to the page width; "half" cards take half; the rest keep their width.</summary>
    void FitCards()
    {
        foreach (Control c in _page.Controls)
        {
            if (c.Tag as string == "wide") c.Width = Math.Max(300, PageWidth);
            else if (c.Tag as string == "half") c.Width = HalfWidth;
        }
    }

    const int Half = -1;

    /// <summary>Card on the current page. Width: pixels, 0 with wide = full row, or Half.</summary>
    Card NewCard(string title, int width, int height, bool wide = false)
    {
        string? tag = wide ? "wide" : width == Half ? "half" : null;
        if (wide) width = Math.Max(300, PageWidth);
        else if (width == Half) width = HalfWidth;
        var card = new Card { Width = width, Height = height, Margin = new Padding(0, 0, 14, 14), Tag = tag };
        if (title.Length > 0)
        {
            var t = Ui.Text(title, Theme.Heading);
            t.Location = new Point(18, 14);
            card.Controls.Add(t);
        }
        _page.Controls.Add(card);
        return card;
    }

    /// <summary>A wrapping row of controls inside a card, below its title.</summary>
    static FlowLayoutPanel Flow(Card card, int top = 46, FlowDirection dir = FlowDirection.LeftToRight)
    {
        var f = new FlowLayoutPanel
        {
            Location = new Point(16, top), Size = new Size(card.Width - 32, card.Height - top - 12),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            WrapContents = true, FlowDirection = dir, BackColor = Color.Transparent,
        };
        card.Controls.Add(f);
        return f;
    }

    static Control Line(string label, Control right, int width)
    {
        var row = new Panel { Width = width, Height = Math.Max(34, right.Height + 6), BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 4) };
        var l = Ui.Text(label);
        l.Location = new Point(0, (row.Height - l.PreferredHeight) / 2);
        right.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        right.Location = new Point(width - right.Width, (row.Height - right.Height) / 2);
        row.Controls.Add(l);
        row.Controls.Add(right);
        return row;
    }

    PillButton Action(FlowLayoutPanel into, string text, Func<Task> run, PillStyle style = PillStyle.Secondary, bool needsRunning = true)
    {
        var b = new PillButton(text, style) { Margin = new Padding(0, 0, 8, 8) };
        b.Enabled = !needsRunning || Selected?.Running == true;
        b.Click += async (_, _) =>
        {
            b.Enabled = false;
            try { await run(); }
            catch (Exception ex) { SetStatus(ex.Message); }
            finally { b.Enabled = !needsRunning || Selected?.Running == true; }
        };
        into.Controls.Add(b);
        return b;
    }

    // Overview: live screen, specs, usage, performance, quick actions
    void BuildOverview(DeviceView v)
    {
        var screen = NewCard("", 262, 540);
        screen.Padding = new Padding(12);
        _preview = new PictureBox
        {
            Location = new Point(12, 12), Size = new Size(238, 516), SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black, Cursor = v.Running ? Cursors.Hand : Cursors.Default,
        };
        _preview.Click += (_, _) => { if (Selected is { Running: true } s) ShowEmulatorWindow(s.Avd); };
        screen.Controls.Add(_preview);
        var hint = Ui.Text(v.Running ? "" : "Not running", Theme.Heading, sub: true);
        hint.BackColor = Color.Black;
        hint.Location = new Point(12 + (238 - hint.PreferredWidth) / 2, 260);
        if (!v.Running) screen.Controls.Add(hint);
        hint.BringToFront();
        if (_thumbs.TryGetValue(v.Avd, out var last) && v.Running) _preview.Image = (Image)last.Clone();

        var right = new FlowLayoutPanel { Width = 350, Height = 540, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        _page.Controls.Add(right);

        var profile = DeviceProfile.Find(v.Info.Profile);
        var ram = v.Info.RamMb ?? AvdFactory.ConfiguredRamMb(v.Avd);
        var specs = new Card { Width = 336, Height = 176, Margin = new Padding(0, 0, 0, 14) };
        right.Controls.Add(specs);
        AddTitle(specs, "Device");
        AddSpec(specs, 0, "Screen", profile?.Description ?? ScreenFromConfig(v.Avd));
        AddSpec(specs, 1, "Memory", ram > 0 ? $"{ram / 1024.0:0.#} GB" : "Default");
        AddSpec(specs, 2, "CPU", v.Info.Cores is { } c ? $"{c} cores" : "Default");
        AddSpec(specs, 3, "adb serial", v.Serial ?? (v.Info.Port > 0 ? $"emulator-{v.Info.Port}" : "assigned on first start"));

        var usage = new Card { Width = 336, Height = 88, Margin = new Padding(0, 0, 0, 14) };
        right.Controls.Add(usage);
        AddTitle(usage, "Live usage");
        _usage = Ui.Text(v.Running ? "Measuring…" : "—", Theme.Body, sub: true);
        _usage.Location = new Point(18, 48);
        usage.Controls.Add(_usage);

        var perf = new Card { Width = 336, Height = 136, Margin = new Padding(0, 0, 0, 14) };
        right.Controls.Add(perf);
        AddTitle(perf, "Performance");
        var pf = Flow(perf, 46);
        PillButton? lastPreset = null;
        foreach (var preset in PerfPreset.All)
        {
            var b = new PillButton(preset.Name) { Selected = v.Info.Preset == preset.Name, Margin = new Padding(0, 0, 6, 6), Height = 32, Width = 70 };
            _tips.SetToolTip(b, preset.Description);
            b.Click += (_, _) => ApplyPreset(v.Avd, preset);
            pf.Controls.Add(b);
            lastPreset = b;
        }
        if (lastPreset != null) pf.SetFlowBreak(lastPreset, true);
        var pdesc = Ui.Text(PerfPreset.Find(v.Info.Preset)?.Description ?? "Custom settings", Theme.Small, sub: true);
        pf.Controls.Add(pdesc);

        var quick = NewCard("Quick actions", 0, 150, wide: true);
        var qf = Flow(quick);
        Action(qf, "📷  Screenshot", () => TakeScreenshot(v.Avd));
        _recButton = Action(qf, _recording.Contains(v.Avd) ? "■  Stop recording" : "⏺  Record screen", () => ToggleRecording(v.Avd),
            _recording.Contains(v.Avd) ? PillStyle.Danger : PillStyle.Secondary);
        Action(qf, "Install APK…", () => PickAndInstall(v.Avd));
        Action(qf, "Send files…", () => PickAndSendFiles(v.Avd));
        Action(qf, "Logcat", () => { WithSerial(v.Avd, _sdk.Logcat); return Task.CompletedTask; });
        Action(qf, "adb shell", () => { WithSerial(v.Avd, _sdk.OpenShell); return Task.CompletedTask; });
        Action(qf, "Media folder", () => { Directory.CreateDirectory(MediaFolder); Sdk.Launch("explorer.exe", $"\"{MediaFolder}\""); return Task.CompletedTask; }, needsRunning: false);
        _recBadge = Ui.Text(_recording.Contains(v.Avd) ? "● Recording" : "", Theme.SmallBold);
        _recBadge.ForeColor = Ui.T.Bad;
        qf.Controls.Add(_recBadge);
    }

    static void AddTitle(Card card, string title)
    {
        var t = Ui.Text(title, Theme.Heading);
        t.Location = new Point(18, 14);
        card.Controls.Add(t);
    }

    static void AddSpec(Card card, int row, string key, string value)
    {
        var k = Ui.Text(key, Theme.Body, sub: true);
        k.Location = new Point(18, 48 + row * 28);
        var val = Ui.Text(value, Theme.Body);
        val.Location = new Point(110, 48 + row * 28);
        val.AutoSize = false;
        val.Size = new Size(card.Width - 128, 22);
        val.AutoEllipsis = true;
        card.Controls.Add(k);
        card.Controls.Add(val);
    }

    static string ScreenFromConfig(string avd)
    {
        try
        {
            var cfg = File.ReadAllLines(Path.Combine(Paths.AvdHome, avd + ".avd", "config.ini"))
                .Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
            return $"{cfg.GetValueOrDefault("hw.lcd.width")} × {cfg.GetValueOrDefault("hw.lcd.height")} · {cfg.GetValueOrDefault("hw.lcd.density")} dpi";
        }
        catch { return "—"; }
    }

    // Controls: hardware buttons, location, battery, network, display, sensors
    Toggle? _darkToggle, _wifiToggle, _airplaneToggle, _gestureToggle, _chargingToggle;
    Slider? _battery;

    void BuildControls(DeviceView v)
    {
        if (!v.Running)
        {
            var off = NewCard("Phone controls", 0, 110, wide: true);
            var l = Ui.Text("Start the device to use its controls.", Theme.Body, sub: true);
            l.Location = new Point(18, 52);
            off.Controls.Add(l);
            return;
        }
        var serial = v.Serial!;
        var con = EmulatorConsole.For(serial);

        var buttons = NewCard("Buttons", 0, 150, wide: true);
        var bf = Flow(buttons);
        Action(bf, "◁  Back", () => _sdk.KeyEvent(serial, 4));
        Action(bf, "○  Home", () => _sdk.KeyEvent(serial, 3));
        Action(bf, "▢  Recents", () => _sdk.KeyEvent(serial, 187));
        Action(bf, "⏻  Power", () => _sdk.KeyEvent(serial, 26));
        Action(bf, "🔊  Vol +", () => _sdk.KeyEvent(serial, 24));
        Action(bf, "🔉  Vol −", () => _sdk.KeyEvent(serial, 25));
        Action(bf, "⟳  Rotate", () => con.Send("rotate"));
        Action(bf, "📷  Screenshot", () => TakeScreenshot(v.Avd));
        if (DeviceProfile.Find(v.Info.Profile)?.Foldable == true)
        {
            Action(bf, "Fold", () => con.Send("fold"));
            Action(bf, "Unfold", () => con.Send("unfold"));
        }

        var loc = NewCard("Location", Half, 200);
        var place = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 200, Font = Theme.Body };
        place.Items.AddRange(Places.All.Select(p => (object)p.Name).ToArray());
        var lat = new TextBox { Width = 150, PlaceholderText = "Latitude", BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body };
        var lon = new TextBox { Width = 150, PlaceholderText = "Longitude", BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body };
        place.SelectedIndexChanged += (_, _) =>
        {
            var p = Places.All[place.SelectedIndex];
            lat.Text = p.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lon.Text = p.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture);
        };
        var lf = Flow(loc);
        lf.Controls.Add(Line("City", place, 318));
        var coords = new FlowLayoutPanel { Width = 318, Height = 36, BackColor = Color.Transparent, Margin = new Padding(0) };
        coords.Controls.Add(lat); coords.Controls.Add(lon);
        lf.Controls.Add(coords);
        Action(lf, "Set location", async () =>
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!double.TryParse(lat.Text, System.Globalization.NumberStyles.Float, inv, out var la) ||
                !double.TryParse(lon.Text, System.Globalization.NumberStyles.Float, inv, out var lo) ||
                la is < -90 or > 90 || lo is < -180 or > 180)
                throw new InvalidOperationException("Enter a latitude (-90 to 90) and longitude (-180 to 180).");
            await con.Send($"geo fix {lo.ToString(inv)} {la.ToString(inv)}"); // geo fix takes longitude first
            SetStatus($"{v.Avd}: location set to {la:0.####}, {lo:0.####}.");
        }, PillStyle.Primary);

        var battery = NewCard("Battery", Half, 200);
        var bf2 = Flow(battery);
        _battery = new Slider { Width = 240, Maximum = 100, Value = 100 };
        var pct = Ui.Text("100%", Theme.BodyBold);
        _battery.ValueCommitted += async (_, _) =>
        {
            pct.Text = $"{_battery.Value}%";
            await con.TrySend($"power capacity {_battery.Value}");
        };
        var batRow = new FlowLayoutPanel { Width = 318, Height = 34, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8) };
        batRow.Controls.Add(_battery); batRow.Controls.Add(pct);
        bf2.Controls.Add(batRow);
        _chargingToggle = new Toggle();
        _chargingToggle.Toggled += async (_, _) =>
        {
            await con.TrySend($"power ac {(_chargingToggle.On ? "on" : "off")}");
            await con.TrySend($"power status {(_chargingToggle.On ? "charging" : "discharging")}");
        };
        bf2.Controls.Add(Line("Charging", _chargingToggle, 318));

        var net = NewCard("Network", Half, 200);
        var nf = Flow(net);
        var speed = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 160, Font = Theme.Body };
        var speeds = new[] { ("5G / unlimited", "full"), ("LTE", "lte"), ("3G", "umts"), ("EDGE", "edge"), ("GPRS", "gprs") };
        speed.Items.AddRange(speeds.Select(s => (object)s.Item1).ToArray());
        speed.SelectedIndex = 0;
        speed.SelectedIndexChanged += async (_, _) => await con.TrySend($"network speed {speeds[speed.SelectedIndex].Item2}");
        nf.Controls.Add(Line("Mobile data speed", speed, 318));
        _wifiToggle = new Toggle();
        _wifiToggle.Toggled += async (_, _) => await _sdk.ShellCmd(serial, $"svc wifi {(_wifiToggle.On ? "enable" : "disable")}");
        nf.Controls.Add(Line("Wi-Fi", _wifiToggle, 318));
        _airplaneToggle = new Toggle();
        _airplaneToggle.Toggled += async (_, _) => await _sdk.ShellCmd(serial, $"cmd connectivity airplane-mode {(_airplaneToggle.On ? "enable" : "disable")}");
        nf.Controls.Add(Line("Airplane mode", _airplaneToggle, 318));

        var display = NewCard("Display & system", Half, 200);
        var df = Flow(display);
        _darkToggle = new Toggle();
        _darkToggle.Toggled += async (_, _) => await _sdk.ShellCmd(serial, $"cmd uimode night {(_darkToggle.On ? "yes" : "no")}");
        df.Controls.Add(Line("Dark theme", _darkToggle, 318));
        _gestureToggle = new Toggle();
        _gestureToggle.Toggled += async (_, _) =>
        {
            var overlay = _gestureToggle.On ? "com.android.internal.systemui.navbar.gestural" : "com.android.internal.systemui.navbar.threebutton";
            await _sdk.ShellCmd(serial, $"cmd overlay enable-exclusive --category {overlay}");
        };
        df.Controls.Add(Line("Gesture navigation", _gestureToggle, 318));
        var animations = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 140, Font = Theme.Body };
        animations.Items.AddRange(new object[] { "Normal", "Fast (0.5×)", "Off" });
        animations.SelectedIndex = 0;
        animations.SelectedIndexChanged += async (_, _) =>
        {
            var scale = animations.SelectedIndex switch { 1 => "0.5", 2 => "0", _ => "1" };
            foreach (var key in new[] { "window_animation_scale", "transition_animation_scale", "animator_duration_scale" })
                await _sdk.ShellCmd(serial, $"settings put global {key} {scale}");
        };
        df.Controls.Add(Line("Animations", animations, 318));

        BuildHomeScreenCard(v);

        var sensors = NewCard("Sensors", Half, 150);
        var sf = Flow(sensors);
        Action(sf, "Shake", async () =>
        {
            for (int i = 0; i < 6; i++)
            {
                await con.Send($"sensor set acceleration {(i % 2 == 0 ? "18" : "-18")}:9.8:0");
                await Task.Delay(60);
            }
            await con.Send("sensor set acceleration 0:9.8:0");
        });
        Action(sf, "Touch fingerprint", async () =>
        {
            await con.Send("finger touch 1");
            await Task.Delay(400);
            await con.Send("finger remove");
        });
    }

    async Task LoadControlStates(DeviceView v)
    {
        if (!v.Running || v.Serial == null) return;
        try
        {
            var night = await _sdk.ShellCmd(v.Serial, "cmd uimode night");
            var wifi = await _sdk.ShellCmd(v.Serial, "settings get global wifi_on");
            var air = await _sdk.ShellCmd(v.Serial, "settings get global airplane_mode_on");
            var nav = await _sdk.ShellCmd(v.Serial, "cmd overlay list --user 0 android");
            var power = await EmulatorConsole.For(v.Serial).Send("power display");
            if (_selected != v.Avd || _tabs.SelectedIndex != 1) return;
            _darkToggle?.SetQuiet(night.Contains("yes"));
            _wifiToggle?.SetQuiet(wifi.Trim() != "0");
            _airplaneToggle?.SetQuiet(air.Trim() == "1");
            _gestureToggle?.SetQuiet(nav.Contains("[x] com.android.internal.systemui.navbar.gestural"));
            var cap = power.Split('\n').FirstOrDefault(l => l.StartsWith("capacity:"));
            if (cap != null && int.TryParse(cap[9..].Trim(), out var pct) && _battery != null) _battery.Value = pct;
            _chargingToggle?.SetQuiet(power.Contains("AC: online"));
        }
        catch { }
    }

    // Snapshots
    void BuildSnapshots(DeviceView v)
    {
        var card = NewCard("Snapshots", 0, 420, wide: true);
        var note = Ui.Text("Save the device's exact state and jump back to it in seconds — e.g. \"fresh install\" or \"level 10\".", Theme.Small, sub: true);
        note.Location = new Point(18, 42);
        card.Controls.Add(note);
        _snapList = new ListView
        {
            Location = new Point(18, 72), Size = new Size(card.Width - 36, 270), View = View.Details, FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.None, Font = Theme.Body, MultiSelect = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _snapList.Columns.Add("Name", 300);
        _snapList.Columns.Add("Saved", 200);
        _snapList.Columns.Add("Size", 120);
        card.Controls.Add(_snapList);
        var row = new FlowLayoutPanel { Location = new Point(18, 354), Size = new Size(card.Width - 36, 50), BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        card.Controls.Add(row);

        Action(row, "Save current state…", async () =>
        {
            var name = Prompt("Save snapshot", "Name for this snapshot (letters, numbers, - and _):", $"snap-{DateTime.Now:MMdd-HHmm}");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!AvdFactory.ValidName.IsMatch(name)) throw new InvalidOperationException("Use letters, numbers, '.', '_' or '-'.");
            SetStatus($"Saving snapshot {name}…");
            await EmulatorConsole.For(v.Serial!).Send($"avd snapshot save {name}");
            SetStatus($"Saved snapshot {name}.");
            await LoadSnapshots(v);
        }, PillStyle.Primary);
        Action(row, "Restore", async () =>
        {
            var name = SelectedSnapshot(); if (name == null) return;
            SetStatus($"Restoring {name}…");
            await EmulatorConsole.For(v.Serial!).Send($"avd snapshot load {name}");
            SetStatus($"Restored {name}.");
        });
        Action(row, "Delete", async () =>
        {
            var name = SelectedSnapshot(); if (name == null) return;
            if (MessageBox.Show(this, $"Delete snapshot '{name}'?", "Delete snapshot", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            await EmulatorConsole.For(v.Serial!).Send($"avd snapshot delete {name}");
            await LoadSnapshots(v);
        }, PillStyle.Danger);
        if (!v.Running)
        {
            var l = Ui.Text("Start the device to save or restore snapshots.", Theme.Small, sub: true);
            row.Controls.Add(l);
        }
    }

    string? SelectedSnapshot()
    {
        if (_snapList?.SelectedItems.Count is not > 0) { SetStatus("Pick a snapshot first."); return null; }
        return (string)_snapList.SelectedItems[0].Tag!;
    }

    Task LoadSnapshots(DeviceView v)
    {
        if (_snapList == null) return Task.CompletedTask;
        _snapList.Items.Clear();
        var dir = Path.Combine(Paths.AvdHome, v.Avd + ".avd", "snapshots");
        if (!Directory.Exists(dir)) return Task.CompletedTask;
        foreach (var d in new DirectoryInfo(dir).GetDirectories().OrderByDescending(d => d.LastWriteTime))
        {
            long size = 0;
            try { size = d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
            var label = d.Name == "default_boot" ? "Quick boot (automatic)" : d.Name;
            var item = new ListViewItem(new[] { label, d.LastWriteTime.ToString("g"), $"{size / 1048576.0:0} MB" }) { Tag = d.Name };
            _snapList.Items.Add(item);
        }
        return Task.CompletedTask;
    }

    // Settings: profile, performance, camera/mic, background, label, manage
    void BuildSettings(DeviceView v)
    {
        bool stopped = !v.Running && !v.Starting;

        var hw = NewCard("Hardware", Half, 300);
        var hf = Flow(hw);
        var profile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 200, Font = Theme.Body };
        profile.Items.Add("Keep current screen");
        profile.Items.AddRange(DeviceProfile.All.Select(p => (object)p.Name).ToArray());
        profile.SelectedIndex = Math.Max(0, Array.FindIndex(DeviceProfile.All, p => p.Name == v.Info.Profile) + 1);
        profile.Enabled = stopped;
        hf.Controls.Add(Line("Device profile", profile, 318));
        var ramBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 200, Font = Theme.Body };
        var ramChoices = new int?[] { null, 2048, 3072, 4096, 6144, 8192 };
        ramBox.Items.AddRange(ramChoices.Select(m => (object)(m is { } mb ? $"{mb / 1024} GB" : $"Default ({AvdFactory.ConfiguredRamMb(v.Avd) / 1024.0:0.#} GB)")).ToArray());
        ramBox.SelectedIndex = Math.Max(0, Array.IndexOf(ramChoices, v.Info.RamMb));
        hf.Controls.Add(Line("Memory", ramBox, 318));
        var coresBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 200, Font = Theme.Body };
        var coreChoices = new int?[] { null, 2, 4, 6, 8 }.Where(c => c == null || c <= Environment.ProcessorCount).ToArray();
        coresBox.Items.AddRange(coreChoices.Select(c => (object)(c is { } n ? $"{n} cores" : "Default")).ToArray());
        coresBox.SelectedIndex = Math.Max(0, Array.IndexOf(coreChoices, v.Info.Cores));
        hf.Controls.Add(Line("CPU", coresBox, 318));
        var bg = new Toggle(); bg.SetQuiet(v.Info.Headless);
        hf.Controls.Add(Line("Run in background (no window)", bg, 318));
        Action(hf, "Save hardware", () =>
        {
            InstanceStore.Edit(v.Avd, i =>
            {
                i.RamMb = ramChoices[ramBox.SelectedIndex];
                i.Cores = coreChoices[coresBox.SelectedIndex];
                i.Headless = bg.On;
                if (PerfPreset.Find(i.Preset) is { } pp && (pp.RamMb != i.RamMb || pp.EffectiveCores != i.Cores)) i.Preset = null;
            });
            if (profile.SelectedIndex > 0 && stopped) AvdFactory.ApplyProfile(v.Avd, DeviceProfile.All[profile.SelectedIndex - 1]);
            SetStatus(v.Running ? $"{v.Avd}: saved — restart the device to apply." : $"{v.Avd}: saved.");
            return RefreshAll();
        }, PillStyle.Primary, needsRunning: false);

        var media = NewCard("Camera & microphone", Half, 300);
        var mf = Flow(media);
        ComboBox Cam(string? current)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 180, Font = Theme.Body };
            c.Items.AddRange(CameraModes.All.Select(m => (object)m.Label).ToArray());
            c.SelectedIndex = Math.Max(0, Array.FindIndex(CameraModes.All, m => m.Value == (current ?? "emulated")));
            return c;
        }
        var back = Cam(v.Info.CameraBack);
        var front = Cam(v.Info.CameraFront);
        mf.Controls.Add(Line("Back camera", back, 318));
        mf.Controls.Add(Line("Front camera", front, 318));
        var mic = new Toggle(); mic.SetQuiet(v.Info.HostMic);
        mf.Controls.Add(Line("Use PC microphone", mic, 318));
        var mnote = Ui.Text("\"PC webcam\" uses your first webcam. Changes apply on the next start.", Theme.Small, sub: true);
        mnote.MaximumSize = new Size(318, 0);
        mf.Controls.Add(mnote);
        Action(mf, "Save camera & mic", () =>
        {
            InstanceStore.Edit(v.Avd, i =>
            {
                i.CameraBack = CameraModes.All[back.SelectedIndex].Value;
                i.CameraFront = CameraModes.All[front.SelectedIndex].Value;
                i.HostMic = mic.On;
            });
            SetStatus(v.Running ? $"{v.Avd}: saved — restart the device to apply." : $"{v.Avd}: saved.");
            return Task.CompletedTask;
        }, PillStyle.Primary, needsRunning: false);

        var ident = NewCard("Project & tools", Half, 210);
        var idf = Flow(ident);
        var label = new TextBox { Width = 200, Text = v.Info.Project ?? "", PlaceholderText = "e.g. Weaverse", BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body };
        idf.Controls.Add(Line("Project label", label, 318));
        Action(idf, "Save label", () => { InstanceStore.SetProject(v.Avd, label.Text); return RefreshAll(); }, needsRunning: false);
        Action(idf, "Copy adb serial", () =>
        {
            var s = v.Serial ?? "emulator-" + InstanceStore.EnsurePort(v.Avd);
            Clipboard.SetText(s);
            SetStatus($"Copied {s}.");
            return Task.CompletedTask;
        }, needsRunning: false);

        var manage = NewCard("Manage", Half, 210);
        var mgf = Flow(manage);
        Action(mgf, "Duplicate…", () => NewInstance(v.Avd), needsRunning: false);
        Action(mgf, "Cold boot", () => { StartWith(v.Avd, "-no-snapshot-load"); return Task.CompletedTask; }, needsRunning: false).Enabled = stopped;
        Action(mgf, "Writable system", () => { StartWith(v.Avd, "-writable-system -no-snapshot-load"); return Task.CompletedTask; }, needsRunning: false).Enabled = stopped;
        Action(mgf, "Open folder", () => { Sdk.Launch("explorer.exe", $"\"{Path.Combine(Paths.AvdHome, v.Avd + ".avd")}\""); return Task.CompletedTask; }, needsRunning: false);
        Action(mgf, "Wipe data…", () =>
        {
            if (MessageBox.Show(this, $"Erase all apps and data on '{v.Avd}'? This cannot be undone.", "Wipe data",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                StartWith(v.Avd, "-wipe-data");
            return Task.CompletedTask;
        }, PillStyle.Danger, needsRunning: false).Enabled = stopped;
        Action(mgf, "Delete device…", () => DeleteInstance(v.Avd), PillStyle.Danger, needsRunning: false).Enabled = stopped;
    }

    // ── Live preview, usage, media ─────────────────────────────────────────

    /// <summary>Refreshes the big preview (selected device) and that device's usage numbers.</summary>
    async Task RefreshPreview()
    {
        var v = Selected;
        if (v == null || _previewBusy || !Visible || WindowState == FormWindowState.Minimized) return;
        _previewBusy = true;
        try
        {
            if (_usage != null) _usage.Text = v.Running ? Usage(v.Avd) : "—";
            if (_preview == null || !v.Running || v.Serial == null || !v.Booted) return;
            var png = await _sdk.Screencap(v.Serial);
            if (png == null || _preview == null || _selected != v.Avd) return;
            using var ms = new MemoryStream(png);
            using var full = Image.FromStream(ms);
            var shown = new Bitmap(full, new Size(Math.Max(1, full.Width / 3), Math.Max(1, full.Height / 3)));
            var old = _preview.Image;
            _preview.Image = shown;
            old?.Dispose();
            SetThumb(v.Avd, shown);
        }
        catch { }
        finally { _previewBusy = false; }
    }

    string Usage(string avd)
    {
        if (Sdk.QemuPid(avd) is not { } pid) return "—";
        try
        {
            using var p = Process.GetProcessById(pid);
            var now = DateTime.UtcNow;
            // Process.TotalProcessorTime is denied on the emulator's VM process; a limited-rights query works.
            var cpu = CpuTime(pid) ?? TimeSpan.Zero;
            string cpuText = "…";
            if (_cpuSamples.TryGetValue(avd, out var prev) && (now - prev.at).TotalMilliseconds > 200)
            {
                var pct = (cpu - prev.cpu).TotalMilliseconds / (now - prev.at).TotalMilliseconds / Environment.ProcessorCount * 100;
                cpuText = $"{Math.Clamp(pct, 0, 100):0}%";
            }
            _cpuSamples[avd] = (cpu, now);
            return $"CPU {cpuText}   ·   RAM {p.WorkingSet64 / 1048576.0:N0} MB";
        }
        catch { return "—"; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static TimeSpan? CpuTime(int pid)
    {
        var h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try { return GetProcessTimes(h, out _, out _, out var k, out var u) ? TimeSpan.FromTicks(k + u) : null; }
        finally { CloseHandle(h); }
    }

    void SetThumb(string avd, Image img)
    {
        var small = new Bitmap(img, new Size(68, Math.Max(1, (int)(68.0 * img.Height / img.Width))));
        if (_thumbs.Remove(avd, out var old)) old.Dispose();
        _thumbs[avd] = small;
        if (_tiles.TryGetValue(avd, out var tile)) { tile.Thumb = small; tile.Invalidate(); }
    }

    string MediaFolder => _state.MediaFolder is { Length: > 0 } f
        ? f
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Celestium");

    async Task TakeScreenshot(string avd)
    {
        if (!_views.TryGetValue(avd, out var v) || v.Serial == null) return;
        var png = await _sdk.Screencap(v.Serial) ?? throw new InvalidOperationException("Couldn't capture the screen yet.");
        Directory.CreateDirectory(MediaFolder);
        var file = Path.Combine(MediaFolder, $"{avd}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
        await File.WriteAllBytesAsync(file, png);
        SetStatus($"Saved screenshot {Path.GetFileName(file)}.");
        _tray.ShowBalloonTip(2500, "Screenshot saved", file, ToolTipIcon.None);
    }

    async Task ToggleRecording(string avd)
    {
        if (!_views.TryGetValue(avd, out var v) || v.Serial == null) return;
        var con = EmulatorConsole.For(v.Serial);
        // The console splits on spaces, so record to a space-free temp path and move the file afterwards.
        var temp = Path.Combine(Path.GetTempPath(), $"celestium_{avd}.webm");
        if (_recording.Remove(avd))
        {
            await con.Send("screenrecord stop");
            await Task.Delay(1500);
            Directory.CreateDirectory(MediaFolder);
            var file = Path.Combine(MediaFolder, $"{avd}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.webm");
            if (File.Exists(temp)) File.Move(temp, file, true);
            SetStatus($"Saved recording {Path.GetFileName(file)}.");
        }
        else
        {
            if (File.Exists(temp)) File.Delete(temp);
            await con.Send($"screenrecord start --time-limit 1800 --fps 30 {temp}");
            _recording.Add(avd);
            SetStatus($"Recording {avd}… press Stop recording when done (30 min max).");
        }
        if (_selected == avd && _tabs.SelectedIndex == 0) BuildPage();
    }

    async Task PickAndSendFiles(string avd)
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = "Send files to the device's Downloads folder" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await SendFiles(avd, dlg.FileNames);
    }

    async Task SendFiles(string avd, IEnumerable<string> files)
    {
        if (!_views.TryGetValue(avd, out var v) || v.Serial == null) { SetStatus("Start the device first."); return; }
        int n = 0;
        foreach (var f in files)
        {
            SetStatus($"Sending {Path.GetFileName(f)} to {avd}…");
            var (code, output) = await _sdk.Push(v.Serial, f, "/sdcard/Download/");
            if (code != 0) { SetStatus($"Couldn't send {Path.GetFileName(f)}: {output.Trim()}"); return; }
            n++;
        }
        // Make them show up in Files/Gallery right away.
        await _sdk.ShellCmd(v.Serial, "am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d file:///sdcard/Download/");
        SetStatus($"Sent {n} file(s) to {avd} › Downloads.");
    }

    void ShowEmulatorWindow(string avd)
    {
        if (Sdk.QemuPid(avd) is not { } pid) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            var h = p.MainWindowHandle;
            if (h == IntPtr.Zero) return;
            if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
            SetForegroundWindow(h);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

    void ApplyPreset(string avd, PerfPreset preset)
    {
        InstanceStore.Edit(avd, i => { i.Preset = preset.Name; i.RamMb = preset.RamMb; i.Cores = preset.EffectiveCores; });
        SetStatus(_running.ContainsKey(avd) ? $"{avd}: {preset.Name} preset saved — restart the device to apply." : $"{avd}: {preset.Name} preset saved.");
        _ = RefreshAll();
    }
}

/// <summary>Everything the UI shows about one device, rebuilt on every poll.</summary>
sealed record DeviceView(string Avd, InstanceInfo Info, string? Serial, bool Running, bool Booted, bool Starting)
{
    public string StatusText => Running ? (Booted ? $"Running · {Serial}" : $"Booting · {Serial}")
        : Starting ? "Starting…"
        : Info.Port > 0 ? $"Stopped · emulator-{Info.Port}" : "Stopped";
}
