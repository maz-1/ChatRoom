using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ChatRoomTray.ConfigEditor;

/// <summary>Two-field prompt for creating or editing one HTTP request header.</summary>
public sealed class HeaderInputDialog : Form
{
    private static readonly Regex HeaderName = new(
        "^[!#$%&'*+\\-.^_`|~0-9A-Za-z]+$",
        RegexOptions.CultureInvariant);

    private readonly TextBox _keyBox;
    private readonly TextBox _valueBox;
    private readonly Label _errorLabel = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = Color.Firebrick,
        AutoSize = false,
        Height = 34,
    };

    public HeaderInputDialog(string title, string key, string value)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 185);
        AutoScaleMode = AutoScaleMode.Font;

        _keyBox = new TextBox { Text = key, Dock = DockStyle.Fill };
        _valueBox = new TextBox { Text = value, Dock = DockStyle.Fill };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(FieldLabel("键"), 0, 0);
        layout.Controls.Add(_keyBox, 1, 0);
        layout.Controls.Add(FieldLabel("值"), 0, 1);
        layout.Controls.Add(_valueBox, 1, 1);
        layout.Controls.Add(_errorLabel, 0, 2);
        layout.SetColumnSpan(_errorLabel, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 0, 12, 12),
        };
        var ok = new Button { Text = "确定", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Confirm();
        buttons.Controls.AddRange(new Control[] { ok, cancel });

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string HeaderKey => _keyBox.Text.Trim();

    public string HeaderValue => _valueBox.Text.Trim();

    private void Confirm()
    {
        var key = HeaderKey;
        if (key.Length == 0)
        {
            _errorLabel.Text = "请填写请求头的键。";
            return;
        }

        if (!HeaderName.IsMatch(key))
        {
            _errorLabel.Text = "请求头的键包含无效字符。";
            return;
        }

        if (_valueBox.Text.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            _errorLabel.Text = "请求头的值不能包含换行符。";
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 6, 8, 3),
    };
}
