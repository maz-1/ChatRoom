using System.Drawing;
using System.Windows.Forms;

namespace ChatRoomTray;

internal sealed class StartupArgsDialog : Form
{
    private readonly TextBox _argsBox;
    private readonly TextBox _nodeArgsBox;

    public StartupArgsDialog(string initialValue, string initialNodeArgs)
    {
        Text = "ChatRoom 启动参数";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, 0);
        MinimumSize = new Size(636, 0);
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var description = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(588, 0),
            Text = "分别设置 Node.js 运行时参数和 ChatRoom 命令，留空表示不传参数。端口、host 等请通过“编辑配置”修改。保存后可立即重启 ChatRoom。",
        };

        _nodeArgsBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = initialNodeArgs,
            PlaceholderText = "例如 --experimental-quic",
            AccessibleName = "Node.js 启动参数",
            TabIndex = 0,
        };

        _argsBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = initialValue,
            PlaceholderText = "例如 serve",
            AccessibleName = "ChatRoom 启动参数",
            TabIndex = 1,
        };

        var ok = new Button { Text = "确定", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0),
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 4,
            MinimumSize = new Size(620, 0),
            Padding = new Padding(16),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < layout.RowCount; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        description.Margin = new Padding(0, 0, 0, 16);
        _nodeArgsBox.Margin = new Padding(0, 0, 0, 12);
        _argsBox.Margin = new Padding(0, 0, 0, 12);
        layout.Controls.Add(description, 0, 0);
        layout.SetColumnSpan(description, 2);
        layout.Controls.Add(new Label { Text = "Node.js 参数", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 16, 12) }, 0, 1);
        layout.Controls.Add(_nodeArgsBox, 1, 1);
        layout.Controls.Add(new Label { Text = "ChatRoom 参数", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 16, 12) }, 0, 2);
        layout.Controls.Add(_argsBox, 1, 2);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        ActiveControl = _nodeArgsBox;
    }

    public string Value => _argsBox.Text.Trim();

    public string NodeArgs => _nodeArgsBox.Text.Trim();
}
