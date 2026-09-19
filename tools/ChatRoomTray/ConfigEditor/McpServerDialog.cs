using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Eto.Drawing;
using Eto.Forms;

namespace ChatRoomTray.ConfigEditor;

/// <summary>Adds or edits a single entry under <c>mcp.servers</c>.</summary>
public sealed class McpServerDialog : Dialog<bool>
{
    private readonly TextBox _nameBox = new();
    private readonly DropDown _typeBox = new() { Width = 140 };

    private readonly TextBox _commandBox = new() { ID = "McpCommandBox", Width = 500 };
    private readonly Button _completeCommandButton = new()
    {
        ID = "McpCompleteCommandButton",
        Text = "完整命令…",
    };
    private readonly ListBox _argsList = new()
    {
        ID = "McpArgsList",
        Width = 500,
        Height = 110,
    };
    private readonly List<string> _arguments = new();
    private readonly Button _argNewButton = new() { Text = "新建" };
    private readonly Button _argEditButton = new() { Text = "编辑" };
    private readonly Button _argDeleteButton = new() { Text = "删除" };
    private readonly Button _argUpButton = new() { Text = "上移" };
    private readonly Button _argDownButton = new() { Text = "下移" };
    private readonly TextArea _envBox = new() { Width = 580, Height = 80 };
    private readonly TextBox _cwdBox = new() { Width = 500 };

    private readonly TextBox _urlBox = new() { Width = 580 };
    private readonly GridView _headersList = new()
    {
        ID = "McpHeadersList",
        AllowMultipleSelection = false,
        ShowHeader = true,
        Width = 420,
        Height = 130,
    };
    private readonly List<HeaderRow> _headers = new();
    private readonly Button _headerNewButton = new() { Text = "添加" };
    private readonly Button _headerEditButton = new() { Text = "编辑" };
    private readonly Button _headerDeleteButton = new() { Text = "删除" };
    private readonly Button _authTokenButton = new() { Text = "填写 Auth Token…" };
    private readonly TextBox _proxyBox = new() { Width = 580 };

    private readonly Panel _transportPanel = new() { Width = 700 };
    private readonly DynamicLayout _stdioPanel;
    private readonly DynamicLayout _httpPanel;
    private readonly Label _errorLabel = new()
    {
        TextColor = Colors.Firebrick,
        Height = 40,
        Wrap = WrapMode.Word,
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

        Title = originalName is null ? "添加 MCP 服务" : $"编辑 MCP 服务：{originalName}";
        MinimumSize = new Size(680, 540);
        AutoSize = false;
        Resizable = true;
        ShowInTaskbar = false;

        _typeBox.DataStore = new[] { "stdio", "http" };
        _stdioPanel = BuildStdioPanel();
        _httpPanel = BuildHttpPanel();

        var ok = new Button { Text = "确定" };
        var cancel = new Button { Text = "取消" };
        ok.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Close(false);
        DefaultButton = ok;
        AbortButton = cancel;

        var fields = new DynamicLayout
        {
            Padding = new Padding(12),
            Spacing = new Size(8, 8),
        };
        AddField(fields, "名称", _nameBox);
        AddField(fields, "传输方式", _typeBox);
        fields.Add(_transportPanel);
        fields.AddRow(_errorLabel);
        fields.Add(null, yscale: true);
        fields.AddSeparateRow(null, cancel, ok);
        Content = fields;

        _typeBox.SelectedIndexChanged += (_, _) => ApplyTypeVisibility();
        _nameBox.TextChanged += (_, _) => _errorLabel.Text = string.Empty;

        LoadFrom(server);
        Size = new Size(680, 540);
    }

    public string ServerName { get; private set; } = string.Empty;

    public McpServerConfig? ServerConfig { get; private set; }

    private void LoadFrom(McpServerConfig? server)
    {
        switch (server)
        {
            case StdioMcpServerConfig stdio:
                _nameBox.Text = _originalName ?? string.Empty;
                _typeBox.SelectedIndex = 0;
                _commandBox.Text = stdio.Command;
                _arguments.Clear();
                _arguments.AddRange(stdio.Args);
                RefreshArguments();
                _envBox.Text = FormatPairs(stdio.Env, "=");
                _cwdBox.Text = stdio.Cwd ?? string.Empty;
                break;

            case HttpMcpServerConfig http:
                _nameBox.Text = _originalName ?? string.Empty;
                _typeBox.SelectedIndex = 1;
                _urlBox.Text = http.Url;
                LoadHeaders(http.Headers);
                _proxyBox.Text = http.Proxy ?? string.Empty;
                break;

            default:
                _nameBox.Text = string.Empty;
                _typeBox.SelectedIndex = 0;
                RefreshArguments();
                LoadHeaders(new Dictionary<string, string>());
                break;
        }

        ApplyTypeVisibility();
    }

