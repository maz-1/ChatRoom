using System.Text.RegularExpressions;
using Eto.Drawing;
using Eto.Forms;

namespace ChatRoomTray.ConfigEditor;

/// <summary>Two-field prompt for creating or editing one HTTP request header.</summary>
public sealed class HeaderInputDialog : Dialog<bool>
{
    private static readonly Regex HeaderName = new(
        "^[!#$%&'*+\\-.^_`|~0-9A-Za-z]+$",
        RegexOptions.CultureInvariant);

    private readonly TextBox _keyBox;
    private readonly TextBox _valueBox;
    private readonly Label _errorLabel = new()
    {
        TextColor = Colors.Firebrick,
        Height = 34,
    };

    public HeaderInputDialog(string title, string key, string value)
    {
        Title = title;
        Resizable = false;
        ShowInTaskbar = false;
        EditorLayout.SizeWindow(this, new Size(520, 210), new Size(360, 185));

        _keyBox = new TextBox { Text = key };
        _valueBox = new TextBox { Text = value };

        var ok = new Button { Text = "确定" };
        var cancel = new Button { Text = "取消" };
        ok.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Close(false);

        DefaultButton = ok;
        AbortButton = cancel;

        var layout = new DynamicLayout
        {
            Padding = new Padding(12),
            Spacing = new Size(8, 8),
        };
        layout.AddRow(FieldLabel("键"), _keyBox);
        layout.AddRow(FieldLabel("值"), _valueBox);
        layout.AddRow(_errorLabel);
        layout.Add(null);
        layout.AddSeparateRow(null, cancel, ok);
        Content = layout;
    }

    public string HeaderKey => (_keyBox.Text ?? string.Empty).Trim();

    public string HeaderValue => (_valueBox.Text ?? string.Empty).Trim();

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

        if ((_valueBox.Text ?? string.Empty).IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            _errorLabel.Text = "请求头的值不能包含换行符。";
            return;
        }

        Close(true);
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
