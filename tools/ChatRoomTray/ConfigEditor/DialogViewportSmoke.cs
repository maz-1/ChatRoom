#if WINDOWS_TRAY
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Eto.Drawing;
using Eto.Forms;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace ChatRoomTray.ConfigEditor;

/// <summary>Regression for initial-size reset and content hidden behind the MCP footer.
/// Uses native control geometry only; no desktop window or real configuration is opened.</summary>
internal static class DialogViewportSmoke
{
    public static void Run(Action<string, bool, string?> check)
    {
        void Case(string name, Action test)
        {
            try { test(); check(name, true, null); }
            catch (Exception error) { check(name, false, error.GetBaseException().Message); }
        }

        var dialog = CreateDialog(many: false);
        var expected = dialog.ClientSize;
        var native = Prepare(dialog);
        Case("MCP 原生 Load 后保留设定的初始客户区", () =>
        {
            Require(dialog.ClientSize == expected, $"Expected {expected}, got {dialog.ClientSize}.");
            ValidateViewport(dialog);
        });

        // Match the report's 150% font metrics even on a different-DPI test machine.
        var fonts = MatchFonts(dialog, 1.5f);
        try
        {
            var compact = new Size(870, 544);
            dialog.MinimumSize = new Size(100, 100);
            Case("MCP 截图尺寸 870×544：全部按钮与工作目录完整可见", () =>
            {
                Resize(dialog, compact);
                ValidateViewport(dialog);
            });
            var table = Walk(dialog).OfType<GridView>().Single(c => c.ID == "McpEnvironmentList");
            var compactHeight = table.Height;
            Case("MCP 环境变量表格使用增加的窗口高度", () =>
            {
                Resize(dialog, new Size(compact.Width, compact.Height + 200));
                ValidateViewport(dialog);
                Require(table.Height >= compactHeight + 180, "The environment table did not grow.");
            });
            Case("MCP 放大后缩回截图尺寸不遗留空白或截断", () =>
            {
                Resize(dialog, compact);
                ValidateViewport(dialog);
            });
            Case("MCP 错误提示显示与清除后仍能看到全部操作", () =>
            {
                typeof(McpServerDialog).GetMethod("Reject", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(dialog, new object[] { "请填写启动命令。" });
                Settle(native);
                ValidateViewport(dialog);
                typeof(McpServerDialog).GetMethod("Reject", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(dialog, new object[] { string.Empty });
                Settle(native);
                ValidateViewport(dialog);
            });
            Case("MCP 截图尺寸下切换 HTTP／stdio 后控件仍在可视区", () =>
            {
                var transport = Walk(dialog).OfType<DropDown>().Single();
                for (var i = 0; i < 2; i++)
                {
                    transport.SelectedIndex = 1 - transport.SelectedIndex;
                    CreateHandles(native);
                    Settle(native);
                    ValidateViewport(dialog);
                }
            });
            Case("MCP 极小窗口可滚动到工作目录且恢复后无截断", () =>
            {
                Resize(dialog, new Size(870, 360));
                var scroll = Walk(dialog).OfType<Scrollable>().Single(c => c.ID == "McpDialogScroll");
                scroll.ScrollPosition = new Point(0, Math.Max(0, scroll.ScrollSize.Height - scroll.ClientSize.Height));
                Settle(native);
                var cwd = Walk(dialog).OfType<TextBox>().Single(c => c.ID == "McpWorkingDirectoryBox");
                ValidateControl(cwd, native);
                ValidateControl(dialog.DefaultButton!, native);
                ValidateControl(dialog.AbortButton!, native);
                scroll.ScrollPosition = Point.Empty;
                Resize(dialog, compact);
                ValidateViewport(dialog);
            });
            Case("MCP 初始尺寸修正仅执行一次，不覆盖用户调整", () =>
            {
                typeof(WF.Form).GetMethod("OnLoad", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(native, new object[] { EventArgs.Empty });
                Settle(native);
                Require(dialog.ClientSize == compact, "A repeated Load reset the user-selected size.");
                ValidateViewport(dialog);
            });
        }
        finally
        {
            dialog.Dispose();
            foreach (var font in fonts) font.Dispose();
        }

        var large = CreateDialog(many: true);
        var largeFonts = MatchFonts(large, 1.5f);
        try
        {
            Prepare(large);
            large.MinimumSize = new Size(100, 100);
            Case("MCP 30 个参数及 50 个环境变量在列表内滚动，不挤走按钮", () =>
            {
                Resize(large, new Size(870, 544));
                ValidateViewport(large);
                var view = Walk(large).OfType<GridView>().Single(c => c.ID == "McpEnvironmentList");
                Require(view.DataStore.Count() == 50, "Environment rows were lost.");
            });
        }
        finally
        {
            large.Dispose();
            foreach (var font in largeFonts) font.Dispose();
        }
    }

    private static McpServerDialog CreateDialog(bool many) => new("test", new StdioMcpServerConfig
    {
        Command = "server.exe",
        Args = many ? Enumerable.Range(0, 30).Select(i => "--argument-" + i).ToList() : new List<string>(),
        Env = Enumerable.Range(0, many ? 50 : 3).ToDictionary(
            i => "POWERSHELL_MCP_TEST_VARIABLE_" + i, i => "Test value " + i),
    }, new HashSet<string>());

    private static List<Font> MatchFonts(Window window, float scale)
    {
        var fonts = new List<Font>();
        var native = (WF.Form)window.ControlObject;
        var ratio = scale / (native.DeviceDpi / 96f);
        foreach (var control in Walk(window).OfType<CommonControl>())
        {
            var font = new Font(control.Font.Family, control.Font.Size * ratio);
            fonts.Add(font);
            control.Font = font;
        }
        return fonts;
    }

    private static WF.Form Prepare(Window window)
    {
        var native = (WF.Form)window.ControlObject;
        window.AttachNative();
        CreateHandles(native);
        typeof(WF.Form).GetMethod("OnLoad", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(native, new object[] { EventArgs.Empty });
        Settle(native);
        return native;
    }

    private static void Resize(Window window, Size size)
    {
        window.ClientSize = size;
        Settle((WF.Form)window.ControlObject);
    }

    private static void ValidateViewport(Window window)
    {
        var native = (WF.Form)window.ControlObject;
        foreach (var child in Walk(window))
        {
            if (child is Scrollable && child.ControlObject is WF.ScrollableControl scroll)
                Require(!scroll.VerticalScroll.Visible && !scroll.HorizontalScroll.Visible,
                    $"{child.ID}: unexpected outer scrollbar, viewport={scroll.ClientSize}, content={scroll.DisplayRectangle}.");
            if (child is Button or TextBox or ListBox or GridView) ValidateControl(child, native);
        }
    }

    private static void ValidateControl(Control child, WF.Form window)
    {
        if (child.ControlObject is not WF.Control control) return;
        for (WF.Control? ancestor = control.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor != window && ancestor is not WF.ScrollableControl) continue;
            var bounds = new SD.Rectangle(ancestor.PointToClient(control.PointToScreen(SD.Point.Empty)), control.Size);
            var viewport = ancestor.ClientRectangle;
            viewport.Inflate(2, 2);
            Require(bounds.Width > 0 && bounds.Height > 0 && viewport.Contains(bounds),
                $"{child.ID ?? child.GetType().Name}: clipped {bounds} outside {ancestor.GetType().Name} {viewport}.");
        }
    }

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    private static void CreateHandles(WF.Control root)
    {
        typeof(WF.Control).GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(bool) }, null)!.Invoke(root, new object[] { true });
        foreach (WF.Control child in root.Controls) CreateHandles(child);
    }

    private static void Settle(WF.Control root)
    {
        for (var i = 0; i < 4; i++) { Layout(root); WF.Application.DoEvents(); }
    }

    private static void Layout(WF.Control root)
    {
        root.ResumeLayout(false);
        root.PerformLayout();
        foreach (WF.Control child in root.Controls) Layout(child);
    }

    private static IEnumerable<Control> Walk(Control root)
    {
        yield return root;
        if (root is Container container)
            foreach (var child in container.Controls)
                foreach (var item in Walk(child)) yield return item;
    }
}
#endif
