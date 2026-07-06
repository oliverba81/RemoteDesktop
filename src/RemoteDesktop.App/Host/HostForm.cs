using System.Text;
using Microsoft.Win32;
using RemoteDesktop.App.Ui;
using RemoteDesktop.Shared.Util;
using RemoteDesktop.Windows;

namespace RemoteDesktop.Host;

/// <summary>
/// The host window (controlled PC). Two access modes:
///
/// Attended — fresh session id + password per start; a person at this machine
/// reads them out to the viewer (the consent surface).
///
/// Unattended — a stable device id plus a permanent device password (DPAPI at
/// rest). Combined with "start with Windows" and the tray icon, this machine can
/// be reached without anyone sitting in front of it.
/// </summary>
public sealed class HostForm : Form
{
    private readonly HostSettings _settings = HostSettings.Load();
    private readonly TextBox _relayBox = new();
    private readonly ComboBox _monitorBox = new();
    private readonly CheckBox _unattendedCheck = new();
    private readonly TextBox _devicePasswordBox = new();
    private readonly Label _sessionIdLabel = new();
    private readonly Label _passwordLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _newButton = new();
    private readonly Button _sendFileButton = new();
    private readonly CheckBox _autostartCheck = new();
    private readonly NotifyIcon _tray = new();
    private readonly ClipboardMonitor _clipboard = new();

    private readonly bool _autoStart;
    private string _sessionId = "";
    private string _password = "";
    private CancellationTokenSource? _cts;
    private HostSession? _session;
    private bool _running;
    private bool _exiting;
    private bool _trayHintShown;

    public HostForm(bool autoStart = false)
    {
        _autoStart = autoStart;
        Text = "RemoteDesktop — Host";
        ClientSize = new Size(520, 484);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Theme.Apply(this);

        int y = 20;

        // ---- connection card ----
        var connCard = MakeCard("Connection", ref y, 128);
        AddRow(connCard, 40, "Relay address", _relayBox);
        Theme.StyleTextBox(_relayBox);
        _relayBox.PlaceholderText = "name.synology.me (port optional, default 9945)";
        _relayBox.Text = string.IsNullOrEmpty(_settings.RelayHost)
            ? ""
            : $"{_settings.RelayHost}:{_settings.RelayPort}";

        AddRow(connCard, 80, "Monitor", _monitorBox);
        Theme.StyleComboBox(_monitorBox);
        _monitorBox.DropDownStyle = ComboBoxStyle.DropDownList;
        Screen[] screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            _monitorBox.Items.Add($"Display {i + 1} — {b.Width}×{b.Height}{(screens[i].Primary ? " (primary)" : "")}");
        }
        _monitorBox.SelectedIndex = Math.Clamp(_settings.MonitorIndex, 0, screens.Length - 1);

        // ---- access card ----
        var accessCard = MakeCard("Access", ref y, 172);

        _unattendedCheck.Text = "Unattended access (fixed ID + device password)";
        _unattendedCheck.AutoSize = true;
        _unattendedCheck.Location = new Point(16, 38);
        _unattendedCheck.Checked = _settings.UnattendedEnabled;
        _unattendedCheck.CheckedChanged += (_, _) => ApplyMode();
        accessCard.Controls.Add(_unattendedCheck);

        AddRow(accessCard, 66, "Device password", _devicePasswordBox);
        Theme.StyleTextBox(_devicePasswordBox);
        _devicePasswordBox.UseSystemPasswordChar = true;

        var sessionCaption = new Label
        {
            Text = "Session ID", ForeColor = Theme.TextMuted, AutoSize = true, Location = new Point(16, 102),
        };
        _sessionIdLabel.Font = Theme.MonoBig;
        _sessionIdLabel.ForeColor = Theme.Accent;
        _sessionIdLabel.AutoSize = true;
        _sessionIdLabel.Location = new Point(14, 122);

        var passwordCaption = new Label
        {
            Text = "Password", ForeColor = Theme.TextMuted, AutoSize = true, Location = new Point(280, 102),
        };
        _passwordLabel.Font = Theme.MonoBig;
        _passwordLabel.AutoSize = true;
        _passwordLabel.Location = new Point(278, 122);

        accessCard.Controls.Add(sessionCaption);
        accessCard.Controls.Add(_sessionIdLabel);
        accessCard.Controls.Add(passwordCaption);
        accessCard.Controls.Add(_passwordLabel);

