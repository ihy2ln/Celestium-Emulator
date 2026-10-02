namespace DroidLauncher;

/// <summary>Apps and Files tabs, plus home-screen customisation helpers.</summary>
partial class MainForm
{
    ListView? _appList, _fileList;
    TextBox? _appFilter;
    Label? _filePath;
    bool _showSystemApps;
    string _cwd = "/sdcard";
    List<(string Package, string Version)> _apps = new();

    static string Q(string path) => "'" + path.Replace("'", "'\\''") + "'"; // quote for the device shell

    // ── Apps ────────────────────────────────────────────────────────────────

    void BuildApps(DeviceView v)
    {
        if (!NeedRunning(v, "Apps")) return;
        var serial = v.Serial!;
        var card = NewCard("Installed apps", 0, 480, wide: true);

        var bar = new FlowLayoutPanel { Location = new Point(16, 46), Size = new Size(card.Width - 32, 44), BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        card.Controls.Add(bar);
        _appFilter = new TextBox { Width = 220, PlaceholderText = "Search apps", BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body, Margin = new Padding(0, 4, 12, 0) };
        _appFilter.TextChanged += (_, _) => ShowApps();
        bar.Controls.Add(_appFilter);
        var sys = new Toggle { Margin = new Padding(0, 6, 6, 0) };
        sys.SetQuiet(_showSystemApps);
        sys.Toggled += async (_, _) => { _showSystemApps = sys.On; await LoadApps(v); };
        bar.Controls.Add(sys);
        bar.Controls.Add(new Label { Text = "System apps", AutoSize = true, Margin = new Padding(0, 9, 16, 0), ForeColor = Ui.T.Text, Font = Theme.Body });
        Action(bar, "↻ Refresh", () => LoadApps(v));
        Action(bar, "Install APK…", () => PickAndInstall(v.Avd));

        _appList = new ListView
        {
            Location = new Point(18, 148), Size = new Size(card.Width - 36, 316), View = View.Details, FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Clickable, BorderStyle = BorderStyle.None, Font = Theme.Body, MultiSelect = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _appList.Columns.Add("App", 220);
        _appList.Columns.Add("Package", 280);
        _appList.Columns.Add("Version", 140);
        _appList.DoubleClick += async (_, _) => { if (SelectedApp() is { } p) await _sdk.ShellCmd(serial, $"monkey -p {p} -c android.intent.category.LAUNCHER 1"); };
        card.Controls.Add(_appList);

        var actions = new FlowLayoutPanel { Location = new Point(16, 92), Size = new Size(card.Width - 32, 50), BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        card.Controls.Add(actions);
        Action(actions, "▶ Open", async () => { if (SelectedApp() is { } p) await _sdk.ShellCmd(serial, $"monkey -p {p} -c android.intent.category.LAUNCHER 1"); }, PillStyle.Primary);
        Action(actions, "Force stop", async () => { if (SelectedApp() is { } p) { await _sdk.ShellCmd(serial, $"am force-stop {p}"); SetStatus($"Stopped {p}."); } });
        Action(actions, "App info", async () => { if (SelectedApp() is { } p) await _sdk.ShellCmd(serial, $"am start -a android.settings.APPLICATION_DETAILS_SETTINGS -d package:{p}"); });
        Action(actions, "Save APK…", async () =>
        {
            if (SelectedApp() is not { } p) return;
            var paths = (await _sdk.ShellCmd(serial, $"pm path {p}")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.StartsWith("package:") ? l[8..] : l).ToList();
            if (paths.Count == 0) throw new InvalidOperationException("Couldn't find that app's APK.");
            using var dlg = new FolderBrowserDialog { Description = $"Save {p} APK to…", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            foreach (var remote in paths)
            {
                var name = paths.Count == 1 ? $"{p}.apk" : $"{p}_{Path.GetFileName(remote)}";
                await Sdk.Run(_sdk.Adb, $"-s {serial} pull \"{remote}\" \"{Path.Combine(dlg.SelectedPath, name)}\"", 600_000);
            }
            SetStatus($"Saved {p} to {dlg.SelectedPath}.");
        });
        Action(actions, "Clear data…", async () =>
        {
            if (SelectedApp() is not { } p) return;
            if (MessageBox.Show(this, $"Erase all data for {p}? The app goes back to a fresh install.", "Clear data", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            SetStatus(await _sdk.ShellCmd(serial, $"pm clear {p}") == "Success" ? $"Cleared {p}." : $"Couldn't clear {p}.");
        });
        Action(actions, "Uninstall…", async () =>
        {
            if (SelectedApp() is not { } p) return;
            if (MessageBox.Show(this, $"Uninstall {p} from {v.Avd}?", "Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var r = await _sdk.ShellCmd(serial, $"pm uninstall {p}");
            SetStatus(r.Contains("Success") ? $"Uninstalled {p}." : $"Couldn't uninstall {p}: {r}");
            await LoadApps(v);
        }, PillStyle.Danger);
    }

    string? SelectedApp()
    {
        if (_appList?.SelectedItems.Count is not > 0) { SetStatus("Pick an app first."); return null; }
        return (string)_appList.SelectedItems[0].Tag!;
    }

    static string FriendlyName(string package)
    {
        var last = package.Split('.').Last();
        return last.Length == 0 ? package : char.ToUpperInvariant(last[0]) + last[1..];
    }

    async Task LoadApps(DeviceView v)
    {
        if (_appList == null || v.Serial == null) return;
        SetStatus("Loading apps…");
        var flag = _showSystemApps ? "" : "-3";
        var list = await _sdk.ShellCmd(v.Serial, $"pm list packages {flag}", 30_000);
        var packages = list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("package:")).Select(l => l[8..]).OrderBy(p => p).ToList();
        _apps = packages.Select(p => (p, "")).ToList();
        ShowApps();
        // Versions in one shell round trip, only for user apps (system apps would mean hundreds of dumpsys calls).
        if (!_showSystemApps && packages.Count > 0)
        {
            // No double quotes: Windows argument parsing would strip them before adb sees the command.
            var script = $"for p in {string.Join(' ', packages)}; do v=$(dumpsys package $p | grep -m1 versionName); echo $p ${{v#*=}}; done";
            var output = await _sdk.ShellCmd(v.Serial, script, 60_000);
            var versions = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split(' ', 2)).Where(a => a.Length == 2).GroupBy(a => a[0]).ToDictionary(g => g.Key, g => g.First()[1]);
            _apps = packages.Select(p => (p, versions.GetValueOrDefault(p, ""))).ToList();
            if (_selected == v.Avd) ShowApps();
        }
        SetStatus($"{packages.Count} {(_showSystemApps ? "" : "user ")}app{(packages.Count == 1 ? "" : "s")} on {v.Avd}. Double-click one to open it.");
    }

    void ShowApps()
    {
        if (_appList == null) return;
        var filter = _appFilter?.Text.Trim() ?? "";
        _appList.BeginUpdate();
        _appList.Items.Clear();
        foreach (var (pkg, ver) in _apps.Where(a => filter.Length == 0 || a.Package.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            _appList.Items.Add(new ListViewItem(new[] { FriendlyName(pkg), pkg, ver }) { Tag = pkg });
        _appList.EndUpdate();
    }

    // ── Files ───────────────────────────────────────────────────────────────

    void BuildFiles(DeviceView v)
    {
        if (!NeedRunning(v, "Files")) return;
        var serial = v.Serial!;
        var card = NewCard("Phone storage", 0, 480, wide: true);

        var bar = new FlowLayoutPanel { Location = new Point(16, 46), Size = new Size(card.Width - 32, 44), BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        card.Controls.Add(bar);
        Action(bar, "↑ Up", async () => { if (_cwd != "/sdcard" && _cwd != "/") { _cwd = _cwd[.._cwd.LastIndexOf('/')]; if (_cwd.Length == 0) _cwd = "/"; await LoadFiles(v); } });
        Action(bar, "⌂ Storage", async () => { _cwd = "/sdcard"; await LoadFiles(v); });
        Action(bar, "Downloads", async () => { _cwd = "/sdcard/Download"; await LoadFiles(v); });
        Action(bar, "Pictures", async () => { _cwd = "/sdcard/Pictures"; await LoadFiles(v); });
        _filePath = new Label { AutoSize = true, Margin = new Padding(8, 9, 0, 0), Font = Theme.BodyBold, ForeColor = Ui.T.Accent, Tag = "accent", UseMnemonic = false };
        bar.Controls.Add(_filePath);

        _fileList = new ListView
        {
            Location = new Point(18, 148), Size = new Size(card.Width - 36, 316), View = View.Details, FullRowSelect = true,
            BorderStyle = BorderStyle.None, Font = Theme.Body, MultiSelect = true, AllowDrop = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _fileList.Columns.Add("Name", 360);
        _fileList.Columns.Add("Size", 110, HorizontalAlignment.Right);
        _fileList.Columns.Add("Modified", 170);
        _fileList.DoubleClick += async (_, _) =>
        {
            if (_fileList.SelectedItems.Count == 1 && _fileList.SelectedItems[0].Tag is (string name, true))
            {
                _cwd = _cwd.TrimEnd('/') + "/" + name;
                await LoadFiles(v);
            }
        };
        // Files dropped on the list go to the folder being shown (not just Downloads).
        _fileList.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        _fileList.DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) await Upload(v, files);
        };
        card.Controls.Add(_fileList);

        var actions = new FlowLayoutPanel { Location = new Point(16, 92), Size = new Size(card.Width - 32, 50), BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        card.Controls.Add(actions);
        Action(actions, "Upload files…", async () =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Title = $"Upload to {_cwd}" };
            if (dlg.ShowDialog(this) == DialogResult.OK) await Upload(v, dlg.FileNames);
        }, PillStyle.Primary);
        Action(actions, "Download to PC", async () =>
        {
            var picked = SelectedFiles(); if (picked.Count == 0) return;
            var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Celestium");
            Directory.CreateDirectory(dest);
            foreach (var (name, _) in picked)
            {
                SetStatus($"Downloading {name}…");
                var (code, output) = await Sdk.Run(_sdk.Adb, $"-s {serial} pull \"{_cwd.TrimEnd('/')}/{name}\" \"{dest}\"", 1_800_000);
                if (code != 0) throw new InvalidOperationException($"Couldn't download {name}: {output.Trim()}");
            }
            SetStatus($"Downloaded {picked.Count} item(s) to {dest}.");
            Sdk.Launch("explorer.exe", $"\"{dest}\"");
        });
        Action(actions, "New folder…", async () =>
        {
            var name = Prompt("New folder", $"Folder name inside {_cwd}:", "New folder");
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) return;
            await _sdk.ShellCmd(serial, $"mkdir -p {Q(_cwd.TrimEnd('/') + "/" + name.Trim())}");
            await LoadFiles(v);
        });
        Action(actions, "Delete…", async () =>
        {
            var picked = SelectedFiles(); if (picked.Count == 0) return;
            var what = picked.Count == 1 ? picked[0].Name : $"{picked.Count} items";
            if (MessageBox.Show(this, $"Permanently delete {what} from the phone?", "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            foreach (var (name, _) in picked) await _sdk.ShellCmd(serial, $"rm -rf {Q(_cwd.TrimEnd('/') + "/" + name)}");
            await LoadFiles(v);
        }, PillStyle.Danger);
        Action(actions, "↻ Refresh", () => LoadFiles(v));
    }

    List<(string Name, bool Dir)> SelectedFiles()
    {
        var list = _fileList?.SelectedItems.Cast<ListViewItem>().Select(i => ((string, bool))i.Tag!).ToList() ?? new();
        if (list.Count == 0) SetStatus("Pick one or more files first.");
        return list;
    }

    async Task Upload(DeviceView v, IEnumerable<string> paths)
    {
        int n = 0;
        foreach (var f in paths)
        {
            SetStatus($"Uploading {Path.GetFileName(f)}…");
            var (code, output) = await _sdk.Push(v.Serial!, f, _cwd.TrimEnd('/') + "/");
            if (code != 0) { SetStatus($"Couldn't upload {Path.GetFileName(f)}: {output.Trim()}"); break; }
            n++;
        }
        await _sdk.ShellCmd(v.Serial!, $"am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d {Q("file://" + _cwd)}");
        SetStatus($"Uploaded {n} item(s) to {_cwd}.");
        await LoadFiles(v);
    }

    async Task LoadFiles(DeviceView v)
    {
        if (_fileList == null || v.Serial == null) return;
        if (_filePath != null) _filePath.Text = _cwd;
        var output = await _sdk.ShellCmd(v.Serial, $"ls -lA {Q(_cwd.TrimEnd('/') + "/")}", 20_000);
        _fileList.BeginUpdate();
        _fileList.Items.Clear();
        var rows = new List<(string name, bool dir, long size, string when)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // drwxrws--- 2 u0_a207 media_rw 4096 2026-10-01 13:58 Alarms
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8 || line.StartsWith("total")) continue;
            bool dir = parts[0].StartsWith('d');
            long.TryParse(parts[4], out var size);
            var name = string.Join(' ', parts.Skip(7));
            if (parts[0].StartsWith('l') && name.Contains(" -> ")) name = name[..name.IndexOf(" -> ")];
            rows.Add((name, dir, size, $"{parts[5]} {parts[6]}"));
        }
        foreach (var r in rows.OrderByDescending(r => r.dir).ThenBy(r => r.name, StringComparer.OrdinalIgnoreCase))
            _fileList.Items.Add(new ListViewItem(new[] { (r.dir ? "📁  " : "📄  ") + r.name, r.dir ? "" : HumanSize(r.size), r.when }) { Tag = (r.name, r.dir) });
        _fileList.EndUpdate();
        if (output.Contains("Permission denied") || output.Contains("No such file")) SetStatus(output.Split('\n')[0]);
    }

    static string HumanSize(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1048576.0:0.#} MB",
        _ => $"{b / 1073741824.0:0.##} GB",
    };

    bool NeedRunning(DeviceView v, string what)
    {
        if (v.Running && v.Booted) return true;
        var off = NewCard(what, 0, 110, wide: true);
        var l = Ui.Text(v.Running ? "Waiting for Android to finish booting…" : $"Start the device to use {what}.", Theme.Body, sub: true);
        l.Location = new Point(18, 52);
        off.Controls.Add(l);
        return false;
    }

    // ── Home screen ─────────────────────────────────────────────────────────

    static readonly (string Name, string Package)[] Launchers =
    {
        ("Lawnchair", "app.lawnchair.play"),
        ("Nova Launcher", "com.teslacoilsw.launcher"),
        ("Niagara Launcher", "bitpit.launcher"),
        ("Smart Launcher 6", "ginlemon.flowerfree"),
    };

    void BuildHomeScreenCard(DeviceView v)
    {
        var serial = v.Serial!;
        var card = NewCard("Home screen & style", 0, 250, wide: true);
        var f = Flow(card);
        Action(f, "🖼  Set wallpaper from PC…", async () =>
        {
            using var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.webp", Title = "Choose a wallpaper" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            var name = $"celestium_wallpaper_{DateTime.Now:yyyyMMddHHmmss}{ext}";
            await _sdk.ShellCmd(serial, "mkdir -p /sdcard/Pictures/Wallpapers");
            var (code, output) = await _sdk.Push(serial, dlg.FileName, "/sdcard/Pictures/Wallpapers/" + name);
            if (code != 0) throw new InvalidOperationException(output.Trim());
            await _sdk.ShellCmd(serial, $"am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d file:///sdcard/Pictures/Wallpapers/{name}");
            string id = "";
            for (int i = 0; i < 10 && id.Length == 0; i++)
            {
                await Task.Delay(400);
                // \" survives Windows argument parsing as a literal quote, so the device shell keeps the SQL quotes.
                var q = await _sdk.ShellCmd(serial, $"content query --uri content://media/external/images/media --projection _id --where \\\"_display_name='{name}'\\\"");
                var m = System.Text.RegularExpressions.Regex.Match(q, @"_id=(\d+)");
                if (m.Success) id = m.Groups[1].Value;
            }
            if (id.Length == 0) throw new InvalidOperationException("The phone didn't index the picture yet; it's in Pictures › Wallpapers.");
            var mime = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : "image/jpeg";
            // Android's own "Set as wallpaper" screen (lets you crop and pick home/lock).
            await _sdk.ShellCmd(serial, $"am start -a android.intent.action.ATTACH_DATA -d content://media/external/images/media/{id} -t {mime} --grant-read-uri-permission");
            SetStatus("Choose Photos › Wallpaper on the phone, then crop and apply.");
            ShowEmulatorWindow(v.Avd);
        }, PillStyle.Primary);
        Action(f, "Wallpaper & style settings", () => _sdk.ShellCmd(serial, "am start -a android.intent.action.SET_WALLPAPER"));

        var font = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 150, Font = Theme.Body };
        var fonts = new[] { ("Small", "0.85"), ("Default", "1.0"), ("Large", "1.15"), ("Largest", "1.3") };
        font.Items.AddRange(fonts.Select(x => (object)x.Item1).ToArray());
        font.SelectedIndex = 1;
        font.SelectedIndexChanged += async (_, _) => await _sdk.ShellCmd(serial, $"settings put system font_scale {fonts[font.SelectedIndex].Item2}");
        var size = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 150, Font = Theme.Body };
        var sizes = new[] { ("Smaller", 0.85), ("Default", 1.0), ("Larger", 1.15), ("Largest", 1.3) };
        size.Items.AddRange(sizes.Select(x => (object)x.Item1).ToArray());
        size.SelectedIndex = 1;
        size.SelectedIndexChanged += async (_, _) =>
        {
            if (sizes[size.SelectedIndex].Item2 == 1.0) { await _sdk.ShellCmd(serial, "wm density reset"); return; }
            var physical = await _sdk.ShellCmd(serial, "wm density");
            var m = System.Text.RegularExpressions.Regex.Match(physical, @"Physical density: (\d+)");
            if (m.Success) await _sdk.ShellCmd(serial, $"wm density {(int)(int.Parse(m.Groups[1].Value) * sizes[size.SelectedIndex].Item2)}");
        };
        var row = new FlowLayoutPanel { Width = Math.Max(300, card.Width - 40), Height = 40, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 4) };
        row.Controls.Add(new Label { Text = "Font size", AutoSize = true, Margin = new Padding(0, 8, 8, 0), ForeColor = Ui.T.Text });
        row.Controls.Add(font);
        row.Controls.Add(new Label { Text = "Display size", AutoSize = true, Margin = new Padding(24, 8, 8, 0), ForeColor = Ui.T.Text });
        row.Controls.Add(size);
        f.Controls.Add(row);
        f.SetFlowBreak(row, true);

        var hasStore = AvdFactory.HasPlayStore(v.Avd);
        f.Controls.Add(new Label
        {
            Text = hasStore ? "Get a home-screen launcher:" : "Launchers need the Play Store (use a device made from \"Games\") — or install a launcher APK.",
            AutoSize = true, Margin = new Padding(0, 8, 12, 0), ForeColor = Ui.T.SubText, Tag = "sub",
        });
        if (hasStore)
            foreach (var (name, pkg) in Launchers)
                Action(f, name, () => _sdk.ShellCmd(serial, $"am start -a android.intent.action.VIEW -d market://details?id={pkg}"));
    }
}
