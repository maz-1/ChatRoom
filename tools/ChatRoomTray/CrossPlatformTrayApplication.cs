#if !WINDOWS_TRAY
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Eto.Forms;
using ChatRoomTray.ConfigEditor;

namespace ChatRoomTray;

internal static class CrossPlatformTrayApplication
{
    public static int Run()
    {
        using var instanceLock = TryAcquireInstanceLock();
        if (instanceLock is null) return 0;

        using var application = CreateApplication();
        using var host = new CrossPlatformTrayHost(application);
        using var sigTerm = RegisterTerminationSignal(PosixSignal.SIGTERM, host);
        using var sigInt = RegisterTerminationSignal(PosixSignal.SIGINT, host);

        host.Initialize();
        application.Run();
        return 0;
    }

    internal static int RunSmoke()
    {
        using var application = CreateApplication();
        using var host = new CrossPlatformTrayHost(application);
        using var timer = new UITimer { Interval = 1.0 };

        timer.Elapsed += (_, _) =>
        {
            timer.Stop();
            Environment.Exit(0);
        };

        host.Initialize(startRuntime: false);
        timer.Start();
        application.Run();
        return 0;
    }

    private static Application CreateApplication()
    {
#if LINUX_TRAY
        return new Application(Eto.Platforms.Gtk);
#elif MACOS_TRAY
        return new Application(Eto.Platforms.Mac64);
#else
        throw new PlatformNotSupportedException("跨平台托盘仅支持 Linux 和 macOS。");
#endif
    }

    private static PosixSignalRegistration? RegisterTerminationSignal(
        PosixSignal signal,
        CrossPlatformTrayHost host)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return null;

