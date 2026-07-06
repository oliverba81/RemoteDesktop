using RemoteDesktop.App.Ui;

namespace RemoteDesktop.Viewer;

/// <summary>
/// Entry window: an address book of saved targets (with an online indicator) plus
/// a quick-connect panel. Double-clicking an entry connects.
/// </summary>
public sealed class ConnectForm : Form
{
    private readonly AddressBook _book = AddressBook.Load();
    private readonly ListView _list = new();
    private readonly TextBox _relayBox = new();
    private readonly TextBox _sessionBox = new();
    private readonly TextBox _passwordBox = new();
    private readonly CheckBox _saveBox = new();

    public ConnectForm()
    {
        Text = "RemoteDesktop — Connect";
        Width = 580;
        Height = 500;
        StartPosition = FormStartPosition.CenterScreen;
        Theme.Apply(this);

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.Dock = DockStyle.Fill;
        Theme.StyleListView(_list);
        _list.Columns.Add("", 28);
        _list.Columns.Add("Name", 160);
        _list.Columns.Add("Relay", 200);
        _list.Columns.Add("Session", 120);
        _list.DoubleClick += (_, _) => ConnectSelected();

        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        AddButton(listButtons, "Connect", ConnectSelected);
        AddButton(listButtons, "Add…", () => EditEntry(null));
        AddButton(listButtons, "Edit…", () => { if (Selected() is { } e) EditEntry(e); });
        AddButton(listButtons, "Delete", DeleteSelected);
        AddButton(listButtons, "Refresh status", () => _ = RefreshStatusAsync());
        AddButton(listButtons, "Sync…", OpenSync);

        var quick = new GroupBox
        {
            Text = "Quick connect",
            Dock = DockStyle.Bottom,
            Height = 158,
            ForeColor = Theme.TextMuted,
            FlatStyle = FlatStyle.Flat,
        };
        var q = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(8) };
        q.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        q.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _relayBox.Dock = DockStyle.Fill;
        _relayBox.PlaceholderText = "name.synology.me (port optional, default 9945)";
        _sessionBox.Dock = DockStyle.Fill;
        _passwordBox.Dock = DockStyle.Fill;
        _passwordBox.UseSystemPasswordChar = true;
        foreach (var box in new[] { _relayBox, _sessionBox, _passwordBox }) Theme.StyleTextBox(box);
        _saveBox.Text = "Save to address book";
        _saveBox.AutoSize = true;
        _saveBox.ForeColor = Theme.Text;
        q.Controls.Add(new Label { Text = "Relay:", Anchor = AnchorStyles.Left, AutoSize = true, ForeColor = Theme.TextMuted }, 0, 0);
        q.Controls.Add(_relayBox, 1, 0);
        q.Controls.Add(new Label { Text = "Session ID:", Anchor = AnchorStyles.Left, AutoSize = true, ForeColor = Theme.TextMuted }, 0, 1);
        q.Controls.Add(_sessionBox, 1, 1);
        q.Controls.Add(new Label { Text = "Password:", Anchor = AnchorStyles.Left, AutoSize = true, ForeColor = Theme.TextMuted }, 0, 2);
        q.Controls.Add(_passwordBox, 1, 2);
        var quickConnect = new Button { Text = "Connect", Dock = DockStyle.Fill };
        Theme.StylePrimary(quickConnect);
        quickConnect.Click += (_, _) => QuickConnect();
        q.Controls.Add(_saveBox, 1, 3);
        q.Controls.Add(quickConnect, 1, 4);
        quick.Controls.Add(q);

        Controls.Add(_list);
        Controls.Add(quick);
        Controls.Add(listButtons);

