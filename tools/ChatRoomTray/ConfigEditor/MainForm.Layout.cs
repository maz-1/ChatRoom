using System;
using Eto.Drawing;
using Eto.Forms;

namespace ChatRoomTray.ConfigEditor;

/// <summary>Cross-platform Eto.Forms UI construction for <see cref="MainForm"/>.</summary>
public partial class MainForm : Form
{
    private readonly Button _newButton = new() { Text = "新建默认配置" };
    private readonly Button _openButton = new() { Text = "打开…" };
    private readonly Button _reloadButton = new() { Text = "重新加载" };
    private readonly Button _saveButton = new() { Text = "保存" };
    private readonly Button _saveAsButton = new() { Text = "另存为…" };
    private readonly Button _checkButton = new() { Text = "校验" };
    private readonly Button _folderButton = new() { Text = "打开所在文件夹" };

    private readonly TextBox _pathBox = new()
    {
        ReadOnly = true,
        ID = "ConfigPathBox",
    };

    private readonly TabControl _tabs = new() { ID = "ConfigTabs" };

    private readonly ListBox _rootsList = new() { ID = "AllowedRootsList" };
    private readonly TextBox _dataDirBox = new();
    private readonly TextBox _databasePathBox = new();

    private readonly TextBox _hostBox = new() { Width = 220 };
    private readonly NumericStepper _portBox = new() { Width = 110, DecimalPlaces = 0 };
    private readonly CheckBox _localWebAuthBox = new() { Text = "启用本地 WebUI 登录（localWebAuth）" };
    private readonly Label _tokenStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _copyTokenButton = new() { Text = "复制" };
    private readonly Button _regenerateTokenButton = new() { Text = "重新生成" };
    private readonly Button _clearTokenButton = new() { Text = "清除" };
    private readonly TextBox _mcpPublicBaseUrlBox = new();
    private readonly TextBox _webPublicBaseUrlBox = new();
    private readonly ListBox _redirectHostsList = new() { ID = "RedirectHostsList" };

    private readonly NumericStepper _httpDefaultTimeout = IntegerStepper(150);
    private readonly NumericStepper _httpMaxTimeout = IntegerStepper(150);
    private readonly NumericStepper _httpMaxResponseBytes = IntegerStepper(170);
    private readonly NumericStepper _operationsMaxPayload = IntegerStepper(170);
    private readonly NumericStepper _processMaxOutput = IntegerStepper(170);
    private readonly NumericStepper _processDefaultTimeout = IntegerStepper(150);
    private readonly NumericStepper _processMaxCompleted = IntegerStepper(120);
    private readonly NumericStepper _mcpCallTimeout = IntegerStepper(150);
    private readonly NumericStepper _mcpMaxResultBytes = IntegerStepper(170);

    private readonly GridView _serversView = new()
    {
        ID = "McpServersView",
        AllowMultipleSelection = false,
        ShowHeader = true,
    };

    private readonly Button _addServerButton = new() { Text = "添加服务…" };
    private readonly Button _editServerButton = new() { Text = "编辑…" };
    private readonly Button _removeServerButton = new() { Text = "删除" };

    private readonly GridView _issuesView = new()
    {
        ID = "ValidationIssuesView",
        AllowMultipleSelection = false,
        ShowHeader = true,
    };

