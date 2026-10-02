using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DroidLauncher;

/// <summary>The current theme. Custom-painted widgets read it on every paint, so switching just needs an Invalidate.</summary>
static class Ui
{
    public static Theme T = Theme.DarkTheme;

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    /// <summary>Recolour standard WinForms controls after a theme change; custom ones repaint themselves.</summary>
    public static void Recolor(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox or ComboBox or NumericUpDown:
                    c.BackColor = T.Field; c.ForeColor = T.Text; break;
                case Label l when l.Tag as string == "sub":
                    l.ForeColor = T.SubText; break;
                case Label l when l.Tag as string == "accent":
                    l.ForeColor = T.Accent; break;
                case Label:
                    c.ForeColor = T.Text; break;
                case ListView lv:
                    lv.BackColor = T.Card; lv.ForeColor = T.Text; break;
            }
            Recolor(c);
            c.Invalidate();
        }
    }

    public static Label Text(string text, Font? font = null, bool sub = false) => new()
    {
        Text = text, AutoSize = true, Font = font ?? Theme.Body, BackColor = Color.Transparent, UseMnemonic = false,
        ForeColor = sub ? T.SubText : T.Text, Tag = sub ? "sub" : null,
    };
}

enum PillStyle { Primary, Secondary, Danger, Ghost, Success }

/// <summary>Rounded, custom-painted button with hover/press states.</summary>
class PillButton : Control
{
    public PillStyle Style { get; set; }
    public bool Selected { get; set; }
    bool _hover, _down;

    public PillButton(string text, PillStyle style = PillStyle.Secondary)
    {
        Text = text;
        Style = style;
        Font = Theme.BodyBold;
        Height = 36;
        Width = Math.Max(44, TextRenderer.MeasureText(text, Font).Width + 32);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

    // Keyboard: Tab to it, Space/Enter presses it, with a visible focus ring.
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { PerformClick(); e.Handled = true; }
        base.OnKeyUp(e);
    }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    // Exposed as a real button to screen readers and UI automation.
    protected override AccessibleObject CreateAccessibilityInstance() => new ButtonAccessible(this);

    sealed class ButtonAccessible(PillButton owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.PushButton;
        public override string DefaultAction => "Press";
        public override void DoDefaultAction() => owner.PerformClick();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Ui.T;
        var g = e.Graphics;
        Ui.Smooth(g);
        var style = Selected ? PillStyle.Primary : Style;
        (Color back, Color fore) = style switch
        {
            PillStyle.Primary => (t.Accent, t.AccentText),
            PillStyle.Danger => (t.Bad, Color.White),
            PillStyle.Success => (t.Good, Color.White),
            PillStyle.Ghost => (Color.Transparent, t.Text),
            _ => (t.Card, t.Text),
        };
        if (!Enabled) { back = Theme.Blend(back.A == 0 ? t.Window : back, t.Window, 0.5); fore = t.SubText; }
        else if (_down) back = Theme.Blend(back.A == 0 ? t.CardHover : back, t.Dark ? Color.Black : Color.Gray, 0.18);
        else if (_hover) back = back.A == 0 ? t.CardHover : Theme.Blend(back, t.Dark ? Color.White : Color.Black, 0.08);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Rounded(r, Height / 2);
        if (back.A > 0) using (var b = new SolidBrush(back)) g.FillPath(b, path);
        if (style == PillStyle.Secondary) using (var p = new Pen(t.Border)) g.DrawPath(p, path);
        if (Focused && ShowFocusCues)
        {
            using var inner = Theme.Rounded(new Rectangle(2, 2, Width - 5, Height - 5), (Height - 4) / 2);
            using var focus = new Pen(t.Accent, 2f);
            g.DrawPath(focus, inner);
        }
        TextRenderer.DrawText(g, Text, Font, r, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Rounded card background. Children sit on top.</summary>
class Card : Panel
{
    public int Radius { get; set; } = 16;

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Padding = new Padding(18);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Ui.Smooth(e.Graphics);
        using var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
        using var b = new SolidBrush(Ui.T.Card);
        e.Graphics.FillPath(b, path);
        using var p = new Pen(Ui.T.Border);
        e.Graphics.DrawPath(p, path);
    }
}

/// <summary>On/off switch with a short slide animation.</summary>
class Toggle : Control
{
    bool _on;
    float _pos;
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 12 };
    public event EventHandler? Toggled;

    public Toggle()
    {
        Size = new Size(46, 26);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        _anim.Tick += (_, _) =>
        {
            var target = _on ? 1f : 0f;
            _pos += Math.Sign(target - _pos) * 0.2f;
            if (Math.Abs(target - _pos) < 0.2f) { _pos = target; _anim.Stop(); }
            Invalidate();
        };
    }

    public bool On
    {
        get => _on;
        set { if (_on == value) return; _on = value; _anim.Start(); }
    }

    /// <summary>Set without animation or event (when loading state).</summary>
    public void SetQuiet(bool on) { _on = on; _pos = on ? 1 : 0; Invalidate(); }

    protected override void OnClick(EventArgs e)
    {
        On = !On;
        Toggled?.Invoke(this, EventArgs.Empty);
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        var t = Ui.T;
        var track = Theme.Blend(t.Border, t.Accent, _pos);
        using (var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2))
        using (var b = new SolidBrush(Enabled ? track : t.Border))
            g.FillPath(b, path);
        int d = Height - 6;
        int x = 3 + (int)((Width - d - 6) * _pos);
        using var knob = new SolidBrush(Color.White);
        g.FillEllipse(knob, x, 3, d, d);
    }

