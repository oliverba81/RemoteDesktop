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
        Width = 520;
        Height = 320;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(20),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var intro = new Label
        {
            Text = "What should this PC do?",
            Font = new Font(Font.FontFamily, 12, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(3, 0, 3, 16),
        };
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 2);

        var hostButton = new Button
        {
            Text = "Share this PC\n(Host)",
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 0, 12, 0),
        };
        hostButton.Click += (_, _) => Pick(LauncherChoice.Host);

        var viewerButton = new Button
        {
            Text = "Control another PC\n(Viewer)",
            Dock = DockStyle.Fill,
            Margin = new Padding(12, 0, 3, 0),
        };
        viewerButton.Click += (_, _) => Pick(LauncherChoice.Viewer);

        layout.Controls.Add(hostButton, 0, 1);
        layout.Controls.Add(viewerButton, 1, 1);

        var hostDesc = new Label
        {
            Text = "Let someone see and control this machine. Shows a session ID " +
                   "and password to hand to the other side.",
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 10, 12, 0),
        };
        var viewerDesc = new Label
        {
            Text = "Connect to a shared machine. Enter relay address, session ID " +
                   "and password, or pick a saved target from the address book.",
            Dock = DockStyle.Fill,
            Margin = new Padding(12, 10, 3, 0),
        };
        layout.Controls.Add(hostDesc, 0, 2);
        layout.Controls.Add(viewerDesc, 1, 2);

        Controls.Add(layout);
        AcceptButton = viewerButton;
    }

    private void Pick(LauncherChoice choice)
    {
        Choice = choice;
        Close();
    }
}
