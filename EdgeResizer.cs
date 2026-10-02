using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DroidLauncher;

/// <summary>
/// Lets emulator windows be resized from any edge. The emulator's own frameless window only resizes from its
/// corners, and it rejects any size that doesn't keep the screen's aspect ratio, so an edge drag just snaps back.
/// We place thin invisible grip windows just outside each edge (owned by the emulator window, so they follow its
/// z-order and minimise with it) and, while one is dragged, resize the emulator with the other dimension
/// computed from its aspect ratio. No hooks or injection into the emulator process.
/// </summary>
sealed class EdgeResizer : IDisposable
{
    const int Thickness = 7;
    readonly Dictionary<IntPtr, Grip[]> _grips = new();
    readonly System.Windows.Forms.Timer _scan = new() { Interval = 1000 };
    readonly WinEventProc _onLocation;
    IntPtr _hook;

    public EdgeResizer()
    {
        _onLocation = OnWinEvent;
        // Out-of-context hook: we're only told that a window moved; nothing is loaded into other processes.
        _hook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, _onLocation, 0, 0, WinEventOutOfContext);
        _scan.Tick += (_, _) => Scan();
        _scan.Start();
        Scan();
    }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject == 0 && _grips.TryGetValue(hwnd, out var grips)) Place(hwnd, grips);
    }

    /// <summary>Find emulator windows that appeared or went away.</summary>
    void Scan()
    {
        var found = new HashSet<IntPtr>();
        var qemuPids = Process.GetProcesses()
            .Where(p => p.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase))
            .Select(p => { var id = (uint)p.Id; p.Dispose(); return id; })
            .ToHashSet();
        if (qemuPids.Count > 0)
        {
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (qemuPids.Contains(pid) && IsWindowVisible(h) && Title(h).StartsWith("Android Emulator", StringComparison.Ordinal))
                    found.Add(h);
                return true;
            }, IntPtr.Zero);
        }

        foreach (var gone in _grips.Keys.Where(h => !found.Contains(h)).ToList())
        {
            foreach (var g in _grips[gone]) g.Dispose();
            _grips.Remove(gone);
        }
        foreach (var h in found)
        {
            if (!_grips.TryGetValue(h, out var grips))
            {
                grips = new[] { new Grip(this, h, Edge.Left), new Grip(this, h, Edge.Right), new Grip(this, h, Edge.Top), new Grip(this, h, Edge.Bottom) };
                _grips[h] = grips;
            }
            Place(h, grips);
        }
    }

    static void Place(IntPtr emu, Grip[] grips)
    {
        if (!GetWindowRect(emu, out var r) || IsIconic(emu)) { foreach (var g in grips) g.Park(); return; }
        int w = r.Right - r.Left, h = r.Bottom - r.Top, inset = Math.Min(24, h / 6);
        foreach (var g in grips)
        {
            var rect = g.Edge switch
            {
                // Corners are left to the emulator's own diagonal resize.
                Edge.Left => new Rectangle(r.Left - Thickness, r.Top + inset, Thickness, h - 2 * inset),
                Edge.Right => new Rectangle(r.Right, r.Top + inset, Thickness, h - 2 * inset),
                Edge.Top => new Rectangle(r.Left + inset, r.Top - Thickness, w - 2 * inset, Thickness),
                _ => new Rectangle(r.Left + inset, r.Bottom, w - 2 * inset, Thickness),
            };
            g.MoveTo(rect);
        }
    }

    public void Dispose()
    {
        _scan.Dispose();
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        foreach (var g in _grips.Values.SelectMany(x => x)) g.Dispose();
        _grips.Clear();
    }

    enum Edge { Left, Right, Top, Bottom }

    /// <summary>An almost-transparent strip that shows a resize cursor and turns drags into aspect-locked resizes.</summary>
    sealed class Grip : Form
    {
        public readonly Edge Edge;
        readonly IntPtr _emu;
        Point _startCursor;
        RECT _startRect;
        bool _dragging;
        long _lastApply;

        public Grip(EdgeResizer owner, IntPtr emulatorWindow, Edge edge)
        {
            Edge = edge;
            _emu = emulatorWindow;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Opacity = 0.01; // layered + nearly invisible, but still receives the mouse
            Cursor = edge is Edge.Left or Edge.Right ? Cursors.SizeWE : Cursors.SizeNS;
            Bounds = new Rectangle(-32000, -32000, 1, 1);
            Show();
            // Owned by the emulator window: stays just above it and hides when it's minimised or covered.
            SetWindowLongPtr(Handle, GwlpHwndParent, emulatorWindow);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExToolWindow | WsExNoActivate;
                return cp;
            }
        }

        public void Park() => SetWindowPos(Handle, IntPtr.Zero, -32000, -32000, 1, 1, SwpNoZOrder | SwpNoActivate);

        public void MoveTo(Rectangle r)
        {
            if (_dragging || r.Width <= 0 || r.Height <= 0) return;
            SetWindowPos(Handle, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SwpNoZOrder | SwpNoActivate);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !GetWindowRect(_emu, out _startRect)) return;
            _startCursor = Cursor.Position;
            _dragging = true;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging) return;
            // The emulator re-renders on every resize; ~60 updates a second is plenty.
            var now = Environment.TickCount64;
            if (now - _lastApply < 16) return;
            _lastApply = now;

            var p = Cursor.Position;
            int dx = p.X - _startCursor.X, dy = p.Y - _startCursor.Y;
            int w0 = _startRect.Right - _startRect.Left, h0 = _startRect.Bottom - _startRect.Top;
            double ratio = (double)h0 / w0;
            int w, h;
            if (Edge is Edge.Left or Edge.Right)
            {
                w = Math.Max(180, w0 + (Edge == Edge.Right ? dx : -dx));
                h = (int)Math.Round(w * ratio);
            }
            else
            {
                h = Math.Max(240, h0 + (Edge == Edge.Bottom ? dy : -dy));
                w = (int)Math.Round(h / ratio);
            }
            // Anchor the opposite edge so the window grows toward the cursor, and keep it centred on the other axis.
            int x = Edge switch
            {
                Edge.Left => _startRect.Right - w,
                Edge.Right => _startRect.Left,
                _ => _startRect.Left + (w0 - w) / 2,
            };
            int y = Edge switch
            {
                Edge.Top => _startRect.Bottom - h,
                Edge.Bottom => _startRect.Top,
                _ => _startRect.Top + (h0 - h) / 2,
            };
            SetWindowPos(_emu, IntPtr.Zero, x, y, w, h, SwpNoZOrder | SwpNoActivate);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
        }
    }

    // ── Win32 ──────────────────────────────────────────────────────────────

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    const uint EventObjectLocationChange = 0x800B, WinEventOutOfContext = 0;
    const int GwlpHwndParent = -8, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    const uint SwpNoZOrder = 0x4, SwpNoActivate = 0x10;

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    static string Title(IntPtr h)
    {
        var sb = new StringBuilder(128);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
