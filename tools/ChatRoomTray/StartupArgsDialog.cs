using System.Drawing;
using System.Windows.Forms;

namespace ChatRoomTray;

internal sealed class StartupArgsDialog : Form
{
    private readonly TextBox _argsBox;

    public StartupArgsDialog(string initialValue)
    {
        Text = "ChatRoom 启动参数";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 185);
        AutoScaleMode = AutoScaleMode.Font;

        var description = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Text = "启动 ChatRoom 时传给 chatroom.cmd 的命令行参数。CLI 目前支持 init 和 serve；留空表示无参数（首次运行会自动初始化并打开浏览器）。端口、host 等运行参数请通过“编辑配置”修改后重启。",
        };

        _argsBox = new TextBox
        {
            Dock = DockStyle.Top,
            Text = initialValue,
        };

        var ok = new Button { Text = "确定", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        description.Margin = new Padding(0, 0, 0, 14);
        _argsBox.Margin = new Padding(0, 0, 0, 14);
        layout.Controls.Add(description, 0, 0);
        layout.Controls.Add(_argsBox, 0, 1);
        layout.Controls.Add(buttons, 0, 2);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string Value => _argsBox.Text.Trim();
}