    private void ApplyTypeVisibility()
    {
        _transportPanel.Content = _typeBox.SelectedIndex == 1 ? _httpPanel : _stdioPanel;
    }

    private void Confirm()
    {
        var name = (_nameBox.Text ?? string.Empty).Trim();
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

        if (_typeBox.SelectedIndex != 1)
        {
            var command = (_commandBox.Text ?? string.Empty).Trim();
            if (command.Length == 0)
            {
                Reject("stdio 服务必须填写启动命令。");
                return;
            }

            if (!TryParsePairs(_envBox.Text ?? string.Empty, '=', out var env, out var envError))
            {
                Reject($"环境变量格式有误：{envError}");
                return;
            }

            ServerName = name;
            ServerConfig = new StdioMcpServerConfig
            {
                Command = command,
                Args = _arguments.ToList(),
                Env = env,
                Cwd = NullIfBlank(_cwdBox.Text ?? string.Empty),
            };
        }
        else
        {
            var url = (_urlBox.Text ?? string.Empty).Trim();
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                Reject("http 服务必须填写合法的绝对 URL。");
                return;
            }

            if (!TryCollectHeaders(out var headers, out var headerError))
            {
                Reject($"请求头格式有误：{headerError}");
                return;
            }

            ServerName = name;
            ServerConfig = new HttpMcpServerConfig
            {
                Url = url,
                Headers = headers,
                Proxy = NullIfBlank(_proxyBox.Text ?? string.Empty),
            };
        }

