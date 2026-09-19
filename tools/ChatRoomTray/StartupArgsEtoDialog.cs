using Eto.Drawing;
using Eto.Forms;

namespace ChatRoomTray;

internal sealed class StartupArgsEtoDialog : Dialog<bool>
{
    private readonly TextBox _argsBox;

    public StartupArgsEtoDialog(string initialValue)
    {
        Title = "ChatRoom 启动参数";
        ClientSize = new Size(620, 190);
        Resizable = false;

        _argsBox = new TextBox { Text = initialValue ?? string.Empty };

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
            Text = "启动 ChatRoom 时传给 Node.js CLI 的命令行参数。CLI 目前支持 init 和 serve；"
                 + "留空表示无参数。端口、host 等运行参数请通过“编辑配置”修改后重启。",
            Wrap = WrapMode.Word,
        });
        layout.AddRow(_argsBox);
        layout.Add(null, yscale: true);
        layout.AddSeparateRow(null, cancel, ok);
        Content = layout;
    }

    public string Value => (_argsBox.Text ?? string.Empty).Trim();
}
