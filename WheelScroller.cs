using System.Runtime.InteropServices;

namespace DroidLauncher;

/// <summary>
/// Turns mouse-wheel notches into a short finger swipe on the phone: finger down where the cursor is, glide a bit
/// per notch, hold still briefly, lift. Holding still before lifting means Android sees no fling, so each notch
/// scrolls a predictable distance, and a fast spin keeps extending the same gesture instead of producing taps.
/// Works vertically (wheel) or horizontally (Shift+wheel, tilt wheel).
/// </summary>
sealed class WheelGesture
{
    const double PerNotch = 0.07;       // fraction of the screen per wheel notch
    readonly TouchInjector _touch;
    readonly int _slot;
    readonly bool _horizontal;
    readonly System.Windows.Forms.Timer _step = new() { Interval = 12 };
    double _fixed, _pos, _target, _start;
    bool _down;
    long _lastInput, _lastRotationCheck;

    public WheelGesture(TouchInjector touch, bool horizontal = false)
    {
        _touch = touch;
        _horizontal = horizontal;
        _slot = horizontal ? 7 : 8;
        _step.Tick += (_, _) => Step();
    }

    void Send(bool down = false)
    {
        var (u, v) = _horizontal ? (_pos, _fixed) : (_fixed, _pos);
        if (down) _touch.Down(_slot, u, v); else _touch.Move(_slot, u, v);
    }

    /// <summary>delta: +120 per notch (wheel away from you / tilt right), -120 the other way.</summary>
    public async void Wheel(double u, double v, int delta)
    {
        if (Environment.TickCount64 - _lastRotationCheck > 3000)
        {
            _lastRotationCheck = Environment.TickCount64;
            await _touch.RefreshRotation();
        }
        _lastInput = Environment.TickCount64;
        // Wheel up = content moves down = finger moves down. Tilt right = content moves left = finger moves left.
        double move = (_horizontal ? -delta : delta) / 120.0 * PerNotch;
        if (!_down)
        {
            _fixed = _horizontal ? v : u;
            _pos = _start = _target = Math.Clamp(_horizontal ? u : v, 0.15, 0.85);
            Send(down: true);
            _down = true;
            _step.Start();
        }
        _target += move;
        // Running out of screen: lift here and put the finger back down where the gesture started.
        if (_target is < 0.05 or > 0.95)
        {
            _touch.Up(_slot);
            _pos = _start;
            _target = _start + move;
            Send(down: true);
        }
    }

    void Step()
    {
        if (!_down) { _step.Stop(); return; }
        var diff = _target - _pos;
        if (Math.Abs(diff) > 0.002)
        {
            _pos += Math.Sign(diff) * Math.Min(Math.Abs(diff), 0.018); // glide
            Send();
            return;
        }
        // At rest: lift ~110 ms after the last notch (still finger = no fling).
        if (Environment.TickCount64 - _lastInput > 110)
        {
            _touch.Up(_slot);
            _down = false;
            _step.Stop();
        }
    }
}

/// <summary>Ctrl+wheel (and laptop touchpad pinch, which Windows sends as Ctrl+wheel) → a two-finger pinch.</summary>
sealed class PinchGesture
{
    const double PerNotch = 0.035;
    readonly TouchInjector _touch;
    readonly System.Windows.Forms.Timer _step = new() { Interval = 12 };
    double _u, _v, _spread, _target;
    bool _down;
    long _lastInput;

    public PinchGesture(TouchInjector touch)
    {
        _touch = touch;
        _step.Tick += (_, _) => Step();
    }

    void Place(bool down)
    {
        double a = Math.Clamp(_u - _spread, 0.01, 0.99), b = Math.Clamp(_u + _spread, 0.01, 0.99);
        if (down) { _touch.Down(5, a, _v); _touch.Down(6, b, _v); }
        else { _touch.Move(5, a, _v); _touch.Move(6, b, _v); }
    }