        // ---- buttons ----
        _startButton.Text = "Start session";
        _startButton.Size = new Size(130, 36);
        _startButton.Location = new Point(20, y);
        Theme.StylePrimary(_startButton);
        _startButton.Click += (_, _) => Start();

        _stopButton.Text = "Stop";
        _stopButton.Size = new Size(80, 36);
        _stopButton.Location = new Point(158, y);
        Theme.StyleSecondary(_stopButton);
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => Stop();

        _newButton.Text = "New credentials";
        _newButton.Size = new Size(140, 36);
        _newButton.Location = new Point(246, y);
        Theme.StyleSecondary(_newButton);
        _newButton.Click += (_, _) => { Regenerate(); ApplyMode(); };

        _sendFileButton.Text = "Send file…";
        _sendFileButton.Size = new Size(106, 36);
        _sendFileButton.Location = new Point(394, y);
        Theme.StyleSecondary(_sendFileButton);
        _sendFileButton.Enabled = false;
        _sendFileButton.Click += (_, _) => SendFile();

        Controls.Add(_startButton);
        Controls.Add(_stopButton);
        Controls.Add(_newButton);
        Controls.Add(_sendFileButton);
        y += 52;

        _autostartCheck.Text = "Start with Windows (host mode, in the tray)";
        _autostartCheck.AutoSize = true;
        _autostartCheck.ForeColor = Theme.TextMuted;
        _autostartCheck.Location = new Point(22, y);
        _autostartCheck.Checked = IsAutostartEnabled();
        _autostartCheck.CheckedChanged += (_, _) => SetAutostart(_autostartCheck.Checked);
        Controls.Add(_autostartCheck);
        y += 32;

        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = Theme.TextMuted;
        _statusLabel.Location = new Point(22, y);
        _statusLabel.Text = "Idle.";
        Controls.Add(_statusLabel);

