using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Eto.Forms;
using Newtonsoft.Json;

namespace ChatRoomTray.ConfigEditor;

/// <summary>Tests the environment editor with synthetic data, without opening windows.</summary>
internal static class EnvironmentVariablesSmoke
{
    public static void Run(Action<string, bool, string?> check)
    {
        void Check(string name, bool condition) => check(name, condition, null);
        var original = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["POWERSHELL_MCP_TERMINAL_WINDOW"] = "PowerShellMCP",
            ["POWERSHELL_MCP_WT_PATH"] = @"C:\Program Files\WindowsApps\wt.exe",
            ["EMPTY"] = string.Empty,
            ["SPACES"] = "  value=with spaces\t ",
            ["JSON"] = "{\"text\":\"中文\",\"quoted\":\"a=b\"}",
            ["MULTILINE"] = "first\r\nsecond\nthird",
            ["LONG"] = new string('x', 2048),
        };
        var source = new StdioMcpServerConfig { Command = "node", Env = original };
        var sourceSnapshot = JsonConvert.SerializeObject(source);
        using var dialog = new McpServerDialog("local", source, new HashSet<string>(StringComparer.Ordinal));
        var controls = Walk(dialog).ToList();
        var view = controls.OfType<GridView>().Single(control => control.ID == "McpEnvironmentList");
        var edit = controls.OfType<Button>().Single(control => control.ID == "McpEnvironmentEditButton");
        var delete = controls.OfType<Button>().Single(control => control.ID == "McpEnvironmentDeleteButton");
        Check("stdio 环境变量使用名称／值两列表格",
            view.Columns.Select(column => column.HeaderText).SequenceEqual(new[] { "名称", "值" })
            && !controls.Any(control => control.ID == "McpEnvironmentBox"));
        Check("环境变量未选择时禁用编辑与删除", view.SelectedRow == -1 && !edit.Enabled && !delete.Enabled);
        Check("环境变量加载保留空值、空白、等号、引号、中文、换行与长值",
            dialog.TryCollectEnvironmentVariables(out var collected, out _)
            && Equal(original, collected));
        Check("环境变量列宽模式不冲突", view.Columns.All(column => !column.Expand || !column.AutoSize));
#if WINDOWS_TRAY
        try
        {
            GridRenderingSmoke.Verify(view);
            Check("环境变量六行首次绘制、重绘和调整宽度", true);
        }
        catch (Exception error)
        {
            check("环境变量六行首次绘制、重绘和调整宽度", false, error.GetBaseException().Message);
        }
#endif
        Check("环境变量允许新增空值",
            dialog.TrySetEnvironmentVariable(-1, "NEW", string.Empty, out _)
            && dialog.TryCollectEnvironmentVariables(out collected, out _)
            && collected.Count == original.Count + 1 && collected["NEW"] == string.Empty);
        var addedIndex = view.SelectedRow;
        Check("环境变量新增后选中新行并启用操作", addedIndex == original.Count && edit.Enabled && delete.Enabled);
        const string editedValue = "  a=b=c; \"quoted\" $PATH C:\\tmp  ";
        Check("环境变量编辑名称和值并保留选中行",
            dialog.TrySetEnvironmentVariable(addedIndex, "RENAMED", editedValue, out _)
            && view.SelectedRow == addedIndex
            && dialog.TryCollectEnvironmentVariables(out collected, out _)
            && !collected.ContainsKey("NEW") && collected["RENAMED"] == editedValue);
        Check("环境变量拒绝重复新增与重复重命名且不覆盖旧值",
            !dialog.TrySetEnvironmentVariable(-1, "EMPTY", "overwrite", out _)
            && !dialog.TrySetEnvironmentVariable(addedIndex, "EMPTY", "overwrite", out _)
            && dialog.TryCollectEnvironmentVariables(out collected, out _)
            && collected["EMPTY"] == string.Empty && collected["RENAMED"] == editedValue);
        Check("环境变量拒绝空名称、等号、换行、空字符与无效编辑行",
            new[] { "", "  ", "BAD=NAME", "BAD\nNAME", "BAD\0NAME" }
                .All(name => !dialog.TrySetEnvironmentVariable(-1, name, "value", out _))
            && !dialog.TrySetEnvironmentVariable(-1, "NUL_VALUE", "bad\0value", out _)
            && !dialog.TrySetEnvironmentVariable(-2, "INVALID_INDEX", "value", out _)
            && !dialog.TrySetEnvironmentVariable(999, "INVALID_INDEX", "value", out _));

