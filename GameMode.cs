using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DroidLauncher;

// ── Key maps ─────────────────────────────────────────────────────────────────

enum BindingKind { Button, Joystick, Camera }

/// <summary>One on-screen control. Positions are normalized (0..1) to the emulator screen as shown in the window.</summary>
sealed class Binding
{
    public BindingKind Kind { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Joystick/camera radius as a fraction of the screen width.</summary>
    public double Radius { get; set; } = 0.08;
    /// <summary>Button: the key. Joystick: up/left/down/right keys.</summary>
    public Keys Key { get; set; }
    public Keys Up { get; set; } = Keys.W;
    public Keys Left { get; set; } = Keys.A;
    public Keys Down { get; set; } = Keys.S;
    public Keys Right { get; set; } = Keys.D;
    /// <summary>Gamepad button for a Button binding (A, B, X, Y, LB, RB, LT, RT, Up, Down, Left, Right, Start, Back, LS, RS).</summary>
    public string? Pad { get; set; }
    /// <summary>Joystick/Camera: which analog stick drives it ("Left", "Right" or null).</summary>
    public string? Stick { get; set; }
    /// <summary>Button: tap repeatedly while held.</summary>
    public bool Turbo { get; set; }

    public string Label => Kind switch
    {
        BindingKind.Button => (Key != Keys.None ? KeyName(Key) : "") + (Pad != null ? (Key != Keys.None ? " · " : "") + Pad : ""),
        BindingKind.Joystick => Stick == "Left" ? "L-stick" : "Move",
        _ => Stick == "Right" ? "R-stick" : "Camera",
    };

    public static string KeyName(Keys k) => k switch
    {
        Keys.Space => "Space", Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => "Shift",
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => "Ctrl", Keys.Menu => "Alt",
        >= Keys.D0 and <= Keys.D9 => ((int)k - (int)Keys.D0).ToString(),
        Keys.Oemcomma => ",", Keys.OemPeriod => ".", Keys.OemQuestion => "/", Keys.OemSemicolon => ";", Keys.OemQuotes => "'",
        Keys.OemOpenBrackets => "[", Keys.OemCloseBrackets => "]", Keys.OemMinus => "-", Keys.Oemplus => "=", Keys.Oemtilde => "`",
        _ => k.ToString(),
    };
}

/// <summary>One recorded touch event of a macro (window-normalized, so it replays at any window size).</summary>
sealed class MacroStep
{
    public long Ms { get; set; }
    public string Type { get; set; } = "D"; // D(own), M(ove), U(p)
    public int Slot { get; set; }
    public double U { get; set; }
    public double V { get; set; }
}

static class MacroStore
{
    static string FileFor(string package) => Path.Combine(Paths.DataDir, "keymaps", string.Concat(package.Split(Path.GetInvalidFileNameChars())) + ".macro.json");
    public static List<MacroStep> Load(string package) => Paths.ReadJson<List<MacroStep>>(FileFor(package));
    public static void Save(string package, List<MacroStep> steps) { try { Paths.WriteJson(FileFor(package), steps); } catch { } }
}

sealed class KeymapProfile
{
    public string Package { get; set; } = "default";
    public bool Landscape { get; set; }
    public List<Binding> Bindings { get; set; } = new();

    static string Dir => Path.Combine(Paths.DataDir, "keymaps");
    static string FileFor(string package) => Path.Combine(Dir, string.Concat(package.Split(Path.GetInvalidFileNameChars())) + ".json");

    public static KeymapProfile Load(string package)
    {
        var p = Paths.ReadJson<KeymapProfile>(FileFor(package));
        p.Package = package;
        return p;
    }

    public void Save() { try { Paths.WriteJson(FileFor(Package), this); } catch { } }
}

// ── Touch injection ─────────────────────────────────────────────────────────

/// <summary>
/// Multi-touch through the emulator console (protocol B, 10 slots, panel coordinates 0..32767 in the phone's
/// natural orientation). With sync on, every event also goes to the follower devices — the same normalized
/// coordinates work on any screen size.
/// </summary>
sealed class TouchInjector
{
    readonly string _serial;
    public List<string> Followers { get; } = new();
    int _rotation; // 0..3, how the virtual phone is turned (from its accelerometer)
    int _trackingId = 100;
    readonly int[] _ids = new int[10];

    public TouchInjector(string serial) => _serial = serial;

    /// <summary>When set, every touch is reported here (macro recording).</summary>
    public Action<string, int, double, double>? Recorder;

    /// <summary>Re-read how the phone is turned. Windows show the panel rotated by this amount.</summary>
    public async Task RefreshRotation()
    {
        try
        {
            var a = await EmulatorConsole.For(_serial).Send("sensor get acceleration");
            var m = System.Text.RegularExpressions.Regex.Match(a, @"=\s*([-\d.e+]+):([-\d.e+]+)");
            if (!m.Success) return;
            double x = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            double y = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            _rotation = x < -5 ? 1 : y < -5 ? 2 : x > 5 ? 3 : 0;
        }
        catch { }
    }

    /// <summary>Window-normalized point → panel coordinates (measured: rotation 1 maps (u,v) to (v, 1-u)).</summary>
    (int x, int y) ToPanel(double u, double v)
    {
        (double pu, double pv) = _rotation switch
        {
            1 => (v, 1 - u),
            2 => (1 - u, 1 - v),
            3 => (1 - v, u),
            _ => (u, v),
        };
        return ((int)Math.Round(Math.Clamp(pu, 0, 1) * 32767), (int)Math.Round(Math.Clamp(pv, 0, 1) * 32767));
    }