    protected override void Dispose(bool disposing) { if (disposing) _anim.Dispose(); base.Dispose(disposing); }
}

/// <summary>Horizontal slider (0..Max) in the theme's style.</summary>
class Slider : Control
{
    int _value;
    public int Maximum { get; set; } = 100;
    public event EventHandler? ValueCommitted;
    bool _drag;

    public Slider()
    {
        Height = 26;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    public int Value { get => _value; set { _value = Math.Clamp(value, 0, Maximum); Invalidate(); } }

    void SetFromX(int x) => Value = (int)Math.Round((double)(x - 10) / Math.Max(1, Width - 20) * Maximum);
    protected override void OnMouseDown(MouseEventArgs e) { _drag = true; SetFromX(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetFromX(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { if (!_drag) return; _drag = false; SetFromX(e.X); ValueCommitted?.Invoke(this, EventArgs.Empty); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        var t = Ui.T;
        int y = Height / 2, x0 = 10, x1 = Width - 10;
        int x = x0 + (int)((x1 - x0) * (double)_value / Math.Max(1, Maximum));
        using (var p = new Pen(t.Border, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, x0, y, x1, y);
        using (var p = new Pen(t.Accent, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, x0, y, x, y);
        using var knob = new SolidBrush(Color.White);
        using var ring = new Pen(t.Accent, 2);
        g.FillEllipse(knob, x - 8, y - 8, 16, 16);
        g.DrawEllipse(ring, x - 8, y - 8, 16, 16);
    }
}

/// <summary>Row of pill tabs; exactly one selected.</summary>
class TabStrip : FlowLayoutPanel
{
    public event Action<int>? SelectedChanged;
    readonly List<PillButton> _tabs = new();
    public int SelectedIndex { get; private set; }

    public TabStrip(params string[] names)
    {
        AutoSize = true;
        WrapContents = false;
        BackColor = Color.Transparent;
        Margin = new Padding(0);
        foreach (var (name, i) in names.Select((n, i) => (n, i)))
        {
            var b = new PillButton(name, PillStyle.Ghost) { Margin = new Padding(0, 0, 6, 0), Height = 34 };
            b.Click += (_, _) => Select(i);
            _tabs.Add(b);
            Controls.Add(b);
        }
        Select(0);
    }

    public void Select(int index)
    {
        SelectedIndex = index;
        for (int i = 0; i < _tabs.Count; i++) { _tabs[i].Selected = i == index; _tabs[i].Invalidate(); }
        SelectedChanged?.Invoke(index);
    }
}

/// <summary>Sidebar entry for one device: thumbnail, name, project, status.</summary>
class DeviceTile : Control
{
    public string Avd { get; }
    public string Project = "", Status = "Stopped", Specs = "";
    public Color StatusColor = Color.Gray;
    public Image? Thumb;
    public bool Selected;
    bool _hover;

    public DeviceTile(string avd)
    {
        Avd = avd;
        Height = 84;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 0, 8);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override AccessibleObject CreateAccessibilityInstance() => new TileAccessible(this);

    sealed class TileAccessible(DeviceTile owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.ListItem;
        public override string? Name => owner.Avd;
        public override string? Value => owner.Status;
        public override string DefaultAction => "Select";
        public override void DoDefaultAction() => owner.InvokeOnClick(owner, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        var t = Ui.T;
        var bg = Selected ? t.CardSelected : _hover ? t.CardHover : t.Card;
        using (var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 14))
        {
            using var b = new SolidBrush(bg);
            g.FillPath(b, path);
            if (Selected) using (var p = new Pen(t.Accent, 1.5f)) g.DrawPath(p, path);
        }

        // Phone-shaped thumbnail
        var phone = new Rectangle(12, 10, 34, Height - 20);
        using (var path = Theme.Rounded(phone, 6))
        {
            using var b = new SolidBrush(t.Dark ? Color.Black : Color.FromArgb(30, 32, 38));
            g.FillPath(b, path);
            if (Thumb != null)
            {
                g.SetClip(path);
                g.DrawImage(Thumb, phone);
                g.ResetClip();
            }
        }

        int x = 58;
        TextRenderer.DrawText(g, Avd.Replace('_', ' '), Theme.Heading, new Point(x, 12), t.Text, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        if (Project.Length > 0)
        {
            var nameW = TextRenderer.MeasureText(Avd.Replace('_', ' '), Theme.Heading, Size.Empty, TextFormatFlags.NoPadding).Width;
            var chip = TextRenderer.MeasureText(Project, Theme.SmallBold, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            var cr = new Rectangle(x + nameW + 8, 14, chip.Width + 14, chip.Height + 4);
            if (cr.Right < Width - 8)
            {
                using var path = Theme.Rounded(cr, cr.Height / 2);
                using var b = new SolidBrush(Color.FromArgb(48, t.Accent));
                g.FillPath(b, path);
                TextRenderer.DrawText(g, Project, Theme.SmallBold, new Point(cr.X + 7, cr.Y + 2), t.Accent, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }
        using (var dot = new SolidBrush(StatusColor)) g.FillEllipse(dot, x, 41, 8, 8);
        TextRenderer.DrawText(g, Status, Theme.Small, new Point(x + 14, 37), t.SubText, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Specs, Theme.Small, new Rectangle(x, 56, Width - x - 10, 18), t.SubText,
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
