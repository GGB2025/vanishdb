using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal static class InterfaceTheme
{
    public static readonly Color Page = Color.FromArgb(244, 246, 252);
    public static readonly Color Surface = Color.White;
    public static readonly Color Canvas = Color.FromArgb(237, 240, 249);
    public static readonly Color Ink = Color.FromArgb(26, 33, 54);
    public static readonly Color Muted = Color.FromArgb(87, 98, 123);
    public static readonly Color Border = Color.FromArgb(218, 224, 238);
    public static readonly Color Accent = Color.FromArgb(69, 70, 199);
    public static readonly Color AccentLight = Color.FromArgb(235, 235, 255);
    public static readonly Font Body = new Font("Microsoft YaHei UI", 10f);
    public static readonly Font Small = new Font("Microsoft YaHei UI", 9f);
    public static readonly Font Section = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
    public static readonly Font Brand = new Font("Microsoft YaHei UI", 18f, FontStyle.Bold);

    public static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(2 * radius, Math.Min(rect.Width, rect.Height));
        if (d <= 1)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Shape(Graphics g, RectangleF bounds, float radius, Color fill, Color border)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using (var path = Rounded(bounds, radius))
        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
            if (border.A != 0)
                using (var pen = new Pen(border)) g.DrawPath(pen, path);
        }
    }
}

internal sealed class SurfacePanel : Panel
{
    public SurfacePanel()
    {
        BackColor = InterfaceTheme.Page;
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        InterfaceTheme.Shape(e.Graphics, new RectangleF(.5f, .5f, Width - 1, Height - 1),
            17 * e.Graphics.DpiX / 96f, InterfaceTheme.Surface, InterfaceTheme.Border);
    }
}

internal enum Glyph { None, Image, Open, Sparkle, Select, Save, Fit, Zoom, Upload }
internal enum ButtonStyle { Quiet, Outline, Primary }

internal sealed class InterfaceButton : Button
{
    private readonly Glyph glyph;
    private readonly ButtonStyle style;
    private bool hovered, pressed, active;

    public InterfaceButton(Glyph glyph, ButtonStyle style)
    {
        this.glyph = glyph;
        this.style = style;
        Font = InterfaceTheme.Body;
        Cursor = Cursors.Hand;
        BackColor = InterfaceTheme.Surface;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    public bool Active
    {
        get { return active; }
        set { if (active != value) { active = value; Invalidate(); } }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float scale = g.DpiX / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = BackColor, border = Color.Transparent, ink = InterfaceTheme.Ink;
        if (!Enabled)
        {
            fill = style == ButtonStyle.Quiet ? BackColor : Color.FromArgb(245, 247, 251);
            border = style == ButtonStyle.Quiet ? Color.Transparent : Color.FromArgb(227, 232, 242);
            ink = Color.FromArgb(104, 116, 140);
        }
        else if (style == ButtonStyle.Primary)
        {
            fill = pressed ? Color.FromArgb(50, 49, 156) : hovered ? Color.FromArgb(60, 61, 180) : InterfaceTheme.Accent;
            ink = Color.White;
        }
        else if (active || hovered || pressed)
        {
            fill = pressed ? Color.FromArgb(222, 223, 252) : InterfaceTheme.AccentLight;
            ink = InterfaceTheme.Accent;
            if (style == ButtonStyle.Outline) border = Color.FromArgb(192, 196, 243);
        }
        else if (style == ButtonStyle.Outline)
        {
            border = InterfaceTheme.Border;
        }

        InterfaceTheme.Shape(g, new RectangleF(1, 1, Width - 2, Height - 2), 9 * scale, fill, border);
        if (Focused && ShowFocusCues)
        {
            using (var path = InterfaceTheme.Rounded(new RectangleF(3, 3, Width - 6, Height - 6), 7 * scale))
            using (var pen = new Pen(style == ButtonStyle.Primary ? Color.White : InterfaceTheme.Accent, Math.Max(1, scale)))
                g.DrawPath(pen, path);
        }

        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        int textWidth = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags).Width;
        int iconSize = glyph == Glyph.None ? 0 : (int)Math.Round(17 * scale);
        int gap = glyph == Glyph.None ? 0 : (int)Math.Round(8 * scale);
        int left = Math.Max((int)(7 * scale), (Width - textWidth - iconSize - gap) / 2);
        if (iconSize > 0)
            InterfaceGlyphs.Draw(g, glyph, new RectangleF(left, (Height - iconSize) / 2f, iconSize, iconSize), ink);
        int textLeft = left + iconSize + gap;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(textLeft, 0, Math.Max(1, Width - textLeft - 3), Height),
            ink, flags | TextFormatFlags.EndEllipsis);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = e.Button == MouseButtons.Left; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); pressed = false; Invalidate(); }
}

