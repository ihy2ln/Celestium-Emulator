using System.Runtime.InteropServices;

namespace DroidLauncher;

/// <summary>
/// Turns mouse-wheel notches into a short finger swipe on the phone: finger down where the cursor is, glide a bit
/// per notch, hold still briefly, lift. Holding still before lifting means Android sees no fling, so each notch
/// scrolls a predictable distance, and a fast spin keeps extending the same gesture instead of producing taps.
/// </summary>
sealed class WheelGesture
{
    const int Slot = 8;
    const double PerNotch = 0.07;       // fraction of the screen height per wheel notch
    readonly TouchInjector _touch;
    readonly System.Windows.Forms.Timer _step = new() { Interval = 12 };
    double _u, _v, _targetV, _startV;
    bool _down;
    long _lastInput, _lastRotationCheck;

    public WheelGesture(TouchInjector touch)
    {
        _touch = touch;
        _step.Tick += (_, _) => Step();
    }

    /// <summary>delta: +120 per notch away from the user (scroll up), -120 towards (scroll down).</summary>
    public async void Wheel(double u, double v, int delta)
    {
        if (Environment.TickCount64 - _lastRotationCheck > 3000)
        {
            _lastRotationCheck = Environment.TickCount64;
            await _touch.RefreshRotation();
        }
        _lastInput = Environment.TickCount64;
        if (!_down)
        {
            _u = u; _v = _startV = _targetV = Math.Clamp(v, 0.15, 0.85);
            _touch.Down(Slot, _u, _v);
            _down = true;
            _step.Start();
        }
        // Wheel up = content moves down = finger moves down.
        _targetV += delta / 120.0 * PerNotch;
        // Running out of screen: lift here and put the finger back down where the gesture started.
        if (_targetV is < 0.05 or > 0.95)
        {
            _touch.Up(Slot);
            _v = _startV;
            _targetV = _startV + delta / 120.0 * PerNotch;
            _touch.Down(Slot, _u, _v);
        }
    }

    void Step()
    {
        if (!_down) { _step.Stop(); return; }
        var diff = _targetV - _v;
        if (Math.Abs(diff) > 0.002)
        {
            _v += Math.Sign(diff) * Math.Min(Math.Abs(diff), 0.018); // glide, ~60 px/frame at most
            _touch.Move(Slot, _u, _v);
            return;
        }
        // At rest: lift ~110 ms after the last notch (still finger = no fling).
        if (Environment.TickCount64 - _lastInput > 110)
        {
            _touch.Up(Slot);
            _down = false;
            _step.Stop();
        }
    }
}

/// <summary>
/// Catches the mouse wheel over device windows that use "mouse as a finger" and scrolls with a swipe instead.
/// Uses a low-level mouse hook that only looks at wheel messages; the window lookup is a dictionary hit, so the
/// hook returns immediately and the swipe itself runs on the UI thread.
/// </summary>
sealed class WheelScroller : IDisposable
{
    readonly Func<Dictionary<IntPtr, string>> _windows; // emulator top-level window → adb serial (finger-mode devices only)
    readonly LowLevelMouseProc _proc;
    readonly Control _ui;
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2000 };
    Dictionary<IntPtr, string> _map = new();
    readonly Dictionary<string, WheelGesture> _gestures = new();
    IntPtr _hook;

    public WheelScroller(Control ui, Func<Dictionary<IntPtr, string>> windows)
    {
        _ui = ui;
        _windows = windows;
        _proc = HookProc;
        _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(null), 0);
        _refresh.Tick += (_, _) => _map = _windows();
        _refresh.Start();
        _map = _windows();
    }

    IntPtr HookProc(int code, IntPtr msg, IntPtr data)
    {
        if (code >= 0 && msg == (IntPtr)0x020A /* WM_MOUSEWHEEL */ && _map.Count > 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(data);
            var top = GetAncestor(WindowFromPoint(info.pt), 2 /* GA_ROOT */);
            if (_map.TryGetValue(top, out var serial) && GetClientRect(top, out var c))
            {
                var origin = new POINT();
                ClientToScreen(top, ref origin);
                double u = (info.pt.X - origin.X) / (double)Math.Max(1, c.Right), v = (info.pt.Y - origin.Y) / (double)Math.Max(1, c.Bottom);
                if (u is >= 0 and <= 1 && v is >= 0 and <= 1)
                {
                    int delta = (short)((info.mouseData >> 16) & 0xFFFF);
                    _ui.BeginInvoke(() => GestureFor(serial).Wheel(u, v, delta));
                    return (IntPtr)1; // handled: don't let the emulator turn it into something else
                }
            }
        }
        return CallNextHookEx(_hook, code, msg, data);
    }

    WheelGesture GestureFor(string serial)
    {
        if (!_gestures.TryGetValue(serial, out var g)) _gestures[serial] = g = new WheelGesture(new TouchInjector(serial));
        return g;
    }

    public void Dispose()
    {
        _refresh.Dispose();
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    delegate IntPtr LowLevelMouseProc(int code, IntPtr msg, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
}
