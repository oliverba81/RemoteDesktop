using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RemoteDesktop.App.Ui;

/// <summary>
/// The app-wide dark theme: palette, fonts and stylers for the handful of WinForms
/// control types we use. Kept as plain helpers (no custom controls) so the forms
/// stay ordinary WinForms code.
/// </summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(24, 26, 31);
    public static readonly Color Surface = Color.FromArgb(33, 36, 43);
    public static readonly Color SurfaceHover = Color.FromArgb(44, 48, 58);
    public static readonly Color Field = Color.FromArgb(17, 18, 22);
    public static readonly Color Border = Color.FromArgb(58, 63, 74);
    public static readonly Color Text = Color.FromArgb(233, 235, 240);
    public static readonly Color TextMuted = Color.FromArgb(148, 155, 168);
    public static readonly Color Accent = Color.FromArgb(64, 128, 255);
    public static readonly Color AccentHover = Color.FromArgb(92, 148, 255);
    public static readonly Color AccentDown = Color.FromArgb(48, 104, 220);
    public static readonly Color Success = Color.FromArgb(70, 195, 100);

    public static readonly Font BaseFont = new("Segoe UI", 10f);
    public static readonly Font TitleFont = new("Segoe UI Semibold", 15f);
    public static readonly Font HeadingFont = new("Segoe UI Semibold", 11f);
    public static readonly Font MonoBig = new("Consolas", 17f, FontStyle.Bold);

    /// <summary>Base colors/fonts for a form, plus a dark title bar on Windows 10+.</summary>
    public static void Apply(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = BaseFont;
        form.HandleCreated += (_, _) => EnableDarkTitleBar(form.Handle);
        if (form.IsHandleCreated) EnableDarkTitleBar(form.Handle);
    }

    public static void StylePrimary(Button b)
    {
        StyleButtonBase(b, Accent, Color.White, AccentHover, AccentDown);
        b.FlatAppearance.BorderSize = 0;
    }

    public static void StyleSecondary(Button b)
    {
        StyleButtonBase(b, Surface, Text, SurfaceHover, Background);
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Border;
    }

    private static void StyleButtonBase(Button b, Color back, Color fore, Color hover, Color down)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = back;
        b.ForeColor = fore;
        b.FlatAppearance.MouseOverBackColor = hover;
        b.FlatAppearance.MouseDownBackColor = down;
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
        if (b.MinimumSize.Height < 32) b.MinimumSize = new Size(b.MinimumSize.Width, 32);
        Round(b);
        // A disabled flat button keeps its BackColor; dim it so the state is visible.
        b.EnabledChanged += (_, _) =>
        {
            b.BackColor = b.Enabled ? back : Surface;
            b.ForeColor = b.Enabled ? fore : TextMuted;
        };
    }

    public static void StyleTextBox(TextBox t)
    {
        t.BackColor = Field;
        t.ForeColor = Text;
        t.BorderStyle = BorderStyle.FixedSingle;
    }

    public static void StyleComboBox(ComboBox c)
    {
        c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Field;
        c.ForeColor = Text;
    }

    public static void StyleListView(ListView l)
    {
        l.BackColor = Surface;
        l.ForeColor = Text;
        l.BorderStyle = BorderStyle.None;
        // Default column headers stay light; draw them to match the theme.
        l.OwnerDraw = true;
        l.DrawColumnHeader += (_, e) =>
        {
            using var back = new SolidBrush(Background);
            e.Graphics.FillRectangle(back, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", HeadingFont,
                new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height),
                TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };
        l.DrawItem += (_, e) => e.DrawDefault = true;
        l.DrawSubItem += (_, e) => e.DrawDefault = true;
    }

    public static void StyleToolStrip(ToolStrip strip)
    {
        strip.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()) { RoundedEdges = false };
        strip.BackColor = Surface;
        strip.ForeColor = Text;
        foreach (ToolStripItem item in strip.Items)
        {
            item.ForeColor = Text;
            if (item is ToolStripComboBox combo)
            {
                combo.ComboBox.FlatStyle = FlatStyle.Flat;
                combo.ComboBox.BackColor = Field;
                combo.ComboBox.ForeColor = Text;
            }
        }
    }

    /// <summary>A card-like panel with the surface color and a subtle border.</summary>
    public static void StyleCard(Panel p)
    {
        p.BackColor = Surface;
        p.Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
        Round(p, 8);
    }

    /// <summary>Rounds a control's corners via its window region.</summary>
    public static void Round(Control c, int radius = 6)
    {
        void Update()
        {
            if (c.Width <= 0 || c.Height <= 0) return;
            int d = radius * 2;
            var path = new GraphicsPath();
            var r = new Rectangle(0, 0, c.Width, c.Height);
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            c.Region = new Region(path);
        }
        c.Resize += (_, _) => Update();
        Update();
    }

    // ---- dark title bar (Windows 10 1809+; silently ignored elsewhere) ----

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private static void EnableDarkTitleBar(nint handle)
    {
        int on = 1;
        try
        {
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (19 on pre-20H1 builds).
            if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
        }
        catch (DllNotFoundException) { /* very old Windows */ }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Surface;
        public override Color ToolStripGradientMiddle => Surface;
        public override Color ToolStripGradientEnd => Surface;
        public override Color ToolStripBorder => Border;
        public override Color ButtonSelectedHighlight => SurfaceHover;
        public override Color ButtonSelectedGradientBegin => SurfaceHover;
        public override Color ButtonSelectedGradientMiddle => SurfaceHover;
        public override Color ButtonSelectedGradientEnd => SurfaceHover;
        public override Color ButtonSelectedBorder => Border;
        public override Color ButtonPressedGradientBegin => Background;
        public override Color ButtonPressedGradientMiddle => Background;
        public override Color ButtonPressedGradientEnd => Background;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
        public override Color StatusStripGradientBegin => Surface;
        public override Color StatusStripGradientEnd => Surface;
        public override Color MenuItemSelected => SurfaceHover;
        public override Color MenuItemBorder => Border;
        public override Color MenuBorder => Border;
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
    }
}
