using System;
using System.Collections.Generic;
using Eto.Forms;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;


namespace ChatRoomTray.ConfigEditor;

internal static class ConfigEditorCommands
{
    public static int Run(string[] args)
    {
        if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
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
            case "--traysmoke":
#if WINDOWS_TRAY
                AttachConsole();
                Console.WriteLine("--traysmoke 仅用于 Linux/macOS 跨平台托盘后端。");
                return 64;
#else
                return global::ChatRoomTray.CrossPlatformTrayApplication.RunSmoke();
#endif
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
                var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? "ChatRoomTray.exe"
                    : "./ChatRoomTray";
                Console.WriteLine(
                    "ChatRoomTray\n\n" +
                    $"  {executableName} --tray          启动托盘宿主（Linux/macOS；无参数时相同）\n" +
                    $"  {executableName} --config        仅打开配置编辑器\n" +
                    $"  {executableName} <config.json>   校验指定配置并输出报告\n" +
                    $"  {executableName} --check [path] [--report <file>]\n" +
                    $"                                           校验配置（默认：{ConfigPaths.DefaultConfigPath()}）\n" +
                    $"  {executableName} --roundtrip <src> <dst> [--report <file>]\n" +
                    "                                           载入配置另存并比对是否一致\n" +
                    $"  {executableName} --selftest [--report <file>]\n" +
                    "                                           自检（编码、校验规则、写入不变量）\n" +
                    $"  {executableName} --uismoke [--report <file>]\n" +
                    "                                           界面自检（构造 Eto 窗体并检查关键控件）\n" +
                    $"  {executableName} --traysmoke\n" +
                    "                                           托盘后端 smoke（不启动 ChatRoom，约 1 秒后退出）");
                return 0;
            default:
                return ConfigEditorHost.Run();
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
            string? token = null;
            OwnerTokenStoreException? credentialError = null;
            try
            {
                token = OwnerTokenStore.Read(path) ?? loaded.LegacyOwnerToken;
            }
            catch (OwnerTokenStoreException error)
            {
                credentialError = error;
            }

            var required = ConfigValidator.AuthenticationUsesOwnerToken(loaded.Config);
            var tokenPresentForRules = credentialError is not null && required
                ? true
                : OwnerToken.IsPresent(token);
            var issues = ConfigValidator.Validate(
                loaded.Config,
                loaded.UnknownKeys,
                tokenPresentForRules);

            if (credentialError is not null)
            {
                issues.Add(new ValidationIssue(
                    required ? IssueSeverity.Error : IssueSeverity.Warning,
                    "ownerToken",
                    required
                        ? $"当前配置需要 ownerToken，但系统凭据库不可用：{credentialError.Message}"
                        : $"系统凭据库暂不可用；当前纯本地配置可以继续运行：{credentialError.Message}"));
            }

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
    /// Constructs the integrated Eto.Forms editor and its dialogs without entering
    /// a message loop. This catches missing platform handlers/control wiring and
    /// verifies that secrets are not rendered into visible controls.
    /// </summary>
    private static int RunUiSmoke(string? report)
    {
        try
        {
            return ConfigEditorHost.WithApplication(() =>
            {
                var output = new StringBuilder();
            var failed = 0;
            var results = new List<string>();

            void Check(string name, bool condition, string? detail = null)
            {
                if (condition)
                    results.Add($"PASS {name}");
                else
                {
                    failed++;
                    results.Add($"FAIL {name}" + (string.IsNullOrEmpty(detail) ? string.Empty : $": {detail}"));
                }
            }

            try
            {
                var configPath = ConfigPaths.DefaultConfigPath();
                string? token = null;
                try
                {
                    token = OwnerTokenStore.Read(configPath);
                }
                catch
                {
                    // The UI smoke test is still useful if the credential store is unavailable.
                }

                if (!OwnerToken.IsPresent(token) && File.Exists(configPath))
                    token = ConfigStore.Load(configPath).LegacyOwnerToken;

                using var form = new MainForm();
                var controls = Walk(form).ToList();
                Check("Eto 主窗体构造无异常", true);
                Check("Eto 控件数量合理", controls.Count > 25, $"实际 {controls.Count}");

                if (OwnerToken.IsPresent(token))
                {
                    var visible = controls.SelectMany(CollectTexts).ToList();
                    Check(
                        "ownerToken 不显示在界面控件中",
                        visible.All(text => !string.Equals(text, token, StringComparison.Ordinal)));
                }

                var serverView = controls.OfType<GridView>()
                    .FirstOrDefault(view => view.ID == "McpServersView");
                Check("MCP 服务使用 Eto GridView", serverView is not null);
                Check(
                    "MCP 服务列完整",
                    serverView is not null
                    && serverView.Columns.Any(column => column.HeaderText == "名称")
                    && serverView.Columns.Any(column => column.HeaderText == "传输")
                    && serverView.Columns.Any(column => column.HeaderText == "目标"));

                using (var stdioDialog = new McpServerDialog(
                           "local",
                           new StdioMcpServerConfig
                           {
                               Command = "node",
                               Args = new List<string> { "server.js" },
                               Env = new Dictionary<string, string>(),
                           },
                           new HashSet<string>(StringComparer.Ordinal)))
                {
                    var dialogControls = Walk(stdioDialog).ToList();
                    Check(
                        "stdio 对话框使用 Eto 参数列表",
                        dialogControls.OfType<ListBox>().Any(list => list.ID == "McpArgsList"));
                    Check(
                        "stdio 对话框保留完整命令入口",
                        dialogControls.OfType<Button>().Any(button => button.ID == "McpCompleteCommandButton"));
                }

                using (var httpDialog = new McpServerDialog(
                           "remote",
                           new HttpMcpServerConfig
                           {
                               Url = "https://example.com/mcp",
                               Headers = new Dictionary<string, string>
                               {
                                   ["Authorization"] = "Bearer secret",
                               },
                           },
                           new HashSet<string>(StringComparer.Ordinal)))
                {
                    var dialogControls = Walk(httpDialog).ToList();
                    var headerView = dialogControls.OfType<GridView>()
                        .FirstOrDefault(view => view.ID == "McpHeadersList");
                    Check("HTTP 请求头使用 Eto GridView", headerView is not null);
                    Check(
                        "HTTP 请求头列完整",
                        headerView is not null
                        && headerView.Columns.Any(column => column.HeaderText == "键")
                        && headerView.Columns.Any(column => column.HeaderText == "值"));
                }

                using (var headerDialog = new HeaderInputDialog("测试请求头", string.Empty, string.Empty))
                {
                    var labels = Walk(headerDialog)
                        .OfType<Label>()
                        .Select(label => label.Text ?? string.Empty)
                        .ToList();
                    Check("请求头编辑窗口分别输入键和值", labels.Contains("键") && labels.Contains("值"));
                }

                using (var completeCommandDialog = new TextInputDialog(
                           "输入完整命令",
                           "完整命令",
                           string.Empty,
                           clientWidth: 720))
                {
                    var hasLabel = Walk(completeCommandDialog)
                        .OfType<Label>()
                        .Any(label => label.Text == "完整命令");
                    Check("完整命令输入窗口已迁移到 Eto", hasLabel);
                }
            }
            catch (Exception error)
            {
                failed++;
                results.Add($"FAIL Eto UI smoke: {error}");
            }

                foreach (var result in results) output.AppendLine(result);
                output.AppendLine(failed == 0 ? "UISMOKE OK" : $"UISMOKE FAILED ({failed})");
                if (report is not null)
                    File.WriteAllText(report, output.ToString(), new UTF8Encoding(false));
                AttachConsole();
                Console.Write(output.ToString());
                return failed == 0 ? 0 : 1;
            });
        }
        catch (Exception error)
        {
            var output = $"FAIL Eto application init/dispose: {error}{Environment.NewLine}UISMOKE FAILED (1){Environment.NewLine}";
            if (report is not null)
                File.WriteAllText(report, output, new UTF8Encoding(false));
            AttachConsole();
            Console.Write(output);
            return 1;
        }
    }

    private static IEnumerable<Control> Walk(Control root)
    {
        if (root is not Container container) yield break;
        foreach (var child in container.Controls)
        {
            yield return child;
            foreach (var descendant in Walk(child))
                yield return descendant;
        }
    }

    /// <summary>Every user-visible string reachable from an Eto control.</summary>
    private static IEnumerable<string> CollectTexts(Control control)
    {
        switch (control)
        {
            case Label label:
                yield return label.Text ?? string.Empty;
                break;
            case Button button:
                yield return button.Text ?? string.Empty;
                break;
            case TextBox textBox:
                yield return textBox.Text ?? string.Empty;
                break;
            case TextArea textArea:
                yield return textArea.Text ?? string.Empty;
                break;
            case CheckBox checkBox:
                yield return checkBox.Text ?? string.Empty;
                break;
            case GroupBox group:
                yield return group.Text ?? string.Empty;
                break;
            case TabPage page:
                yield return page.Text ?? string.Empty;
                break;
            case ListBox listBox:
                foreach (var item in listBox.DataStore ?? Enumerable.Empty<object>())
                    yield return item?.ToString() ?? string.Empty;
                break;
            case DropDown dropDown:
                foreach (var item in dropDown.DataStore ?? Enumerable.Empty<object>())
                    yield return item?.ToString() ?? string.Empty;
                break;
            case GridView grid:
                foreach (var column in grid.Columns)
                    yield return column.HeaderText ?? string.Empty;
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

#if WINDOWS_TRAY
        var dpiMode = System.Windows.Forms.Application.HighDpiMode;
        Check(
            "Windows 启动时启用 PerMonitorV2 DPI awareness",
            dpiMode == System.Windows.Forms.HighDpiMode.PerMonitorV2,
            $"实际 DPI 模式：{dpiMode}");
#endif

        // Decisive source-encoding check: the literal must equal the same text
        // written with escapes, which is immune to how the file gets decoded.
        Check("源码中文编码", "中文" == "\u4e2d\u6587");

        var token = OwnerToken.Generate();
        Check("base64url 长度 43", token.Length == 43);
        Check("base64url 字符集", token.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        Check("令牌提示不泄漏内容", OwnerToken.Describe(token) == "已设置 · 43 个字符", OwnerToken.Describe(token));

        var commandInput = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "\"C:\\Program Files\\nodejs\\npx.cmd\" -y \"@scope/server\" \"D:\\My Data\" \"\""
            : "python3 \"/tmp/my script.py\" --name 'hello world' \"\"";
        var commandParsed = CompleteCommandParser.TryParse(
            commandInput,
            out var parsedCommand,
            out var parsedArgs,
            out var commandParseError);
        var commandExpected = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? parsedCommand == @"C:\Program Files\nodejs\npx.cmd"
              && parsedArgs.SequenceEqual(new[] { "-y", "@scope/server", @"D:\My Data", string.Empty })
            : parsedCommand == "python3"
              && parsedArgs.SequenceEqual(new[] { "/tmp/my script.py", "--name", "hello world", string.Empty });
        Check(
            "完整 stdio 命令按当前平台规则拆分",
            commandParsed && commandExpected,
            commandParseError);

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
            "系统凭据库存在令牌后通过",
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

            var trayIni = Path.Combine(directory, "chatroom-tray.ini");
            File.WriteAllText(
                trayIni,
                "[Other]\nKeep=yes\n\n[ChatRoom]\nArgs=serve --old\n",
                new UTF8Encoding(false));
            var traySettings = new global::ChatRoomTray.TraySettings(trayIni);
            Check("TraySettings 读取已有启动参数", traySettings.Args == "serve --old", traySettings.Args);
            traySettings.SaveArgs("serve --port 9000");
            var trayReloaded = new global::ChatRoomTray.TraySettings(trayIni);
            var trayText = File.ReadAllText(trayIni);
            Check("TraySettings 写入后可读回", trayReloaded.Args == "serve --port 9000", trayReloaded.Args);
            Check(
                "TraySettings 保留其他 INI section",
                trayText.Contains("[Other]") && trayText.Contains("Keep=yes"));

#if !WINDOWS_TRAY
            var deployRoot = Path.Combine(directory, "deploy");
            var nestedApp = Path.Combine(deployRoot, "bin", "tray");
            Directory.CreateDirectory(nestedApp);
            Directory.CreateDirectory(Path.Combine(deployRoot, "dist", "cli"));
            File.WriteAllText(Path.Combine(deployRoot, "dist", "cli", "index.js"), string.Empty);
            File.WriteAllText(Path.Combine(deployRoot, "node"), string.Empty);
            var resolvedDeploy = global::ChatRoomTray.CrossPlatformTrayHost.ResolveRuntime(nestedApp);
            Check(
                "跨平台托盘可从父目录解析 node + dist/cli",
                resolvedDeploy.NodeExecutable == Path.Combine(deployRoot, "node")
                && resolvedDeploy.EntryPath == Path.Combine(deployRoot, "dist", "cli", "index.js")
                && resolvedDeploy.WorkingDirectory == deployRoot);

            var bundleRoot = Path.Combine(directory, "ChatRoomTray.app", "Contents");
            var macAppDir = Path.Combine(bundleRoot, "MacOS");
            var macResources = Path.Combine(bundleRoot, "Resources");
            Directory.CreateDirectory(macAppDir);
            Directory.CreateDirectory(Path.Combine(macResources, "dist", "cli"));
            File.WriteAllText(Path.Combine(macResources, "dist", "cli", "index.js"), string.Empty);
            File.WriteAllText(Path.Combine(macResources, "node"), string.Empty);
            var resolvedBundle = global::ChatRoomTray.CrossPlatformTrayHost.ResolveRuntime(macAppDir);
            Check(
                "macOS bundle 优先解析 Contents/Resources runtime",
                resolvedBundle.NodeExecutable == Path.Combine(macResources, "node")
                && resolvedBundle.EntryPath == Path.Combine(macResources, "dist", "cli", "index.js")
                && resolvedBundle.WorkingDirectory == macResources);

            var processExecutable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(Environment.SystemDirectory, "PING.EXE")
                : "/bin/sleep";
            var processEntry = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "-n" : "30";
            var processArgs = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[] { "30", "127.0.0.1" }
                : Array.Empty<string>();
            using (var smokeProcess = global::ChatRoomTray.CrossPlatformTrayHost.StartRuntimeProcess(
                       processExecutable,
                       processEntry,
                       directory,
                       processArgs))
            {
                System.Threading.Thread.Sleep(150);
                Check("跨平台托盘可启动长运行子进程", !smokeProcess.HasExited);
                global::ChatRoomTray.CrossPlatformTrayHost.TerminateRuntimeProcess(smokeProcess);
                Check("跨平台托盘可终止子进程树", smokeProcess.HasExited);
            }

            var lockPath = Path.Combine(directory, "chatroom-tray.lock");
            using (var firstLock = global::ChatRoomTray.CrossPlatformTrayApplication.TryAcquireInstanceLock(lockPath))
            {
                using var secondLock = global::ChatRoomTray.CrossPlatformTrayApplication.TryAcquireInstanceLock(lockPath);
                Check(
                    "跨平台托盘单实例文件锁会拒绝第二实例",
                    firstLock is not null && secondLock is null);
            }
            using (var reacquiredLock = global::ChatRoomTray.CrossPlatformTrayApplication.TryAcquireInstanceLock(lockPath))
            {
                Check("跨平台托盘锁在进程释放后可重新获取", reacquiredLock is not null);
            }
#endif

#if LINUX_TRAY
            var appIndicatorProbeSafe = true;
            string? appIndicatorProbeError = null;
            try
            {
                if (global::ChatRoomTray.NativeAppIndicator.TryLoad(out var nativeIndicator))
                    nativeIndicator.Dispose();
            }
            catch (Exception error)
            {
                appIndicatorProbeSafe = false;
                appIndicatorProbeError = error.Message;
            }
            Check(
                "Linux AppIndicator 动态探测不会抛异常",
                appIndicatorProbeSafe,
                appIndicatorProbeError);

            var watcherProbeSafe = true;
            string? watcherProbeError = null;
            try
            {
                _ = global::ChatRoomTray.LinuxDesktopSession.HasStatusNotifierWatcher();
            }
            catch (Exception error)
            {
                watcherProbeSafe = false;
                watcherProbeError = error.Message;
            }
            Check(
                "Linux StatusNotifierWatcher 探测不会抛异常",
                watcherProbeSafe,
                watcherProbeError);
#endif

            // Timestamped config backups are rotated, while unrelated .bak files are untouched.
            for (var index = 0; index < 12; index++)
            {
                var stamp = new DateTime(2020, 1, 1, 0, 0, index).ToString("yyyyMMdd-HHmmss");
                File.WriteAllText(target + "." + stamp + ".bak", "backup-" + index);
            }
            var unrelatedBackup = target + ".manual.bak";
            File.WriteAllText(unrelatedBackup, "keep me");
            var newestBackup = ConfigStore.Save(target, defaults);
            var timestampedBackups = Directory.GetFiles(directory, "config.json.*.bak")
                .Where(candidate =>
                {
                    var name = Path.GetFileName(candidate);
                    var prefix = "config.json.";
                    const string suffix = ".bak";
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)
                        || !name.EndsWith(suffix, StringComparison.Ordinal))
                        return false;
                    var stamp = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
                    return DateTime.TryParseExact(
                        stamp,
                        "yyyyMMdd-HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out _);
                })
                .ToList();
            Check(
                "配置历史备份最多保留 10 个",
                timestampedBackups.Count == 10,
                $"实际 {timestampedBackups.Count} 个");
            Check(
                "最新自动备份被保留",
                newestBackup is not null && File.Exists(newestBackup),
                newestBackup ?? "未生成备份");
            Check("非自动备份文件不会被清理", File.Exists(unrelatedBackup));

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

    /// <summary>
    /// The Windows tray build is a WinExe and borrows the caller's console.
    /// Linux/macOS builds are normal console executables, so no attachment is needed.
    /// </summary>
    private static void AttachConsole()
    {
#if WINDOWS_TRAY
        try
        {
            NativeAttachConsole(-1);
        }
        catch
        {
            // No parent console (double-clicked); console output is simply lost.
        }
#endif
    }

#if WINDOWS_TRAY
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool NativeAttachConsole(int processId);
#endif
}
