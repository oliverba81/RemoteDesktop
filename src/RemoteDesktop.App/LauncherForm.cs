using RemoteDesktop.App.Ui;
using RemoteDesktop.App.Update;

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
        // Layout is authored at 96 DPI; PerMonitorV2 then scales ClientSize and
        // control bounds so emoji metrics / wrapped text still fit at 125–150%.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, 440);
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
            new Point(28, 100), "🖥", "Share this PC", "Host",
            "Let someone see and control this machine. Shows a session ID and " +
            "password to hand to the other side.",
            LauncherChoice.Host));

        Controls.Add(MakeCard(
            new Point(354, 100), "🎯", "Control another PC", "Viewer",
            "Connect to a shared machine — enter its session ID and password, " +
            "or pick a saved target from the address book.",
            LauncherChoice.Viewer));

        var hint = new Label
        {
            Text = "Tip: shortcuts can skip this screen with --host or --viewer",
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            Location = new Point(30, 390),
        };
        Controls.Add(hint);

        var version = new Label
        {
            Text = $"v{UpdateService.CurrentVersion.ToString(3)}",
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        Controls.Add(version);
        version.Location = new Point(ClientSize.Width - version.Width - 28, 24);

        var check = new LinkLabel
        {
            Text = "Check for updates",
            AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.AccentHover,
        };
        check.LinkClicked += (_, _) => _ = CheckForUpdatesAsync(interactive: true);
        Controls.Add(check);
        check.Location = new Point(ClientSize.Width - check.Width - 28, 390);

        Shown += (_, _) => _ = CheckForUpdatesAsync(interactive: false);
    }

    /// <summary>
    /// Checks GitHub for a newer release. On start-up (interactive: false) it stays
    /// silent unless an update exists; from the link it also reports "up to date"
    /// and any error.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool interactive)
    {
        UpdateService.UpdateInfo? update;
        try
        {
            update = await UpdateService.CheckAsync();
        }
        catch (Exception ex)
        {
            if (interactive)
                MessageBox.Show(this, $"Could not check for updates:\n{ex.Message}", "RemoteDesktop");
            return;
        }

        if (update is null)
        {
            if (interactive)
                MessageBox.Show(this, "You are running the latest version.", "RemoteDesktop");
            return;
        }

        string notes = update.Notes.Length > 600 ? update.Notes[..600] + "…" : update.Notes;
        DialogResult r = MessageBox.Show(this,
            $"Version {update.Version.ToString(3)} is available (you have {UpdateService.CurrentVersion.ToString(3)}).\n\n" +
            $"{notes}\n\nDownload and install now? The app will restart.",
            "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r != DialogResult.Yes) return;

        try
        {
            if (await UpdateService.DownloadAndApplyAsync(update))
                Application.Exit(); // updater swaps the files and relaunches
            else
                MessageBox.Show(this, "The update could not be downloaded.", "RemoteDesktop");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Update failed:\n{ex.Message}", "RemoteDesktop");
        }
    }

    private Panel MakeCard(Point location, string glyph, string heading, string tag,
        string description, LauncherChoice choice)
    {
        var card = new Panel { Location = location, Size = new Size(298, 260), Cursor = Cursors.Hand };
        Theme.StyleCard(card);

        // Fixed icon box: Segoe UI Emoji + AutoSize reports a huge height and the
        // label sits at z-order 0 (top), so it used to paint over the heading.
        var icon = new Label
        {
            Text = glyph,
            Font = new Font("Segoe UI Emoji", 28f),
            AutoSize = false,
            Size = new Size(52, 52),
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(18, 16),
            BackColor = Color.Transparent,
        };
        var head = new Label
        {
            Text = heading,
            Font = Theme.HeadingFont,
            AutoSize = true,
            Location = new Point(20, 78),
            BackColor = Color.Transparent,
        };
        var role = new Label
        {
            Text = tag.ToUpperInvariant(),
            ForeColor = Theme.Accent,
            Font = new Font("Segoe UI Semibold", 8.5f),
            AutoSize = true,
            Location = new Point(21, 104),
            BackColor = Color.Transparent,
        };
        var desc = new Label
        {
            Text = description,
            ForeColor = Theme.TextMuted,
            Location = new Point(20, 130),
            Size = new Size(258, 110),
            BackColor = Color.Transparent,
        };

        // Text first so it wins the z-order; icon goes behind afterwards.
        card.Controls.Add(head);
        card.Controls.Add(role);
        card.Controls.Add(desc);
        card.Controls.Add(icon);
        icon.SendToBack();

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
