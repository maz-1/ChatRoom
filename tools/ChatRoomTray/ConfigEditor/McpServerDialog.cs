using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;

namespace ChatRoomTray.ConfigEditor;

/// <summary>Adds or edits a single entry under <c>mcp.servers</c>.</summary>
public sealed partial class McpServerDialog : Dialog<bool>
{
    private readonly TextBox _nameBox = new();
    private readonly DropDown _typeBox = new() { Width = 140 };

    private readonly TextBox _commandBox = new() { ID = "McpCommandBox" };
    private readonly Button _completeCommandButton = new()
    {
        ID = "McpCompleteCommandButton",
        Text = "完整命令…",
    };
    private readonly ListBox _argsList = new()
    {
        ID = "McpArgsList",
        Height = 48, // Compact initial size; RefreshArguments accounts for row count.
    };
    private readonly List<string> _arguments = new();
    private readonly Button _argNewButton = new() { Text = "新建" };
    private readonly Button _argEditButton = new() { Text = "编辑" };
    private readonly Button _argDeleteButton = new() { Text = "删除" };
    private readonly Button _argUpButton = new() { Text = "上移" };
    private readonly Button _argDownButton = new() { Text = "下移" };
    private readonly TextBox _cwdBox = new() { ID = "McpWorkingDirectoryBox" };

    private readonly TextBox _urlBox = new();
    private readonly GridView _headersList = new()
    {
        ID = "McpHeadersList",
        AllowMultipleSelection = false,
        ShowHeader = true,
        Height = 160,
    };
    private readonly List<HeaderRow> _headers = new();
    private readonly Button _headerNewButton = new() { Text = "添加" };
    private readonly Button _headerEditButton = new() { Text = "编辑" };
    private readonly Button _headerDeleteButton = new() { Text = "删除" };
    private readonly Button _authTokenButton = new() { Text = "填写 Auth Token…" };
    private readonly TextBox _proxyBox = new();

    private readonly Panel _transportPanel = new();
    private readonly DynamicLayout _stdioPanel;
    private readonly DynamicLayout _httpPanel;
    private readonly Label _errorLabel = new()
    {
        TextColor = Colors.Firebrick,
        Visible = false,
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
        EditorLayout.SizeWindow(this, new Size(760, 620), new Size(600, 400));
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
        var identity = new DynamicLayout { Spacing = new Size(8, 8) };
        AddField(identity, "名称", _nameBox);
        AddField(identity, "传输方式", _typeBox);
        fields.Add(identity, xscale: true);
        fields.Add(_transportPanel, xscale: true, yscale: true);

        var footer = new DynamicLayout
        {
            ID = "McpDialogFooter",
            Padding = new Padding(12, 4, 12, 12),
            Spacing = new Size(8, 8),
        };
        footer.AddRow(_errorLabel);
        footer.AddSeparateRow(null, cancel, ok);
        var root = new DynamicLayout();
        var scroll = EditorLayout.VerticalScroll(fields, "McpDialogScroll");
        scroll.ExpandContentHeight = true;
        root.Add(scroll, yscale: true);
        root.AddRow(footer);
        Content = root;

        _typeBox.SelectedIndexChanged += (_, _) => ApplyTypeVisibility();
        _nameBox.TextChanged += (_, _) => Reject(string.Empty);

        LoadFrom(server);
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
                LoadEnvironmentVariables(stdio.Env);
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

            if (!TryCollectEnvironmentVariables(out var env, out var envError))
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

    private void Reject(string message)
    {
        _errorLabel.Text = message;
        _errorLabel.Visible = !string.IsNullOrEmpty(message);
    }

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

        var cwdRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { new StackLayoutItem(_cwdBox, true), new StackLayoutItem(browse, false) },
        };

        var table = NewFieldLayout();
        AddField(table, "命令", BuildCommandEditor());
        AddField(table, "参数", BuildArgsEditor(), grow: true);
        AddField(table, "环境变量", BuildEnvironmentEditor(), grow: true, stretch: true);
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
        Reject(string.Empty);
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

        var layout = new DynamicLayout { Spacing = new Size(6, 6) };
        layout.AddRow(_argsList);
        layout.AddRow(Horizontal(_argNewButton, _argEditButton, _argDeleteButton, _argUpButton, _argDownButton));
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
        // Do not let an empty/small argument list consume half the dialog. Longer
        // argument lists scroll internally; the environment table gets free height.
        var rowHeight = (int)Math.Ceiling(_argsList.Font.LineHeight) + 4;
#if WINDOWS_TRAY
        var nativeList = _argsList.ControlObject as System.Windows.Forms.ListBox;
        if (nativeList is not null)
        {
            rowHeight = nativeList.ItemHeight;
            nativeList.MaximumSize = System.Drawing.Size.Empty;
            nativeList.IntegralHeight = false;
        }
#endif
        var height = Math.Max(48, rowHeight * Math.Clamp(_arguments.Count, 1, 3) + 4);
        _argsList.Height = height;
#if WINDOWS_TRAY
        // WinForms ListBox.GetPreferredSize grows with item count even when Eto
        // Height is set. Cap only height; leave width free to follow the dialog.
        if (nativeList is not null)
            nativeList.MaximumSize = new System.Drawing.Size(0, height);
#endif
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
            AutoSize = false,
            Expand = true,
            MinWidth = 160,
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

        var layout = new DynamicLayout { Spacing = new Size(6, 6) };
        layout.Add(_headersList, yscale: true);
        layout.AddRow(Horizontal(_headerNewButton, _headerEditButton, _headerDeleteButton, _authTokenButton));
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
        Reject(string.Empty);
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
        Reject(string.Empty);
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
        Reject(string.Empty);
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
        AddField(table, "请求头", BuildHeadersEditor(), grow: true, stretch: true);
        AddField(table, "代理", _proxyBox);
        return table;
    }

    private static DynamicLayout NewFieldLayout() => new()
    {
        Padding = new Padding(4),
        Spacing = new Size(8, 8),
    };

    private static void AddField(DynamicLayout table, string label, Control control, bool grow = false, bool stretch = false)
    {
        var caption = FieldLabel(label);
        caption.VerticalAlignment = grow ? VerticalAlignment.Top : VerticalAlignment.Center;
        table.BeginHorizontal(yscale: stretch);
        table.Add(caption, xscale: false);
        table.Add(control, xscale: true);
        table.EndHorizontal();
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

    private static string DirectoryOrHome(string? path)
    {
        var value = path ?? string.Empty;
        if (System.IO.Directory.Exists(value)) return value;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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
