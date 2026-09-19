using System;
using System.Drawing;
using System.Windows.Forms;

namespace ChatRoomTray.ConfigEditor;

/// <summary>UI construction for <see cref="MainForm"/> (kept apart from the logic).</summary>
public partial class MainForm : Form
{
    private readonly ToolStrip _toolStrip = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Fill };
    private readonly ToolStripButton _newButton = new("新建默认配置");
    private readonly ToolStripButton _openButton = new("打开…");
    private readonly ToolStripButton _reloadButton = new("重新加载");
    private readonly ToolStripButton _saveButton = new("保存");
    private readonly ToolStripButton _saveAsButton = new("另存为…");
    private readonly ToolStripButton _checkButton = new("校验");
    private readonly ToolStripButton _folderButton = new("打开所在文件夹");

    private readonly TextBox _pathBox = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    private readonly ListBox _rootsList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _dataDirBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _databasePathBox = new() { Dock = DockStyle.Fill };

    private readonly TextBox _hostBox = new() { Width = 220 };
    private readonly NumericUpDown _portBox = new() { Width = 110 };
    private readonly CheckBox _localWebAuthBox = new() { Text = "启用本地 WebUI 登录（localWebAuth）", AutoSize = true };
    private readonly Label _tokenStatus = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _copyTokenButton = new() { Text = "复制", AutoSize = true };
    private readonly Button _regenerateTokenButton = new() { Text = "重新生成", AutoSize = true };
    private readonly Button _clearTokenButton = new() { Text = "清除", AutoSize = true };
    private readonly TextBox _mcpPublicBaseUrlBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _webPublicBaseUrlBox = new() { Dock = DockStyle.Fill };
    private readonly ListBox _redirectHostsList = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    private readonly NumericUpDown _httpDefaultTimeout = new() { Width = 150 };
    private readonly NumericUpDown _httpMaxTimeout = new() { Width = 150 };
    private readonly NumericUpDown _httpMaxResponseBytes = new() { Width = 170 };
    private readonly NumericUpDown _operationsMaxPayload = new() { Width = 170 };
    private readonly NumericUpDown _processMaxOutput = new() { Width = 170 };
    private readonly NumericUpDown _processDefaultTimeout = new() { Width = 150 };
    private readonly NumericUpDown _processMaxCompleted = new() { Width = 120 };
    private readonly NumericUpDown _mcpCallTimeout = new() { Width = 150 };
    private readonly NumericUpDown _mcpMaxResultBytes = new() { Width = 170 };

    private readonly ListView _serversView = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };

    private readonly Button _addServerButton = new() { Text = "添加服务…", AutoSize = true };
    private readonly Button _editServerButton = new() { Text = "编辑…", AutoSize = true };
    private readonly Button _removeServerButton = new() { Text = "删除", AutoSize = true };

    private readonly ListView _issuesView = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
    };

    private readonly StatusStrip _statusStrip = new() { Dock = DockStyle.Fill };
    private readonly ToolStripStatusLabel _statusLabel = new("就绪");

    private void BuildUi()
    {
        Text = "ChatRoom 配置编辑器";
        MinimumSize = new Size(960, 700);
        Size = new Size(1100, 800);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;

        BuildToolStrip();
        BuildTabs();
        ConfigureLimits();

        _issuesView.Columns.Add("级别", 70);
        _issuesView.Columns.Add("字段", 260);
        _issuesView.Columns.Add("说明", 660);

        var issuesGroup = new GroupBox
        {
            Text = "校验结果",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
        };
        issuesGroup.Controls.Add(_issuesView);

        _statusStrip.Items.Add(_statusLabel);

        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 6, 8, 4),
        };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.Controls.Add(new Label
        {
            Text = "配置文件",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 6, 8, 3),
        }, 0, 0);
        pathRow.Controls.Add(_pathBox, 1, 0);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
        };
        split.Panel1.Controls.Add(_tabs);
        split.Panel1MinSize = 220;
        split.Panel2MinSize = 120;
        split.Panel2.Controls.Add(issuesGroup);

        // One explicit grid keeps the vertical stacking deterministic instead of
        // relying on dock z-order.
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
        };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.Controls.Add(_toolStrip, 0, 0);
        outer.Controls.Add(pathRow, 0, 1);
        outer.Controls.Add(split, 0, 2);
        outer.Controls.Add(_statusStrip, 0, 3);
        Controls.Add(outer);

        Shown += (_, _) => split.SplitterDistance = Math.Max(300, split.Height - 200);
    }

    private void BuildToolStrip()
    {
        _toolStrip.Items.AddRange(new ToolStripItem[]
        {
            _newButton, _openButton, _reloadButton,
            new ToolStripSeparator(),
            _saveButton, _saveAsButton, _checkButton,
            new ToolStripSeparator(),
            _folderButton,
        });
    }

    private void BuildTabs()
    {
        _tabs.TabPages.Add(BuildPathsTab());
        _tabs.TabPages.Add(BuildAuthTab());
        _tabs.TabPages.Add(BuildLimitsTab());
        _tabs.TabPages.Add(BuildMcpTab());
    }

    private TabPage BuildPathsTab()
    {
        var builder = new TableBuilder();

        var rootsListPanel = new Panel { Dock = DockStyle.Fill };
        rootsListPanel.Controls.Add(_rootsList);
        var rootsButtons = Stacked(
            ("添加目录…", (_, _) => AddRoot()),
            ("编辑…", (_, _) => EditRoot()),
            ("删除", (_, _) => RemoveSelected(_rootsList, "工作区根目录")));

        var dataDirButton = new Button { Text = "浏览…", AutoSize = true };
        dataDirButton.Click += (_, _) => BrowseInto(_dataDirBox, saveFile: false);

        var databaseButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
        };
        var databaseBrowse = new Button { Text = "浏览…", AutoSize = true };
        var databaseClear = new Button { Text = "清除", AutoSize = true };
        databaseBrowse.Click += (_, _) => BrowseInto(_databasePathBox, saveFile: true);
        databaseClear.Click += (_, _) => _databasePathBox.Text = string.Empty;
        databaseButtons.Controls.AddRange(new Control[] { databaseBrowse, databaseClear });

        builder.Row("工作区根目录", rootsListPanel, rootsButtons, height: 170);
        builder.Row("数据目录", _dataDirBox, dataDirButton);
        builder.Row("数据库文件", _databasePathBox, databaseButtons);
        builder.Note("allowedRoots 决定 ChatGPT 可访问哪些目录；dataDir 存放运行数据；databasePath 留空时 ChatRoom 使用 <dataDir>/chatroom.sqlite。");

        return WrapTab("工作区与路径", builder.Table);
    }

    private TabPage BuildAuthTab()
    {
        var builder = new TableBuilder();

        var hostRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
        };
        hostRow.Controls.Add(_hostBox);
        hostRow.Controls.Add(new Label
        {
            Text = "端口",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(12, 8, 6, 3),
        });
        hostRow.Controls.Add(_portBox);

        var tokenRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
        };
        tokenRow.Controls.AddRange(new Control[]
        {
            _tokenStatus, _copyTokenButton, _regenerateTokenButton, _clearTokenButton,
        });

        var redirectPanel = new Panel { Dock = DockStyle.Fill };
        redirectPanel.Controls.Add(_redirectHostsList);
        var redirectButtons = Stacked(
            ("添加…", (_, _) => AddRedirectHost()),
            ("编辑…", (_, _) => EditRedirectHost()),
            ("删除", (_, _) => RemoveSelected(_redirectHostsList, "重定向主机")));

        builder.Row("监听地址", hostRow);
        builder.Row("本地登录", _localWebAuthBox);
        builder.Row("ownerToken", tokenRow);
        builder.Row("MCP 公网地址", _mcpPublicBaseUrlBox);
        builder.Row("WebUI 公网地址", _webPublicBaseUrlBox);
        builder.Row("重定向主机", redirectPanel, redirectButtons, height: 140);
        builder.Note("ownerToken 用于 ChatGPT(OAuth) 与 WebUI 的登录授权。本界面不会显示它的内容，只能复制到剪贴板或重新生成；重新生成后已授权的客户端需要重新授权。");

        return WrapTab("服务与认证", builder.Table);
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

        return WrapTab("限额", builder.Table);
    }

    private TabPage BuildMcpTab()
    {
        _serversView.Columns.Add("名称", 170);
        _serversView.Columns.Add("传输", 80);
        _serversView.Columns.Add("目标", 560);
        _serversView.DoubleClick += (_, _) => EditServer();

        var buttons = Stacked(
            ("添加服务…", (_, _) => AddServer()),
            ("编辑…", (_, _) => EditServer()),
            ("删除", (_, _) => RemoveServer()));

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(10),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_serversView, 0, 0);
        layout.Controls.Add(buttons, 1, 0);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            ForeColor = SystemColors.GrayText,
            Text = "stdio 服务会以 ChatRoom 进程身份启动本地命令；http 支持自定义请求头与代理。env/headers 通常存放密钥，只保存在本机配置文件中。修改后需要重启 ChatRoom 才会生效。",
        }, 0, 1);

        var page = new TabPage("MCP 服务") { Padding = new Padding(6) };
        page.Controls.Add(layout);
        return page;
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

    private static void SetRange(NumericUpDown box, int min, int max)
    {
        box.Minimum = min;
        box.Maximum = max;
    }

    private static TabPage WrapTab(string title, TableLayoutPanel table)
    {
        var page = new TabPage(title) { Padding = new Padding(12), AutoScroll = true };
        page.Controls.Add(table);
        return page;
    }

    private static FlowLayoutPanel Stacked(params (string Text, EventHandler Handler)[] buttons)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
        };
        foreach (var (text, handler) in buttons)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += handler;
            panel.Controls.Add(button);
        }
        return panel;
    }

    /// <summary>
    /// Builds rows with explicit row indexes; relying on the implicit "next free
    /// cell" flow breaks as soon as a row spans columns.
    /// </summary>
    private sealed class TableBuilder
    {
        private int _row;

        public TableBuilder()
        {
            Table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 3,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            // Auto-sized label column: a fixed width makes longer labels wrap,
            // which then cannot align with single-line inputs and breaks under
            // DPI scaling.
            Table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        }

        public TableLayoutPanel Table { get; }

        public void Row(string label, Control content, Control? trailing = null, int? height = null)
        {
            Table.RowCount = _row + 1;
            Table.RowStyles.Add(height is null
                ? new RowStyle(SizeType.AutoSize)
                : new RowStyle(SizeType.Absolute, height.Value));

            Table.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                // Tall rows (lists) read better with the label pinned to the top.
                Anchor = height is null
                    ? AnchorStyles.Left
                    : AnchorStyles.Top | AnchorStyles.Left,
                Margin = new Padding(3, height is null ? 9 : 10, 8, 3),
            }, 0, _row);

            content.Margin = new Padding(3, 4, 8, 4);
            Table.Controls.Add(content, 1, _row);

            if (trailing is not null)
            {
                trailing.Margin = new Padding(0, 4, 3, 4);
                Table.Controls.Add(trailing, 2, _row);
            }

            _row++;
        }

        public void Header(string text)
        {
            Table.RowCount = _row + 1;
            Table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
                Margin = new Padding(3, 14, 3, 4),
            };
            Table.Controls.Add(label, 0, _row);
            Table.SetColumnSpan(label, 3);
            _row++;
        }

        public void Note(string text)
        {
            Table.RowCount = _row + 1;
            Table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(820, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 12, 3, 4),
            };
            Table.Controls.Add(label, 0, _row);
            Table.SetColumnSpan(label, 3);
            _row++;
        }
    }
}
