#if WINDOWS_TRAY
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Eto.Drawing;
using Eto.Forms;
using WF = System.Windows.Forms;

namespace ChatRoomTray.ConfigEditor;

/// <summary>Checks the result grid inside the actual main-window splitter, without showing a window.</summary>
internal static class ValidationPaneSmoke
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<string, bool, string?> check)
    {
        var previous = Environment.GetEnvironmentVariable(ConfigPaths.ConfigPathVariable);
        Environment.SetEnvironmentVariable(ConfigPaths.ConfigPathVariable,
            Path.Combine(Path.GetTempPath(), "ChatRoom-results-" + Guid.NewGuid().ToString("N"), "config.json"));
        try
        {
            using var form = new MainForm();
            var native = (WF.Form)form.ControlObject;
            var splitter = Walk(form).OfType<Splitter>().Single();
            var split = (WF.SplitContainer)splitter.ControlObject;
            var tabs = Walk(form).OfType<TabControl>().Single();
            form.AttachNative();
            CreateHandles(native);
            typeof(WF.Form).GetMethod("OnLoad", PrivateInstance)!.Invoke(native, new object[] { EventArgs.Empty });
            Settle(native);

            void Test(string name, Action action)
            {
                try { action(); check(name, true, null); }
                catch (Exception error) { check(name, false, error.GetBaseException().Message); }
            }

            Test("校验区域原生首次加载后有高度且说明列填满", () => VerifyVisible(form));
            form.MinimumSize = new Size(100, 100);
            Test("校验区域缩小、放大和切换全部页面后仍完整可见", () =>
            {
                foreach (var size in new[] { new Size(860, 730), new Size(760, 480), new Size(1100, 800), new Size(860, 730) })
                {
                    form.ClientSize = size;
                    for (var page = 0; page < tabs.Pages.Count; page++)
                    {
                        tabs.SelectedIndex = page;
                        CreateHandles(native);
                        Settle(native);
                        VerifyVisible(form);
                    }
                }
            });
            Test("校验区域空结果、错误警告、长说明、大量结果和清空后不消失", () =>
            {
                foreach (var count in new[] { 0, 1, 6, 50, 0 })
                {
                    var issues = Enumerable.Range(0, count).Select(index => new ValidationIssue(
                        index % 2 == 0 ? IssueSeverity.Error : IssueSeverity.Warning,
                        "mcp.servers.test", "仅用于布局测试：" + new string('x', 500))).ToList();
                    typeof(MainForm).GetField("_issues", PrivateInstance)!.SetValue(form, issues);
                    typeof(MainForm).GetMethod("RenderIssues", PrivateInstance)!.Invoke(form, null);
                    Settle(native);
                    var grid = ResultGrid(form);
                    if (grid.RowCount != count) throw new InvalidOperationException("Validation rows were lost.");
                    VerifyVisible(form);
                }
            });
            Test("校验区域分隔条拖到下端不能压成零高度", () =>
            {
                // Native SplitterMoved is the same path used when the user moves the divider.
                split.SplitterDistance = split.Height - split.SplitterWidth;
                Settle(native);
                if (split.Panel2.Height < splitter.Panel2MinimumSize - split.SplitterWidth)
                    throw new InvalidOperationException("The results pane minimum was not enforced.");
                VerifyVisible(form);
            });
            Test("校验区域手动调整后窗口变宽不重置分隔位置", () =>
            {
                split.SplitterDistance = split.Height - split.SplitterWidth - 240;
                Settle(native);
                var height = split.Panel2.Height;
                var client = form.ClientSize;
                form.ClientSize = new Size(client.Width + 160, client.Height + 80);
                Settle(native);
                if (Math.Abs(height - split.Panel2.Height) > 2)
                    throw new InvalidOperationException("Resizing reset the user-selected results height.");
                VerifyVisible(form);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigPaths.ConfigPathVariable, previous);
        }
    }

    internal static void VerifyVisible(MainForm form)
    {
        var splitter = Walk(form).OfType<Splitter>().Single();
        var split = (WF.SplitContainer)splitter.ControlObject;
        var grid = ResultGrid(form);
        if (split.Panel2Collapsed || split.Panel2.Height <= 0
            || grid.Height < grid.ColumnHeadersHeight + grid.RowTemplate.Height + 2)
            throw new InvalidOperationException(
                $"Validation pane not visible: panel={split.Panel2.Height}, grid={grid.Height}, splitter={split.Height}/{split.SplitterDistance}.");
        if (split.Panel1.Height <= 0)
            throw new InvalidOperationException("The settings pane has no height.");

        // Checking only the grid's own bounds misses clipping by its parent pane.
        for (WF.Control? child = grid; child?.Parent is WF.Control parent; child = parent)
        {
            if (child.Bounds.Top < -2 || child.Bounds.Bottom > parent.ClientSize.Height + 2
                || child.Bounds.Left < -2 || child.Bounds.Right > parent.ClientSize.Width + 2)
                throw new InvalidOperationException($"Results clipped by {parent.GetType().Name}: {child.Bounds}, parent={parent.ClientSize}.");
        }
        // A hidden ancestor makes GetCellDisplayRectangle return an empty rectangle.
        // Native column widths still reflect layout, including the expanding column.
        if (grid.Columns[grid.ColumnCount - 1].AutoSizeMode != WF.DataGridViewAutoSizeColumnMode.Fill)
            throw new InvalidOperationException("The results column lost Fill sizing.");
        var columnsWidth = grid.Columns.GetColumnsWidth(WF.DataGridViewElementStates.Visible);
        var gap = grid.ClientSize.Width - columnsWidth;
        var scrollbarWidth = WF.SystemInformation.VerticalScrollBarWidth;
        if (Math.Abs(gap) > 2 && Math.Abs(gap - scrollbarWidth) > 2)
            throw new InvalidOperationException($"Results column does not fill the visible grid (gap={gap}).");
    }

    private static WF.DataGridView ResultGrid(MainForm form) =>
        (WF.DataGridView)Walk(form).OfType<GridView>().Single(view => view.ID == "ValidationIssuesView").ControlObject;

    private static void CreateHandles(WF.Control control)
    {
        typeof(WF.Control).GetMethod("CreateControl", PrivateInstance, null, new[] { typeof(bool) }, null)!
            .Invoke(control, new object[] { true });
        foreach (WF.Control child in control.Controls) CreateHandles(child);
    }

    private static void Settle(WF.Control control)
    {
        for (var pass = 0; pass < 3; pass++) Layout(control);
    }

    private static void Layout(WF.Control control)
    {
        control.ResumeLayout(false);
        control.PerformLayout();
        foreach (WF.Control child in control.Controls) Layout(child);
    }

    private static IEnumerable<Control> Walk(Control control)
    {
        yield return control;
        if (control is Container parent)
            foreach (var child in parent.Controls)
                foreach (var descendant in Walk(child)) yield return descendant;
    }
}
#endif