internal sealed class InterfaceBadge : Label
{
    public InterfaceBadge()
    {
        Font = InterfaceTheme.Small;
        TextAlign = ContentAlignment.MiddleCenter;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public Color Tint { get; set; }
    public bool HasDot { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        float scale = e.Graphics.DpiX / 96f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        InterfaceTheme.Shape(e.Graphics, new RectangleF(0, 0, Width, Height), Height / 2f, Tint, Color.Transparent);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        if (!HasDot)
        {
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, flags);
            return;
        }
        int size = (int)Math.Round(6 * scale), gap = (int)Math.Round(6 * scale);
        int labelWidth = TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty, flags).Width;
        int left = (Width - size - gap - labelWidth) / 2;
        using (var brush = new SolidBrush(ForeColor))
            e.Graphics.FillEllipse(brush, left, (Height - size) / 2f, size, size);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(left + size + gap, 0, labelWidth + 1, Height),
            ForeColor, flags);
    }
}

internal sealed class BrandMark : Control
{
    public BrandMark()
    {
        BackColor = InterfaceTheme.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        Draw(e.Graphics, new RectangleF(0, 0, Width, Height));
    }

    internal static void Draw(Graphics g, RectangleF bounds)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = InterfaceTheme.Rounded(bounds, bounds.Width * .29f))
        using (var fill = new LinearGradientBrush(bounds, Color.FromArgb(108, 96, 235), Color.FromArgb(56, 58, 187), 55))
            g.FillPath(fill, path);
        float inset = bounds.Width * .22f;
        InterfaceGlyphs.Draw(g, Glyph.Image, RectangleF.Inflate(bounds, -inset, -inset), Color.White);
    }
}

internal static class InterfaceGlyphs
{
    public static void Draw(Graphics g, Glyph glyph, RectangleF rect, Color color)
    {
        GraphicsState saved = g.Save();
        try
        {
            g.TranslateTransform(rect.X, rect.Y);
            g.ScaleTransform(rect.Width / 24, rect.Height / 24);
            using (var pen = new Pen(color, 1.65f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (glyph)
                {
                    case Glyph.Open:
                        Line(g, pen, 3, 19, 3, 6, 9, 6, 11, 8, 20, 8, 20, 11);
                        Line(g, pen, 3, 19, 6, 11, 22, 11, 19, 19, 3, 19);
                        break;
                    case Glyph.Sparkle:
                        Star(g, pen, 10, 13, 7);
                        Star(g, pen, 19, 5, 3);
                        break;
                    case Glyph.Select:
                        Line(g, pen, 3, 8, 3, 3, 8, 3);
                        Line(g, pen, 16, 3, 21, 3, 21, 8);
                        Line(g, pen, 21, 16, 21, 21, 16, 21);
                        Line(g, pen, 8, 21, 3, 21, 3, 16);
                        g.DrawRectangle(pen, 8, 8, 8, 8);
                        break;
                    case Glyph.Save:
                        Line(g, pen, 12, 3, 12, 15);
                        Line(g, pen, 7, 10, 12, 15, 17, 10);
                        Line(g, pen, 4, 15, 4, 20, 20, 20, 20, 15);
                        break;
                    case Glyph.Fit:
                        Line(g, pen, 3, 8, 3, 3, 8, 3);
                        Line(g, pen, 16, 3, 21, 3, 21, 8);
                        Line(g, pen, 21, 16, 21, 21, 16, 21);
                        Line(g, pen, 8, 21, 3, 21, 3, 16);
                        g.DrawRectangle(pen, 7, 9, 10, 6);
                        break;
                    case Glyph.Zoom:
                        g.DrawEllipse(pen, 3, 3, 13, 13);
                        Line(g, pen, 14, 14, 21, 21);
                        Line(g, pen, 6.5f, 9.5f, 12.5f, 9.5f);
                        Line(g, pen, 9.5f, 6.5f, 9.5f, 12.5f);
                        break;
                    case Glyph.Upload:
                        Line(g, pen, 12, 16, 12, 4);
                        Line(g, pen, 7, 9, 12, 4, 17, 9);
                        Line(g, pen, 4, 14, 4, 20, 20, 20, 20, 14);
                        break;
                    case Glyph.Image:
                        using (var path = InterfaceTheme.Rounded(new RectangleF(2, 4, 18, 16), 2.5f)) g.DrawPath(pen, path);
                        g.DrawEllipse(pen, 6, 7, 3, 3);
                        Line(g, pen, 3, 17, 8, 13, 12, 16, 16, 12, 19, 15);
                        Star(g, pen, 20, 4, 3);
                        break;
                }
            }
        }
        finally { g.Restore(saved); }
    }

    private static void Star(Graphics g, Pen pen, float x, float y, float r)
    {
        float k = r * .30f;
        Line(g, pen, x, y - r, x + k, y - k, x + r, y, x + k, y + k,
            x, y + r, x - k, y + k, x - r, y, x - k, y - k, x, y - r);
    }

    private static void Line(Graphics g, Pen pen, params float[] points)
    {
        var line = new PointF[points.Length / 2];
        for (int i = 0; i < line.Length; i++) line[i] = new PointF(points[2 * i], points[2 * i + 1]);
        g.DrawLines(pen, line);
    }
}
