using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ChatRoomConfigEditor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            return RunCheck(Path.GetFullPath(args[0]), report: null);

        switch (args.FirstOrDefault())
        {
            case "--check":
            {
                var target = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
                    ? Path.GetFullPath(args[1])
                    : ConfigPaths.DefaultConfigPath();
                var report = ReadOption(args, "--report");
                return RunCheck(target, report);
            }
            case "--selftest":
                return RunSelfTest(ReadOption(args, "--report"));
            case "--uismoke":
                return RunUiSmoke(ReadOption(args, "--report"));
            case "--roundtrip":
            {
                if (args.Length < 3)
                {
                    AttachConsole();
                    Console.WriteLine("用法：--roundtrip <源配置> <目标文件>");
                    return 64;
                }

                return RunRoundTrip(
                    Path.GetFullPath(args[1]),
                    Path.GetFullPath(args[2]),
                    ReadOption(args, "--report"));
            }
            case "--help":
            case "-h":
                AttachConsole();
                Console.WriteLine(
                    "ChatRoomConfigEditor — 编辑 ChatRoom 配置文件（图形界面）\n\n" +
                    "  ChatRoomConfigEditor.exe                 打开图形界面\n" +
                    "  ChatRoomConfigEditor.exe <config.json>   校验指定配置并输出报告\n" +
                    "  ChatRoomConfigEditor.exe --check [path] [--report <file>]\n" +
                    "                                           校验配置（默认路径来自 CHATROOM_CONFIG\n" +
                    "                                           或 %APPDATA%\\ChatRoom\\config.json）\n" +
                    "  ChatRoomConfigEditor.exe --roundtrip <src> <dst> [--report <file>]\n" +
                    "                                           载入配置另存并比对是否一致\n" +
                    "  ChatRoomConfigEditor.exe --selftest [--report <file>]\n" +
                    "                                           自检（编码、校验规则、写入不变量）\n" +
                    "  ChatRoomConfigEditor.exe --uismoke [--report <file>]\n" +
                    "                                           界面自检（构造窗体并断言布局对齐）");
                return 0;
            default:
                // .NET Framework equivalent of ApplicationConfiguration.Initialize().
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int RunCheck(string path, string? report)
    {
        var output = new StringBuilder();
        var exitCode = 0;
        try
        {
            var loaded = ConfigStore.Load(path);
            var token = OwnerTokenStore.Read(path) ?? loaded.LegacyOwnerToken;
            var issues = ConfigValidator.Validate(
                loaded.Config,
                loaded.UnknownKeys,
                OwnerToken.IsPresent(token));
            var errors = issues.Count(issue => issue.Severity == IssueSeverity.Error);
            var warnings = issues.Count - errors;

            output.AppendLine($"配置文件：{path}");
            output.AppendLine($"服务：{loaded.Config.Server.Host}:{loaded.Config.Server.Port}");
            output.AppendLine($"MCP 服务：{loaded.Config.Mcp.Servers.Count} 个");
            output.AppendLine($"ownerToken：{OwnerToken.Describe(token)}");
            output.AppendLine($"错误 {errors} 项，警告 {warnings} 项");
            foreach (var issue in issues)
                output.AppendLine($"  [{(issue.Severity == IssueSeverity.Error ? "错误" : "警告")}] {issue.Path}: {issue.Message}");

            exitCode = errors == 0 ? 0 : 2;
        }
        catch (Exception error)
        {
            output.AppendLine($"读取失败：{error.Message}");
            exitCode = 3;
        }

        if (report is not null) File.WriteAllText(report, output.ToString(), new UTF8Encoding(false));
        AttachConsole();
        Console.Write(output.ToString());
        return exitCode;
    }

    /// <summary>
    /// Builds the real form without showing it, to catch construction/layout
    /// failures, and asserts that the owner token never appears in any control.
    /// </summary>
    private static int RunUiSmoke(string? report)
    {
        var output = new StringBuilder();
        var failed = 0;
        var results = new List<string>();

        void Check(string name, bool ok, string? detail = null)
        {
            results.Add($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail is null ? "" : $" — {detail}")}");
            if (!ok) failed++;
        }

        try
        {
            // .NET Framework equivalent of ApplicationConfiguration.Initialize().
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var configPath = ConfigPaths.DefaultConfigPath();
            string? token = OwnerTokenStore.Read(configPath);
            if (!OwnerToken.IsPresent(token) && File.Exists(configPath))
                token = ConfigStore.Load(configPath).LegacyOwnerToken;

            using var form = new MainForm();
            // Shown off-screen so every tab really lays out; a hidden form leaves
            // unselected tab pages unmeasured.
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-8000, -8000);
            form.Show();
            Application.DoEvents();

            var controls = Walk(form).ToList();
            Check("窗体构造无异常", true);
            Check("控件数量合理", controls.Count > 60, $"实际 {controls.Count}");

            var tabControl = controls.OfType<TabControl>().FirstOrDefault();
            var tabs = tabControl?.TabPages.Cast<TabPage>().ToList() ?? new List<TabPage>();
            Check(
                "包含四个编辑页",
                tabs.Count == 4,
                string.Join(",", tabs.Select(tab => tab.Text)));
            Check(
                "包含 MCP 服务页",
                tabs.Any(tab => tab.Text.Contains("MCP")),
                string.Join(",", tabs.Select(tab => tab.Text)));

            var texts = controls.SelectMany(CollectTexts).ToList();
            if (token is not null)
            {
                Check(
                    "ownerToken 未出现在任何控件文本中",
                    texts.All(text => text.IndexOf(token, StringComparison.Ordinal) < 0));
                Check(
                    "令牌状态以脱敏形式提示",
                    texts.Any(text => text.StartsWith("已设置 · ", StringComparison.Ordinal)),
                    string.Join(" | ", texts.Where(text => text.Contains("设置"))));
            }

            var serverView = controls.OfType<ListView>()
                .FirstOrDefault(view => view.Columns.Cast<ColumnHeader>().Any(column => column.Text == "传输"));
            Check(
                "MCP 服务列表已填充",
                serverView is not null && serverView.Items.Count > 0,
                serverView is null ? "未找到服务列表" : $"{serverView.Items.Count} 行");

            using (var httpDialog = new McpServerDialog(
                       "remote",
                       new HttpMcpServerConfig
                       {
                           Url = "https://example.com/mcp",
                           Headers = { ["Authorization"] = "Bearer test-token" },
                       },
                       new HashSet<string>(StringComparer.Ordinal)))
            {
                var httpControls = Walk(httpDialog).ToList();
                var headerView = httpControls.OfType<ListView>()
                    .FirstOrDefault(view => view.Name == "McpHeadersList");
                Check(
                    "HTTP MCP 请求头使用键值列表",
                    headerView is not null
                    && headerView.Columns.Count == 2
                    && headerView.Columns[0].Text == "键"
                    && headerView.Columns[1].Text == "值");
                Check(
                    "HTTP MCP 请求头能加载已有配置",
                    headerView is not null
                    && headerView.Items.Count == 1
                    && headerView.Items[0].Text == "Authorization"
                    && headerView.Items[0].SubItems.Count > 1
                    && headerView.Items[0].SubItems[1].Text == "Bearer test-token");
                Check(
                    "HTTP MCP 提供 Auth Token 快捷按钮",
                    httpControls.OfType<Button>().Any(button => button.Text == "填写 Auth Token…"));
            }

            using (var headerDialog = new HeaderInputDialog("测试请求头", string.Empty, string.Empty))
            {
                var labels = Walk(headerDialog).OfType<Label>().Select(label => label.Text).ToList();
                Check(
                    "请求头编辑窗口分别输入键和值",
                    labels.Contains("键") && labels.Contains("值"));
            }

            Check("校验结果面板存在", controls.OfType<ListView>().Any(view =>
                view.Columns.Cast<ColumnHeader>().Any(column => column.Text == "级别")));

            // The tab tables are 3 columns: label | field | optional buttons.
            var worstTab = 0;
            var tabDetail = new List<string>();
            var measuredRows = 0;
            if (tabControl is not null)
            {
                foreach (TabPage page in tabControl.TabPages)
                {
                    tabControl.SelectedTab = page;
                    Application.DoEvents();

                    foreach (var table in Walk(page).OfType<TableLayoutPanel>()
                                 .Where(table => table.ColumnCount == 3))
                    {
                        for (var row = 0; row < table.RowCount && row < table.RowStyles.Count; row++)
                        {
                            if (table.GetControlFromPosition(0, row) is not Label label) continue;
                            if (table.GetControlFromPosition(1, row) is not { } field) continue;
                            if (label.Height == 0 || field.Height == 0) continue;

                            measuredRows++;
                            var delta = table.RowStyles[row].SizeType == SizeType.Absolute
                                ? Math.Abs(label.Top - field.Top)
                                : Math.Abs(label.Top + label.Height / 2 - (field.Top + field.Height / 2));
                            if (delta > worstTab) worstTab = delta;
                            if (delta > 8)
                                tabDetail.Add(
                                    $"{page.Text}/{label.Text}:{delta}px " +
                                    $"[label top={label.Top} h={label.Height}, field {field.GetType().Name} top={field.Top} h={field.Height}, " +
                                    $"row={table.RowStyles[row].SizeType}/{table.GetRowHeights()[row]}]");
                        }
                    }
                }
            }

            Check(
                "各标签页标签与输入框对齐",
                measuredRows > 0 && worstTab <= 8,
                measuredRows == 0
                    ? "未测量到任何行"
                    : $"最大偏差 {worstTab}px {string.Join(" ", tabDetail)}");

            failed += MeasureDialogAlignment(results);
            form.Close();
        }
        catch (Exception error)
        {
            results.Add($"FAIL 界面构造：{error}");
            failed++;
        }

        output.AppendLine(string.Join(Environment.NewLine, results));
        output.AppendLine(failed == 0 ? "UISMOKE OK" : $"UISMOKE FAILED ({failed})");
        if (report is not null) File.WriteAllText(report, output.ToString(), new UTF8Encoding(false));
        AttachConsole();
        Console.Write(output.ToString());
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Lays out the MCP server dialog off-screen and asserts that every label is
    /// vertically aligned with its field. Regression guard for the row sizing
    /// that once pushed labels below their inputs.
    /// </summary>
    private static int MeasureDialogAlignment(List<string> results)
    {
        var failed = 0;

        void Check(string name, bool ok, string? detail = null)
        {
            results.Add($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail is null ? "" : $" — {detail}")}");
            if (!ok) failed++;
        }

        foreach (var transport in new[] { "stdio", "http" })
        {
            using var dialog = new McpServerDialog(
                null,
                transport == "stdio"
                    ? new StdioMcpServerConfig
                    {
                        Command = "python",
                        Args = { "-m", "mcp_windbg" },
                    }
                    : new HttpMcpServerConfig(),
                new HashSet<string>(StringComparer.Ordinal));
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-8000, -8000);
            dialog.Show();
            Application.DoEvents();

            if (transport == "stdio")
            {
                var argsList = Walk(dialog)
                    .OfType<ListBox>()
                    .FirstOrDefault(list => list.Name == "McpArgsList");
                var argumentButtons = Walk(dialog)
                    .OfType<Button>()
                    .Select(button => button.Text)
                    .ToHashSet(StringComparer.Ordinal);
                Check(
                    "MCP stdio 参数使用独立列表项",
                    argsList is not null
                    && argsList.Items.Count == 2
                    && string.Equals((string)argsList.Items[0], "-m", StringComparison.Ordinal)
                    && string.Equals((string)argsList.Items[1], "mcp_windbg", StringComparison.Ordinal));
                Check(
                    "MCP stdio 参数列表提供编辑与排序操作",
                    new[] { "新建", "编辑", "删除", "上移", "下移" }
                        .All(argumentButtons.Contains));
            }

            var tables = Walk(dialog)
                .OfType<TableLayoutPanel>()
                .Where(table => table.ColumnCount == 2 && table.RowCount >= 3)
                .ToList();

            var worst = 0;
            var measured = 0;
            var detail = new List<string>();
            foreach (var table in tables)
            {
                for (var row = 0; row < table.RowCount && row < table.RowStyles.Count; row++)
                {
                    var label = table.GetControlFromPosition(0, row) as Label;
                    var field = table.GetControlFromPosition(1, row);
                    if (label is null || field is null) continue;
                    if (label.Height == 0 || field.Height == 0) continue;

                    measured++;
                    int delta;
                    if (table.RowStyles[row].SizeType == SizeType.Percent)
                    {
                        // Multiline editors: the label belongs at the top.
                        delta = Math.Abs(label.Top - field.Top);
                    }
                    else
                    {
                        var labelCenter = label.Top + label.Height / 2;
                        var fieldCenter = field.Top + field.Height / 2;
                        delta = Math.Abs(labelCenter - fieldCenter);
                    }

                    if (delta > worst) worst = delta;
                    if (delta > 8)
                        detail.Add($"{label.Text}:{delta}px");
                }
            }

            dialog.Close();
            Check($"MCP 对话框({transport}) 标签与输入框对齐", measured > 0 && worst <= 8,
                measured == 0 ? "未能测量到任何行" : $"最大偏差 {worst}px {string.Join(" ", detail)}");
        }

        return failed;
    }

    private static IEnumerable<Control> Walk(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Walk(child)) yield return descendant;
        }
    }

    /// <summary>Every user-visible string reachable from a control.</summary>
    private static IEnumerable<string> CollectTexts(Control control)
    {
        yield return control.Text;
        switch (control)
        {
            case ListBox listBox:
                foreach (var item in listBox.Items) yield return item?.ToString() ?? string.Empty;
                break;
            case ListView listView:
                foreach (ListViewItem item in listView.Items)
                {
                    yield return item.Text;
                    foreach (ListViewItem.ListViewSubItem sub in item.SubItems)
                        yield return sub.Text;
                }
                foreach (ColumnHeader column in listView.Columns) yield return column.Text;
                break;
            case ComboBox comboBox:
                foreach (var item in comboBox.Items) yield return item?.ToString() ?? string.Empty;
                break;
            // StatusStrip derives from ToolStrip, so this covers both.
            case ToolStrip toolStrip:
                foreach (ToolStripItem item in toolStrip.Items)
                    yield return item.Text ?? string.Empty;
                break;
        }
    }

    /// <summary>
    /// Loads a real configuration, writes it out again, and reports whether the
    /// result is semantically identical. Used to check the editor against a live
    /// file without touching it.
    /// </summary>
    private static int RunRoundTrip(string source, string target, string? report)
    {
        var output = new StringBuilder();
        var exitCode = 0;
        try
        {
            var loaded = ConfigStore.Load(source);
            ConfigStore.Save(target, loaded.Config);
            var reloaded = ConfigStore.Load(target);

            var before = ConfigStore.Serialize(loaded.Config);
            var after = ConfigStore.Serialize(reloaded.Config);
            var same = string.Equals(before, after, StringComparison.Ordinal);

            output.AppendLine($"源文件：{source}");
            output.AppendLine($"输出：  {target}");
            output.AppendLine($"往返一致：{(same ? "是" : "否")}");

            if (!same)
            {
                output.AppendLine("差异（源 → 重读）：");
                var beforeLines = before.Split('\n');
                var afterLines = after.Split('\n');
                for (var index = 0; index < Math.Max(beforeLines.Length, afterLines.Length); index++)
                {
                    var left = index < beforeLines.Length ? beforeLines[index].TrimEnd('\r') : "<缺失>";
                    var right = index < afterLines.Length ? afterLines[index].TrimEnd('\r') : "<缺失>";
                    if (left != right) output.AppendLine($"  - {left}{Environment.NewLine}  + {right}");
                }
                exitCode = 2;
            }

            output.AppendLine("规范化后的内容：");
            output.AppendLine(after);
        }
        catch (Exception error)
        {
            output.AppendLine($"失败：{error.Message}");
            exitCode = 3;
        }

        if (report is not null) File.WriteAllText(report, output.ToString(), new UTF8Encoding(false));
        AttachConsole();
        Console.Write(output.ToString());
        return exitCode;
    }

    private static int RunSelfTest(string? report)
    {
        var results = new List<string>();
        var failed = 0;

        void Check(string name, bool ok, string? detail = null)
        {
            results.Add($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail is null ? "" : $" — {detail}")}");
            if (!ok) failed++;
        }

        // Decisive source-encoding check: the literal must equal the same text
        // written with escapes, which is immune to how the file gets decoded.
        Check("源码中文编码", "中文" == "\u4e2d\u6587");

        var token = OwnerToken.Generate();
        Check("base64url 长度 43", token.Length == 43);
        Check("base64url 字符集", token.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        Check("令牌提示不泄漏内容", OwnerToken.Describe(token) == "已设置 · 43 个字符", OwnerToken.Describe(token));

        var defaults = ConfigStore.CreateDefault();
        var defaultIssues = ConfigValidator.Validate(
            defaults,
            Array.Empty<string>(),
            ownerTokenPresent: false);
        Check(
            "默认配置本身校验通过",
            !ConfigValidator.HasErrors(defaultIssues),
            string.Join("; ", defaultIssues.Select(i => $"{i.Path}: {i.Message}")));

        // Security rule 1: an authenticated ingress requires an owner token in the credential store.
        defaults.Auth.WebPublicBaseUrl = "https://chatroom.example.com";
        var missingToken = ConfigValidator.Validate(
                defaults,
                Array.Empty<string>(),
                ownerTokenPresent: false)
            .Where(i => i.Severity == IssueSeverity.Error).ToList();
        Check(
            "启用公网地址但缺令牌 → 报 ownerToken",
            missingToken.Count == 1 && missingToken[0].Path == "ownerToken",
            string.Join("; ", missingToken.Select(i => $"{i.Path}: {i.Message}")));

        Check(
            "凭据管理器存在令牌后通过",
            !ConfigValidator.HasErrors(ConfigValidator.Validate(
                defaults,
                Array.Empty<string>(),
                ownerTokenPresent: true)));

        // Security rule 2: binding beyond loopback requires localWebAuth.
        defaults.Server.Host = "0.0.0.0";
        var nonLoopback = ConfigValidator.Validate(
                defaults,
                Array.Empty<string>(),
                ownerTokenPresent: true)
            .Where(i => i.Severity == IssueSeverity.Error).ToList();
        Check(
            "非回环地址且未开 localWebAuth → 报 server.host",
            nonLoopback.Count == 1 && nonLoopback[0].Path == "server.host",
            string.Join("; ", nonLoopback.Select(i => $"{i.Path}: {i.Message}")));

        // Strict schema: unknown keys are errors, exactly like ChatRoom's zod.
        defaults.Server.Host = "127.0.0.1";
        var unknown = ConfigValidator.Validate(
                defaults,
                new[] { "surprise" },
                ownerTokenPresent: true)
            .Where(i => i.Severity == IssueSeverity.Error).ToList();
        Check("未知字段被报错", unknown.Count == 1 && unknown[0].Path == "surprise");

        // Round trip through the real file writer: no BOM, reloadable, same values.
        // .NET Framework has no Directory.CreateTempSubdirectory().
        var directory = Path.Combine(
            Path.GetTempPath(), "chatroom-editor-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, "config.json");
            ConfigStore.Save(target, defaults);
            var bytes = File.ReadAllBytes(target);
            Check("写出文件不带 BOM", bytes.Length > 0 && bytes[0] == (byte)'{', $"首字节 0x{bytes[0]:X2}");

            var reloaded = ConfigStore.Load(target);
            Check(
                "写出的文件能原样读回",
                ConfigStore.Serialize(reloaded.Config) == ConfigStore.Serialize(defaults));

            var text = File.ReadAllText(target);
            Check("包含 mcp.servers", text.Contains("\"servers\": {}"));

            // A minimal config exercising both transports survives a round trip.
            var edge = ConfigStore.CreateDefault();
            edge.Mcp.Servers["local"] = new StdioMcpServerConfig
            {
                Command = "npx",
                Args = { "-y", "server-filesystem" },
                Env = { ["API_KEY"] = "value" },
                Cwd = null,
            };
            edge.Mcp.Servers["remote"] = new HttpMcpServerConfig
            {
                Url = "https://example.com/mcp",
                Headers = { ["authorization"] = "Bearer x" },
                Proxy = "http://127.0.0.1:8080",
            };
            var edgePath = Path.Combine(directory, "edge.json");
            ConfigStore.Save(edgePath, edge);
            var edgeLoaded = ConfigStore.Load(edgePath);
            var edgeReloaded = edgeLoaded.Config;

            // Guards against editor-only members leaking into the file: any such
            // property would surface here as an unknown key, which is exactly what
            // makes ChatRoom refuse to start.
            Check(
                "写出的文件没有多余字段",
                edgeLoaded.UnknownKeys.Count == 0,
                string.Join(",", edgeLoaded.UnknownKeys));

            var stdio = (StdioMcpServerConfig)edgeReloaded.Mcp.Servers["local"];
            var http = (HttpMcpServerConfig)edgeReloaded.Mcp.Servers["remote"];
            Check(
                "stdio/http 两种服务往返一致",
                stdio.Command == "npx"
                && stdio.Args.Count == 2
                && stdio.Env["API_KEY"] == "value"
                && stdio.Cwd is null
                && http.Url == "https://example.com/mcp"
                && http.Headers["authorization"] == "Bearer x"
                && http.Proxy == "http://127.0.0.1:8080");

            foreach (var name in new[] { "unknown-field.json", "sse.json" })
            {
                var casePath = Path.Combine(directory, name);
                if (name == "unknown-field.json")
                    File.WriteAllText(casePath, text.Replace("\"dataDir\"", "\"surpriseKey\": 1,\n  \"dataDir\""));
                else
                    File.WriteAllText(
                        casePath,
                        "{\"allowedRoots\":[\"C:/x\"],\"dataDir\":\"C:/y\",\"server\":{\"host\":\"127.0.0.1\",\"port\":8765},"
                        + "\"auth\":{\"localWebAuth\":false,\"ownerToken\":null,\"mcpPublicBaseUrl\":null,"
                        + "\"webPublicBaseUrl\":null,\"allowedRedirectHosts\":[]},"
                        + "\"http\":{\"defaultTimeoutMs\":30000,\"maxTimeoutMs\":120000,\"maxResponseBytes\":4096},"
                        + "\"operations\":{\"maxPayloadBytes\":4096},"
                        + "\"mcp\":{\"callTimeoutMs\":60000,\"maxResultBytes\":4096,"
                        + "\"servers\":{\"legacy\":{\"type\":\"sse\",\"url\":\"https://example.com/sse\"}}},"
                        + "\"process\":{\"maxOutputBytes\":4096,\"defaultTimeoutMs\":1000,\"maxCompletedProcesses\":1}}");
            }

            var unknownLoaded = ConfigStore.Load(Path.Combine(directory, "unknown-field.json"));
            Check(
                "未知顶层字段被识别",
                unknownLoaded.UnknownKeys.Contains("surpriseKey"),
                string.Join(",", unknownLoaded.UnknownKeys));

            string sseMessage;
            try
            {
                ConfigStore.Load(Path.Combine(directory, "sse.json"));
                sseMessage = string.Empty;
            }
            catch (InvalidDataException error)
            {
                sseMessage = error.Message;
            }
            Check("sse 传输被拒绝并给出指引", sseMessage.Contains("sse") && sseMessage.Contains("http"), sseMessage);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }

        var output = string.Join(Environment.NewLine, results) + Environment.NewLine
            + (failed == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({failed})") + Environment.NewLine;
        if (report is not null) File.WriteAllText(report, output, new UTF8Encoding(false));
        AttachConsole();
        Console.Write(output);
        return failed == 0 ? 0 : 1;
    }

    /// <summary>A WinExe has no console of its own; borrow the caller's.</summary>
    private static void AttachConsole()
    {
        try
        {
            AttachConsole(-1);
        }
        catch
        {
            // No parent console (double-clicked); console output is simply lost.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