        return PosixSignalRegistration.Create(signal, context =>
        {
            try
            {
                host.PrepareForSignalTermination();
            }
            catch
            {
                // Best effort: the OS still performs the default signal action.
            }
            finally
            {
                // Let SIGTERM/SIGINT perform their normal process termination
                // after the child runtime has been cleaned up.
                context.Cancel = false;
            }
        });
    }

    internal static FileStream? TryAcquireInstanceLock(string? lockPath = null)
    {
        var path = lockPath ?? Path.Combine(
            ConfigPaths.StateDirectory(),
            "chatroom-tray.lock");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            try
            {
                if (stream.Length == 0)
                {
                    stream.WriteByte(0);
                    stream.Flush(flushToDisk: true);
                }

                // Holding a FileStream opened with FileShare.None provides the
                // cross-process lifetime lock on Windows, Linux and macOS.
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

}

internal sealed class CrossPlatformTrayHost : IDisposable
{
    private static readonly TimeSpan QuickExitWindow = TimeSpan.FromSeconds(15);

    private readonly Application _application;
    private readonly string _appDir;
    private readonly string _chatEntryPath;
    private readonly string _nodeExecutable;
    private readonly string _runtimeWorkingDirectory;
    private readonly TraySettings _settings;
    private readonly UITimer _watchTimer;
    private readonly TrayActions _actions;

    private ITrayBackend? _tray;
    private Process? _process;
    private MainForm? _configForm;
    private Form? _fallbackWindow;
    private Label? _fallbackReason;
    private Label? _fallbackStatus;
    private Button? _fallbackStart;
    private Button? _fallbackStop;
    private Button? _fallbackRestart;
    private DateTime _recentStartUtc;
    private bool _wasRunning;
    private bool _exiting;
    private bool _disposed;

    public CrossPlatformTrayHost(Application application)
    {
        _application = application;
        _appDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var runtime = ResolveRuntime(_appDir);
        _chatEntryPath = runtime.EntryPath;
        _nodeExecutable = runtime.NodeExecutable;
        _runtimeWorkingDirectory = runtime.WorkingDirectory;
        _settings = new TraySettings(ResolveTraySettingsPath());

        _actions = new TrayActions
        {
            OpenWebUi = OpenWebUi,
            Start = () => StartChatRoom(),
            Stop = StopChatRoom,
            Restart = RestartChatRoom,
            EditStartupArgs = EditStartupArgs,
            EditConfig = OpenConfigEditor,
            OpenData = OpenDataDirectory,
            Exit = ExitRequested,
        };

        _watchTimer = new UITimer { Interval = 2.0 };
        _watchTimer.Elapsed += (_, _) => WatchProcess();
    }

    public void Initialize(bool startRuntime = true)
    {
        _tray = CreateTrayBackend(_actions);
        _tray.Update(IsRunning);
        _watchTimer.Start();

        if (!startRuntime) return;

        if (StartChatRoom())
            _tray.Notify("ChatRoom", "ChatRoom 已在后台启动。");
    }

    private ITrayBackend CreateTrayBackend(TrayActions actions)
    {
#if LINUX_TRAY
        if (LinuxAppIndicatorTrayBackend.TryCreate(actions, out var linuxTray, out var warning))
        {
            if (!string.IsNullOrWhiteSpace(warning))
                ShowFallbackWindow("Linux 托盘兼容回退", warning);
            return linuxTray;
        }

        var desktop = LinuxDesktopSession.Describe();
        var fallbackReason = string.IsNullOrWhiteSpace(warning)
            ? "无法创建现代 StatusNotifierItem。"
            : warning;

        fallbackReason += "\n\n当前桌面：" + desktop + "\n"
            + "不会继续使用 GTK legacy tray，因为在 GNOME/Wayland 上通常不可见。\n"
            + "建议安装发行版的 libayatana-appindicator3 包；GNOME 还需要启用 "
            + "AppIndicator/KStatusNotifierItem Shell 扩展。";
        ShowFallbackWindow("Linux 托盘兼容回退", fallbackReason);
        return new NullTrayBackend();
#elif MACOS_TRAY
        try
        {
            return new EtoTrayBackend(actions);
        }
        catch (Exception error)
        {
            ShowFallbackWindow(
                "macOS 菜单栏兼容回退",
                "无法创建菜单栏图标：" + error.Message
                + "\n\n已切换到窗口控制模式。");
            return new NullTrayBackend();
        }
#else
        throw new PlatformNotSupportedException();
#endif
    }

    private bool IsRunning
    {
        get
        {
            if (_process is null) return false;
            try
            {
                return !_process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    private bool StartChatRoom()
    {
        if (IsRunning) return false;

        DisposeTrackedProcess();

        if (!File.Exists(_chatEntryPath))
        {
            ShowError(
                "未找到 ChatRoom 运行入口：\n" + _chatEntryPath
                + "\n\n请将托盘程序与 ChatRoom 的 app/dist/cli/index.js 一起部署。",
                "ChatRoom 启动失败");
            return false;
        }

        var configExists = File.Exists(ConfigPaths.DefaultConfigPath());
        var startupArgs = configExists ? _settings.Args : string.Empty;

        try
        {
            _process = StartRuntimeProcess(
                _nodeExecutable,
                _chatEntryPath,
                _runtimeWorkingDirectory,
                ParseStartupArguments(startupArgs));
            _recentStartUtc = DateTime.UtcNow;
            _wasRunning = true;
            UpdateTray();
            return true;
        }
        catch (Exception error)
        {
            DisposeTrackedProcess();
            UpdateTray();
            ShowError(error.Message, "ChatRoom 启动失败");
            return false;
        }
    }

    private void StopChatRoom()
    {
        if (!IsRunning)
        {
            DisposeTrackedProcess();
            _wasRunning = false;
            UpdateTray();
            return;
        }

        var process = _process!;
        try
        {
            TerminateRuntimeProcess(process);
        }
        finally
        {
            DisposeTrackedProcess();
            _wasRunning = false;
            UpdateTray();
        }
    }

    private void RestartChatRoom()
    {
        StopChatRoom();
        System.Threading.Thread.Sleep(250);
        if (StartChatRoom())
            _tray?.Notify("ChatRoom", "ChatRoom 已重启。");
    }

    private void OpenWebUi()
    {
        var port = GetConfiguredPort();
        try
        {
            _application.Open("http://127.0.0.1:" + port);
        }
        catch (Exception error)
        {
            ShowError(error.Message, "无法打开 WebUI");
        }
    }

    private int GetConfiguredPort()
    {
        try
        {
            var path = ConfigPaths.DefaultConfigPath();
            if (!File.Exists(path)) return 8765;
            return ConfigStore.Load(path).Config.Server.Port;
        }
        catch
        {
            return 8765;
        }
    }

    private void EditStartupArgs()
    {
        using var dialog = new StartupArgsEtoDialog(_settings.Args);
        if (!dialog.ShowModal()) return;

        try
        {
            _settings.SaveArgs(dialog.Value);
        }
        catch (Exception error)
        {
            ShowError(error.Message, "保存启动参数失败");
            return;
        }

        if (!IsRunning) return;

        var answer = MessageBox.Show(
            "参数已保存。立即用新参数重启 ChatRoom 吗？",
            "ChatRoom Tray",
            MessageBoxButtons.YesNo,
            MessageBoxType.Question);
        if (answer == DialogResult.Yes) RestartChatRoom();
    }

    private void OpenConfigEditor()
    {
        if (_configForm is not null && !_configForm.IsDisposed)
        {
            if (_configForm.WindowState == WindowState.Minimized)
                _configForm.WindowState = WindowState.Normal;
            _configForm.Show();
            _configForm.BringToFront();
            _configForm.Focus();
            return;
        }

        _configForm = new MainForm { ShowInTaskbar = true };
        _configForm.Closed += (_, _) => _configForm = null;
        _configForm.Show();
        _configForm.BringToFront();
    }
    private void OpenDataDirectory()
    {
        try
        {
            var directory = ConfigPaths.DataDirectory();
            Directory.CreateDirectory(directory);
            PlatformShell.OpenDirectory(directory);
        }
        catch (Exception error)
        {
            ShowError(error.Message, "无法打开数据目录");
        }
    }

    private void ShowFallbackWindow(string heading, string reason)
    {
        if (_fallbackWindow is not null && !_fallbackWindow.IsDisposed)
        {
            if (_fallbackReason is not null) _fallbackReason.Text = reason;
            _fallbackWindow.Show();
            _fallbackWindow.BringToFront();
            return;
        }

        _fallbackStatus = new Label
        {
            Text = IsRunning ? "状态：运行中" : "状态：已停止",
            Font = Eto.Drawing.SystemFonts.Bold(),
        };
        _fallbackStart = new Button { Text = "启动" };
        _fallbackStop = new Button { Text = "停止" };
        _fallbackRestart = new Button { Text = "重启" };
        var openWeb = new Button { Text = "打开 WebUI" };
        var editConfig = new Button { Text = "编辑配置" };
        var openData = new Button { Text = "打开数据目录" };
        var exit = new Button { Text = "退出" };

        openWeb.Click += (_, _) => OpenWebUi();
        _fallbackStart.Click += (_, _) => StartChatRoom();
        _fallbackStop.Click += (_, _) => StopChatRoom();
        _fallbackRestart.Click += (_, _) => RestartChatRoom();
        editConfig.Click += (_, _) => OpenConfigEditor();
        openData.Click += (_, _) => OpenDataDirectory();
        exit.Click += (_, _) => ExitRequested();

        var buttons = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                openWeb,
                _fallbackStart,
                _fallbackStop,
                _fallbackRestart,
                editConfig,
                openData,
                exit,
            },
        };

        var layout = new DynamicLayout
        {
            Padding = new Eto.Drawing.Padding(16),
            Spacing = new Eto.Drawing.Size(8, 10),
        };
        layout.AddRow(new Label
        {
            Text = heading,
            Font = Eto.Drawing.SystemFonts.Bold(),
        });
        layout.AddRow(_fallbackStatus);
        _fallbackReason = new Label
        {
            Text = reason,
            Wrap = WrapMode.Word,
        };
        layout.AddRow(_fallbackReason);
        layout.AddRow(buttons);

        _fallbackWindow = new Form
        {
            Title = "ChatRoom Tray",
            ClientSize = new Eto.Drawing.Size(820, 240),
            MinimumSize = new Eto.Drawing.Size(640, 210),
            Resizable = true,
            ShowInTaskbar = true,
            Content = layout,
        };
        _fallbackWindow.Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            ExitRequested();
        };
        _fallbackWindow.Closed += (_, _) =>
        {
            _fallbackWindow = null;
            _fallbackReason = null;
            _fallbackStatus = null;
            _fallbackStart = null;
            _fallbackStop = null;
            _fallbackRestart = null;
        };
        _fallbackWindow.Show();
        UpdateFallbackControls();
    }

    private void UpdateFallbackControls()
    {
        if (_fallbackStatus is null) return;
        var running = IsRunning;
        _fallbackStatus.Text = running ? "状态：运行中" : "状态：已停止";
        if (_fallbackStart is not null) _fallbackStart.Enabled = !running;
        if (_fallbackStop is not null) _fallbackStop.Enabled = running;
        if (_fallbackRestart is not null) _fallbackRestart.Enabled = running;
    }


    private void WatchProcess()
    {
        var running = IsRunning;
        if (_wasRunning && !running)
        {
            var quickExit = DateTime.UtcNow - _recentStartUtc < QuickExitWindow;
            DisposeTrackedProcess();
            _tray?.Notify(
                "ChatRoom",
                quickExit
                    ? "ChatRoom 启动后很快退出了。请检查端口占用和配置。"
                    : "ChatRoom 进程已退出。");
        }

        _wasRunning = running;
        UpdateTray();
    }

    private void UpdateTray()
    {
        _tray?.Update(IsRunning);
        UpdateFallbackControls();
    }

    internal void PrepareForSignalTermination()
    {
        if (_exiting) return;
        _exiting = true;

        var process = _process;
        try
        {
            if (process is not null) TerminateRuntimeProcess(process);
        }
        finally
        {
            DisposeTrackedProcess();
            _wasRunning = false;
        }
    }

    private void ExitRequested()
    {
        if (_configForm is not null && !_configForm.IsDisposed)
        {
            _configForm.Close();
            if (!_configForm.IsDisposed) return;
        }

        _exiting = true;
        StopChatRoom();
        _application.Quit();
    }

    private void ShowError(string message, string title)
    {
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxType.Error);
    }

    private void DisposeTrackedProcess()
    {
        _process?.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watchTimer.Stop();
        _watchTimer.Dispose();

        if (!_exiting)
        {
            _exiting = true;
            StopChatRoom();
        }

        if (_fallbackWindow is not null && !_fallbackWindow.IsDisposed)
        {
            try { _fallbackWindow.Close(); } catch { }
            _fallbackWindow = null;
        }

        _tray?.Dispose();
        _tray = null;
        DisposeTrackedProcess();
    }

    internal static Process StartRuntimeProcess(
        string executable,
        string entryPath,
        string workingDirectory,
        IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(entryPath);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        return Process.Start(start)
               ?? throw new InvalidOperationException("无法创建 ChatRoom Node.js 进程。");
    }

    internal static void TerminateRuntimeProcess(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
            try
            {
                if (process.HasExited) return;
                process.Kill();
                process.WaitForExit(3000);
            }
            catch
            {
                // It may have exited between checks.
            }
        }
    }

    private static IReadOnlyList<string> ParseStartupArguments(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return Array.Empty<string>();

        if (!CompleteCommandParser.TryParse(
                "chatroom " + args,
                out _,
                out var parsed,
                out var error))
            throw new InvalidOperationException("无法解析启动参数：" + error);

        return parsed;
    }

    private static string ResolveTraySettingsPath()
    {
        var configDirectory = Path.GetDirectoryName(ConfigPaths.DefaultConfigPath());
        if (string.IsNullOrEmpty(configDirectory))
            configDirectory = AppContext.BaseDirectory;
        return Path.Combine(configDirectory, "chatroom-tray.ini");
    }

    internal static (string NodeExecutable, string EntryPath, string WorkingDirectory) ResolveRuntime(string appDir)
    {
        var roots = new List<string>();

        void AddRoot(string path)
        {
            var full = Path.GetFullPath(path);
            if (!roots.Contains(full, StringComparer.Ordinal))
                roots.Add(full);
        }

        AddRoot(appDir);

        // Eto.Mac64 application bundles place auxiliary runtime files under
        // Contents/Resources. Include that sibling before walking ancestors.
        var resources = Path.GetFullPath(Path.Combine(appDir, "..", "Resources"));
        if (Directory.Exists(resources)) AddRoot(resources);

        var current = new DirectoryInfo(appDir);
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
            AddRoot(current.FullName);

        foreach (var root in roots)
        {
            foreach (var relative in new[]
                     {
                         Path.Combine("app", "dist", "cli", "index.js"),
                         Path.Combine("dist", "cli", "index.js"),
                     })
            {
                var entry = Path.Combine(root, relative);
                if (!File.Exists(entry)) continue;

                foreach (var nodeName in new[] { "node", "node.exe" })
                {
                    var bundledNode = Path.Combine(root, nodeName);
                    if (File.Exists(bundledNode))
                        return (bundledNode, entry, root);
                }

                return ("node", entry, root);
            }
        }

        return (
            "node",
            Path.Combine(appDir, "app", "dist", "cli", "index.js"),
            appDir);
    }
}

