using Eto.Forms;

namespace ChatRoomTray.ConfigEditor;

public partial class MainForm
{
    private readonly CheckBox _js2rayEnabled = new() { Text = "启用进程内 js2ray" };
    private readonly TextBox _js2rayConfigPath = new();
    private readonly TextBox _js2rayOutboundTag = new();
    private readonly CheckBox _js2rayReverseEnabled = new() { Text = "启用远程 MCP / WebUI 反代" };
    private readonly TextBox _js2rayBridgeTag = new();
    private readonly TextBox _js2rayLocalTag = new();
    private readonly TextBox _js2rayTargetHost = new();
    private readonly NumericStepper _js2rayTargetPort = IntegerStepper(110);

    private TabPage BuildJs2rayTab()
    {
        SetRange(_js2rayTargetPort, ConfigValidator.PortMin, ConfigValidator.PortMax);
        var builder = new TableBuilder();
        var browse = new Button { Text = "浏览…" };
        browse.Click += (_, _) => {
            var dialog = new OpenFileDialog { Title = "选择 js2ray 节点配置" };
            if (dialog.ShowDialog(this) == DialogResult.Ok) _js2rayConfigPath.Text = dialog.FileName;
        };
        builder.Row("运行时", _js2rayEnabled);
        builder.Row("节点配置文件", _js2rayConfigPath, browse);
        builder.Row("固定出站 tag", _js2rayOutboundTag);
        builder.Note("节点配置必须没有代理入站。相对路径以 ChatRoom 配置文件所在目录为基准。出站 tag 留空时遵循节点配置的分流规则；http_request 设置 proxy 为 js2ray 即可使用。修改后重启 ChatRoom 生效。");
        builder.Header("反向代理");
        builder.Row("远程访问", _js2rayReverseEnabled);
        builder.Row("Bridge tag", _js2rayBridgeTag);
        builder.Row("本地服务出站 tag", _js2rayLocalTag);
        builder.Row("Portal 业务目标主机", _js2rayTargetHost);
        builder.Row("Portal 业务目标端口", _js2rayTargetPort);
        builder.Note("目标主机和端口须与远端 Portal 发来的业务目标完全一致，不是 ChatRoom 的监听端口。请同时在“服务与认证”中配置公网地址；MCP 与 WebUI 保留各自认证。");
        return WrapTab("js2ray", builder.Layout);
    }

    private void LoadJs2rayIntoUi()
    {
        var settings = _config.Js2ray;
        _js2rayEnabled.Checked = settings.Enabled;
        _js2rayConfigPath.Text = settings.ConfigPath ?? string.Empty;
        _js2rayOutboundTag.Text = settings.OutboundTag ?? string.Empty;
        _js2rayReverseEnabled.Checked = settings.Reverse.Enabled;
        _js2rayBridgeTag.Text = settings.Reverse.BridgeTag;
        _js2rayLocalTag.Text = settings.Reverse.LocalOutboundTag;
        _js2rayTargetHost.Text = settings.Reverse.TargetHost;
        _js2rayTargetPort.Value = Clamp(settings.Reverse.TargetPort, _js2rayTargetPort.MinValue, _js2rayTargetPort.MaxValue);
    }

    private void ReadJs2rayFromUi()
    {
        var settings = _config.Js2ray;
        settings.Enabled = _js2rayEnabled.Checked == true;
        settings.ConfigPath = NullIfBlank(_js2rayConfigPath.Text ?? string.Empty);
        settings.OutboundTag = NullIfBlank(_js2rayOutboundTag.Text ?? string.Empty);
        settings.Reverse.Enabled = _js2rayReverseEnabled.Checked == true;
        settings.Reverse.BridgeTag = (_js2rayBridgeTag.Text ?? string.Empty).Trim();
        settings.Reverse.LocalOutboundTag = (_js2rayLocalTag.Text ?? string.Empty).Trim();
        settings.Reverse.TargetHost = (_js2rayTargetHost.Text ?? string.Empty).Trim();
        settings.Reverse.TargetPort = (int)_js2rayTargetPort.Value;
    }
}
