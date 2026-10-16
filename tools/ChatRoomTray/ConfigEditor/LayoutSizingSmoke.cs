#if WINDOWS_TRAY
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Eto.Drawing;
using Eto.Forms;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace ChatRoomTray.ConfigEditor;

/// <summary>
/// Exercises real Eto/WinForms layouts offscreen. The scale matrix simulates font
/// and client-area sizes; it does not change the desktop DPI or show any window.
/// </summary>
internal static class LayoutSizingSmoke
{
    public static void Run(Action<string, bool, string?> check)
    {
        var previous = Environment.GetEnvironmentVariable(ConfigPaths.ConfigPathVariable);
        Environment.SetEnvironmentVariable(ConfigPaths.ConfigPathVariable,
            Path.Combine(Path.GetTempPath(), "ChatRoom-layout-" + Guid.NewGuid().ToString("N"), "config.json"));
        try
        {
            foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f, 2.5f })
            {
                Verify(() => new MainForm(), "主窗口", scale, new Size(1100, 800), new Size(760, 480), check);
                Verify(() => new McpServerDialog("local", new StdioMcpServerConfig
                {
                    Command = @"C:\Program Files\MCP\server.exe",
                    Args = new List<string> { "--stdio", "a long argument with spaces" },
                    Env = new Dictionary<string, string> { ["POWERSHELL_MCP_TERMINAL_WINDOW"] = "PowerShellMCP" },
                }, new HashSet<string>()), "stdio 编辑", scale, new Size(760, 620), new Size(600, 400), check);
                Verify(() => new McpServerDialog("remote", new HttpMcpServerConfig
                {
                    Url = "https://example.com/mcp",
                    Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer test-only" },
                }, new HashSet<string>()), "HTTP 编辑", scale, new Size(760, 620), new Size(600, 400), check);
                Verify(() => new TextInputDialog("参数", "完整命令", "test", 720), "文本输入", scale,
                    new Size(720, 150), new Size(420, 150), check);
                Verify(() => new EnvironmentVariableInputDialog("环境变量", "POWERSHELL_MCP_TERMINAL_WINDOW", "PowerShellMCP"),
                    "环境变量输入", scale, new Size(640, 300), new Size(420, 220), check);
                Verify(() => new HeaderInputDialog("请求头", "X-Test", "value"), "请求头输入", scale,
                    new Size(520, 210), new Size(420, 210), check);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigPaths.ConfigPathVariable, previous);
        }
    }

    private static void Verify(Func<Window> create, string name, float scale, Size regular, Size compact,
        Action<string, bool, string?> check)
    {
        var fonts = new List<Font>();
        try
        {
            using var window = create();
            var native = (WF.Form)window.ControlObject;
            var ratio = scale / (native.DeviceDpi / 96f);
            foreach (var control in Walk(window, allTabs: true).OfType<CommonControl>())
            {
                var font = new Font(control.Font.Family, control.Font.Size * ratio);
                fonts.Add(font);
                control.Font = font;
            }
            window.AttachNative();
            typeof(WF.Control).GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(bool) }, null)!.Invoke(native, new object[] { true });
            typeof(WF.Form).GetMethod("OnLoad", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(native, new object[] { EventArgs.Empty });
            if (window is MainForm initialForm)
            {
                for (var pass = 0; pass < 3; pass++) Layout(native);
                ValidationPaneSmoke.VerifyVisible(initialForm);
            }
            window.MinimumSize = new Size(100, 100);

            foreach (var size in new[] { regular, compact, regular })
            {
                window.ClientSize = new Size((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
                var tabs = Walk(window, allTabs: true).OfType<TabControl>().FirstOrDefault();
                for (var page = 0; page < (tabs?.Pages.Count ?? 1); page++)
                {
                    if (tabs is not null) tabs.SelectedIndex = page;
                    for (var pass = 0; pass < 3; pass++) Layout(native);
                    Validate(window, native);
                }
            }
            if (window is McpServerDialog && scale == 1.5f)
            {
                var transport = Walk(window, true).OfType<DropDown>().Single();
                for (var pass = 0; pass < 2; pass++)
                {
                    transport.SelectedIndex = 1 - transport.SelectedIndex;
                    // Newly attached controls in a hidden form do not get native
                    // handles from visibility changes as they would in a real dialog.
                    CreateHandles(native);
                    for (var layout = 0; layout < 3; layout++) Layout(native);
                    Validate(window, native);
                }
            }
            check($"DPI 布局模拟 {scale:P0} {name}：正常、缩小、恢复", true, null);
        }
        catch (Exception error)
        {
            check($"DPI 布局模拟 {scale:P0} {name}：正常、缩小、恢复", false, error.GetBaseException().Message);
        }
        finally
        {
            foreach (var font in fonts) font.Dispose();
        }
    }

    private static void Validate(Window window, WF.Form native)
    {
        if (window is MainForm mainForm) ValidationPaneSmoke.VerifyVisible(mainForm);
        foreach (var child in Walk(window, allTabs: false))
        {
            if (child.ControlObject is not WF.Control control) continue;
            if (child is Scrollable && control is WF.ScrollableControl scroll && scroll.HorizontalScroll.Visible)
                throw new InvalidOperationException($"{child.ID}: unexpected horizontal scrollbar ({scroll.DisplayRectangle.Width}/{scroll.ClientSize.Width}).");
            if (child is Label label && !string.IsNullOrEmpty(label.Text))
            {
                var required = control.GetPreferredSize(new SD.Size(control.Width, 0));
                if (required.Height > control.Height + 2)
                    throw new InvalidOperationException($"Label height clipped: {label.Text}, required={required.Height}, actual={control.Height}.");
            }
            if (child is not Button && child is not TextBox && child is not TextArea && child is not ListBox && child is not GridView)
                continue;
            var bounds = new SD.Rectangle(native.PointToClient(control.PointToScreen(SD.Point.Empty)), control.Size);
            if (bounds.Left < -2 || bounds.Right > native.ClientSize.Width + 2 || bounds.Width <= 0)
                throw new InvalidOperationException($"{child.ID ?? child.GetType().Name}: horizontal clipping {bounds}, client={native.ClientSize}.");
        }
        if (window is Dialog<bool> dialog)
        {
            foreach (var button in new[] { dialog.DefaultButton, dialog.AbortButton })
            {
                if (button?.ControlObject is not WF.Control control) continue;
                var bounds = new SD.Rectangle(native.PointToClient(control.PointToScreen(SD.Point.Empty)), control.Size);
                if (bounds.Top < 0 || bounds.Bottom > native.ClientSize.Height + 2)
                    throw new InvalidOperationException($"{button.Text}: footer clipped {bounds}, client={native.ClientSize}.");
            }
        }
    }

    private static void CreateHandles(WF.Control root)
    {
        typeof(WF.Control).GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(bool) }, null)!.Invoke(root, new object[] { true });
        foreach (WF.Control child in root.Controls) CreateHandles(child);
    }

    private static void Layout(WF.Control root)
    {
        root.ResumeLayout(false);
        root.PerformLayout();
        foreach (WF.Control child in root.Controls) Layout(child);
    }

    private static IEnumerable<Control> Walk(Control root, bool allTabs)
    {
        yield return root;
        if (!allTabs && root is TabControl tabs)
        {
            if (tabs.SelectedPage is not null)
                foreach (var item in Walk(tabs.SelectedPage, false)) yield return item;
        }
        else if (root is Container container)
            foreach (var child in container.Controls)
                foreach (var item in Walk(child, allTabs)) yield return item;
    }
}
#endif