    void Send(string events)
    {
        _ = EmulatorConsole.For(_serial).TrySend("event send " + events);
        foreach (var f in Followers) _ = EmulatorConsole.For(f).TrySend("event send " + events);
    }

    public void Down(int slot, double u, double v)
    {
        Recorder?.Invoke("D", slot, u, v);
        var (x, y) = ToPanel(u, v);
        _ids[slot] = ++_trackingId;
        Send($"EV_ABS:ABS_MT_SLOT:{slot} EV_ABS:ABS_MT_TRACKING_ID:{_ids[slot]} EV_ABS:ABS_MT_POSITION_X:{x} EV_ABS:ABS_MT_POSITION_Y:{y} EV_ABS:ABS_MT_PRESSURE:200 EV_SYN:0:0");
    }

    public void Move(int slot, double u, double v)
    {
        Recorder?.Invoke("M", slot, u, v);
        var (x, y) = ToPanel(u, v);
        Send($"EV_ABS:ABS_MT_SLOT:{slot} EV_ABS:ABS_MT_POSITION_X:{x} EV_ABS:ABS_MT_POSITION_Y:{y} EV_SYN:0:0");
    }

    public void Up(int slot)
    {
        Recorder?.Invoke("U", slot, 0, 0);
        Send($"EV_ABS:ABS_MT_SLOT:{slot} EV_ABS:ABS_MT_TRACKING_ID:-1 EV_SYN:0:0");
    }

    public void Text(string text)
    {
        if (text.Length == 0) return;
        _ = EmulatorConsole.For(_serial).TrySend("event text " + text);
        foreach (var f in Followers) _ = EmulatorConsole.For(f).TrySend("event text " + text);
    }

    public void Key(string linuxKey)
    {
        Send($"EV_KEY:{linuxKey}:1 EV_SYN:0:0");
        Send($"EV_KEY:{linuxKey}:0 EV_SYN:0:0");
    }
}

// ── Gamepad ─────────────────────────────────────────────────────────────────

static class Gamepad
{
    [StructLayout(LayoutKind.Sequential)]
    struct XInputGamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState { public uint Packet; public XInputGamepad Pad; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    static extern int XInputGetState(int index, out XInputState state);

    public sealed record State(HashSet<string> Buttons, double LX, double LY, double RX, double RY);

    static readonly (ushort mask, string name)[] Map =
    {
        (0x0001, "Up"), (0x0002, "Down"), (0x0004, "Left"), (0x0008, "Right"), (0x0010, "Start"), (0x0020, "Back"),
        (0x0040, "LS"), (0x0080, "RS"), (0x0100, "LB"), (0x0200, "RB"), (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y"),
    };

    public static readonly string[] ButtonNames = { "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "Up", "Down", "Left", "Right", "Start", "Back", "LS", "RS" };

    /// <summary>First connected controller, or null.</summary>
    public static State? Read()
    {
        for (int i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetState(i, out var s) != 0) continue;
                var set = new HashSet<string>(Map.Where(m => (s.Pad.Buttons & m.mask) != 0).Select(m => m.name));
                if (s.Pad.LeftTrigger > 60) set.Add("LT");
                if (s.Pad.RightTrigger > 60) set.Add("RT");
                return new State(set, Axis(s.Pad.LX), -Axis(s.Pad.LY), Axis(s.Pad.RX), -Axis(s.Pad.RY));
            }
            catch (DllNotFoundException) { return null; }
        }
        return null;
    }

    static double Axis(short v)
    {
        const double dead = 7849; // XInput's recommended left-stick dead zone
        double a = v;
        if (Math.Abs(a) < dead) return 0;
        return Math.Clamp((a - Math.Sign(a) * dead) / (32767 - dead), -1, 1);
    }
}

// ── Game mode session ───────────────────────────────────────────────────────

/// <summary>
/// Game mode for one device: an input surface laid over the emulator screen that turns keys, mouse and a
/// gamepad into multi-touch, a per-pixel-alpha layer drawing the key hints, and a small floating toolbar.
/// All three are owned by the emulator window, so they follow its z-order and minimise with it.
/// </summary>
sealed class GameSession : IDisposable
{
    public string Avd { get; }
    readonly string _serial;
    readonly IntPtr _emu;
    readonly Sdk _sdk;
    readonly Func<IEnumerable<string>> _otherRunningSerials;
    readonly Action<string> _status;
    public readonly TouchInjector Touch;
    readonly InputLayer _input;
    readonly HintLayer _hints;
    readonly GameToolbar _bar;
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 16 };
    readonly WinEventProc _onMove;
    IntPtr _hook;
    long _lastPackageCheck;

    public KeymapProfile Profile { get; private set; }
    public bool Editing { get; private set; }
    public bool ShowHints { get; private set; } = true;
    public bool Sync { get; private set; }
    public event Action? Closed;

    // live state
    readonly HashSet<Keys> _keysDown = new();
    readonly Dictionary<Binding, int> _slots = new();
    readonly Dictionary<Binding, (double u, double v)> _stickPos = new();
    Gamepad.State? _pad;
    readonly HashSet<string> _padDown = new();
    bool _mouseDown;
    long _turboTick;

