using System.Drawing.Drawing2D;

namespace MixerNet.UI;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(11, 11, 13);
    public static readonly Color Card = Color.FromArgb(21, 21, 24);
    public static readonly Color Line = Color.FromArgb(38, 38, 43);
    public static readonly Color Control = Color.FromArgb(29, 29, 34);
    public static readonly Color Hover = Color.FromArgb(40, 40, 47);
    public static readonly Color Text = Color.FromArgb(232, 232, 236);
    public static readonly Color Dim = Color.FromArgb(141, 141, 150);
    public static readonly Color Accent = Color.FromArgb(77, 163, 255);
    public static readonly Color Danger = Color.FromArgb(255, 93, 93);
    public static readonly Color Ok = Color.FromArgb(74, 222, 128);
    public static readonly Color Warn = Color.FromArgb(250, 204, 21);
    public static readonly Color OnAccent = Color.FromArgb(8, 16, 30);
}

/// <summary>Bordered dark panel.</summary>
sealed class Card : Panel
{
    public Card()
    {
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var p = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>Owner-drawn dark drop-down list.</summary>
sealed class DarkCombo : ComboBox
{
    public DarkCombo()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        BackColor = Theme.Control;
        ForeColor = Theme.Text;
        ItemHeight = 20;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool sel = (e.State & DrawItemState.Selected) != 0 && !edit;
        using (var bg = new SolidBrush(sel ? Theme.Accent : Theme.Control))
            e.Graphics.FillRectangle(bg, e.Bounds);
        var r = e.Bounds;
        r.Inflate(-4, 0);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, r,
            sel ? Theme.OnAccent : Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (edit && Focused)
        {
            using var p = new Pen(Theme.Accent);
            e.Graphics.DrawRectangle(p, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
        }
    }
}

/// <summary>
/// Keyboard and mouse accessible slider. Arrows ±1, PageUp/PageDown ±10, Home/End,
/// mouse wheel, double-click resets to default.
/// </summary>
sealed class DarkSlider : Control
{
    const int Pad = 9;
    int _value;
    bool _drag;

    public int Minimum { get; }
    public int Maximum { get; }
    public int DefaultValue { get; }
    public bool Bipolar { get; }
    public event EventHandler? ValueChanged;

    public DarkSlider(int min, int max, int defaultValue, bool bipolar = false)
    {
        Minimum = min;
        Maximum = max;
        DefaultValue = defaultValue;
        Bipolar = bipolar;
        _value = defaultValue;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardDoubleClick |
                 ControlStyles.SupportsTransparentBackColor, true);
        TabStop = true;
        Height = 22;
        BackColor = Theme.Card;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Slider;
        AccessibleDescription = defaultValue.ToString();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, Minimum, Maximum);
            if (v == _value) return;
            _value = v;
            AccessibleDescription = v.ToString();
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    int XFor(int v) => Pad + (int)Math.Round((v - Minimum) / (double)(Maximum - Minimum) * (Width - 2 * Pad));

    int ValueAt(int x) =>
        Minimum + (int)Math.Round((x - Pad) / (double)Math.Max(1, Width - 2 * Pad) * (Maximum - Minimum));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cy = Height / 2;
        int x0 = Bipolar ? XFor(0) : Pad;
        int xv = XFor(_value);

        using (var track = new Pen(Theme.Line, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(track, Pad, cy, Width - Pad, cy);

        if (Bipolar)
        {
            using var tick = new Pen(Theme.Dim, 1);
            g.DrawLine(tick, x0, cy - 6, x0, cy + 6);
        }

        if (xv != x0)
        {
            using var fill = new Pen(Enabled ? Theme.Accent : Color.FromArgb(70, 70, 78), 4)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(fill, Math.Min(x0, xv), cy, Math.Max(x0, xv), cy);
        }

        const int r = 7;
        using (var thumb = new SolidBrush(Enabled ? Theme.Text : Color.FromArgb(80, 80, 88)))
            g.FillEllipse(thumb, xv - r, cy - r, 2 * r, 2 * r);

        if (Focused)
        {
            using var ring = new Pen(Theme.Accent, 2);
            g.DrawEllipse(ring, xv - r - 2, cy - r - 2, 2 * r + 4, 2 * r + 4);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _drag = true;
        Capture = true;
        Value = ValueAt(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag) Value = ValueAt(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = false;
        Capture = false;
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        Value = DefaultValue;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Value += e.Delta > 0 ? 2 : -2;
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.Down: Value -= 1; break;
            case Keys.Right: case Keys.Up: Value += 1; break;
            case Keys.PageDown: Value -= 10; break;
            case Keys.PageUp: Value += 10; break;
            case Keys.Home: Value = Minimum; break;
            case Keys.End: Value = Maximum; break;
            case Keys.Delete: case Keys.Back: Value = DefaultValue; break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

/// <summary>Horizontal peak meter, -60..0 dBFS.</summary>
sealed class LevelMeter : Control
{
    float _level;

    public LevelMeter()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 8;
        TabStop = false;
    }

    /// <summary>Linear peak 0..1.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float Level
    {
        get => _level;
        set
        {
            if (Math.Abs(value - _level) < 1e-4f) return;
            _level = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.FromArgb(14, 14, 16));
        float pct = _level <= 0.0005f ? 0f : Math.Clamp((20f * MathF.Log10(_level) + 60f) / 60f, 0f, 1f);
        int w = (int)(pct * (Width - 2));
        if (w > 0 && Width > 2 && Height > 2)
        {
            var full = new Rectangle(1, 1, Width - 2, Height - 2);
            using var br = new LinearGradientBrush(full, Theme.Ok, Theme.Danger, 0f)
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Theme.Ok, Theme.Ok, Theme.Warn, Theme.Danger },
                    Positions = new[] { 0f, 0.7f, 0.85f, 1f },
                },
            };
            g.FillRectangle(br, 1, 1, w, Height - 2);
        }
        using var p = new Pen(Theme.Line);
        g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }
}
