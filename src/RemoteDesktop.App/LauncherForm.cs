using RemoteDesktop.App.Ui;

namespace RemoteDesktop.App;

internal enum LauncherChoice
{
    None,
    Host,
    Viewer,
}

/// <summary>
/// Start-up chooser: one executable, two roles. The user picks whether this
/// machine is shared (Host) or controls another one (Viewer); closing the chosen
/// window brings the chooser back.
/// </summary>
internal sealed class LauncherForm : Form
{
    public LauncherChoice Choice { get; private set; } = LauncherChoice.None;

    public LauncherForm()
    {
        Text = "RemoteDesktop";
        ClientSize = new Size(620, 360);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Theme.Apply(this);

        var title = new Label
        {
            Text = "RemoteDesktop",
            Font = Theme.TitleFont,
            AutoSize = true,
            Location = new Point(28, 24),
        };
        var subtitle = new Label
        {
            Text = "What should this PC do?",
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(30, 58),
        };
        Controls.Add(title);
        Controls.Add(subtitle);

        Controls.Add(MakeCard(
            new Point(28, 96), "🖥", "Share this PC", "Host",
            "Let someone see and control this machine. Shows a session ID and " +
            "password to hand to the other side.",
            LauncherChoice.Host));

        Controls.Add(MakeCard(
            new Point(318, 96), "🎯", "Control another PC", "Viewer",
            "Connect to a shared machine — enter its session ID and password, " +
            "or pick a saved target from the address book.",
            LauncherChoice.Viewer));

        var hint = new Label
        {
            Text = "Tip: shortcuts can skip this screen with  --host  or  --viewer",
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(30, 322),
        };
        Controls.Add(hint);
    }

    private Panel MakeCard(Point location, string glyph, string heading, string tag,
        string description, LauncherChoice choice)
    {
        var card = new Panel { Location = location, Size = new Size(274, 210), Cursor = Cursors.Hand };
        Theme.StyleCard(card);

        var icon = new Label
        {
            Text = glyph,
            Font = new Font("Segoe UI Emoji", 26f),
            AutoSize = true,
            Location = new Point(18, 16),
            BackColor = Color.Transparent,
        };
        var head = new Label
        {
            Text = heading,
            Font = Theme.HeadingFont,
            AutoSize = true,
            Location = new Point(20, 76),
            BackColor = Color.Transparent,
        };
        var role = new Label
        {
            Text = tag.ToUpperInvariant(),
            ForeColor = Theme.Accent,
            Font = new Font("Segoe UI Semibold", 8.5f),
            AutoSize = true,
            Location = new Point(21, 100),
            BackColor = Color.Transparent,
        };
        var desc = new Label
        {
            Text = description,
            ForeColor = Theme.TextMuted,
            Location = new Point(20, 124),
            Size = new Size(234, 76),
            BackColor = Color.Transparent,
        };

        card.Controls.Add(icon);
        card.Controls.Add(head);
        card.Controls.Add(role);
        card.Controls.Add(desc);

        void Pick(object? s, EventArgs e)
        {
            Choice = choice;
            Close();
        }
        void Hover(object? s, EventArgs e) => card.BackColor = Theme.SurfaceHover;
        void Leave(object? s, EventArgs e) => card.BackColor = Theme.Surface;

        card.Click += Pick;
        card.MouseEnter += Hover;
        card.MouseLeave += Leave;
        foreach (Control child in card.Controls)
        {
            child.Click += Pick;
            child.MouseEnter += Hover;
        }
        return card;
    }
}