        RefreshList();
        _ = RefreshStatusAsync();
    }

    private static void AddButton(Control parent, string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        Theme.StyleSecondary(b);
        b.Click += (_, _) => onClick();
        parent.Controls.Add(b);
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (AddressBookEntry e in _book.Entries)
        {
            string dot = e.Online switch { true => "●", false => "○", _ => "…" };
            var item = new ListViewItem(new[] { dot, e.DisplayName, $"{e.RelayHost}:{e.RelayPort}", e.SessionId })
            {
                Tag = e,
                ForeColor = e.Online == true ? Theme.Success : Theme.Text,
            };
            _list.Items.Add(item);
        }
    }

    private AddressBookEntry? Selected() =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as AddressBookEntry : null;

    private async Task RefreshStatusAsync()
    {
        foreach (var group in _book.Entries.GroupBy(e => (e.RelayHost, e.RelayPort)))
        {
            if (string.IsNullOrWhiteSpace(group.Key.RelayHost)) continue;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var ids = group.Select(e => e.SessionId).Distinct().ToList();
                var status = await RelayPresence.QueryAsync(group.Key.RelayHost, group.Key.RelayPort, ids, cts.Token);
                foreach (var e in group)
                    e.Online = status.TryGetValue(e.SessionId, out bool on) ? on : null;
            }
            catch { foreach (var e in group) e.Online = null; }
        }
        if (IsHandleCreated) BeginInvoke(RefreshList);
    }

    private void EditEntry(AddressBookEntry? existing)
    {
        using var dlg = new EntryEditForm(existing);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (existing is null) _book.Entries.Add(dlg.Result!);
        _book.Save();
        RefreshList();
        _ = RefreshStatusAsync();
    }

    private void OpenSync()
    {
        string defaultRelay = _relayBox.Text.Trim();
        if (defaultRelay.Length == 0 && _book.Entries.Count > 0)
        {
            AddressBookEntry e = _book.Entries[0];
            defaultRelay = $"{e.RelayHost}:{e.RelayPort}";
        }
        using var dlg = new SyncForm(_book, defaultRelay);
        dlg.ShowDialog(this);
        if (dlg.BookChanged)
        {
            RefreshList();
            _ = RefreshStatusAsync();
        }
    }

    private void DeleteSelected()
    {
        if (Selected() is not { } e) return;
        _book.Entries.Remove(e);
        _book.Save();
        RefreshList();
    }

    private void ConnectSelected()
    {
        if (Selected() is not { } e) return;
        string? password = AddressBook.Unprotect(e.ProtectedPassword)
            ?? Prompt.Password($"Password for {e.DisplayName}");
        if (password is null) return;
        Launch(e.RelayHost, e.RelayPort, e.SessionId, password);
    }

    private void QuickConnect()
    {
        if (!TryParseRelay(_relayBox.Text, out string host, out int port) ||
            string.IsNullOrWhiteSpace(_sessionBox.Text) || _passwordBox.Text.Length == 0)
        {
            MessageBox.Show(this, "Enter relay (host:port), session ID and password.", "RemoteDesktop");
            return;
        }
        string session = _sessionBox.Text.Replace(" ", "").Trim();
        if (_saveBox.Checked)
        {
            _book.Entries.Add(new AddressBookEntry
            {
                DisplayName = host,
                RelayHost = host,
                RelayPort = port,
                SessionId = session,
                ProtectedPassword = AddressBook.Protect(_passwordBox.Text),
            });
            _book.Save();
            RefreshList();
        }
        Launch(host, port, session, _passwordBox.Text);
    }

    private void Launch(string host, int port, string sessionId, string password)
    {
        Hide();
        using var viewer = new ViewerForm(host, port, sessionId, password);
        viewer.ShowDialog(this);
        Show();
        _ = RefreshStatusAsync();
    }

    internal static bool TryParseRelay(string text, out string host, out int port)
    {
        host = "";
        port = 9945;
        text = text.Trim();
        if (text.Length == 0) return false;
        int colon = text.LastIndexOf(':');
        if (colon < 0) { host = text; return true; }
        host = text[..colon];
        return int.TryParse(text[(colon + 1)..], out port) && port is > 0 and < 65536;
    }
}
