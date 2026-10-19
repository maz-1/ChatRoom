using Eto.Drawing;
using Eto.Forms;

namespace ChatRoomTray;

internal sealed class StartupArgsEtoDialog : Dialog<bool>
{
    private readonly TextBox _argsBox;
    private readonly TextBox _nodeArgsBox;

    public StartupArgsEtoDialog(string initialValue, string initialNodeArgs)
    {
        Title = "ChatRoom 启动参数";
        MinimumSize = new Size(620, 0);
        Resizable = false;

        _argsBox = new TextBox { Text = initialValue ?? string.Empty };
        _nodeArgsBox = new TextBox { Text = initialNodeArgs ?? string.Empty, PlaceholderText = "例如 --experimental-quic" };

        var ok = new Button { Text = "确定" };
        var cancel = new Button { Text = "取消" };
        ok.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        DefaultButton = ok;
        AbortButton = cancel;

        var layout = new DynamicLayout
        {
            Padding = new Padding(16),
            Spacing = new Size(8, 10),
        };
        layout.AddRow(new Label
        {
            Text = "分别设置 Node.js 运行时参数和 ChatRoom 命令，留空表示不传参数。"
                 + "端口、host 等请通过“编辑配置”修改。保存后可立即重启 ChatRoom。",
            Wrap = WrapMode.Word,
        });
        layout.AddRow(new Label { Text = "Node.js 参数", VerticalAlignment = VerticalAlignment.Center }, _nodeArgsBox);
        layout.AddRow(new Label { Text = "ChatRoom 参数", VerticalAlignment = VerticalAlignment.Center }, _argsBox);
        layout.AddSeparateRow(null, cancel, ok);
        Content = layout;
    }

    public string Value => (_argsBox.Text ?? string.Empty).Trim();

    public string NodeArgs => (_nodeArgsBox.Text ?? string.Empty).Trim();
}
