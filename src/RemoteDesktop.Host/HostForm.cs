using System.Text;
using RemoteDesktop.Shared.Util;

namespace RemoteDesktop.Host;

/// <summary>
/// The host window (controlled PC). Shows the freshly generated session id and
/// password that a viewer must enter, and the live connection status. The window
/// is the consent surface: a person at this machine deliberately starts it and
/// reads out the credentials — nobody connects unattended in v1.
/// </summary>
public sealed class HostForm : Form
{
    private readonly HostSettings _settings = HostSettings.Load();
    private readonly TextBox _relayBox = new();
    private readonly Label _sessionIdLabel = new();
    private readonly Label _passwordLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();

    private string _sessionId = "";
    private string _password = "";
    private CancellationTokenSource? _cts;
    private HostSession? _session;

    public HostForm()
    {
        Text = "RemoteDesktop — Host";
        Width = 460;
        Height = 340;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _relayBox.Text = string.IsNullOrEmpty(_settings.RelayHost)
            ? ""
            : $"{_settings.RelayHost}:{_settings.RelayPort}";
        _relayBox.Dock = DockStyle.Fill;
        _relayBox.PlaceholderText = "relay.example.synology.me:9945";

        _sessionIdLabel.Font = new Font(FontFamily.GenericMonospace, 16, FontStyle.Bold);
        _passwordLabel.Font = new Font(FontFamily.GenericMonospace, 16, FontStyle.Bold);
        _statusLabel.AutoSize = true;
        _statusLabel.Text = "Idle.";

        _startButton.Text = "Start session";
        _startButton.Click += (_, _) => Start();
        _stopButton.Text = "Stop";
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => Stop();

        var regen = new Button { Text = "New credentials" };
        regen.Click += (_, _) => Regenerate();

        layout.Controls.Add(new Label { Text = "Relay address:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
        layout.Controls.Add(_relayBox, 1, 0);
        layout.Controls.Add(new Label { Text = "Session ID:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
        layout.Controls.Add(_sessionIdLabel, 1, 1);
        layout.Controls.Add(new Label { Text = "Password:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 2);
        layout.Controls.Add(_passwordLabel, 1, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(_startButton);
        buttons.Controls.Add(_stopButton);
        buttons.Controls.Add(regen);
        layout.Controls.Add(new Label(), 0, 3);
        layout.Controls.Add(buttons, 1, 3);

        layout.Controls.Add(new Label { Text = "Status:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 4);
        layout.Controls.Add(_statusLabel, 1, 4);

        Controls.Add(layout);
        Regenerate();
    }

    private void Regenerate()
    {
        _sessionId = SessionId.NewNumericId();
        _password = SessionId.NewPassword();
        _sessionIdLabel.Text = SessionId.FormatNumericForDisplay(_sessionId);
        _passwordLabel.Text = _password;
    }

    private void Start()
    {
        if (!TryParseRelay(out string host, out int port))
        {
            MessageBox.Show(this, "Enter the relay address as host:port.", "RemoteDesktop");
            return;
        }
        _settings.RelayHost = host;
        _settings.RelayPort = port;
        _settings.Save();

        // A stable per-install owner tag lets this host reclaim its id on reconnect.
        byte[] ownerTag = Encoding.UTF8.GetBytes(Environment.MachineName + "|" + _sessionId);

        _cts = new CancellationTokenSource();
        _session = new HostSession(host, port, _sessionId, _password, ownerTag);
        _session.Status += s => BeginInvoke(() => _statusLabel.Text = s);

        _ = _session.RunAsync(_cts.Token);
        _startButton.Enabled = false;
        _stopButton.Enabled = true;
        _relayBox.Enabled = false;
    }

    private void Stop()
    {
        _cts?.Cancel();
        _startButton.Enabled = true;
        _stopButton.Enabled = false;
        _relayBox.Enabled = true;
        _statusLabel.Text = "Stopped.";
    }

    private bool TryParseRelay(out string host, out int port)
    {
        host = "";
        port = 9945;
        string text = _relayBox.Text.Trim();
        if (text.Length == 0) return false;
        int colon = text.LastIndexOf(':');
        if (colon < 0) { host = text; return true; }
        host = text[..colon];
        return int.TryParse(text[(colon + 1)..], out port) && port is > 0 and < 65536;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosing(e);
    }
}
