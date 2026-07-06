using RemoteDesktop.App.Ui;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Windows;

namespace RemoteDesktop.Viewer;

/// <summary>
/// The remote-control window. Hosts the screen control and forwards input.
///
/// Keyboard handling (v1): printable characters go through WM_CHAR / OnKeyPress
/// and are injected as Unicode on the host (layout independent — fixes DE-viewer /
/// US-host mismatches, AltGr and dead keys). Non-character keys and any key while a
/// modifier is held go as virtual-key codes so shortcuts work. Known v1 limits (no
/// low-level hook yet): Win, Alt+Tab and Alt+Esc are swallowed by the local OS;
/// Ctrl+Alt+Del cannot be injected at all.
/// </summary>
public sealed class ViewerForm : Form
{
    private readonly RemoteScreenControl _screen = new();
    private readonly ToolStripStatusLabel _status = new("Connecting…");
    private readonly ViewerSession _session;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<Keys> _vkDown = new();
    private readonly ClipboardMonitor _clipboard = new();
    private readonly ToolStripButton _sendFile = new("Send file…");
    private volatile bool _everConnected;

    public ViewerForm(string relayHost, int relayPort, string sessionId, string password)
    {
        Text = $"RemoteDesktop — {sessionId}";
        Width = 1100;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        Theme.Apply(this);

        _screen.Dock = DockStyle.Fill;
        _session = new ViewerSession(relayHost, relayPort, sessionId, password, _screen);
        _session.Status += s => TryUi(() => _status.Text = s);
        _session.Connected += size =>
        {
            _everConnected = true;
            TryUi(() => { _screen.ResetRemote(size); _sendFile.Enabled = true; });
        };
        _session.Disconnected += () => TryUi(() => _sendFile.Enabled = false);
        _session.RemoteClipboardText += t => TryUi(() => _clipboard.SetRemoteText(t));
        _session.FileProgress += (name, done, total, incoming) => TryUi(() =>
            _status.Text = $"{(incoming ? "Receiving" : "Sending")} {name} — {done * 100 / Math.Max(1, total)}%");
        _session.AcceptFile = AcceptFileAsync;
        _clipboard.TextCopied += t => _session.SendClipboardText(t);

        var toolbar = new ToolStrip();
        var quality = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        quality.Items.AddRange(new object[] { "Quality: Low", "Quality: Medium", "Quality: High" });
        quality.SelectedIndex = 1;
        quality.SelectedIndexChanged += (_, _) => _session.SetQuality((QualityLevel)quality.SelectedIndex);
        var disconnect = new ToolStripButton("Disconnect");
        disconnect.Click += (_, _) => Close();
        _sendFile.Enabled = false;
        _sendFile.Click += (_, _) => SendFile();
        toolbar.Items.Add(disconnect);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_sendFile);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(quality);

        Theme.StyleToolStrip(toolbar);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        Theme.StyleToolStrip(statusStrip);
        _status.ForeColor = Theme.TextMuted;

        Controls.Add(_screen);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);

        Load += (_, _) => _ = RunSessionAsync();
        Deactivate += (_, _) => _session.SendInputReset(); // release modifiers on focus loss
    }

    /// <summary>
    /// Connect loop with auto-reconnect: a failure before the first successful
    /// pairing is fatal (wrong id/password/relay → dialog + close), but once a
    /// session existed, drops and host restarts are retried every few seconds
    /// until the user closes the window.
    /// </summary>
    private async Task RunSessionAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _session.RunAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // window closing
            }
            catch (Exception ex)
            {
                if (!_everConnected)
                {
                    TryUi(() =>
                    {
                        MessageBox.Show(this, ex.Message, "Connection failed");
                        Close();
                    });
                    return;
                }
                TryUi(() => _status.Text = $"Connection lost — reconnecting… ({ex.Message})");
            }

            if (_cts.IsCancellationRequested) return;
            TryUi(() => _status.Text = "Connection lost — reconnecting…");
            try { await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void TryUi(Action action)
    {
        if (!IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { /* closing */ }
    }

    private void SendFile()
    {
        using var dlg = new OpenFileDialog { Title = "Send file to host" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string path = dlg.FileName;
        _ = Task.Run(async () =>
        {
            try { await _session.SendFileAsync(path, _cts.Token); }
            catch (Exception ex) { TryUi(() => _status.Text = $"Send failed: {ex.Message}"); }
        });
    }

    /// <summary>Prompts the user (on the UI thread) to accept an incoming file.</summary>
    private Task<bool> AcceptFileAsync(FileOfferPayload offer)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!IsHandleCreated) { tcs.SetResult(false); return tcs.Task; }
        BeginInvoke(() =>
        {
            string sizeMb = (offer.Size / 1024.0 / 1024.0).ToString("0.0");
            DialogResult r = MessageBox.Show(this,
                $"The host wants to send \"{offer.FileName}\" ({sizeMb} MB).\nSave to your Downloads folder?",
                "Incoming file", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            tcs.SetResult(r == DialogResult.Yes);
        });
        return tcs.Task;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (SendsAsCharacter(e))
            return; // OnKeyPress will inject the Unicode character

        _vkDown.Add(e.KeyCode);
        _session.SendVirtualKey((ushort)e.KeyCode, down: true, IsExtended(e.KeyCode));
        e.SuppressKeyPress = true;
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_vkDown.Remove(e.KeyCode))
            _session.SendVirtualKey((ushort)e.KeyCode, down: false, IsExtended(e.KeyCode));
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        // Inject the fully-composed character (respects viewer layout, AltGr, dead keys).
        _session.SendChar(e.KeyChar, down: true);
        _session.SendChar(e.KeyChar, down: false);
        e.Handled = true;
    }

    private static bool SendsAsCharacter(KeyEventArgs e)
    {
        if (e.Control || e.Alt) return false; // shortcut → virtual key
        Keys k = e.KeyCode;
        bool printable =
            (k >= Keys.A && k <= Keys.Z) ||
            (k >= Keys.D0 && k <= Keys.D9) ||
            (k >= Keys.NumPad0 && k <= Keys.Divide) ||
            (k >= Keys.Oem1 && k <= Keys.OemBackslash) ||
            k == Keys.Space;
        return printable;
    }

    private static bool IsExtended(Keys k) => k is
        Keys.Up or Keys.Down or Keys.Left or Keys.Right or
        Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or
        Keys.Insert or Keys.Delete or Keys.RControlKey or Keys.RMenu;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts.Cancel();
        _clipboard.Dispose();
        _ = _session.DisposeAsync();
        base.OnFormClosing(e);
    }
}