        // ---- tray ----
        _tray.Icon = SystemIcons.Application;
        _tray.Text = "RemoteDesktop Host";
        _tray.Visible = false;
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("Stop session", null, (_, _) => { RestoreFromTray(); Stop(); });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => { _exiting = true; RestoreFromTray(); Close(); });
        Theme.StyleToolStrip(trayMenu);
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        // Clipboard sync (both directions), on while a session is active.
        _clipboard.TextCopied += t => _session?.SendClipboardText(t);

        Regenerate();
        ApplyMode();

        Load += (_, _) => AutoStartIfConfigured();
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && _running) HideToTray();
        };
    }

    /// <summary>Attended mode: fresh credentials. Unattended keeps its stable id.</summary>
    private void Regenerate()
    {
        _sessionId = SessionId.NewNumericId();
        _password = SessionId.NewPassword();
    }

    /// <summary>Reflects the access mode in the credential display and controls.</summary>
    private void ApplyMode()
    {
        bool unattended = _unattendedCheck.Checked;
        if (unattended && string.IsNullOrEmpty(_settings.UnattendedId))
        {
            _settings.UnattendedId = SessionId.NewUnattendedId();
            _settings.Save();
        }

        _devicePasswordBox.Enabled = unattended && !_running;
        _devicePasswordBox.PlaceholderText =
            _settings.ProtectedUnattendedPassword is null ? "choose a device password" : "(unchanged)";
        _newButton.Enabled = !unattended && !_running;

        if (unattended)
        {
            _sessionIdLabel.Text = _settings.UnattendedId;
            _passwordLabel.Text = "••••••";
        }
        else
        {
            _sessionIdLabel.Text = SessionId.FormatNumericForDisplay(_sessionId);
            _passwordLabel.Text = _password;
        }
    }

    private void Start()
    {
        if (!TryParseRelay(out string host, out int port))
        {
            MessageBox.Show(this, "Enter the relay address (host, port optional).", "RemoteDesktop");
            return;
        }

        bool unattended = _unattendedCheck.Checked;
        string sessionId;
        string password;
        if (unattended)
        {
            sessionId = _settings.UnattendedId;
            password = _devicePasswordBox.Text.Length > 0
                ? _devicePasswordBox.Text
                : HostSettings.Unprotect(_settings.ProtectedUnattendedPassword) ?? "";
            if (password.Length < 8)
            {
                MessageBox.Show(this, "Choose a device password with at least 8 characters.", "RemoteDesktop");
                return;
            }
            _settings.ProtectedUnattendedPassword = HostSettings.Protect(password);
            _devicePasswordBox.Text = "";
        }
        else
        {
            sessionId = _sessionId;
            password = _password;
        }

        _settings.RelayHost = host;
        _settings.RelayPort = port;
        _settings.UnattendedEnabled = unattended;
        _settings.MonitorIndex = Math.Max(0, _monitorBox.SelectedIndex);
        _settings.Save();

        Screen[] screens = Screen.AllScreens;
        Rectangle bounds = screens[Math.Clamp(_settings.MonitorIndex, 0, screens.Length - 1)].Bounds;

        // A stable per-install owner tag lets this host reclaim its id on reconnect.
        byte[] ownerTag = Encoding.UTF8.GetBytes(Environment.MachineName + "|" + sessionId);

        _cts = new CancellationTokenSource();
        _session = new HostSession(host, port, sessionId, password, ownerTag, bounds);
        _session.Status += s => BeginInvoke(() => _statusLabel.Text = s);
        _session.RemoteClipboardText += t => BeginInvoke(() => _clipboard.SetRemoteText(t));
        _session.ViewerConnected += () => BeginInvoke(() => _sendFileButton.Enabled = true);
        _session.ViewerDisconnected += () => BeginInvoke(() => _sendFileButton.Enabled = false);

        _ = _session.RunAsync(_cts.Token);
        _running = true;
        _startButton.Enabled = false;
        _stopButton.Enabled = true;
        _relayBox.Enabled = false;
        _monitorBox.Enabled = false;
        _unattendedCheck.Enabled = false;
        ApplyMode();
    }

    private void Stop()
    {
        _cts?.Cancel();
        _running = false;
        _startButton.Enabled = true;
        _stopButton.Enabled = false;
        _sendFileButton.Enabled = false;
        _relayBox.Enabled = true;
        _monitorBox.Enabled = true;
        _unattendedCheck.Enabled = true;
        _statusLabel.Text = "Stopped.";
        ApplyMode();
    }

    private void SendFile()
    {
        using var dlg = new OpenFileDialog { Title = "Send file to viewer" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string path = dlg.FileName;
        _ = Task.Run(async () =>
        {
            try { await _session!.SendFileAsync(path, _cts?.Token ?? CancellationToken.None); }
            catch (Exception ex) { BeginInvoke(() => _statusLabel.Text = $"Send failed: {ex.Message}"); }
        });
    }

    // ---- tray ----

    private void HideToTray()
    {
        Hide();
        _tray.Visible = true;
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(3000, "RemoteDesktop",
                "The host keeps running in the tray. Double-click to open.", ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        _tray.Visible = false;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>With --host and a fully configured unattended mode, go straight to
    /// serving from the tray (autostart path).</summary>
    private void AutoStartIfConfigured()
    {
        if (!_autoStart) return;
        if (!_settings.UnattendedEnabled) return;
        if (string.IsNullOrEmpty(_settings.RelayHost)) return;
        if (HostSettings.Unprotect(_settings.ProtectedUnattendedPassword) is null) return;

        _unattendedCheck.Checked = true;
        Start();
        if (_running) BeginInvoke(HideToTray);
    }

    // ---- autostart (HKCU Run key) ----

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RemoteDesktop";

    private static bool IsAutostartEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        catch { return false; }
    }

    private void SetAutostart(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key?.SetValue(RunValue, $"\"{Application.ExecutablePath}\" --host");
            else key?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not update autostart: {ex.Message}", "RemoteDesktop");
        }
    }

    // ---- helpers ----

    private Panel MakeCard(string heading, ref int y, int height)
    {
        var card = new Panel { Location = new Point(20, y), Size = new Size(480, height) };
        Theme.StyleCard(card);
        card.Controls.Add(new Label
        {
            Text = heading.ToUpperInvariant(),
            Font = new Font("Segoe UI Semibold", 8.5f),
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(16, 12),
        });
        Controls.Add(card);
        y += height + 14;
        return card;
    }

    private static void AddRow(Panel card, int y, string caption, Control field)
    {
        card.Controls.Add(new Label
        {
            Text = caption, ForeColor = Theme.TextMuted, AutoSize = true, Location = new Point(16, y + 4),
        });
        field.Location = new Point(150, y);
        field.Width = card.Width - 150 - 16;
        card.Controls.Add(field);
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
        // While serving, X hides to the tray instead of killing the session;
        // Exit in the tray menu (or app shutdown) really closes.
        if (_running && !_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        _cts?.Cancel();
        _tray.Dispose();
        _clipboard.Dispose();
        base.OnFormClosing(e);
    }
}