internal sealed class TrayActions
{
    public Action OpenWebUi { get; set; } = () => { };
    public Action Start { get; set; } = () => { };
    public Action Stop { get; set; } = () => { };
    public Action Restart { get; set; } = () => { };
    public Action EditStartupArgs { get; set; } = () => { };
    public Action EditConfig { get; set; } = () => { };
    public Action OpenData { get; set; } = () => { };
    public Action Exit { get; set; } = () => { };
}

internal interface ITrayBackend : IDisposable
{
    void Update(bool running);
    void Notify(string title, string message);
}

internal sealed class NullTrayBackend : ITrayBackend
{
    public void Update(bool running) { }
    public void Notify(string title, string message) { }
    public void Dispose() { }
}

internal sealed class EtoTrayBackend : ITrayBackend
{
    private readonly ButtonMenuItem _statusItem;
    private readonly ButtonMenuItem _startItem;
    private readonly ButtonMenuItem _stopItem;
    private readonly ButtonMenuItem _restartItem;
    private readonly TrayIndicator _tray;
    private readonly Eto.Drawing.Bitmap _icon;

    public EtoTrayBackend(TrayActions actions)
    {
        _statusItem = new ButtonMenuItem { Text = "状态：已停止", Enabled = false };
        _startItem = MenuItem("启动 ChatRoom", actions.Start);
        _stopItem = MenuItem("停止 ChatRoom", actions.Stop);
        _restartItem = MenuItem("重启 ChatRoom", actions.Restart);

        var menu = new ContextMenu
        {
            Items =
            {
                _statusItem,
                new SeparatorMenuItem(),
                MenuItem("打开 WebUI", actions.OpenWebUi),
                new SeparatorMenuItem(),
                _startItem,
                _stopItem,
                _restartItem,
                MenuItem("启动参数…", actions.EditStartupArgs),
                new SeparatorMenuItem(),
                MenuItem("编辑配置 (config.json)", actions.EditConfig),
                MenuItem("打开数据目录", actions.OpenData),
                new SeparatorMenuItem(),
                MenuItem("退出", actions.Exit),
            }
        };

        _icon = Eto.Drawing.Bitmap.FromResource("ChatRoomTray.chatgpt.png");
        _tray = new TrayIndicator
        {
            Image = _icon,
            Menu = menu,
            Visible = true,
        };
        _tray.Show();
    }

    public void Update(bool running)
    {
        _statusItem.Text = running ? "状态：运行中" : "状态：已停止";
        _startItem.Enabled = !running;
        _stopItem.Enabled = running;
        _restartItem.Enabled = running;
    }

    public void Notify(string title, string message)
    {
        try
        {
            var notification = new Notification
            {
                Title = title,
                Message = message,
            };
            notification.Show();
        }
        catch
        {
            // Notifications are optional; the tray menu remains functional.
        }
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _icon.Dispose();
    }

    private static ButtonMenuItem MenuItem(string text, Action action) =>
        new()
        {
            Text = text,
            Command = new Command((_, _) => action()),
        };
}
#endif