    public GameSession(string avd, string serial, IntPtr emulatorWindow, Sdk sdk, Func<IEnumerable<string>> otherRunningSerials, Action<string> status)
    {
        Avd = avd; _serial = serial; _emu = emulatorWindow; _sdk = sdk; _otherRunningSerials = otherRunningSerials; _status = status;
        Touch = new TouchInjector(serial);
        Profile = KeymapProfile.Load("default");
        _input = new InputLayer(this);
        _hints = new HintLayer(this);
        _bar = new GameToolbar(this);
        _input.Show(); _hints.Show(); _bar.Show();
        Native.SetOwner(_input.Handle, _emu);
        Native.SetOwner(_hints.Handle, _input.Handle);
        Native.SetOwner(_bar.Handle, _emu);
        // Overlays are created on top of everything; slot the emulator window directly beneath them so the
        // whole group sits together (other apps can't end up between the phone and its key hints).
        Native.SetWindowPos(_emu, _input.Handle, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        _onMove = (_, _, h, idObject, _, _, _) => { if (h == _emu && idObject == 0) Place(); };
        _hook = Native.SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, _onMove, 0, 0, 0);
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        Place();
        _ = Init();
    }

    async Task Init()
    {
        await Touch.RefreshRotation();
        await CheckForegroundApp(force: true);
        _input.Activate();
    }

    /// <summary>Switch to the key map of whatever app is in front.</summary>
    async Task CheckForegroundApp(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastPackageCheck < 2500) return;
        _lastPackageCheck = now;
        try
        {
            var top = await _sdk.ShellCmd(_serial, "dumpsys activity activities | grep -m1 topResumedActivity");
            var m = System.Text.RegularExpressions.Regex.Match(top, @"u0 ([\w.]+)/");
            var pkg = m.Success ? m.Groups[1].Value : "default";
            // Don't swap key maps mid-edit, mid-recording or mid-playback (a recorded tap often opens another app).
            if (pkg != Profile.Package && !Editing && !Recording && !Playing)
            {
                ReleaseAll();
                var p = KeymapProfile.Load(pkg);
                Profile = p.Bindings.Count > 0 || pkg == "default" ? p : WithPackage(KeymapProfile.Load("default"), pkg);
                _bar.Refresh();
                _hints.Redraw();
            }
        }
        catch { }
        await Touch.RefreshRotation();
    }

    static KeymapProfile WithPackage(KeymapProfile p, string pkg)
    {
        // An app without its own map starts from the default map; saving then creates the app's own.
        return new KeymapProfile { Package = pkg, Landscape = p.Landscape, Bindings = p.Bindings.Select(Clone).ToList() };
    }

    static Binding Clone(Binding b) => JsonSerializer.Deserialize<Binding>(JsonSerializer.Serialize(b))!;

    public Rectangle ScreenRect
    {
        get
        {
            Native.GetClientRect(_emu, out var c);
            var o = new Native.POINT();
            Native.ClientToScreen(_emu, ref o);
            return new Rectangle(o.X, o.Y, c.Right, c.Bottom);
        }
    }

    void Place()
    {
        if (Native.IsIconic(_emu)) return;
        var r = ScreenRect;
        Native.SetWindowPos(_input.Handle, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, 0x14);
        Native.SetWindowPos(_hints.Handle, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, 0x14);
        Native.GetWindowRect(_emu, out var w);
        // Centre the toolbar above the phone, but keep it fully on the monitor.
        var area = Screen.FromHandle(_emu).WorkingArea;
        int bx = Math.Clamp(w.Left + ((w.Right - w.Left) - _bar.Width) / 2, area.Left, Math.Max(area.Left, area.Right - _bar.Width));
        int by = w.Top - _bar.Height - 6 >= area.Top ? w.Top - _bar.Height - 6 : w.Bottom + 6;
        Native.SetWindowPos(_bar.Handle, IntPtr.Zero, bx, by, _bar.Width, _bar.Height, 0x14);
        _hints.Redraw();
    }

    // ── Modes ───────────────────────────────────────────────────────────────

    public void SetEditing(bool on)
    {
        ReleaseAll();
        Editing = on;
        if (!on)
        {
            Native.GetClientRect(_emu, out var c);
            Profile.Landscape = c.Right > c.Bottom;
            Profile.Save();
            _status($"Saved key map for {Profile.Package} ({Profile.Bindings.Count} controls).");
        }
        else _status("Edit keys: right-click to add a control, drag to move, right-click a control to change or delete it, scroll to resize.");
        _bar.Refresh();
        _hints.Redraw();
        _input.Activate();
    }

    public void ToggleHints() { ShowHints = !ShowHints; _bar.Refresh(); _hints.Redraw(); }

    public void ToggleSync()
    {
        Sync = !Sync;
        Touch.Followers.Clear();
        if (Sync) Touch.Followers.AddRange(_otherRunningSerials());
        _status(Sync ? (Touch.Followers.Count == 0 ? "Sync on — no other devices are running yet." : $"Sync on — mirroring to {Touch.Followers.Count} other device(s).") : "Sync off.");
        _bar.Refresh();
    }

    // ── Input → touch ───────────────────────────────────────────────────────

    int FreeSlot()
    {
        for (int s = 0; s < 9; s++) if (!_slots.ContainsValue(s)) return s;
        return 8;
    }

    void Press(Binding b, double u, double v)
    {
        if (_slots.ContainsKey(b)) return;
        var slot = FreeSlot();
        _slots[b] = slot;
        Touch.Down(slot, u, v);
    }

    void Release(Binding b)
    {
        if (!_slots.Remove(b, out var slot)) return;
        Touch.Up(slot);
        _stickPos.Remove(b);
    }

    public void ReleaseAll()
    {
        foreach (var b in _slots.Keys.ToList()) Release(b);
        if (_mouseDown) { Touch.Up(9); _mouseDown = false; }
        _keysDown.Clear();
        _padDown.Clear();
    }