        using (var entry = new EnvironmentVariableInputDialog("编辑环境变量", "MULTILINE", original["MULTILINE"]))
        {
            Check("环境变量弹窗分别编辑名称和值并保留未修改的换行",
                entry.VariableName == "MULTILINE" && entry.VariableValue == original["MULTILINE"]
                && entry.TryValidate(out _)
                && Walk(entry).OfType<TextArea>().Any(control => control.ID == "EnvironmentVariableValue" && !control.Wrap));
        }
        using (var duplicate = new EnvironmentVariableInputDialog("新建环境变量", "EMPTY", editedValue,
                   name => original.ContainsKey(name)))
        {
            Check("环境变量重复名称在输入弹窗内拦截并保留已输入值",
                !duplicate.TryValidate(out var error) && error.Length > 0 && duplicate.VariableValue == editedValue);
        }

        var transport = controls.OfType<DropDown>().Single();
        transport.SelectedIndex = 1;
        var httpView = Walk(dialog).OfType<GridView>().Single(control => control.ID == "McpHeadersList");
        Check("环境变量与 HTTP 请求头使用独立数据",
            !ReferenceEquals(view, httpView) && !(httpView.DataStore?.Any() ?? false));
        transport.SelectedIndex = 0;
        Check("stdio／HTTP 切换保留环境变量编辑内容",
            ReferenceEquals(view, Walk(dialog).OfType<GridView>().Single(control => control.ID == "McpEnvironmentList"))
            && dialog.TryCollectEnvironmentVariables(out collected, out _) && collected["RENAMED"] == editedValue);

        // Avoid reassigning the same selection: Eto WinForms clears it in that case.
        if (view.SelectedRow != addedIndex) view.SelectedRow = addedIndex;
        dialog.DeleteEnvironmentVariable();
        Check("环境变量删除后选中相邻项",
            view.SelectedRow == original.Count - 1
            && dialog.TryCollectEnvironmentVariables(out collected, out _) && Equal(original, collected));
        view.SelectedRow = -1;
        dialog.DeleteEnvironmentVariable();
        Check("环境变量无选择时删除安全且不修改数据",
            !edit.Enabled && !delete.Enabled
            && dialog.TryCollectEnvironmentVariables(out collected, out _) && Equal(original, collected));
        for (var remaining = original.Count; remaining > 0; remaining--)
        {
            if (view.SelectedRow != 0) view.SelectedRow = 0;
            dialog.DeleteEnvironmentVariable();
        }
        Check("环境变量删除至空列表",
            view.SelectedRow == -1 && !edit.Enabled && !delete.Enabled
            && dialog.TryCollectEnvironmentVariables(out collected, out _) && collected.Count == 0);
        Check("环境变量空列表可重新新增",
            dialog.TrySetEnvironmentVariable(-1, "RESTORED", "", out _)
            && dialog.TryCollectEnvironmentVariables(out collected, out _) && collected.Count == 1);
        Check("环境变量编辑不改变输入配置（取消安全）", JsonConvert.SerializeObject(source) == sourceSnapshot);

        using var saved = new McpServerDialog("local", source, new HashSet<string>(StringComparer.Ordinal));
        Check("环境变量保存前可增加新条目", saved.TrySetEnvironmentVariable(-1, "EXTRA", editedValue, out _));
        typeof(McpServerDialog).GetMethod("Confirm", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(saved, null);
        var serialized = JsonConvert.SerializeObject(saved.ServerConfig);
        var restored = JsonConvert.DeserializeObject<McpServerConfig>(serialized) as StdioMcpServerConfig;
        Check("环境变量通过确定按钮保存为原有 env 对象并可 JSON 往返",
            restored is not null && restored.Env.Count == original.Count + 1
            && original.All(pair => restored.Env.TryGetValue(pair.Key, out var value) && value == pair.Value)
            && restored.Env["EXTRA"] == editedValue && restored.Command == source.Command);
        Check("环境变量确认后仍不原地修改旧配置", JsonConvert.SerializeObject(source) == sourceSnapshot);
    }

    private static bool Equal(Dictionary<string, string> first, Dictionary<string, string> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static IEnumerable<Control> Walk(Control root)
    {
        yield return root;
        if (root is Container container)
            foreach (var child in container.Controls)
                foreach (var descendant in Walk(child)) yield return descendant;
    }
}