    /// <summary>delta &gt; 0 zooms in (fingers apart), &lt; 0 zooms out.</summary>
    public void Wheel(double u, double v, int delta)
    {
        _lastInput = Environment.TickCount64;
        if (!_down)
        {
            _u = u; _v = v;
            _spread = _target = 0.12;
            Place(down: true);
            _down = true;
            _step.Start();
        }
        _target = Math.Clamp(_target + delta / 120.0 * PerNotch, 0.03, 0.45);
    }

    void Step()
    {
        if (!_down) { _step.Stop(); return; }
        var diff = _target - _spread;
        if (Math.Abs(diff) > 0.002) { _spread += Math.Sign(diff) * Math.Min(Math.Abs(diff), 0.012); Place(down: false); return; }
        if (Environment.TickCount64 - _lastInput > 140)
        {
            _touch.Up(5); _touch.Up(6);
            _down = false;
            _step.Stop();
        }
    }
}

/// <summary>
/// PC-style mouse and keyboard for device windows whose mouse acts as a finger:
/// wheel = smooth scroll, Shift+wheel / tilt = horizontal scroll, Ctrl+wheel = pinch zoom,
/// right-click and the mouse's Back button = Back, Esc = Back.
/// The low-level mouse hook only reacts to those buttons over a device screen (a dictionary lookup, then the work is
/// posted to the UI thread). Esc uses a hotkey registered only while a device window is in front — no keyboard hook.
/// </summary>
sealed class WheelScroller : IDisposable
{
    readonly Func<Dictionary<IntPtr, string>> _windows; // emulator top-level window → adb serial (finger-mode devices only)
    readonly Func<bool> _backButtons;                   // right-click / Esc / mouse Back act as Android Back
    readonly Action<string> _back;
    readonly LowLevelMouseProc _proc;
    readonly WinEventProc _onForeground;
    readonly Control _ui;
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2000 };
    readonly HotkeyWindow _hotkeys;
    Dictionary<IntPtr, string> _map = new();
    readonly Dictionary<string, WheelGesture> _vertical = new(), _horizontal = new();
    readonly Dictionary<string, PinchGesture> _pinch = new();
    IntPtr _hook, _fgHook;
    string? _rightDownOn;

    public WheelScroller(Control ui, Func<Dictionary<IntPtr, string>> windows, Func<bool> backButtons, Action<string> back)
    {
        _ui = ui;
        _windows = windows;
        _backButtons = backButtons;
        _back = back;
        _proc = HookProc;
        _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(null), 0);
        _hotkeys = new HotkeyWindow(() => { if (ForegroundSerial() is { } s) _back(s); });
        _onForeground = (_, _, _, _, _, _, _) => UpdateEscHotkey();
        _fgHook = SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, IntPtr.Zero, _onForeground, 0, 0, 0);
        _refresh.Tick += (_, _) => { _map = _windows(); UpdateEscHotkey(); };
        _refresh.Start();
        _map = _windows();
    }

    string? ForegroundSerial() => _map.TryGetValue(GetForegroundWindow(), out var s) ? s : null;

    /// <summary>Esc belongs to Android (as Back) only while a device window is in front; everywhere else it's untouched.</summary>
    void UpdateEscHotkey() => _hotkeys.SetEscape(_backButtons() && ForegroundSerial() != null);

    bool OverScreen(MSLLHOOKSTRUCT info, out string serial, out double u, out double v)
    {
        serial = ""; u = v = 0;
        var top = GetAncestor(WindowFromPoint(info.pt), 2 /* GA_ROOT */);
        if (!_map.TryGetValue(top, out var s) || !GetClientRect(top, out var c)) return false;
        var origin = new POINT();
        ClientToScreen(top, ref origin);
        u = (info.pt.X - origin.X) / (double)Math.Max(1, c.Right);
        v = (info.pt.Y - origin.Y) / (double)Math.Max(1, c.Bottom);
        serial = s;
        return u is >= 0 and <= 1 && v is >= 0 and <= 1;
    }

    IntPtr HookProc(int code, IntPtr msg, IntPtr data)
    {
        if (code >= 0 && _map.Count > 0)
        {
            int m = (int)msg;
            if (m is 0x020A or 0x020E or 0x0204 or 0x0205 or 0x020B or 0x020C)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(data);
                if (OverScreen(info, out var serial, out var u, out var v))
                {
                    int hi = (short)((info.mouseData >> 16) & 0xFFFF);
                    bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0, shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                    switch (m)
                    {
                        case 0x020A when ctrl: // Ctrl+wheel → pinch
                            _ui.BeginInvoke(() => Pinch(serial).Wheel(u, v, hi));
                            return (IntPtr)1;
                        case 0x020A when shift: // Shift+wheel → horizontal
                            _ui.BeginInvoke(() => Gesture(_horizontal, serial, true).Wheel(u, v, -hi));
                            return (IntPtr)1;
                        case 0x020A:
                            _ui.BeginInvoke(() => Gesture(_vertical, serial, false).Wheel(u, v, hi));
                            return (IntPtr)1;
                        case 0x020E: // tilt wheel
                            _ui.BeginInvoke(() => Gesture(_horizontal, serial, true).Wheel(u, v, hi));
                            return (IntPtr)1;
                        case 0x0204 when _backButtons(): // right button down: remember, act on release
                            _rightDownOn = serial;
                            return (IntPtr)1;
                        case 0x0205 when _backButtons() && _rightDownOn != null:
                            var target = _rightDownOn; _rightDownOn = null;
                            _ui.BeginInvoke(() => _back(target));
                            return (IntPtr)1;
                        case 0x020B when hi == 1 && _backButtons(): // mouse Back (XBUTTON1) down
                            return (IntPtr)1;
                        case 0x020C when hi == 1 && _backButtons():
                            _ui.BeginInvoke(() => _back(serial));
                            return (IntPtr)1;
                    }
                }
                else if (m == 0x0205 && _rightDownOn != null) { _rightDownOn = null; return (IntPtr)1; } // released elsewhere
            }
        }
        return CallNextHookEx(_hook, code, msg, data);
    }

    WheelGesture Gesture(Dictionary<string, WheelGesture> set, string serial, bool horizontal)
    {
        if (!set.TryGetValue(serial, out var g)) set[serial] = g = new WheelGesture(new TouchInjector(serial), horizontal);
        return g;
    }

    PinchGesture Pinch(string serial)
    {
        if (!_pinch.TryGetValue(serial, out var g)) _pinch[serial] = g = new PinchGesture(new TouchInjector(serial));
        return g;
    }

    public void Dispose()
    {
        _refresh.Dispose();
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        if (_fgHook != IntPtr.Zero) { UnhookWinEvent(_fgHook); _fgHook = IntPtr.Zero; }
        _hotkeys.Dispose();
    }

    /// <summary>Invisible message window that owns the Esc hotkey while a device window is in front.</summary>
    sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        readonly Action _onEscape;
        bool _registered;

        public HotkeyWindow(Action onEscape)
        {
            _onEscape = onEscape;
            CreateHandle(new CreateParams { Parent = (IntPtr)(-3) /* HWND_MESSAGE */ });
        }

        public void SetEscape(bool on)
        {
            if (on == _registered) return;
            if (on) _registered = RegisterHotKey(Handle, 1, 0x4000 /* MOD_NOREPEAT */, 0x1B /* VK_ESCAPE */);
            else { UnregisterHotKey(Handle, 1); _registered = false; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 /* WM_HOTKEY */) { _onEscape(); return; }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_registered) UnregisterHotKey(Handle, 1);
            DestroyHandle();
        }
    }

    delegate IntPtr LowLevelMouseProc(int code, IntPtr msg, IntPtr data);
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
}
