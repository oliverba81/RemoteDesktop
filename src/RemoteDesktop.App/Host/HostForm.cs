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
        // Font autoscaling + stacked fields: side-by-side absolute rows overlap when
        // display scaling enlarges fonts without growing the reserved label gutter.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(860, 680);
        MinimumSize = new Size(820, 640);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Theme.Apply(this);

        // Autostart launches straight into the tray (minimized), never a visible
        // window flash, always in host mode (the --host launch creates this form).
        if (_autoStart)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
        }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(24),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // connection
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // access
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // autostart
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // status

        // ---- connection card ----
        var connCard = MakeCard("Connection");
        Theme.StyleTextBox(_relayBox);
        _relayBox.PlaceholderText = "name.synology.me (port optional, default 9945)";
        _relayBox.Text = string.IsNullOrEmpty(_settings.RelayHost)
            ? ""
            : $"{_settings.RelayHost}:{_settings.RelayPort}";
        AddStackedField(connCard, "Relay address", _relayBox);

        Theme.StyleComboBox(_monitorBox);
        _monitorBox.DropDownStyle = ComboBoxStyle.DropDownList;
        Screen[] screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            _monitorBox.Items.Add($"Display {i + 1} — {b.Width}×{b.Height}{(screens[i].Primary ? " (primary)" : "")}");
        }
        _monitorBox.SelectedIndex = Math.Clamp(_settings.MonitorIndex, 0, screens.Length - 1);
        AddStackedField(connCard, "Monitor", _monitorBox);
        root.Controls.Add(connCard, 0, 0);

        // ---- access card ----
        var accessCard = MakeCard("Access");
        _unattendedCheck.Text = "Unattended access (fixed ID + device password)";
        _unattendedCheck.AutoSize = true;
        _unattendedCheck.Margin = new Padding(0, 4, 0, 10);
        _unattendedCheck.Checked = _settings.UnattendedEnabled;
        _unattendedCheck.CheckedChanged += (_, _) => ApplyMode();
        accessCard.Controls.Add(_unattendedCheck);

        Theme.StyleTextBox(_devicePasswordBox);
        _devicePasswordBox.UseSystemPasswordChar = true;
        AddStackedField(accessCard, "Device password", _devicePasswordBox);

        var creds = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 2,
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };
        creds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        creds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        creds.Controls.Add(new Label
        {
            Text = "Session ID", ForeColor = Theme.TextMuted, AutoSize = true, Margin = new Padding(0, 0, 0, 4),
        }, 0, 0);
        creds.Controls.Add(new Label
        {
            Text = "Password", ForeColor = Theme.TextMuted, AutoSize = true, Margin = new Padding(12, 0, 0, 4),
        }, 1, 0);
        _sessionIdLabel.Font = Theme.MonoBig;
        _sessionIdLabel.ForeColor = Theme.Accent;
        _sessionIdLabel.AutoSize = true;
        _passwordLabel.Font = Theme.MonoBig;
        _passwordLabel.AutoSize = true;
        _passwordLabel.Margin = new Padding(12, 0, 0, 0);
        creds.Controls.Add(_sessionIdLabel, 0, 1);
        creds.Controls.Add(_passwordLabel, 1, 1);
        accessCard.Controls.Add(creds);
        root.Controls.Add(accessCard, 0, 1);

        // ---- buttons ----
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 8, 0, 8),
        };
        _startButton.Text = "Start session";
        _startButton.AutoSize = true;
        _startButton.MinimumSize = new Size(140, 44);
        _startButton.Padding = new Padding(12, 6, 12, 6);
        Theme.StylePrimary(_startButton);
        _startButton.Click += (_, _) => Start();

        _stopButton.Text = "Stop";
        _stopButton.AutoSize = true;
        _stopButton.MinimumSize = new Size(90, 44);
        _stopButton.Padding = new Padding(12, 6, 12, 6);
        Theme.StyleSecondary(_stopButton);
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => Stop();

        _newButton.Text = "New credentials";
        _newButton.AutoSize = true;
        _newButton.MinimumSize = new Size(150, 44);
        _newButton.Padding = new Padding(12, 6, 12, 6);
        Theme.StyleSecondary(_newButton);
        _newButton.Click += (_, _) => { Regenerate(); ApplyMode(); };

        _sendFileButton.Text = "Send file…";
        _sendFileButton.AutoSize = true;
        _sendFileButton.MinimumSize = new Size(120, 44);
        _sendFileButton.Padding = new Padding(12, 6, 12, 6);
        Theme.StyleSecondary(_sendFileButton);
        _sendFileButton.Enabled = false;
        _sendFileButton.Click += (_, _) => SendFile();

        buttons.Controls.AddRange(new Control[] { _startButton, _stopButton, _newButton, _sendFileButton });
        root.Controls.Add(buttons, 0, 2);

        _autostartCheck.Text = "Start with Windows and begin serving automatically (in the tray)";
        _autostartCheck.AutoSize = true;
        _autostartCheck.ForeColor = Theme.TextMuted;
        _autostartCheck.Checked = IsAutostartEnabled();
        _autostartCheck.CheckedChanged += (_, _) => SetAutostart(_autostartCheck.Checked);
        root.Controls.Add(_autostartCheck, 0, 3);

        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = Theme.TextMuted;
        _statusLabel.Text = "Idle.";
        _statusLabel.Margin = new Padding(0, 8, 0, 0);
        root.Controls.Add(_statusLabel, 0, 4);

        Controls.Add(root);

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

        Load += (_, _) =>
        {
            // Grow width if the autostart line is still clipped after font scaling.
            int need = Math.Max(_autostartCheck.PreferredSize.Width + 64, ClientSize.Width);
            if (need > ClientSize.Width)
                ClientSize = new Size(need, ClientSize.Height);
            AutoStartIfConfigured();
        };
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

    /// <summary>Restore from the tray on a second-launch "show" ping (single instance).</summary>
    public void RestoreFromTrayPublic() => RestoreFromTray();

    private void RestoreFromTray()
    {
        _tray.Visible = false;
        ShowInTaskbar = true; // autostart started us without a taskbar button
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>Autostart path (--host): always land minimized in the tray, in host
    /// mode. If unattended access is fully configured, also start serving right away;
    /// otherwise the host waits in the tray and can be opened to finish setup.</summary>
    private void AutoStartIfConfigured()
    {
        if (!_autoStart) return;

        bool canServe = _settings.UnattendedEnabled
            && !string.IsNullOrEmpty(_settings.RelayHost)
            && HostSettings.Unprotect(_settings.ProtectedUnattendedPassword) is not null;

        if (canServe)
        {
            _unattendedCheck.Checked = true;
            Start();
        }

        BeginInvoke(HideToTray);
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
            if (enabled)
            {
                key?.SetValue(RunValue, $"\"{Application.ExecutablePath}\" --host");

                // Autostart only makes sense with unattended access: an attended
                // session uses fresh random credentials each start that nobody would
                // know. Turn it on and make sure the session can actually start after
                // a reboot (relay + device password saved).
                if (!_unattendedCheck.Checked) _unattendedCheck.Checked = true; // -> ApplyMode

                bool hasPassword = _devicePasswordBox.Text.Length >= 8
                    || _settings.ProtectedUnattendedPassword is not null;
                bool hasRelay = _relayBox.Text.Trim().Length > 0;
                if (!hasPassword || !hasRelay)
                    MessageBox.Show(this,
                        "For the host to start serving automatically after login, set the relay "
                        + "address and a device password, then click \"Start session\" once. "
                        + "Those settings are reused on every autostart.",
                        "RemoteDesktop", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                key?.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not update autostart: {ex.Message}", "RemoteDesktop");
        }
    }

    // ---- helpers ----

    private static TableLayoutPanel MakeCard(string heading)
    {
        var card = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(20, 16, 20, 16),
            Margin = new Padding(0, 0, 0, 14),
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Theme.StyleCard(card);
        card.Controls.Add(new Label
        {
            Text = heading.ToUpperInvariant(),
            Font = new Font("Segoe UI Semibold", 8.5f),
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        });
        return card;
    }

    private static void AddStackedField(TableLayoutPanel card, string caption, Control field)
    {
        card.Controls.Add(new Label
        {
            Text = caption,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        });
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(0, 0, 0, 12);
        if (field.Height < 28) field.Height = 28;
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