    private readonly Label _statusLabel = new()
    {
        Text = "就绪",
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void BuildUi()
    {
        Title = "ChatRoom 配置编辑器";
        MinimumSize = new Size(960, 700);
        ClientSize = new Size(1100, 800);

        ConfigureLimits();
        BuildTabs();
        BuildServerGrid();
        BuildIssuesGrid();

        var toolbar = Horizontal(
            _newButton,
            _openButton,
            _reloadButton,
            Spacer(10),
            _saveButton,
            _saveAsButton,
            _checkButton,
            Spacer(10),
            _folderButton);
        toolbar.Padding = new Padding(8, 8, 8, 4);

        var pathRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Padding(8, 4, 8, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        pathRow.Items.Add(new StackLayoutItem(new Label
        {
            Text = "配置文件",
            Width = 80,
            VerticalAlignment = VerticalAlignment.Center,
        }, false));
        pathRow.Items.Add(new StackLayoutItem(_pathBox, true));

        var issuesGroup = new GroupBox
        {
            Text = "校验结果",
            Padding = new Padding(8),
            Content = _issuesView,
        };

        var split = new Splitter
        {
            Orientation = Orientation.Vertical,
            Panel1 = _tabs,
            Panel2 = issuesGroup,
            Position = 560,
            FixedPanel = SplitterFixedPanel.Panel2,
        };

        var root = new DynamicLayout
        {
            Spacing = new Size(0, 0),
        };
        root.AddRow(toolbar);
        root.AddRow(pathRow);
        root.Add(split, yscale: true);
        root.AddRow(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Padding = new Padding(10, 5),
            Items = { new StackLayoutItem(_statusLabel, true) },
        });
        Content = root;
    }

    private void BuildServerGrid()
    {
        _serversView.Columns.Add(new GridColumn
        {
            HeaderText = "名称",
            Width = 170,
            DataCell = new TextBoxCell { Binding = Binding.Property<ServerGridRow, string>(row => row.Name) },
        });
        _serversView.Columns.Add(new GridColumn
        {
            HeaderText = "传输",
            Width = 90,
            DataCell = new TextBoxCell { Binding = Binding.Property<ServerGridRow, string>(row => row.Transport) },
        });
        _serversView.Columns.Add(new GridColumn
        {
            HeaderText = "目标",
            // Eto WinForms must not call AutoResizeColumn(Fill) after the first row paints.
            AutoSize = false,
            Expand = true,
            MinWidth = 320,
            DataCell = new TextBoxCell { Binding = Binding.Property<ServerGridRow, string>(row => row.Target) },
        });
        _serversView.CellDoubleClick += (_, _) => EditServer();
        _serversView.SelectionChanged += (_, _) => UpdateServerButtons();
    }

    private void BuildIssuesGrid()
    {
        _issuesView.Columns.Add(new GridColumn
        {
            HeaderText = "级别",
            Width = 70,
            DataCell = new TextBoxCell { Binding = Binding.Property<IssueGridRow, string>(row => row.Severity) },
        });
        _issuesView.Columns.Add(new GridColumn
        {
            HeaderText = "字段",
            Width = 260,
            DataCell = new TextBoxCell { Binding = Binding.Property<IssueGridRow, string>(row => row.Path) },
        });
        _issuesView.Columns.Add(new GridColumn
        {
            HeaderText = "说明",
            AutoSize = true,
            DataCell = new TextBoxCell { Binding = Binding.Property<IssueGridRow, string>(row => row.Message) },
        });
        _issuesView.CellFormatting += (_, e) =>
        {
            if (e.Item is not IssueGridRow row) return;
            e.ForegroundColor = row.IsError ? Colors.Firebrick : Colors.DarkGoldenrod;
        };
    }

    private void BuildTabs()
    {
        _tabs.Pages.Add(BuildPathsTab());
        _tabs.Pages.Add(BuildAuthTab());
        _tabs.Pages.Add(BuildLimitsTab());
        _tabs.Pages.Add(BuildMcpTab());
    }

    private TabPage BuildPathsTab()
    {
        var builder = new TableBuilder();

        var rootsButtons = VerticalButtons(
            ("添加目录…", (_, _) => AddRoot()),
            ("编辑…", (_, _) => EditRoot()),
            ("删除", (_, _) => RemoveSelected(_rootsList, "工作区根目录")));

        var dataDirButton = new Button { Text = "浏览…" };
        dataDirButton.Click += (_, _) => BrowseInto(_dataDirBox, saveFile: false);

        var databaseBrowse = new Button { Text = "浏览…" };
        var databaseClear = new Button { Text = "清除" };
        databaseBrowse.Click += (_, _) => BrowseInto(_databasePathBox, saveFile: true);
        databaseClear.Click += (_, _) => _databasePathBox.Text = string.Empty;
        var databaseButtons = Horizontal(databaseBrowse, databaseClear);

        builder.Row("工作区根目录", _rootsList, rootsButtons, height: 170);
        builder.Row("数据目录", _dataDirBox, dataDirButton);
        builder.Row("数据库文件", _databasePathBox, databaseButtons);
        builder.Note("allowedRoots 决定 ChatGPT 可访问哪些目录；dataDir 存放运行数据；databasePath 留空时 ChatRoom 使用 <dataDir>/chatroom.sqlite。");

        return WrapTab("工作区与路径", builder.Layout);
    }

    private TabPage BuildAuthTab()
    {
        var builder = new TableBuilder();

        var portLabel = new Label
        {
            Text = "端口",
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hostRow = Horizontal(_hostBox, portLabel, _portBox);
        var tokenRow = Horizontal(_tokenStatus, _copyTokenButton, _regenerateTokenButton, _clearTokenButton);

        var redirectButtons = VerticalButtons(
            ("添加…", (_, _) => AddRedirectHost()),
            ("编辑…", (_, _) => EditRedirectHost()),
            ("删除", (_, _) => RemoveSelected(_redirectHostsList, "重定向主机")));

        builder.Row("监听地址", hostRow);
        builder.Row("本地登录", _localWebAuthBox);
        builder.Row("ownerToken", tokenRow);
        builder.Row("MCP 公网地址", _mcpPublicBaseUrlBox);
        builder.Row("WebUI 公网地址", _webPublicBaseUrlBox);
        builder.Row("重定向主机", _redirectHostsList, redirectButtons, height: 140);
        builder.Note("ownerToken 用于 ChatGPT(OAuth) 与 WebUI 的登录授权。本界面不会显示它的内容，只能复制到剪贴板或重新生成；重新生成后已授权的客户端需要重新授权。");

        return WrapTab("服务与认证", builder.Layout);
    }

    private TabPage BuildLimitsTab()
    {
        var builder = new TableBuilder();

        builder.Header("HTTP 请求（http_request 工具）");
        builder.Row("默认超时 (ms)", _httpDefaultTimeout);
        builder.Row("超时上限 (ms)", _httpMaxTimeout);
        builder.Row("响应体上限 (bytes)", _httpMaxResponseBytes);

        builder.Header("操作日志");
        builder.Row("单条记录上限 (bytes)", _operationsMaxPayload);

        builder.Header("进程");
        builder.Row("输出上限 (bytes)", _processMaxOutput);
        builder.Row("默认超时 (ms)", _processDefaultTimeout);
        builder.Row("保留已结束进程数", _processMaxCompleted);

        builder.Header("MCP 代理");
        builder.Row("调用超时 (ms)", _mcpCallTimeout);
        builder.Row("结果上限 (bytes)", _mcpMaxResultBytes);

        builder.Note("所有数值上限与 ChatRoom 的配置校验完全一致，超出范围的值在保存前就会被拦下。");
        return WrapTab("限额", builder.Layout);
    }

    private TabPage BuildMcpTab()
    {
        var buttons = VerticalButtons(
            ("添加服务…", (_, _) => AddServer()),
            ("编辑…", (_, _) => EditServer()),
            ("删除", (_, _) => RemoveServer()));

        var top = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        top.Items.Add(new StackLayoutItem(_serversView, true));
        top.Items.Add(new StackLayoutItem(buttons, false));

        var note = new Label
        {
            Text = "stdio 服务会以 ChatRoom 进程身份启动本地命令；http 支持自定义请求头与代理。env/headers 通常存放密钥，只保存在本机配置文件中。修改后需要重启 ChatRoom 才会生效。",
            TextColor = Colors.Gray,
            Wrap = WrapMode.Word,
        };

        var layout = new DynamicLayout
        {
            Padding = new Padding(10),
            Spacing = new Size(8, 10),
        };
        layout.Add(top, yscale: true);
        layout.AddRow(note);
        return new TabPage
        {
            Text = "MCP 服务",
            Content = layout,
        };
    }

    private void ConfigureLimits()
    {
        SetRange(_httpDefaultTimeout, ConfigValidator.MillisecondsMin, ConfigValidator.MillisecondsMax);
        SetRange(_httpMaxTimeout, ConfigValidator.MillisecondsMin, ConfigValidator.MillisecondsMax);
        SetRange(_httpMaxResponseBytes, ConfigValidator.SmallBytesMin, ConfigValidator.PayloadBytesMax);
        SetRange(_operationsMaxPayload, ConfigValidator.SmallBytesMin, ConfigValidator.PayloadBytesMax);
        SetRange(_processMaxOutput, ConfigValidator.SmallBytesMin, ConfigValidator.OutputBytesMax);
        SetRange(_processDefaultTimeout, ConfigValidator.MillisecondsMin, ConfigValidator.MillisecondsMax);
        SetRange(_processMaxCompleted, 0, ConfigValidator.MaxCompletedProcessesMax);
        SetRange(_mcpCallTimeout, ConfigValidator.MillisecondsMin, ConfigValidator.MillisecondsMax);
        SetRange(_mcpMaxResultBytes, ConfigValidator.SmallBytesMin, ConfigValidator.OutputBytesMax);
        SetRange(_portBox, ConfigValidator.PortMin, ConfigValidator.PortMax);
    }

    private static NumericStepper IntegerStepper(int width) => new()
    {
        Width = width,
        DecimalPlaces = 0,
        Increment = 1,
    };

    private static void SetRange(NumericStepper box, int min, int max)
    {
        box.MinValue = min;
        box.MaxValue = max;
    }

    private static TabPage WrapTab(string title, Control content)
    {
        var scroll = new Scrollable
        {
            Content = content,
            ExpandContentWidth = true,
        };
        return new TabPage
        {
            Text = title,
            Padding = new Padding(6),
            Content = scroll,
        };
    }

    private static StackLayout Horizontal(params Control[] controls)
    {
        var panel = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        foreach (var control in controls)
            panel.Items.Add(new StackLayoutItem(control, false));
        return panel;
    }

    private static StackLayout VerticalButtons(params (string Text, EventHandler<EventArgs> Handler)[] buttons)
    {
        var panel = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
        };
        foreach (var (text, handler) in buttons)
        {
            var button = new Button { Text = text, MinimumSize = new Size(90, 0) };
            button.Click += handler;
            panel.Items.Add(new StackLayoutItem(button, false));
        }
        return panel;
    }

    private static Panel Spacer(int width) => new() { Width = width };

    private sealed class TableBuilder
    {
        public DynamicLayout Layout { get; } = new()
        {
            Padding = new Padding(12),
            Spacing = new Size(8, 8),
        };

        public void Row(string label, Control content, Control? trailing = null, int? height = null)
        {
            if (height is not null)
                content.Height = height.Value;

            var row = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalContentAlignment = height is null
                    ? VerticalAlignment.Center
                    : VerticalAlignment.Top,
            };
            row.Items.Add(new StackLayoutItem(new Label
            {
                Text = label,
                Width = 145,
                VerticalAlignment = height is null
                    ? VerticalAlignment.Center
                    : VerticalAlignment.Top,
            }, false));
            row.Items.Add(new StackLayoutItem(content, true));
            if (trailing is not null)
                row.Items.Add(new StackLayoutItem(trailing, false));
            Layout.AddRow(row);
        }

        public void Header(string text)
        {
            Layout.AddRow(new Label
            {
                Text = text,
                Font = SystemFonts.Bold(),
            });
        }

        public void Note(string text)
        {
            Layout.AddRow(new Label
            {
                Text = text,
                TextColor = Colors.Gray,
                Wrap = WrapMode.Word,
            });
        }
    }

    private sealed class ServerGridRow
    {
        public string Name { get; set; } = string.Empty;
        public string Transport { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
    }

    private sealed class IssueGridRow
    {
        public string Severity { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public bool IsError { get; set; }
    }
}
