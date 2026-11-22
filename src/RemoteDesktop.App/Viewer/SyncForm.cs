using RemoteDesktop.App.Ui;

namespace RemoteDesktop.Viewer;

/// <summary>
/// Encrypted address-book sync through the relay. Upload pushes the local book;
/// download pulls the remote one and merges it. The passphrase is the only secret
/// and never leaves the machine in the clear.
/// </summary>
public sealed class SyncForm : Form
{
    private readonly AddressBook _book;
    private readonly TextBox _relayBox = new();
    private readonly TextBox _passphraseBox = new();
    private readonly Label _status = new();

    /// <summary>Set when a download merged remote entries; the caller re-renders.</summary>
    public bool BookChanged { get; private set; }

    public SyncForm(AddressBook book, string defaultRelay)
    {
        _book = book;
        Text = "RemoteDesktop — Sync address book";
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(560, 340);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        Theme.Apply(this);

        var intro = new Label
        {
            Text = "Store your address book on the relay, encrypted with a passphrase. " +
                   "Use the same relay and passphrase on another PC to get it back.",
            ForeColor = Theme.TextMuted,
            Location = new Point(24, 20),
            Size = new Size(512, 52),
        };

        var relayLabel = new Label { Text = "Relay:", ForeColor = Theme.TextMuted, AutoSize = true, Location = new Point(24, 90) };
        _relayBox.Location = new Point(160, 86);
        _relayBox.Width = 376;
        _relayBox.Text = defaultRelay;
        _relayBox.PlaceholderText = "name.synology.me (port optional, default 9945)";
        Theme.StyleTextBox(_relayBox);

        var passLabel = new Label { Text = "Sync passphrase:", ForeColor = Theme.TextMuted, AutoSize = true, Location = new Point(24, 130) };
        _passphraseBox.Location = new Point(160, 126);
        _passphraseBox.Width = 376;
        _passphraseBox.UseSystemPasswordChar = true;
        Theme.StyleTextBox(_passphraseBox);

        var upload = new Button { Text = "Upload", Location = new Point(160, 176), Size = new Size(160, 40) };
        Theme.StylePrimary(upload);
        upload.Click += (_, _) => _ = RunAsync(isUpload: true);

        var download = new Button { Text = "Download & merge", Location = new Point(336, 176), Size = new Size(200, 40) };
        Theme.StyleSecondary(download);
        download.Click += (_, _) => _ = RunAsync(isUpload: false);

        _status.ForeColor = Theme.TextMuted;
        _status.Location = new Point(24, 236);
        _status.Size = new Size(512, 44);

        var close = new Button { Text = "Close", Location = new Point(456, 288), Size = new Size(100, 36), DialogResult = DialogResult.OK };
        Theme.StyleSecondary(close);

        Controls.AddRange(new Control[]
        {
            intro, relayLabel, _relayBox, passLabel, _passphraseBox, upload, download, _status, close,
        });
    }

    private async Task RunAsync(bool isUpload)
    {
        if (!ConnectForm.TryParseRelay(_relayBox.Text, out string host, out int port))
        {
            _status.Text = "Enter the relay address.";
            return;
        }
        if (_passphraseBox.Text.Length < 8)
        {
            _status.Text = "Use a passphrase of at least 8 characters.";
            return;
        }

        _status.ForeColor = Theme.TextMuted;
        _status.Text = isUpload ? "Uploading…" : "Downloading…";
        Enabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (isUpload)
            {
                await ProfileSync.UploadAsync(host, port, _passphraseBox.Text, _book, cts.Token);
                _status.ForeColor = Theme.Success;
                _status.Text = "Address book uploaded.";
            }
            else
            {
                AddressBook? remote = await ProfileSync.DownloadAsync(host, port, _passphraseBox.Text, cts.Token);
                if (remote is null)
                {
                    _status.Text = "No profile stored yet — upload first.";
                    return;
                }
                int added = Merge(remote);
                _book.Save();
                BookChanged = true;
                _status.ForeColor = Theme.Success;
                _status.Text = $"Merged {remote.Entries.Count} entries ({added} new).";
            }
        }
        catch (ProfileSync.SyncException ex)
        {
            _status.ForeColor = Theme.Text;
            _status.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
            _status.ForeColor = Theme.Text;
            _status.Text = "Timed out talking to the relay.";
        }
        finally
        {
            Enabled = true;
        }
    }

    /// <summary>Merges remote entries into the local book, keyed by relay+session,
    /// updating existing entries and appending new ones. Returns the new count.</summary>
    private int Merge(AddressBook remote)
    {
        int added = 0;
        foreach (AddressBookEntry r in remote.Entries)
        {
            AddressBookEntry? local = _book.Entries.FirstOrDefault(e =>
                string.Equals(e.RelayHost, r.RelayHost, StringComparison.OrdinalIgnoreCase) &&
                e.RelayPort == r.RelayPort && e.SessionId == r.SessionId);
            if (local is null)
            {
                _book.Entries.Add(r);
                added++;
            }
            else
            {
                local.DisplayName = r.DisplayName;
                if (r.ProtectedPassword is not null) local.ProtectedPassword = r.ProtectedPassword;
            }
        }
        return added;
    }
}
