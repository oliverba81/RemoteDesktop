using System.Drawing.Drawing2D;

namespace RemoteDesktop.App.Ui;

/// <summary>
/// The app-wide light theme: a clean, native-looking light palette plus a few
/// stylers for the WinForms control types we use. Kept as plain helpers (no custom
/// controls) so the forms stay ordinary WinForms code and render close to the
/// standard Windows look.
/// </summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(243, 244, 246); // window
    public static readonly Color Surface = Color.White;                      // cards, lists
    public static readonly Color SurfaceHover = Color.FromArgb(233, 238, 250);
    public static readonly Color Field = Color.White;                        // text inputs
    public static readonly Color Border = Color.FromArgb(208, 213, 221);
    public static readonly Color Text = Color.FromArgb(31, 36, 48);
    public static readonly Color TextMuted = Color.FromArgb(107, 114, 128);
    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    public static readonly Color AccentDown = Color.FromArgb(23, 64, 178);
    public static readonly Color Success = Color.FromArgb(22, 143, 72);

    public static readonly Font BaseFont = new("Segoe UI", 10f);
    public static readonly Font TitleFont = new("Segoe UI Semibold", 15f);
    public static readonly Font HeadingFont = new("Segoe UI Semibold", 11f);
    public static readonly Font MonoBig = new("Consolas", 17f, FontStyle.Bold);

    /// <summary>Base colors and fonts for a form (standard light Windows look).</summary>
    public static void Apply(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = BaseFont;
    }

    public static void StylePrimary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Accent;
        b.ForeColor = Color.White;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.FlatAppearance.MouseDownBackColor = AccentDown;
        b.UseVisualStyleBackColor = false;
        b.Cursor = Cursors.Hand;
        if (b.MinimumSize.Height < 30) b.MinimumSize = new Size(b.MinimumSize.Width, 30);
        b.EnabledChanged += (_, _) =>
        {
            b.BackColor = b.Enabled ? Accent : Color.FromArgb(206, 212, 222);
            b.ForeColor = b.Enabled ? Color.White : TextMuted;
        };
    }

    public static void StyleSecondary(Button b)
    {
        // Native-looking light button: system rendering, no custom paint.
        b.FlatStyle = FlatStyle.System;
        b.UseVisualStyleBackColor = true;
        b.Cursor = Cursors.Hand;
        if (b.MinimumSize.Height < 30) b.MinimumSize = new Size(b.MinimumSize.Width, 30);
    }

    public static void StyleTextBox(TextBox t)
    {
        t.BackColor = Field;
        t.ForeColor = Text;
        t.BorderStyle = BorderStyle.FixedSingle;
    }

    public static void StyleComboBox(ComboBox c)
    {
        c.FlatStyle = FlatStyle.Standard;
        c.BackColor = Field;
        c.ForeColor = Text;
    }

    public static void StyleListView(ListView l)
    {
        // Native light ListView: no owner-draw, so the column header renders
        // normally (no unpainted filler area on the right).
        l.BackColor = Surface;
        l.ForeColor = Text;
        l.BorderStyle = BorderStyle.FixedSingle;
        l.OwnerDraw = false;
    }

    public static void StyleToolStrip(ToolStrip strip)
    {
        strip.RenderMode = ToolStripRenderMode.System;
        strip.BackColor = Background;
        strip.ForeColor = Text;
        foreach (ToolStripItem item in strip.Items)
        {
            item.ForeColor = Text;
            if (item is ToolStripComboBox combo)
            {
                combo.ComboBox.FlatStyle = FlatStyle.Standard;
                combo.ComboBox.BackColor = Field;
                combo.ComboBox.ForeColor = Text;
            }
        }
    }

    /// <summary>
    /// A card-like panel: white surface with a painted rounded border.
    /// No window Region — Regions clip child controls and cut off labels/checkboxes
    /// when fonts grow under display scaling.
    /// </summary>
    public static void StyleCard(Panel p)
    {
        p.BackColor = Surface;
        p.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Border);
            var r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
            using var path = RoundedRect(r, 8);
            e.Graphics.DrawPath(pen, path);
        };
    }

    /// <summary>Rounds a control's corners via its window region.</summary>
    public static void Round(Control c, int radius = 6)
    {
        void Update()
        {
            if (c.Width <= 0 || c.Height <= 0) return;
            using var path = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius);
            c.Region = new Region(path);
        }
        c.Resize += (_, _) => Update();
        Update();
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
