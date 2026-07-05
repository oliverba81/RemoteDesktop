namespace RemoteDesktop.Viewer;

/// <summary>Add/edit dialog for an address-book entry.</summary>
public sealed class EntryEditForm : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _relay = new();
    private readonly TextBox _session = new();
    private readonly TextBox _password = new();
    private readonly bool _isNew;

    public AddressBookEntry? Result { get; private set; }

    public EntryEditForm(AddressBookEntry? existing)
    {
        _isNew = existing is null;
        Text = _isNew ? "Add target" : "Edit target";
        Width = 420;
        Height = 260;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var box in new[] { _name, _relay, _session, _password }) box.Dock = DockStyle.Fill;
        _password.UseSystemPasswordChar = true;
        _password.PlaceholderText = _isNew ? "" : "(unchanged)";

        if (existing is not null)
        {
            _name.Text = existing.DisplayName;
            _relay.Text = $"{existing.RelayHost}:{existing.RelayPort}";
            _session.Text = existing.SessionId;
        }

        layout.Controls.Add(new Label { Text = "Name:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_name, 1, 0);
        layout.Controls.Add(new Label { Text = "Relay host:port:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_relay, 1, 1);
        layout.Controls.Add(new Label { Text = "Session ID:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_session, 1, 2);
        layout.Controls.Add(new Label { Text = "Password:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        layout.Controls.Add(_password, 1, 3);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.None };
        ok.Click += (_, _) => OnOk(existing);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(new Label(), 0, 4);
        layout.Controls.Add(buttons, 1, 4);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void OnOk(AddressBookEntry? existing)
    {
        if (!ConnectForm.TryParseRelay(_relay.Text, out string host, out int port) ||
            string.IsNullOrWhiteSpace(_session.Text))
        {
            MessageBox.Show(this, "Relay (host:port) and session ID are required.", "RemoteDesktop");
            return;
        }

        var entry = existing ?? new AddressBookEntry();
        entry.DisplayName = string.IsNullOrWhiteSpace(_name.Text) ? host : _name.Text.Trim();
        entry.RelayHost = host;
        entry.RelayPort = port;
        entry.SessionId = _session.Text.Replace(" ", "").Trim();
        if (_password.Text.Length > 0)
            entry.ProtectedPassword = AddressBook.Protect(_password.Text);

        Result = entry;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Tiny modal password prompt.</summary>
internal static class Prompt
{
    public static string? Password(string caption)
    {
        using var form = new Form
        {
            Text = caption,
            Width = 340,
            Height = 150,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var box = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true, Margin = new Padding(10) };
        var ok = new Button { Text = "OK", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
        form.Controls.Add(box);
        form.Controls.Add(ok);
        form.AcceptButton = ok;
        return form.ShowDialog() == DialogResult.OK && box.Text.Length > 0 ? box.Text : null;
    }
}
