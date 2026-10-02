using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DroidLauncher;

/// <summary>Light/dark palette in a clean, rounded "premium phone" style. Follows Windows unless overridden.</summary>
sealed class Theme
{
    public required bool Dark { get; init; }
    public required Color Window, Sidebar, Card, CardHover, CardSelected, Border, Text, SubText, Accent, AccentText, Good, Warn, Bad, Field;

    public static readonly Theme DarkTheme = new()
    {
        Dark = true,
        Window = Color.FromArgb(17, 18, 22),
        Sidebar = Color.FromArgb(23, 25, 30),
        Card = Color.FromArgb(32, 34, 41),
        CardHover = Color.FromArgb(40, 43, 51),
        CardSelected = Color.FromArgb(36, 52, 84),
        Border = Color.FromArgb(48, 51, 60),
        Text = Color.FromArgb(236, 238, 242),
        SubText = Color.FromArgb(150, 156, 168),
        Accent = Color.FromArgb(76, 141, 255),
        AccentText = Color.White,
        Good = Color.FromArgb(52, 211, 120),
        Warn = Color.FromArgb(250, 196, 70),
        Bad = Color.FromArgb(240, 90, 90),
        Field = Color.FromArgb(26, 28, 34),
    };

    public static readonly Theme LightTheme = new()
    {
        Dark = false,
        Window = Color.FromArgb(244, 245, 248),
        Sidebar = Color.FromArgb(235, 237, 242),
        Card = Color.White,
        CardHover = Color.FromArgb(248, 249, 252),
        CardSelected = Color.FromArgb(225, 236, 255),
        Border = Color.FromArgb(222, 225, 232),
        Text = Color.FromArgb(22, 24, 30),
        SubText = Color.FromArgb(105, 112, 125),
        Accent = Color.FromArgb(38, 110, 240),
        AccentText = Color.White,
        Good = Color.FromArgb(22, 163, 74),
        Warn = Color.FromArgb(202, 138, 4),
        Bad = Color.FromArgb(220, 38, 38),
        Field = Color.FromArgb(240, 242, 246),
    };

    /// <summary>"system", "light" or "dark".</summary>
    public static Theme Resolve(string? mode) => mode switch
    {
        "light" => LightTheme,
        "dark" => DarkTheme,
        _ => WindowsUsesLightApps() ? LightTheme : DarkTheme,
    };

    static bool WindowsUsesLightApps()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    // Shared fonts (creating fonts per control leaks GDI handles).
    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font BodyBold = new("Segoe UI Semibold", 10f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font SmallBold = new("Segoe UI Semibold", 8.75f);
    public static readonly Font Title = new("Segoe UI Semibold", 15f);
    public static readonly Font Heading = new("Segoe UI Semibold", 11.5f);
    public static readonly Font Huge = new("Segoe UI Semibold", 20f);
    public static readonly Font Icon = new("Segoe UI Symbol", 12f);

    /// <summary>Dark/light title bar and rounded corners on Windows 11.</summary>
    public void ApplyToWindow(Form form)
    {
        if (!form.IsHandleCreated) return;
        int dark = Dark ? 1 : 0;
        DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        int round = 2;
        DwmSetWindowAttribute(form.Handle, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = round
        int caption = ColorTranslator.ToWin32(Window);
        DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}
