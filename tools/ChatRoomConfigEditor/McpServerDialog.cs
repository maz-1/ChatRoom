using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace ChatRoomConfigEditor;

/// <summary>Adds or edits a single entry under <c>mcp.servers</c>.</summary>
public sealed class McpServerDialog : Form
{
    private readonly TextBox _nameBox = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _typeBox = new()
    {
        Dock = DockStyle.Left,
        Width = 140,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };

    private readonly TextBox _commandBox = new();
    private readonly TextBox _argsBox = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
        MinimumSize = new Size(0, 70),
        Font = new Font("Consolas", 9f),
    };
    private readonly TextBox _envBox = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
        MinimumSize = new Size(0, 70),
        Font = new Font("Consolas", 9f),
    };
    private readonly TextBox _cwdBox = new() { Width = 420 };

    private readonly TextBox _urlBox = new();
    private readonly TextBox _headersBox = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
        MinimumSize = new Size(0, 90),
        Font = new Font("Consolas", 9f),
    };
    private readonly TextBox _proxyBox = new();

    private readonly TableLayoutPanel _stdioPanel;
    private readonly TableLayoutPanel _httpPanel;
    private readonly Label _errorLabel = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = Color.Firebrick,
        AutoSize = false,
        Height = 40,
    };

    private readonly HashSet<string> _takenNames;
    private readonly string? _originalName;

    public McpServerDialog(
        string? originalName,
        McpServerConfig? server,
        HashSet<string> takenNames)
    {
        _originalName = originalName;
        _takenNames = takenNames;

        Text = originalName is null ? "添加 MCP 服务" : $"编辑 MCP 服务：{originalName}";
        MinimumSize = new Size(680, 560);
        Size = new Size(760, 640);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;

        _typeBox.Items.AddRange(new object[] { "stdio", "http" });

        _stdioPanel = BuildStdioPanel();
        _httpPanel = BuildHttpPanel();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(FieldLabel("名称"), 0, 0);
        root.Controls.Add(_nameBox, 1, 0);
        root.Controls.Add(FieldLabel("传输方式"), 0, 1);
        root.Controls.Add(_typeBox, 1, 1);

        var holder = new Panel { Dock = DockStyle.Fill };
        holder.Controls.Add(_httpPanel);
        holder.Controls.Add(_stdioPanel);
        root.Controls.Add(holder, 0, 2);
        root.SetColumnSpan(holder, 2);

        root.Controls.Add(_errorLabel, 0, 3);
        root.SetColumnSpan(_errorLabel, 2);

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

        Controls.Add(root);
        Controls.Add(buttons);
        CancelButton = cancel;
        AcceptButton = ok;

        _typeBox.SelectedIndexChanged += (_, _) => ApplyTypeVisibility();
        _nameBox.TextChanged += (_, _) => _errorLabel.Text = string.Empty;

        LoadFrom(server);
    }

    public string ServerName { get; private set; } = string.Empty;

    public McpServerConfig? Result { get; private set; }

    private void LoadFrom(McpServerConfig? server)
    {
        switch (server)
        {
            case StdioMcpServerConfig stdio:
                _nameBox.Text = _originalName ?? string.Empty;
                _typeBox.SelectedItem = "stdio";
                _commandBox.Text = stdio.Command;
                _argsBox.Text = string.Join(Environment.NewLine, stdio.Args);
                _envBox.Text = FormatPairs(stdio.Env, "=");
                _cwdBox.Text = stdio.Cwd ?? string.Empty;
                break;

            case HttpMcpServerConfig http:
                _nameBox.Text = _originalName ?? string.Empty;
                _typeBox.SelectedItem = "http";
                _urlBox.Text = http.Url;
                _headersBox.Text = FormatPairs(http.Headers, ": ");
                _proxyBox.Text = http.Proxy ?? string.Empty;
                break;

            default:
                _nameBox.Text = string.Empty;
                _typeBox.SelectedItem = "stdio";
                break;
        }

        ApplyTypeVisibility();
    }

    private void ApplyTypeVisibility()
    {
        var stdio = (string?)_typeBox.SelectedItem == "stdio";
        _stdioPanel.Visible = stdio;
        _httpPanel.Visible = !stdio;
        if (stdio) _stdioPanel.BringToFront();
        else _httpPanel.BringToFront();
    }

    private void Confirm()
    {
        var name = _nameBox.Text.Trim();
        if (name.Length == 0)
        {
            Reject("请填写服务名称。");
            return;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,64}$"))
        {
            Reject("服务名只能包含字母、数字、下划线和连字符，长度 1-64。");
            return;
        }

        if (_takenNames.Contains(name))
        {
            Reject($"已存在名为“{name}”的服务，请换一个名称。");
            return;
        }

        if ((string?)_typeBox.SelectedItem == "stdio")
        {
            var command = _commandBox.Text.Trim();
            if (command.Length == 0)
            {
                Reject("stdio 服务必须填写启动命令。");
                return;
            }

            if (!TryParsePairs(_envBox.Text, '=', out var env, out var envError))
            {
                Reject($"环境变量格式有误：{envError}");
                return;
            }

            ServerName = name;
            Result = new StdioMcpServerConfig
            {
                Command = command,
                Args = _argsBox.Text
                    .Split('\n')
                    .Select(line => line.TrimEnd('\r'))
                    .Where(line => line.Length > 0)
                    .ToList(),
                Env = env,
                Cwd = NullIfBlank(_cwdBox.Text),
            };
        }
        else
        {
            var url = _urlBox.Text.Trim();
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                Reject("http 服务必须填写合法的绝对 URL。");
                return;
            }

            if (!TryParsePairs(_headersBox.Text, ':', out var headers, out var headerError))
            {
                Reject($"请求头格式有误：{headerError}");
                return;
            }

            ServerName = name;
            Result = new HttpMcpServerConfig
            {
                Url = url,
                Headers = headers,
                Proxy = NullIfBlank(_proxyBox.Text),
            };
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void Reject(string message) => _errorLabel.Text = message;

    private TableLayoutPanel BuildStdioPanel()
    {
        var table = NewFieldTable();

        var cwdRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
        };
        var browse = new Button { Text = "浏览…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择工作目录" };
            if (dialog.ShowDialog(this) == DialogResult.OK) _cwdBox.Text = dialog.SelectedPath;
        };
        cwdRow.Controls.Add(_cwdBox);
        cwdRow.Controls.Add(browse);

        AddField(table, "命令", _commandBox);
        AddField(table, "参数", _argsBox, grow: true);
        AddField(table, "环境变量", _envBox, grow: true);
        AddField(table, "工作目录", cwdRow);
        return table;
    }

    private TableLayoutPanel BuildHttpPanel()
    {
        var table = NewFieldTable();
        AddField(table, "URL", _urlBox);
        AddField(table, "请求头", _headersBox, grow: true);
        AddField(table, "代理", _proxyBox);
        return table;
    }

    private static TableLayoutPanel NewFieldTable()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    /// <summary>
    /// Adds one label/field row. Single-line fields get an auto-sized row so the
    /// label and the field stay vertically aligned; only multiline editors grow
    /// to share the remaining height.
    /// </summary>
    private static void AddField(TableLayoutPanel table, string label, Control control, bool grow = false)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(grow
            ? new RowStyle(SizeType.Percent, 100)
            : new RowStyle(SizeType.AutoSize));

        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = grow
                ? AnchorStyles.Top | AnchorStyles.Left
                : AnchorStyles.Left,
            Margin = new Padding(3, grow ? 8 : 9, 8, 3),
        }, 0, row);

        control.Margin = new Padding(3, 4, 3, 4);
        if (grow)
        {
            control.Dock = DockStyle.Fill;
        }
        else if (control is TextBox { Multiline: false } single)
        {
            // Stretch horizontally but keep the natural height of a single line.
            single.Dock = DockStyle.None;
            single.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        }

        table.Controls.Add(control, 1, row);
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 8, 8, 3),
    };

    private static string FormatPairs(Dictionary<string, string> pairs, string separator)
    {
        var builder = new StringBuilder();
        // KeyValuePair deconstruction does not exist on .NET Framework 4.8.
        foreach (var pair in pairs)
            builder.AppendLine(pair.Key + separator + pair.Value);
        return builder.ToString();
    }

    /// <summary>
    /// Parses "KEY=value" (env) or "Header: value" (headers) lines. Splitting on
    /// the first separator keeps values containing it intact.
    /// </summary>
    private static bool TryParsePairs(
        string text,
        char separator,
        out Dictionary<string, string> pairs,
        out string error)
    {
        pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        error = string.Empty;

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line.Trim().Length == 0) continue;

            var at = line.IndexOf(separator);
            if (at <= 0)
            {
                error = $"第 {index + 1} 行缺少“{separator}”：{line.Trim()}";
                return false;
            }

            var key = line.Substring(0, at).Trim();
            var value = line.Substring(at + 1).TrimStart();
            if (key.Length == 0)
            {
                error = $"第 {index + 1} 行的名称为空。";
                return false;
            }

            pairs[key] = value;
        }

        return true;
    }

    private static string? NullIfBlank(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
