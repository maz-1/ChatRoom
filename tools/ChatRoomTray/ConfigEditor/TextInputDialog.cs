using Eto.Drawing;
using Eto.Forms;

namespace ChatRoomTray.ConfigEditor;

/// <summary>Minimal single-value prompt used for list entries.</summary>
public sealed class TextInputDialog : Dialog<bool>
{
    private readonly TextBox _box;

    public TextInputDialog(string title, string label, string initial, int clientWidth = 420)
    {
        Title = title;
        Resizable = false;
        ShowInTaskbar = false;
        ClientSize = new Size(clientWidth, 130);

        _box = new TextBox
        {
            Text = initial,
        };

        var ok = new Button { Text = "确定" };
        var cancel = new Button { Text = "取消" };
        ok.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);

        DefaultButton = ok;
        AbortButton = cancel;

        var layout = new DynamicLayout
        {
            Padding = new Padding(12),
            Spacing = new Size(8, 10),
        };
        layout.AddRow(new Label { Text = label, VerticalAlignment = VerticalAlignment.Center }, _box);
        layout.Add(null);
        layout.AddSeparateRow(null, cancel, ok);

        Content = layout;
    }

    public string Value => _box.Text ?? string.Empty;
}
