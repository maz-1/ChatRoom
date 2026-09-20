using System;
using Eto.Drawing;
using Eto.Forms;

namespace ChatRoomTray.ConfigEditor;

/// <summary>Edits one environment entry without parsing or trimming its value.</summary>
public sealed class EnvironmentVariableInputDialog : Dialog<bool>
{
    private readonly TextBox _nameBox;
    private readonly TextArea _valueBox;
    private readonly Func<string, bool>? _isDuplicate;
    private readonly string _originalValue;
    private readonly string _initialEditorText;
    private readonly Label _errorLabel = new()
    {
        TextColor = Colors.Firebrick,
        Height = 40,
        Wrap = WrapMode.Word,
    };

    public EnvironmentVariableInputDialog(
        string title, string name, string value, Func<string, bool>? isDuplicate = null)
    {
        Title = title;
        AutoSize = false;
        Resizable = true;
        ShowInTaskbar = false;
        EditorLayout.SizeWindow(this, new Size(640, 300), new Size(420, 220));
        _isDuplicate = isDuplicate;
        _nameBox = new TextBox { ID = "EnvironmentVariableName", Text = name };
        _valueBox = new TextArea { ID = "EnvironmentVariableValue", Text = value, Wrap = false, Height = 100 };
        _originalValue = value;
        _initialEditorText = _valueBox.Text ?? string.Empty;

        var ok = new Button { Text = "确定" };
        var cancel = new Button { Text = "取消" };
        ok.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Close(false);
        DefaultButton = ok;
        AbortButton = cancel;
        _nameBox.TextChanged += (_, _) => _errorLabel.Text = string.Empty;
        _valueBox.TextChanged += (_, _) => _errorLabel.Text = string.Empty;

        var fields = new DynamicLayout { Padding = new Padding(12), Spacing = new Size(8, 8) };
        fields.AddRow(new Label { Text = "名称", VerticalAlignment = VerticalAlignment.Center }, _nameBox);
        fields.AddRow(new Label { Text = "值", VerticalAlignment = VerticalAlignment.Top }, _valueBox);
        fields.Add(null, yscale: true);
        var footer = new DynamicLayout { Padding = new Padding(12, 4, 12, 12), Spacing = new Size(8, 8) };
        footer.AddRow(_errorLabel);
        footer.AddSeparateRow(null, cancel, ok);
        var root = new DynamicLayout();
        root.Add(EditorLayout.VerticalScroll(fields, "EnvironmentVariableScroll"), yscale: true);
        root.AddRow(footer);
        Content = root;
    }

    public string VariableName => _nameBox.Text ?? string.Empty;

    // Native multiline editors can normalize newlines. A no-op edit must not change stored bytes.
    public string VariableValue => string.Equals(_valueBox.Text, _initialEditorText, StringComparison.Ordinal)
        ? _originalValue : _valueBox.Text ?? string.Empty;

    internal bool TryValidate(out string error)
    {
        if (!TryValidateEntry(VariableName, VariableValue, out error)) return false;
        if (_isDuplicate?.Invoke(VariableName) == true)
        {
            error = $"环境变量“{VariableName}”已存在。";
            return false;
        }
        return true;
    }

    internal static bool TryValidateEntry(string name, string value, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            error = "请填写环境变量名称。";
        else if (name.IndexOfAny(new[] { '=', '\0', '\r', '\n' }) >= 0)
            error = "环境变量名称不能包含等号、换行符或空字符。";
        else if (value is null)
            error = "环境变量的值必须是字符串（可以为空）。";
        else if (value.IndexOf('\0') >= 0)
            error = "环境变量的值不能包含空字符。";
        return error.Length == 0;
    }

    private void Confirm()
    {
        if (!TryValidate(out var error))
        {
            _errorLabel.Text = error;
            return;
        }
        Close(true);
    }
}