        Close(true);
    }

    private void Reject(string message) => _errorLabel.Text = message;

    private DynamicLayout BuildStdioPanel()
    {
        var browse = new Button { Text = "浏览…" };
        browse.Click += (_, _) =>
        {
            using var dialog = new SelectFolderDialog
            {
                Title = "选择工作目录",
                Directory = DirectoryOrHome(_cwdBox.Text),
            };
            if (dialog.ShowDialog(this) == DialogResult.Ok)
                _cwdBox.Text = dialog.Directory;
        };

        var cwdRow = Horizontal(_cwdBox, browse);

        var table = NewFieldLayout();
        AddField(table, "命令", BuildCommandEditor());
        AddField(table, "参数", BuildArgsEditor(), grow: true);
        AddField(table, "环境变量", _envBox, grow: true);
        AddField(table, "工作目录", cwdRow);
        return table;
    }

    private Control BuildCommandEditor()
    {
        _completeCommandButton.MinimumSize = new Size(96, 0);
        _completeCommandButton.Click += (_, _) => ImportCompleteCommand();

        var layout = new StackLayout
        {
            ID = "McpCommandEditor",
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        layout.Items.Add(new StackLayoutItem(_commandBox, true));
        layout.Items.Add(new StackLayoutItem(_completeCommandButton, false));
        return layout;
    }

    private void ImportCompleteCommand()
    {
        using var dialog = new TextInputDialog(
            "输入完整命令",
            "完整命令",
            string.Empty,
            clientWidth: 720);
        if (!dialog.ShowModal(this)) return;

        if (!CompleteCommandParser.TryParse(
                dialog.Value,
                out var command,
                out var arguments,
                out var error))
        {
            Reject(error);
            return;
        }

        _commandBox.Text = command;
        _arguments.Clear();
        _arguments.AddRange(arguments);
        RefreshArguments();
        _errorLabel.Text = string.Empty;
        _commandBox.Focus();
    }

    private Control BuildArgsEditor()
    {
        foreach (var button in new[]
        {
            _argNewButton,
            _argEditButton,
            _argDeleteButton,
            _argUpButton,
            _argDownButton,
        })
            button.MinimumSize = new Size(72, 0);

        _argNewButton.Click += (_, _) => AddArgument();
        _argEditButton.Click += (_, _) => EditArgument();
        _argDeleteButton.Click += (_, _) => DeleteArgument();
        _argUpButton.Click += (_, _) => MoveArgument(-1);
        _argDownButton.Click += (_, _) => MoveArgument(1);
        _argsList.Activated += (_, _) => EditArgument();
        _argsList.SelectedIndexChanged += (_, _) => UpdateArgumentButtons();
        _argsList.KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Insert)
            {
                AddArgument();
                e.Handled = true;
            }
            else if (e.Key == Keys.F2)
            {
                EditArgument();
                e.Handled = true;
            }
            else if (e.Key == Keys.Delete)
            {
                DeleteArgument();
                e.Handled = true;
            }
        };

        var layout = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        layout.Items.Add(new StackLayoutItem(_argsList, true));
        layout.Items.Add(new StackLayoutItem(
            Vertical(_argNewButton, _argEditButton, _argDeleteButton, _argUpButton, _argDownButton),
            false));
        UpdateArgumentButtons();
        return layout;
    }

    private void AddArgument()
    {
        using var dialog = new TextInputDialog("新建参数", "参数", string.Empty);
        if (!dialog.ShowModal(this) || dialog.Value.Length == 0) return;

        _arguments.Add(dialog.Value);
        RefreshArguments(_arguments.Count - 1);
        _argsList.Focus();
    }

    private void EditArgument()
    {
        var index = _argsList.SelectedIndex;
        if (index < 0 || index >= _arguments.Count) return;

        using var dialog = new TextInputDialog("编辑参数", "参数", _arguments[index]);
        if (!dialog.ShowModal(this) || dialog.Value.Length == 0) return;

        _arguments[index] = dialog.Value;
        RefreshArguments(index);
        _argsList.Focus();
    }

    private void DeleteArgument()
    {
        var index = _argsList.SelectedIndex;
        if (index < 0 || index >= _arguments.Count) return;

        _arguments.RemoveAt(index);
        RefreshArguments(_arguments.Count == 0 ? -1 : Math.Min(index, _arguments.Count - 1));
        _argsList.Focus();
    }

    private void MoveArgument(int delta)
    {
        var index = _argsList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _arguments.Count) return;

        var value = _arguments[index];
        _arguments.RemoveAt(index);
        _arguments.Insert(target, value);
        RefreshArguments(target);
        _argsList.Focus();
    }

    private void RefreshArguments(int selectedIndex = -1)
    {
        _argsList.DataStore = _arguments.ToList();
        _argsList.SelectedIndex =
            selectedIndex >= 0 && selectedIndex < _arguments.Count ? selectedIndex : -1;
        UpdateArgumentButtons();
    }

    private void UpdateArgumentButtons()
    {
        var index = _argsList.SelectedIndex;
        var selected = index >= 0;
        _argEditButton.Enabled = selected;
        _argDeleteButton.Enabled = selected;
        _argUpButton.Enabled = selected && index > 0;
        _argDownButton.Enabled = selected && index < _arguments.Count - 1;
    }

    private Control BuildHeadersEditor()
    {
        _headersList.Columns.Add(new GridColumn
        {
            HeaderText = "键",
            Width = 140,
            DataCell = new TextBoxCell { Binding = Binding.Property<HeaderRow, string>(row => row.Key) },
        });
        _headersList.Columns.Add(new GridColumn
        {
            HeaderText = "值",
            Width = 260,
            Expand = true,
            MinWidth = 200,
            MaxWidth = 300,
            DataCell = new TextBoxCell { Binding = Binding.Property<HeaderRow, string>(row => row.Value) },
        });

        foreach (var button in new[]
        {
            _headerNewButton,
            _headerEditButton,
            _headerDeleteButton,
            _authTokenButton,
        })
            button.MinimumSize = new Size(92, 0);

        _headerNewButton.Click += (_, _) => AddHeader();
        _headerEditButton.Click += (_, _) => EditHeader();
        _headerDeleteButton.Click += (_, _) => DeleteHeader();
        _authTokenButton.Click += (_, _) => SetAuthToken();
        _headersList.CellDoubleClick += (_, _) => EditHeader();
        _headersList.SelectionChanged += (_, _) => UpdateHeaderButtons();
        _headersList.KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Insert)
            {
                AddHeader();
                e.Handled = true;
            }
            else if (e.Key == Keys.F2)
            {
                EditHeader();
                e.Handled = true;
            }
            else if (e.Key == Keys.Delete)
            {
                DeleteHeader();
                e.Handled = true;
            }
        };

        var layout = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        layout.Items.Add(new StackLayoutItem(_headersList, true));
        layout.Items.Add(new StackLayoutItem(
            Vertical(_headerNewButton, _headerEditButton, _headerDeleteButton, _authTokenButton),
            false));
        UpdateHeaderButtons();
        return layout;
    }

    private void LoadHeaders(Dictionary<string, string> headers)
    {
        _headers.Clear();
        foreach (var pair in headers)
            _headers.Add(new HeaderRow { Key = pair.Key, Value = pair.Value });
        RefreshHeaders();
    }

    private void AddHeader()
    {
        using var dialog = new HeaderInputDialog("新建请求头", string.Empty, string.Empty);
        if (!dialog.ShowModal(this)) return;

        if (FindHeaderIndex(dialog.HeaderKey) >= 0)
        {
            Reject($"请求头“{dialog.HeaderKey}”已存在。");
            return;
        }

        _headers.Add(new HeaderRow { Key = dialog.HeaderKey, Value = dialog.HeaderValue });
        RefreshHeaders(_headers.Count - 1);
        _errorLabel.Text = string.Empty;
    }

    private void EditHeader()
    {
        var index = _headersList.SelectedRow;
        if (index < 0 || index >= _headers.Count) return;
        var item = _headers[index];

        using var dialog = new HeaderInputDialog("编辑请求头", item.Key, item.Value);
        if (!dialog.ShowModal(this)) return;

        var duplicate = FindHeaderIndex(dialog.HeaderKey, index);
        if (duplicate >= 0)
        {
            Reject($"请求头“{dialog.HeaderKey}”已存在。");
            return;
        }

        item.Key = dialog.HeaderKey;
        item.Value = dialog.HeaderValue;
        RefreshHeaders(index);
        _errorLabel.Text = string.Empty;
    }

    private void DeleteHeader()
    {
        var index = _headersList.SelectedRow;
        if (index < 0 || index >= _headers.Count) return;

        _headers.RemoveAt(index);
        RefreshHeaders(_headers.Count == 0 ? -1 : Math.Min(index, _headers.Count - 1));
        _headersList.Focus();
    }

    private void SetAuthToken()
    {
        var index = FindHeaderIndex("Authorization");
        var initial = string.Empty;
        if (index >= 0)
        {
            var current = _headers[index].Value.Trim();
            if (current.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                initial = current.Substring("Bearer ".Length).Trim();
        }

        using var dialog = new TextInputDialog("设置 Auth Token", "Token", initial);
        if (!dialog.ShowModal(this)) return;

        var token = dialog.Value.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token.Substring("Bearer ".Length).Trim();

        if (token.Length == 0)
        {
            Reject("Auth Token 不能为空。");
            return;
        }

        var value = "Bearer " + token;
        if (index >= 0)
            _headers[index].Value = value;
        else
        {
            _headers.Add(new HeaderRow { Key = "Authorization", Value = value });
            index = _headers.Count - 1;
        }

        RefreshHeaders(index);
        _errorLabel.Text = string.Empty;
    }

    private int FindHeaderIndex(string key, int exceptIndex = -1)
    {
        for (var index = 0; index < _headers.Count; index++)
        {
            if (index == exceptIndex) continue;
            if (string.Equals(_headers[index].Key, key, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private void RefreshHeaders(int selectedIndex = -1)
    {
        _headersList.DataStore = _headers.ToList();
        _headersList.SelectedRow =
            selectedIndex >= 0 && selectedIndex < _headers.Count ? selectedIndex : -1;
        if (selectedIndex >= 0 && selectedIndex < _headers.Count)
            _headersList.ScrollToRow(selectedIndex);
        UpdateHeaderButtons();
    }

    private void UpdateHeaderButtons()
    {
        var selected = _headersList.SelectedRow >= 0;
        _headerEditButton.Enabled = selected;
        _headerDeleteButton.Enabled = selected;
    }

    private bool TryCollectHeaders(
        out Dictionary<string, string> headers,
        out string error)
    {
        headers = new Dictionary<string, string>(StringComparer.Ordinal);
        error = string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _headers)
        {
            var key = item.Key.Trim();
            if (key.Length == 0)
            {
                error = "请求头的键不能为空。";
                return false;
            }

            if (!seen.Add(key))
            {
                error = $"存在重复的请求头“{key}”。";
                return false;
            }

            headers[key] = item.Value;
        }

        return true;
    }

    private DynamicLayout BuildHttpPanel()
    {
        var table = NewFieldLayout();
        AddField(table, "URL", _urlBox);
        AddField(table, "请求头", BuildHeadersEditor(), grow: true);
        AddField(table, "代理", _proxyBox);
        return table;
    }

    private static DynamicLayout NewFieldLayout() => new()
    {
        Padding = new Padding(4),
        Spacing = new Size(8, 8),
    };

    private static void AddField(DynamicLayout table, string label, Control control, bool grow = false)
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalContentAlignment = grow ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        row.Items.Add(new StackLayoutItem(FieldLabel(label), false));
        row.Items.Add(new StackLayoutItem(control, true));
        table.AddRow(row);
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        Width = 88,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static StackLayout Horizontal(params Control[] controls)
    {
        var layout = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        foreach (var control in controls)
            layout.Items.Add(new StackLayoutItem(control, false));
        return layout;
    }

    private static StackLayout Vertical(params Control[] controls)
    {
        var layout = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
        };
        foreach (var control in controls)
            layout.Items.Add(new StackLayoutItem(control, false));
        return layout;
    }

    private static string DirectoryOrHome(string? path)
    {
        var value = path ?? string.Empty;
        if (System.IO.Directory.Exists(value)) return value;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string FormatPairs(Dictionary<string, string> pairs, string separator)
    {
        var builder = new StringBuilder();
        foreach (var pair in pairs)
            builder.AppendLine(pair.Key + separator + pair.Value);
        return builder.ToString();
    }

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

    private sealed class HeaderRow
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
