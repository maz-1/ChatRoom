using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ChatRoomConfigEditor;

namespace ChatRoomTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ConsoleTitle = "ChatRoomTray";
    private static readonly TimeSpan QuickExitWindow = TimeSpan.FromSeconds(15);

    private readonly string _appDir;
    private readonly string _nodePath;
    private readonly string _chatEntryPath;
    private readonly TraySettings _settings;
    private readonly Icon _appIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly NotifyIcon _trayIcon;
    private readonly Timer _watchTimer = new() { Interval = 2000 };

    private readonly ToolStripMenuItem _openWebItem = new("打开 WebUI");
    private readonly ToolStripMenuItem _showConsoleItem = new("显示控制台窗口");
    private readonly ToolStripMenuItem _startItem = new("启动 ChatRoom");
    private readonly ToolStripMenuItem _stopItem = new("停止 ChatRoom");
    private readonly ToolStripMenuItem _restartItem = new("重启 ChatRoom");
    private readonly ToolStripMenuItem _argsItem = new("启动参数 ...");
    private readonly ToolStripMenuItem _editConfigItem = new("编辑配置 (config.json)");
    private readonly ToolStripMenuItem _exitItem = new("退出");

    private Process? _process;
    private MainForm? _configForm;
    private DateTime _recentStartUtc;
    private bool _wasRunning;
    private bool _exiting;
    private bool _disposed;

    public TrayApplicationContext()
    {
        _appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _nodePath = Path.Combine(_appDir, "node.exe");
        _chatEntryPath = Path.Combine(_appDir, "app", "dist", "cli", "index.js");

        EnsureRuntimeFiles();

        _settings = new TraySettings(Path.Combine(_appDir, "chatroom-tray.ini"));
        _appIcon = LoadApplicationIcon();
        _trayIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "ChatRoom",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        BuildMenu();
        WireEvents();
        UpdateMenu();

        _watchTimer.Start();
        if (StartChatRoom())
        {
            ShowNotification(
                "ChatRoom 已在后台启动，双击托盘图标可显示/隐藏控制台窗口。",
                ToolTipIcon.Info);
        }
    }

    private void EnsureRuntimeFiles()
    {
        var missing = string.Empty;
        if (!File.Exists(_nodePath)) missing += "\n" + _nodePath;
        if (!File.Exists(_chatEntryPath)) missing += "\n" + _chatEntryPath;
        if (missing.Length == 0) return;

        throw new FileNotFoundException("未找到 ChatRoom 运行文件：" + missing + "\n\n请将 ChatRoomTray.exe 与 ChatRoom 运行文件放在同一目录。");
    }

    private void BuildMenu()
    {
        _showConsoleItem.CheckOnClick = false;
        _menu.Items.Add(_openWebItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_showConsoleItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_startItem);
        _menu.Items.Add(_stopItem);
        _menu.Items.Add(_restartItem);
        _menu.Items.Add(_argsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_editConfigItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);
    }

    private void WireEvents()
    {
        _openWebItem.Click += (_, _) => OpenWebUi();
        _showConsoleItem.Click += (_, _) => ToggleConsole();
        _startItem.Click += (_, _) => StartChatRoom();
        _stopItem.Click += (_, _) => StopChatRoom();
        _restartItem.Click += (_, _) => RestartChatRoom();
        _argsItem.Click += (_, _) => EditStartupArgs();
        _editConfigItem.Click += (_, _) => OpenConfigEditor();
        _exitItem.Click += (_, _) => ExitRequested();
        _trayIcon.DoubleClick += (_, _) => ToggleConsole();
        _watchTimer.Tick += (_, _) => WatchProcess();
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
        var configExists = File.Exists(ConfigPaths.DefaultConfigPath());
        var args = configExists ? _settings.Args : string.Empty;

        try
        {
            _process = StartHiddenConsoleProcess(
                _nodePath,
                BuildNodeArguments(_chatEntryPath, args),
                _appDir);
            _recentStartUtc = DateTime.UtcNow;
            _wasRunning = true;
            UpdateMenu();
            return true;
        }
        catch (Exception error)
        {
            DisposeTrackedProcess();
            UpdateMenu();
            MessageBox.Show(
                error.Message,
                "ChatRoom 启动失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
    }

    private static Process StartHiddenConsoleProcess(string executable, string arguments, string workingDirectory)
    {
        var startupInfo = new NativeMethods.StartupInfo
        {
            cb = Marshal.SizeOf(typeof(NativeMethods.StartupInfo)),
            lpTitle = ConsoleTitle,
            dwFlags = NativeMethods.StartfUseShowWindow,
            wShowWindow = NativeMethods.SwHide,
        };
        var commandLine = new StringBuilder();
        commandLine.Append('"').Append(executable).Append('"');
        if (!string.IsNullOrWhiteSpace(arguments)) commandLine.Append(' ').Append(arguments);

        if (!NativeMethods.CreateProcess(
                executable,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.CreateNewConsole,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out var processInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动 ChatRoom 控制台进程。");
        }

        try
        {
            return Process.GetProcessById(unchecked((int)processInfo.dwProcessId));
        }
        finally
        {
            NativeMethods.CloseHandle(processInfo.hThread);
            NativeMethods.CloseHandle(processInfo.hProcess);
        }
    }

    private static string BuildNodeArguments(string entryPath, string args)
    {
        var suffix = string.IsNullOrWhiteSpace(args) ? string.Empty : " " + args;
        return "\"" + entryPath + "\"" + suffix;
    }

    private void StopChatRoom()
    {
        if (!IsRunning)
        {
            DisposeTrackedProcess();
            UpdateMenu();
            return;
        }

        var process = _process!;
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = "/T /F /PID " + process.Id,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            killer?.WaitForExit(5000);
        }
        catch
        {
            try
            {
                process.Kill();
                process.WaitForExit(3000);
            }
            catch
            {
                // The process may already have exited between the checks above.
            }
        }
        finally
        {
            DisposeTrackedProcess();
            _wasRunning = false;
            UpdateMenu();
        }
    }

    private void RestartChatRoom()
    {
        StopChatRoom();
        System.Threading.Thread.Sleep(300);
        if (StartChatRoom())
            ShowNotification("ChatRoom 已重启。", ToolTipIcon.Info);
    }

    private void OpenWebUi()
    {
        var port = GetConfiguredPort();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "http://127.0.0.1:" + port,
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "无法打开 WebUI", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    private void ToggleConsole()
    {
        if (!IsRunning)
        {
            ShowNotification("未找到控制台窗口，ChatRoom 可能未运行。", ToolTipIcon.Warning);
            return;
        }

        var hwnd = FindConsoleWindow();
        if (hwnd == IntPtr.Zero)
        {
            ShowNotification("未找到控制台窗口，ChatRoom 可能未运行。", ToolTipIcon.Warning);
            return;
        }

        if (NativeMethods.IsWindowVisible(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
        }
        else
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwShow);
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        UpdateMenu();
    }

    private static IntPtr FindConsoleWindow()
    {
        var handle = NativeMethods.FindWindow("ConsoleWindowClass", ConsoleTitle);
        if (handle != IntPtr.Zero) return handle;
        return NativeMethods.FindWindow(null, ConsoleTitle);
    }

    private void EditStartupArgs()
    {
        using var dialog = new StartupArgsDialog(_settings.Args) { Icon = (Icon)_appIcon.Clone() };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        try
        {
            _settings.SaveArgs(dialog.Value);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "保存启动参数失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (IsRunning)
        {
            var answer = MessageBox.Show(
                "参数已保存。立即用新参数重启 ChatRoom 吗？",
                "ChatRoom Tray",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) RestartChatRoom();
        }
    }

    private void OpenConfigEditor()
    {
        if (_configForm is not null && !_configForm.IsDisposed)
        {
            if (_configForm.WindowState == FormWindowState.Minimized)
                _configForm.WindowState = FormWindowState.Normal;
            _configForm.Show();
            _configForm.BringToFront();
            _configForm.Activate();
            return;
        }

        _configForm = new MainForm
        {
            Icon = (Icon)_appIcon.Clone(),
            ShowInTaskbar = true,
        };
        _configForm.FormClosed += (_, _) =>
        {
            _configForm?.Icon?.Dispose();
            _configForm = null;
        };
        _configForm.Show();
        _configForm.BringToFront();
    }

    private void WatchProcess()
    {
        var running = IsRunning;
        if (_wasRunning && !running)
        {
            var quickExit = DateTime.UtcNow - _recentStartUtc < QuickExitWindow;
            DisposeTrackedProcess();
            ShowNotification(
                quickExit
                    ? "ChatRoom 启动后很快退出了。可能是端口被占用（已有 ChatRoom 在运行）或配置有误。"
                    : "ChatRoom 进程已退出。",
                quickExit ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }

        _wasRunning = running;
        UpdateMenu();
    }

    private void UpdateMenu()
    {
        var running = IsRunning;
        _startItem.Enabled = !running;
        _stopItem.Enabled = running;
        _restartItem.Enabled = running;
        _showConsoleItem.Enabled = running;

        var hwnd = running ? FindConsoleWindow() : IntPtr.Zero;
        _showConsoleItem.Checked = hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(hwnd);
        _trayIcon.Text = running ? "ChatRoom - 运行中" : "ChatRoom - 已停止";
    }

    private void ShowNotification(string message, ToolTipIcon icon)
    {
        _trayIcon.BalloonTipTitle = "ChatRoom Tray";
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = icon;
        _trayIcon.ShowBalloonTip(3500);
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
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        if (!_exiting)
        {
            _exiting = true;
            StopChatRoom();
        }

        if (!_disposed)
        {
            _disposed = true;
            _watchTimer.Stop();
            _watchTimer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _menu.Dispose();
            _appIcon.Dispose();
        }

        base.ExitThreadCore();
    }

    private void DisposeTrackedProcess()
    {
        if (_process is null) return;
        _process.Dispose();
        _process = null;
    }

    private static Icon LoadApplicationIcon()
    {
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chatgpt.ico");
        if (File.Exists(iconPath)) return new Icon(iconPath);

        var extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        return extracted is null ? (Icon)SystemIcons.Application.Clone() : (Icon)extracted.Clone();
    }

    private static class NativeMethods
    {
        internal const short SwHide = 0;
        internal const int SwShow = 5;
        internal const int SwRestore = 9;
        internal const int StartfUseShowWindow = 0x00000001;
        internal const uint CreateNewConsole = 0x00000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            internal int cb;
            internal string? lpReserved;
            internal string? lpDesktop;
            internal string? lpTitle;
            internal int dwX;
            internal int dwY;
            internal int dwXSize;
            internal int dwYSize;
            internal int dwXCountChars;
            internal int dwYCountChars;
            internal int dwFillAttribute;
            internal int dwFlags;
            internal short wShowWindow;
            internal short cbReserved2;
            internal IntPtr lpReserved2;
            internal IntPtr hStdInput;
            internal IntPtr hStdOutput;
            internal IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            internal IntPtr hProcess;
            internal IntPtr hThread;
            internal uint dwProcessId;
            internal uint dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(
            string? lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref StartupInfo lpStartupInfo,
            out ProcessInformation lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