    public void KeyDown(Keys key)
    {
        if (Editing) return;
        if (!_keysDown.Add(key)) return; // auto-repeat
        bool used = false;
        foreach (var b in Profile.Bindings)
        {
            if (b.Kind == BindingKind.Button && b.Key == key) { Press(b, b.X, b.Y); used = true; }
            if (b.Kind == BindingKind.Joystick && (key == b.Up || key == b.Down || key == b.Left || key == b.Right)) used = true;
        }
        if (used) { UpdateKeyJoysticks(); return; }
        ForwardUnmapped(key);
    }

    public void KeyUp(Keys key)
    {
        if (Editing) return;
        _keysDown.Remove(key);
        foreach (var b in Profile.Bindings.Where(b => b.Kind == BindingKind.Button && b.Key == key)) Release(b);
        UpdateKeyJoysticks();
    }

    /// <summary>Keys that aren't mapped still work as a keyboard (typing in chat boxes, menus, sync typing).</summary>
    void ForwardUnmapped(Keys key)
    {
        // Android keycodes (console key events don't reach Android, so these go through a persistent adb shell).
        int? android = key switch
        {
            Keys.Enter => 66, Keys.Back => 67, Keys.Escape => 4, Keys.Tab => 61, Keys.Delete => 112,
            Keys.Up => 19, Keys.Down => 20, Keys.Left => 21, Keys.Right => 22, Keys.Home => 122, Keys.End => 123,
            _ => null,
        };
        if (android is { } code)
        {
            KeyInjector.For(_sdk.Adb, _serial).Press(code);
            foreach (var f in Touch.Followers) KeyInjector.For(_sdk.Adb, f).Press(code);
            return;
        }
        var ch = ToChar(key);
        if (ch != null) Touch.Text(ch);
    }

    static string? ToChar(Keys key)
    {
        bool shift = (Control.ModifierKeys & Keys.Shift) != 0;
        if (key is >= Keys.A and <= Keys.Z) { var c = (char)('a' + (key - Keys.A)); return (shift ? char.ToUpperInvariant(c) : c).ToString(); }
        if (key is >= Keys.D0 and <= Keys.D9 && !shift) return ((char)('0' + (key - Keys.D0))).ToString();
        return key switch { Keys.Space => " ", Keys.OemPeriod => ".", Keys.Oemcomma => ",", Keys.OemMinus => "-", Keys.OemQuestion => shift ? "?" : "/", _ => null };
    }

    void UpdateKeyJoysticks()
    {
        foreach (var b in Profile.Bindings.Where(b => b.Kind == BindingKind.Joystick))
        {
            double dx = (_keysDown.Contains(b.Right) ? 1 : 0) - (_keysDown.Contains(b.Left) ? 1 : 0);
            double dy = (_keysDown.Contains(b.Down) ? 1 : 0) - (_keysDown.Contains(b.Up) ? 1 : 0);
            if (_pad != null && b.Stick == "Left" && (_pad.LX != 0 || _pad.LY != 0)) continue; // stick in use
            Steer(b, dx, dy, normalize: true);
        }
    }

    void Steer(Binding b, double dx, double dy, bool normalize)
    {
        if (dx == 0 && dy == 0) { Release(b); return; }
        if (normalize) { var len = Math.Sqrt(dx * dx + dy * dy); dx /= len; dy /= len; }
        var r = ScreenRect;
        double aspect = r.Height == 0 ? 1 : (double)r.Width / r.Height;
        var target = (b.X + dx * b.Radius, b.Y + dy * b.Radius * aspect);
        if (!_slots.ContainsKey(b)) Press(b, b.X, b.Y);
        if (_stickPos.TryGetValue(b, out var last) && Math.Abs(last.u - target.Item1) < 0.002 && Math.Abs(last.v - target.Item2) < 0.002) return;
        _stickPos[b] = target;
        Touch.Move(_slots[b], target.Item1, target.Item2);
    }

    WheelGesture? _wheel, _hwheel;
    PinchGesture? _pinch;
    public void Wheel(double u, double v, int delta) { if (!Editing) (_wheel ??= new WheelGesture(Touch)).Wheel(u, v, delta); }
    public void HWheel(double u, double v, int delta) { if (!Editing) (_hwheel ??= new WheelGesture(Touch, horizontal: true)).Wheel(u, v, delta); }
    public void Pinch(double u, double v, int delta) { if (!Editing) (_pinch ??= new PinchGesture(Touch)).Wheel(u, v, delta); }

    public void MouseDown(double u, double v) { if (Editing) return; _mouseDown = true; Touch.Down(9, u, v); }
    public void MouseMove(double u, double v) { if (!Editing && _mouseDown) Touch.Move(9, u, v); }
    public void MouseUp() { if (Editing || !_mouseDown) return; _mouseDown = false; Touch.Up(9); }

    void Tick()
    {
        if (Native.IsIconic(_emu) || !Native.IsWindow(_emu)) { if (!Native.IsWindow(_emu)) Dispose(); return; }
        _ = CheckForegroundApp();
        if (Editing) return;

        // Only listen to the controller while the game (or this overlay) is in front.
        var fg = Native.GetForegroundWindow();
        bool focused = fg == _input.Handle || fg == _emu || fg == _bar.Handle;
        _pad = focused ? Gamepad.Read() : null;
        var pressed = _pad?.Buttons ?? new HashSet<string>();
        foreach (var b in Profile.Bindings)
        {
            switch (b.Kind)
            {
                case BindingKind.Button when b.Pad != null:
                    bool now = pressed.Contains(b.Pad), was = _padDown.Contains(b.Pad + b.GetHashCode());
                    if (now && !was) { Press(b, b.X, b.Y); _padDown.Add(b.Pad + b.GetHashCode()); }
                    if (!now && was) { if (!_keysDown.Contains(b.Key)) Release(b); _padDown.Remove(b.Pad + b.GetHashCode()); }
                    break;
                case BindingKind.Joystick when b.Stick == "Left" && _pad != null:
                    if (_pad.LX != 0 || _pad.LY != 0) Steer(b, _pad.LX, _pad.LY, normalize: false);
                    else if (!_keysDown.Overlaps(new[] { b.Up, b.Down, b.Left, b.Right })) Release(b);
                    break;
                case BindingKind.Camera when _pad != null:
                    var (sx, sy) = b.Stick == "Left" ? (_pad.LX, _pad.LY) : (_pad.RX, _pad.RY);
                    Steer(b, sx, sy, normalize: false);
                    break;
            }
        }
        // Turbo buttons: re-tap every ~90 ms while held.
        if (Environment.TickCount64 - _turboTick > 90)
        {
            _turboTick = Environment.TickCount64;
            foreach (var b in _slots.Keys.Where(b => b.Turbo).ToList())
            {
                var slot = _slots[b];
                Touch.Up(slot);
                Touch.Down(slot, b.X, b.Y);
            }
        }
    }

