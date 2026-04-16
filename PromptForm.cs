using System.Drawing;

namespace WinTubeRelay.Tray;

internal sealed class PromptForm : Form
{
    private readonly TextBox _textBox;

    private PromptForm(string title, string prompt, string initialValue)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 140);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };

        var label = new Label
        {
            Text = prompt,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };

        _textBox = new TextBox
        {
            Text = initialValue,
            Dock = DockStyle.Fill,
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
        };
        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
        };

        buttonPanel.Controls.Add(okButton);
        buttonPanel.Controls.Add(cancelButton);

        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(_textBox, 0, 1);
        layout.Controls.Add(buttonPanel, 0, 2);
        Controls.Add(layout);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    public static string? ShowDialog(IWin32Window? owner, string title, string prompt, string initialValue = "")
    {
        using var form = new PromptForm(title, prompt, initialValue);
        var result = owner is null ? form.ShowDialog() : form.ShowDialog(owner);
        return result == DialogResult.OK
            ? form._textBox.Text.Trim()
            : null;
    }
}
