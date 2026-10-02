using System.Reflection;
using System.Text.RegularExpressions;

namespace DroidLauncher;

/// <summary>
/// In-app help: the same Markdown pages as docs/ on GitHub, embedded in the app and rendered with the app theme.
/// Topics on the left, search across all pages, matches highlighted.
/// </summary>
sealed class HelpWindow : Form
{
    sealed record Topic(string Title, string Markdown);

    static HelpWindow? _open;
    readonly List<Topic> _topics = LoadTopics();
    readonly ListBox _nav = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 38, IntegralHeight = false };
    readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Search help", BorderStyle = BorderStyle.FixedSingle, Font = Theme.Body };
    readonly RichTextBox _view = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = true, ScrollBars = RichTextBoxScrollBars.Vertical };
    List<Topic> _shown = new();

    /// <summary>Show (or bring back) the help window, optionally at a topic whose title contains <paramref name="topic"/>.</summary>
    public static void Open(IWin32Window? owner, string? topic = null)
    {
        if (_open is { IsDisposed: false }) { _open.Activate(); if (topic != null) _open.Go(topic); return; }
        _open = new HelpWindow();
        _open.Show(owner);
        if (topic != null) _open.Go(topic);
    }

    HelpWindow()
    {
        Text = "Celestium Help";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Size = new Size(1000, 700);
        MinimumSize = new Size(700, 450);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Body;
        KeyPreview = true;

        var left = new Panel { Dock = DockStyle.Left, Width = 250, Padding = new Padding(12) };
        var gap = new Panel { Dock = DockStyle.Top, Height = 10 };
        left.Controls.Add(_nav);
        left.Controls.Add(gap);
        left.Controls.Add(_search);
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 18, 18, 12) };
        right.Controls.Add(_view);
        Controls.Add(right);
        Controls.Add(left);

        _nav.DrawItem += DrawNavItem;
        _nav.SelectedIndexChanged += (_, _) => { if (_nav.SelectedIndex >= 0) Render(_shown[_nav.SelectedIndex]); };
        _search.TextChanged += (_, _) => Filter();
        _view.LinkClicked += (_, e) => { if (e.LinkText?.StartsWith("http") == true) Sdk.Launch(e.LinkText, ""); };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); e.Handled = true; }
        };
        HandleCreated += (_, _) => Ui.T.ApplyToWindow(this);
        FormClosed += (_, _) => _open = null;

        ApplyColors(left, right);
        Filter();
    }

    void ApplyColors(Panel left, Panel right)
    {
        var t = Ui.T;
        BackColor = t.Window;
        left.BackColor = t.Sidebar;
        _nav.BackColor = t.Sidebar;
        _nav.ForeColor = t.Text;
        right.BackColor = t.Window;
        _view.BackColor = t.Window;
        _view.ForeColor = t.Text;
        _search.BackColor = t.Field;
        _search.ForeColor = t.Text;
    }

    static List<Topic> LoadTopics()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("docs/") && n.EndsWith(".md"))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n =>
            {
                using var s = asm.GetManifestResourceStream(n)!;
                using var r = new StreamReader(s);
                var md = r.ReadToEnd().Replace("\r\n", "\n");
                var title = md.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim() ?? Path.GetFileNameWithoutExtension(n);
                return new Topic(title, md);
            })
            .ToList();
    }

    void Go(string topic)
    {
        var i = _shown.FindIndex(t => t.Title.Contains(topic, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _nav.SelectedIndex = i;
    }

    void Filter()
    {
        var q = _search.Text.Trim();
        var keep = _nav.SelectedIndex >= 0 && _nav.SelectedIndex < _shown.Count ? _shown[_nav.SelectedIndex] : null;
        _shown = q.Length == 0 ? _topics : _topics.Where(t => t.Markdown.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _nav.BeginUpdate();
        _nav.Items.Clear();
        foreach (var t in _shown) _nav.Items.Add(t.Title);
        _nav.EndUpdate();
        if (_shown.Count == 0) { _view.Clear(); _view.SelectionFont = Theme.Heading; _view.AppendText($"Nothing matches \"{q}\"."); return; }
        var idx = keep != null ? _shown.IndexOf(keep) : -1;
        _nav.SelectedIndex = idx >= 0 ? idx : 0;
        if (idx >= 0) Render(_shown[idx]); // SelectedIndex didn't change, so re-render for the new highlight
    }

    void DrawNavItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var t = Ui.T;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        var g = e.Graphics;
        Ui.Smooth(g);
        using (var bg = new SolidBrush(t.Sidebar)) g.FillRectangle(bg, e.Bounds);
        if (selected)
        {
            var r = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 3, e.Bounds.Width - 6, e.Bounds.Height - 6);
            using var path = Theme.Rounded(r, 10);
            using var b = new SolidBrush(t.CardSelected);
            g.FillPath(b, path);
        }
        TextRenderer.DrawText(g, (string)_nav.Items[e.Index], selected ? Theme.BodyBold : Theme.Body,
            new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 20, e.Bounds.Height), selected ? t.Accent : t.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    // ── Markdown → RichTextBox ──────────────────────────────────────────────

    static readonly Font H1 = new("Segoe UI Semibold", 20f);
    static readonly Font H2 = new("Segoe UI Semibold", 14f);
    static readonly Font H3 = new("Segoe UI Semibold", 11.5f);
    static readonly Font Mono = new("Cascadia Mono", 9.5f);
    static readonly Font Italic = new("Segoe UI", 10f, FontStyle.Italic);
    static readonly Regex Inline = new(@"(\*\*(.+?)\*\*)|(`([^`]+)`)|(\[([^\]]+)\]\(([^)]+)\))|(\*([^*]+)\*)");

    void Render(Topic topic)
    {
        var t = Ui.T;
        _view.SuspendLayout();
        _view.Clear();
        bool inCode = false;
        var lines = topic.Markdown.Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li].TrimEnd();
            if (line.StartsWith("```")) { inCode = !inCode; if (!inCode) Append("\n", Theme.Body, t.Text); continue; }
            if (inCode) { Append("    " + line + "\n", Mono, t.Text, t.Field); continue; }
            if (line.StartsWith("# ")) { Append(line[2..] + "\n", H1, t.Text); Append("\n", Theme.Small, t.Text); continue; }
            if (line.StartsWith("## ")) { Append("\n" + line[3..] + "\n", H2, t.Accent); continue; }
            if (line.StartsWith("### ")) { Append(line[4..] + "\n", H3, t.Text); continue; }
            if (line.StartsWith("|"))
            {
                // Gather the whole table so its columns can be sized to fit.
                var rows = new List<string[]>();
                for (; li < lines.Length && lines[li].TrimStart().StartsWith("|"); li++)
                {
                    var cells = lines[li].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                    if (!cells.All(c => Regex.IsMatch(c, "^:?-{2,}:?$"))) rows.Add(cells); // skip |---|---|
                }
                li--;
                Table(rows);
                continue;
            }
            var m = Regex.Match(line, @"^(\s*)([-*]|\d+\.)\s+(.*)$");
            if (m.Success)
            {
                int indent = m.Groups[1].Value.Length / 2;
                var bullet = m.Groups[2].Value.EndsWith(".") ? m.Groups[2].Value : "•";
                _view.SelectionIndent = 18 + indent * 22;
                _view.SelectionHangingIndent = 18;
                Append(bullet + "  ", Theme.BodyBold, t.Accent);
                InlineText(m.Groups[3].Value);
                Append("\n", Theme.Body, t.Text);
                _view.SelectionIndent = 0;
                _view.SelectionHangingIndent = 0;
                continue;
            }
            if (line.Length == 0) { Append("\n", Theme.Small, t.Text); continue; }
            InlineText(line);
            Append("\n", Theme.Body, t.Text);
        }
        _view.ResumeLayout();
        // Start at the top of the page, or at the first search match. Done after layout, or the box stays at the end.
        if (!Highlight(_search.Text.Trim()))
            Later(() => { _view.Select(0, 0); SendMessage(_view.Handle, 0x115, (IntPtr)6, IntPtr.Zero); }); // WM_VSCROLL, SB_TOP
    }

    /// <summary>A table as tab-aligned rows: each column as wide as its widest cell (capped), header row in the accent colour.</summary>
    void Table(List<string[]> rows)
    {
        if (rows.Count == 0) return;
        var t = Ui.T;
        int cols = rows.Max(r => r.Length);
        var stops = new List<int>();
        int x = 0;
        for (int c = 0; c < cols - 1; c++)
        {
            int widest = rows.Where(r => c < r.Length).Max(r => TextRenderer.MeasureText(StripInline(r[c]), Theme.BodyBold).Width);
            x += Math.Min(widest, 320) + 28;
            stops.Add(x);
        }
        for (int r = 0; r < rows.Count; r++)
        {
            _view.SelectionStart = _view.TextLength;
            _view.SelectionTabs = stops.ToArray();
            _view.SelectionHangingIndent = stops.Count > 0 ? stops[^1] : 0; // wrapped text lines up under the last column
            var cells = rows[r];
            for (int c = 0; c < cells.Length; c++)
            {
                if (r == 0) Append(StripInline(cells[c]), Theme.BodyBold, t.Accent);
                else InlineText(cells[c]);
                Append(c < cells.Length - 1 ? "\t" : "\n", Theme.Body, t.Text);
            }
        }
        _view.SelectionHangingIndent = 0;
    }

    static string StripInline(string s) => Inline.Replace(s, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Success ? m.Groups[4].Value : m.Groups[6].Success ? m.Groups[6].Value : m.Groups[9].Value);

    void InlineText(string text)
    {
        var t = Ui.T;
        int pos = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index > pos) Append(text[pos..m.Index], Theme.Body, t.Text);
            if (m.Groups[2].Success) Append(m.Groups[2].Value, Theme.BodyBold, t.Text);
            else if (m.Groups[4].Success) Append(m.Groups[4].Value, Mono, t.Text, t.Field);
            else if (m.Groups[6].Success) Append($"{m.Groups[6].Value} ({m.Groups[7].Value})", Theme.Body, t.Accent);
            else if (m.Groups[9].Success) Append(m.Groups[9].Value, Italic, t.Text);
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) Append(text[pos..], Theme.Body, t.Text);
    }

    void Append(string text, Font font, Color color, Color? back = null)
    {
        _view.SelectionStart = _view.TextLength;
        _view.SelectionLength = 0;
        _view.SelectionFont = font;
        _view.SelectionColor = color;
        _view.SelectionBackColor = back ?? _view.BackColor;
        _view.AppendText(text);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    /// <summary>Run after the current layout pass — or once the window is shown, if it isn't yet.</summary>
    void Later(Action a)
    {
        if (IsHandleCreated) { BeginInvoke(a); return; }
        EventHandler? once = null;
        once = (_, _) => { Shown -= once; a(); };
        Shown += once;
    }

    /// <summary>Marks every match; returns whether there was one (and scrolls to the first).</summary>
    bool Highlight(string q)
    {
        if (q.Length < 2) return false;
        var mark = Ui.T.Dark ? Color.FromArgb(120, 95, 20) : Color.FromArgb(255, 236, 150);
        int first = -1, at = 0;
        while ((at = _view.Find(q, at, RichTextBoxFinds.None)) >= 0)
        {
            if (first < 0) first = at;
            _view.SelectionBackColor = mark;
            at += q.Length;
            if (at >= _view.TextLength) break;
        }
        if (first < 0) return false;
        Later(() => { _view.Select(first, 0); _view.ScrollToCaret(); });
        return true;
    }
}