    // ── Macros ──────────────────────────────────────────────────────────────

    List<MacroStep>? _macro;
    string _macroPackage = "default";
    long _macroStart;
    CancellationTokenSource? _playing;
    public bool Recording => _macro != null;
    public bool Playing => _playing != null;
    public bool Looping { get; set; }

    public void ToggleRecord()
    {
        if (Playing) return;
        if (_macro == null)
        {
            ReleaseAll();
            _macro = new List<MacroStep>();
            _macroPackage = Profile.Package;
            _macroStart = Environment.TickCount64;
            Touch.Recorder = (type, slot, u, v) => _macro?.Add(new MacroStep { Ms = Environment.TickCount64 - _macroStart, Type = type, Slot = slot, U = u, V = v });
            _status("Recording a macro... play normally, then press the stop button.");
        }
        else
        {
            Touch.Recorder = null;
            ReleaseAll();
            var steps = _macro;
            _macro = null;
            if (steps.Count > 0)
            {
                MacroStore.Save(_macroPackage, steps);
                _status($"Saved macro for {_macroPackage}: {steps.Count} touch events, {steps[^1].Ms / 1000.0:0.0} s.");
            }
            else _status("Nothing was recorded.");
        }
        _bar.Refresh();
        _input.Activate();
    }

    public async void TogglePlay()
    {
        if (_playing != null) { _playing.Cancel(); return; }
        if (Recording) return;
        var steps = MacroStore.Load(Profile.Package);
        if (steps.Count == 0) { _status($"No macro recorded for {Profile.Package} yet. Press record to make one."); return; }
        _playing = new CancellationTokenSource();
        var token = _playing.Token;
        _bar.Refresh();
        var used = new HashSet<int>();
        try
        {
            do
            {
                var t0 = Environment.TickCount64;
                foreach (var step in steps)
                {
                    var wait = step.Ms - (Environment.TickCount64 - t0);
                    if (wait > 0) await Task.Delay((int)wait, token);
                    int slot = Math.Clamp(step.Slot, 0, 9);
                    switch (step.Type)
                    {
                        case "D": Touch.Down(slot, step.U, step.V); used.Add(slot); break;
                        case "M": Touch.Move(slot, step.U, step.V); break;
                        default: Touch.Up(slot); used.Remove(slot); break;
                    }
                }
                if (Looping) await Task.Delay(300, token);
            } while (Looping && !token.IsCancellationRequested);
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var slot in used) Touch.Up(slot);
            _playing = null;
            if (!_disposed) _bar.Refresh();
        }
    }

    /// <summary>Test hook: record pressing H, go home, replay the macro, and log what's in front each step.</summary>
    public async Task SelfTest(Sdk sdk)
    {
        var log = Path.Combine(Path.GetTempPath(), "celestium_macro_selftest.log");
        async Task Note(string what) => File.AppendAllText(log, $"{what}: {await sdk.ShellCmd(_serial, "dumpsys activity activities | grep -m1 topResumedActivity")}\n");
        File.WriteAllText(log, "");
        await Task.Delay(1500);
        ToggleRecord(); await Task.Delay(500);
        KeyDown(Keys.H); await Task.Delay(120); KeyUp(Keys.H); await Task.Delay(800);
        ToggleRecord();
        await Task.Delay(1500); await Note("after recording");
        await sdk.KeyEvent(_serial, 3); await Task.Delay(1500); await Note("after home");
        TogglePlay(); await Task.Delay(3000); await Note("after replay");
        File.AppendAllText(log, $"macro steps: {MacroStore.Load(Profile.Package).Count}\n");
        await sdk.KeyEvent(_serial, 3);
    }

    // ── Editing ─────────────────────────────────────────────────────────────

    public Binding? HitTest(double u, double v)
    {
        var r = ScreenRect;
        foreach (var b in Enumerable.Reverse(Profile.Bindings))
        {
            double px = (u - b.X) * r.Width, py = (v - b.Y) * r.Height;
            double rad = b.Kind == BindingKind.Button ? 26 : b.Radius * r.Width;
            if (px * px + py * py <= rad * rad) return b;
        }
        return null;
    }

    public void AddBinding(BindingKind kind, double u, double v)
    {
        var b = new Binding { Kind = kind, X = u, Y = v };
        if (kind == BindingKind.Joystick) { b.Stick = "Left"; b.Radius = 0.09; }
        if (kind == BindingKind.Camera) { b.Stick = "Right"; b.Radius = 0.12; }
        if (kind == BindingKind.Button)
        {
            var key = CaptureInput.Ask(_input, "New button", "Press the key and/or gamepad button for this spot.");
            if (key == null) return;
            b.Key = key.Value.key; b.Pad = key.Value.pad;
        }
        Profile.Bindings.Add(b);
        _hints.Redraw();
    }

    public void ChangeBinding(Binding b)
    {
        if (b.Kind == BindingKind.Button)
        {
            var key = CaptureInput.Ask(_input, "Change button", "Press the new key and/or gamepad button.");
            if (key == null) return;
            b.Key = key.Value.key; b.Pad = key.Value.pad;
        }
        else if (b.Kind == BindingKind.Joystick)
        {
            Keys? Ask(string dir) => CaptureInput.Ask(_input, "Joystick keys", $"Press the key for {dir}.")?.key;
            if (Ask("UP") is { } up && Ask("LEFT") is { } left && Ask("DOWN") is { } down && Ask("RIGHT") is { } right)
            { b.Up = up; b.Left = left; b.Down = down; b.Right = right; }
        }
        _hints.Redraw();
    }

    public void Remove(Binding b) { Profile.Bindings.Remove(b); _hints.Redraw(); }
    public void Moved() => _hints.Redraw();

    public void ClearProfile()
    {
        Profile.Bindings.Clear();
        _hints.Redraw();
    }

    public void SaveAsDefault()
    {
        var d = new KeymapProfile { Package = "default", Landscape = Profile.Landscape, Bindings = Profile.Bindings.Select(Clone).ToList() };
        d.Save();
        _status("Saved as the default key map for apps without their own.");
    }

    bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tick.Stop();
        _playing?.Cancel();
        Touch.Recorder = null;
        try { ReleaseAll(); } catch { }
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        if (Editing) Profile.Save();
        _bar.Dispose(); _hints.Dispose(); _input.Dispose(); _tick.Dispose();
        Closed?.Invoke();
    }

    // ── Windows ─────────────────────────────────────────────────────────────

    /// <summary>Nearly invisible window over the emulator screen that receives keyboard, mouse and focus.</summary>
    sealed class InputLayer : Form
    {
        readonly GameSession _s;
        Binding? _drag;
        Point _dragFrom;

        public InputLayer(GameSession s)
        {
            _s = s;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Opacity = 0.01; // layered, but still hit-testable
            KeyPreview = true;
            Text = "Celestium game mode";
            Bounds = new Rectangle(-32000, -32000, 1, 1);
            Cursor = Cursors.Cross;
        }

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } } // tool window

        (double u, double v) Norm(Point p) => (ClientSize.Width == 0 ? 0 : (double)p.X / ClientSize.Width, ClientSize.Height == 0 ? 0 : (double)p.Y / ClientSize.Height);

        // Every key is the game's (Tab, arrows, Alt, Space…): take them before WinForms' dialog-key handling does.
        public override bool PreProcessMessage(ref Message m)
        {
            const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
            if (m.Msg is WM_KEYDOWN or WM_SYSKEYDOWN) { _s.KeyDown((Keys)(int)m.WParam); return true; }
            if (m.Msg is WM_KEYUP or WM_SYSKEYUP) { _s.KeyUp((Keys)(int)m.WParam); return true; }
            return base.PreProcessMessage(ref m);
        }

        protected override void OnDeactivate(EventArgs e) { _s.ReleaseAll(); base.OnDeactivate(e); } // no stuck keys

        // Wheel with modifiers, read from the message itself: Ctrl = pinch zoom, Shift (or tilt) = horizontal.
        protected override void WndProc(ref Message m)
        {
            if (!_s.Editing && m.Msg is 0x020A or 0x020E)
            {
                int keys = (int)((long)m.WParam & 0xFFFF), delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                var p = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
                var (u, v) = Norm(p);
                if (m.Msg == 0x020E) _s.HWheel(u, v, delta);
                else if ((keys & 0x0008) != 0) _s.Pinch(u, v, delta);       // MK_CONTROL
                else if ((keys & 0x0004) != 0) _s.HWheel(u, v, -delta);     // MK_SHIFT
                else _s.Wheel(u, v, delta);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var (u, v) = Norm(e.Location);
            if (!_s.Editing) { if (e.Button == MouseButtons.Left) _s.MouseDown(u, v); return; }
            var hit = _s.HitTest(u, v);
            if (e.Button == MouseButtons.Left && hit != null) { _drag = hit; _dragFrom = e.Location; Capture = true; }
            if (e.Button == MouseButtons.Right) ShowEditMenu(hit, u, v, e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var (u, v) = Norm(e.Location);
            if (_s.Editing && _drag != null) { _drag.X = Math.Clamp(u, 0, 1); _drag.Y = Math.Clamp(v, 0, 1); _s.Moved(); }
            else _s.MouseMove(u, v);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_drag != null) { _drag = null; Capture = false; return; }
            if (e.Button == MouseButtons.Left) _s.MouseUp();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var (u, v) = Norm(e.Location);
            if (!_s.Editing) { _s.Wheel(u, v, e.Delta); return; }
            if (_s.HitTest(u, v) is { Kind: not BindingKind.Button } b)
            {
                b.Radius = Math.Clamp(b.Radius * (e.Delta > 0 ? 1.1 : 0.9), 0.03, 0.3);
                _s.Moved();
            }
        }

        void ShowEditMenu(Binding? hit, double u, double v, Point at)
        {
            var menu = new ContextMenuStrip();
            if (hit == null)
            {
                menu.Items.Add("Add button here…", null, (_, _) => _s.AddBinding(BindingKind.Button, u, v));
                menu.Items.Add("Add movement joystick here (WASD / left stick)", null, (_, _) => _s.AddBinding(BindingKind.Joystick, u, v));
                menu.Items.Add("Add camera stick here (right stick)", null, (_, _) => _s.AddBinding(BindingKind.Camera, u, v));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Save as default for all apps", null, (_, _) => _s.SaveAsDefault());
                menu.Items.Add("Clear all controls", null, (_, _) =>
                {
                    if (MessageBox.Show(this, "Remove every control from this key map?", "Clear", MessageBoxButtons.YesNo) == DialogResult.Yes) _s.ClearProfile();
                });
            }
            else
            {
                menu.Items.Add(hit.Kind == BindingKind.Button ? "Change key / gamepad button…" : hit.Kind == BindingKind.Joystick ? "Change keys…" : "(drag to move, scroll to resize)", null, (_, _) => _s.ChangeBinding(hit));
                if (hit.Kind == BindingKind.Button)
                    menu.Items.Add(new ToolStripMenuItem("Turbo (repeat while held)", null, (_, _) => { hit.Turbo = !hit.Turbo; _s.Moved(); }) { Checked = hit.Turbo });
                if (hit.Kind != BindingKind.Button)
                {
                    var stick = new ToolStripMenuItem("Gamepad stick");
                    foreach (var name in new[] { "Left", "Right", "None" })
                        stick.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => { hit.Stick = name == "None" ? null : name; _s.Moved(); }) { Checked = (hit.Stick ?? "None") == name });
                    menu.Items.Add(stick);
                }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Delete", null, (_, _) => _s.Remove(hit));
            }
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(this, at);
        }
    }

    /// <summary>Click-through layer that draws the key hints with real per-pixel transparency.</summary>
    sealed class HintLayer : Form
    {
        readonly GameSession _s;

        public HintLayer(GameSession s)
        {
            _s = s;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-32000, -32000, 1, 1);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000; // layered, transparent (click-through), tool window, no-activate
                return cp;
            }
        }

        public void Redraw()
        {
            if (!IsHandleCreated) return;
            Native.GetWindowRect(Handle, out var wr);
            int w = Math.Max(1, wr.Right - wr.Left), h = Math.Max(1, wr.Bottom - wr.Top);
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                if (_s.Editing) using (var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillRectangle(dim, 0, 0, w, h);
                if (_s.ShowHints || _s.Editing) foreach (var b in _s.Profile.Bindings) Draw(g, b, w, h);
                if (_s.Editing && _s.Profile.Bindings.Count == 0)
                    using (var f = new Font("Segoe UI Semibold", 11f))
                        TextRenderer.DrawText(g, "Right-click anywhere to add a control", f, new Rectangle(0, h / 2 - 20, w, 40), Color.White, TextFormatFlags.HorizontalCenter);
            }
            Native.UpdateLayered(Handle, bmp, wr.Left, wr.Top);
        }

        void Draw(Graphics g, Binding b, int w, int h)
        {
            float x = (float)(b.X * w), y = (float)(b.Y * h);
            int alpha = _s.Editing ? 230 : 120;
            var accent = b.Turbo ? Color.FromArgb(alpha, 250, 160, 60) : Color.FromArgb(alpha, 76, 141, 255);
            using var font = new Font("Segoe UI Semibold", 9f);
            if (b.Kind == BindingKind.Button)
            {
                const float r = 20;
                using var fill = new SolidBrush(Color.FromArgb(alpha / 2 + 30, 15, 18, 26));
                using var ring = new Pen(accent, 2.5f);
                g.FillEllipse(fill, x - r, y - r, r * 2, r * 2);
                g.DrawEllipse(ring, x - r, y - r, r * 2, r * 2);
                var label = b.Label.Length == 0 ? "?" : b.Label;
                var size = g.MeasureString(label, font);
                using var text = new SolidBrush(Color.FromArgb(Math.Min(255, alpha + 40), 255, 255, 255));
                g.DrawString(label, font, text, x - size.Width / 2, y - size.Height / 2);
            }
            else
            {
                float r = (float)(b.Radius * w);
                using var fill = new SolidBrush(Color.FromArgb(alpha / 3, 15, 18, 26));
                using var ring = new Pen(accent, 2.5f) { DashStyle = b.Kind == BindingKind.Camera ? DashStyle.Dash : DashStyle.Solid };
                g.FillEllipse(fill, x - r, y - r, r * 2, r * 2);
                g.DrawEllipse(ring, x - r, y - r, r * 2, r * 2);
                using var text = new SolidBrush(Color.FromArgb(Math.Min(255, alpha + 40), 255, 255, 255));
                void At(string s, float dx, float dy) { var sz = g.MeasureString(s, font); g.DrawString(s, font, text, x + dx - sz.Width / 2, y + dy - sz.Height / 2); }
                if (b.Kind == BindingKind.Joystick)
                {
                    At(Binding.KeyName(b.Up), 0, -r + 12); At(Binding.KeyName(b.Down), 0, r - 12);
                    At(Binding.KeyName(b.Left), -r + 12, 0); At(Binding.KeyName(b.Right), r - 12, 0);
                }
                At(b.Label, 0, 0);
            }
        }
    }

    /// <summary>Floating toolbar above the emulator window.</summary>
    sealed class GameToolbar : Form
    {
        readonly GameSession _s;
        readonly PillButton _edit = new("✎  Edit") { Height = 30 };
        readonly PillButton _hints = new("Hints") { Height = 30 };
        readonly PillButton _sync = new("⇄  Sync") { Height = 30 };
        readonly PillButton _rec = new("⏺") { Height = 30, Width = 38 };
        readonly PillButton _play = new("▶") { Height = 30, Width = 38 };
        readonly PillButton _close = new("✕", PillStyle.Danger) { Height = 30, Width = 36 };
        readonly Label _profile = new() { AutoSize = true, ForeColor = Color.FromArgb(170, 176, 190), Font = Theme.Small, Margin = new Padding(8, 8, 8, 0), UseMnemonic = false };

        public GameToolbar(GameSession s)
        {
            _s = s;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Ui.T.Sidebar;
            Bounds = new Rectangle(-32000, -32000, 720, 42);
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(6, 5, 6, 5), BackColor = BackColor };
            var title = new Label { Text = "🎮", AutoSize = true, ForeColor = Ui.T.Text, Font = Theme.BodyBold, Margin = new Padding(4, 6, 8, 0) };
            _profile.ForeColor = Ui.T.SubText;
            foreach (var b in new[] { _edit, _hints, _sync, _rec, _play, _close }) b.Margin = new Padding(0, 0, 6, 0);
            row.Controls.AddRange(new Control[] { title, _profile, _edit, _hints, _sync, _rec, _play, _close });
            _tipsBar.SetToolTip(_rec, "Record a macro (your touches, keys and gamepad)");
            _tipsBar.SetToolTip(_play, "Play the macro. Right-click to repeat until stopped.");
            _rec.Click += (_, _) => _s.ToggleRecord();
            _play.Click += (_, _) => _s.TogglePlay();
            _play.MouseUp += (_, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                _s.Looping = !_s.Looping;
                Refresh();
            };
            Controls.Add(row);
            _edit.Click += (_, _) => _s.SetEditing(!_s.Editing);
            _hints.Click += (_, _) => _s.ToggleHints();
            _sync.Click += (_, _) => _s.ToggleSync();
            _close.Click += (_, _) => _s.Dispose();
            Refresh();
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2; Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
        }

        public new void Refresh()
        {
            var pkg = _s.Profile.Package == "default" ? "Default map" : Friendly(_s.Profile.Package);
            _profile.Text = $"{pkg} · {_s.Profile.Bindings.Count}";
            _tipsBar.SetToolTip(_profile, $"Key map for {_s.Profile.Package}: {_s.Profile.Bindings.Count} controls");
            _edit.Text = _s.Editing ? "✓  Done" : "✎  Edit";
            _edit.Style = _s.Editing ? PillStyle.Success : PillStyle.Secondary;
            _hints.Selected = _s.ShowHints;
            _rec.Text = _s.Recording ? "■" : "⏺";
            _rec.Style = _s.Recording ? PillStyle.Danger : PillStyle.Secondary;
            _play.Text = _s.Playing ? "■" : _s.Looping ? "▶∞" : "▶";
            _play.Style = _s.Playing ? PillStyle.Success : PillStyle.Secondary;
            _play.Width = _s.Looping && !_s.Playing ? 48 : 38;
            _rec.Invalidate(); _play.Invalidate();
            _sync.Selected = _s.Sync;
            foreach (var b in new[] { _edit, _hints, _sync }) { b.Width = TextRenderer.MeasureText(b.Text, b.Font).Width + 28; b.Invalidate(); }
            var needed = Controls[0].Controls.Cast<Control>().Sum(c => c.PreferredSize.Width + c.Margin.Horizontal) + 24;
            Width = Math.Max(360, needed);
        }

        readonly ToolTip _tipsBar = new();

        static string Friendly(string package)
        {
            var last = package.Split('.').Last();
            return last.Length == 0 ? package : char.ToUpperInvariant(last[0]) + last[1..];
        }
    }

    /// <summary>Modal "press a key or gamepad button" prompt used while editing.</summary>
    sealed class CaptureInput : Form
    {
        Keys _key = Keys.None;
        string? _pad;
        readonly System.Windows.Forms.Timer _poll = new() { Interval = 30 };

        CaptureInput(string title, string prompt)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(360, 130);
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Ui.T.Window;
            Controls.Add(new Label { Text = prompt + "\n\nEsc cancels.", Bounds = new Rectangle(20, 20, 320, 90), ForeColor = Ui.T.Text, Font = Theme.Body });
            _poll.Tick += (_, _) =>
            {
                var s = Gamepad.Read();
                var b = s?.Buttons.FirstOrDefault();
                if (b != null) { _pad = b; DialogResult = DialogResult.OK; Close(); }
            };
            Shown += (_, _) => { Ui.T.ApplyToWindow(this); _poll.Start(); };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu) key = keyData & Keys.KeyCode;
            _key = key;
            DialogResult = DialogResult.OK;
            Close();
            return true;
        }

        public static (Keys key, string? pad)? Ask(IWin32Window owner, string title, string prompt)
        {
            using var f = new CaptureInput(title, prompt);
            return f.ShowDialog(owner) == DialogResult.OK ? (f._key, f._pad) : null;
        }

        protected override void Dispose(bool disposing) { if (disposing) _poll.Dispose(); base.Dispose(disposing); }
    }

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE size, IntPtr src, ref POINT srcPt, int key, ref BLENDFUNCTION blend, int flags);

        public static void SetOwner(IntPtr window, IntPtr owner) => SetWindowLongPtr(window, -8, owner);

        /// <summary>Show a premultiplied-alpha bitmap as the window's contents.</summary>
        public static void UpdateLayered(IntPtr hwnd, Bitmap bmp, int x, int y)
        {
            var screen = GetDC(IntPtr.Zero);
            var mem = CreateCompatibleDC(screen);
            var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
            var old = SelectObject(mem, hbmp);
            try
            {
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new POINT();
                var pos = new POINT { X = x, Y = y };
                var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_ALPHA
                UpdateLayeredWindow(hwnd, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2); // ULW_ALPHA
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(hbmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
