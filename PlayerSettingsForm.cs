using System.Drawing;

namespace WinTubeRelay.Tray;

internal sealed class PlayerSettingsForm : Form
{
    private readonly TextBox _mpvPathTextBox;
    private readonly TextBox _ytDlpPathTextBox;
    private readonly TextBox _browserTextBox;
    private readonly TextBox _pipeNameTextBox;
    private readonly TextBox _logPathTextBox;
    private readonly NumericUpDown _apiPortInput;
    private readonly TextBox _apiKeyTextBox;
    private readonly NumericUpDown _volumeStepInput;

    public PlayerSettingsForm(AppSettings settings)
    {
        Text = "WinTubeRelay Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 360);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 9,
            Padding = new Padding(12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        _mpvPathTextBox = AddFileRow(layout, 0, "mpv Path", settings.MpvPath);
        _ytDlpPathTextBox = AddFileRow(layout, 1, "yt-dlp Path", settings.YtDlpPath ?? string.Empty);
        _browserTextBox = AddTextRow(layout, 2, "Browser", settings.YtDlpBrowser ?? string.Empty);
        _pipeNameTextBox = AddTextRow(layout, 3, "Pipe Name", settings.MpvPipeName);
        _logPathTextBox = AddTextRow(layout, 4, "Log File", settings.MpvLogFilePath ?? string.Empty);

        layout.Controls.Add(new Label
        {
            Text = "API Port",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        }, 0, 5);

        _apiPortInput = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = Math.Clamp(settings.ApiPort, 1, 65535),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        layout.Controls.Add(_apiPortInput, 1, 5);

        _apiKeyTextBox = AddTextRow(layout, 6, "API Key", settings.ApiKey ?? string.Empty);

        layout.Controls.Add(new Label
        {
            Text = "Volume Step",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        }, 0, 7);

        _volumeStepInput = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 50,
            Value = Math.Clamp(settings.VolumeStep, 1, 50),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        layout.Controls.Add(_volumeStepInput, 1, 7);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            AutoSize = true,
        };
        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
        };

        buttonPanel.Controls.Add(saveButton);
        buttonPanel.Controls.Add(cancelButton);

        layout.Controls.Add(buttonPanel, 0, 8);
        layout.SetColumnSpan(buttonPanel, 3);

        Controls.Add(layout);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    public AppSettings BuildSettings(AppSettings currentSettings)
    {
        return new AppSettings
        {
            SelectedAudioDeviceId = currentSettings.SelectedAudioDeviceId,
            MpvPath = _mpvPathTextBox.Text.Trim(),
            YtDlpPath = NullIfWhiteSpace(_ytDlpPathTextBox.Text),
            YtDlpBrowser = NullIfWhiteSpace(_browserTextBox.Text),
            MpvPipeName = string.IsNullOrWhiteSpace(_pipeNameTextBox.Text)
                ? "wintuberelay-mpv"
                : _pipeNameTextBox.Text.Trim(),
            MpvLogFilePath = NullIfWhiteSpace(_logPathTextBox.Text),
            ApiPort = (int)_apiPortInput.Value,
            ApiKey = NullIfWhiteSpace(_apiKeyTextBox.Text),
            VolumeStep = (int)_volumeStepInput.Value,
            MaxRecentUrls = currentSettings.MaxRecentUrls,
            Favorites = currentSettings.Favorites,
            RecentUrls = currentSettings.RecentUrls,
        };
    }

    private TextBox AddFileRow(TableLayoutPanel layout, int rowIndex, string label, string initialValue)
    {
        var textBox = AddTextRow(layout, rowIndex, label, initialValue);
        var browseButton = new Button
        {
            Text = "Browse...",
            AutoSize = true,
        };
        browseButton.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                FileName = textBox.Text,
                Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*",
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                textBox.Text = dialog.FileName;
            }
        };
        layout.Controls.Add(browseButton, 2, rowIndex);
        return textBox;
    }

    private static TextBox AddTextRow(TableLayoutPanel layout, int rowIndex, string label, string initialValue)
    {
        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        }, 0, rowIndex);

        var textBox = new TextBox
        {
            Text = initialValue,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };

        layout.Controls.Add(textBox, 1, rowIndex);
        return textBox;
    }

    private static string? NullIfWhiteSpace(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
