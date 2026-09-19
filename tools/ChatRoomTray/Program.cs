using System;
using System.Linq;
using ChatRoomTray.ConfigEditor;

#if NETFRAMEWORK
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
#endif

namespace ChatRoomTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
#if NETFRAMEWORK
        return RunWindowsTrayBuild(args);
#else
        if (args.Any(arg => string.Equals(arg, "--config", StringComparison.OrdinalIgnoreCase)))
            return ConfigEditorHost.Run();

        if (args.Length == 0
            || args.Any(arg => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase)))
            return CrossPlatformTrayApplication.Run();

        return ConfigEditorCommands.Run(args);
#endif
    }

#if NETFRAMEWORK
    private static int RunWindowsTrayBuild(string[] args)
    {
        EnableDpiAwareness();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length > 0
            && (!args[0].StartsWith("-", StringComparison.Ordinal)
                || IsConfigEditorCommand(args[0])))
            return ConfigEditorCommands.Run(args);

        if (args.Any(arg => string.Equals(arg, "--config", StringComparison.OrdinalIgnoreCase)))
            return ConfigEditorHost.Run();

        if (args.Any(arg => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                "ChatRoomTray\n\n"
                + "直接启动：常驻托盘并自动启动 ChatRoom。\n"
                + "--tray：显式启动托盘（与直接启动相同）。\n"
                + "--config：仅打开集成的配置编辑器。\n"
                + "--check [path]：校验 ChatRoom 配置。\n"
                + "--roundtrip <src> <dst>：配置往返验证。\n"
                + "--selftest：运行配置逻辑自检。\n"
                + "--uismoke：运行配置界面自检。",
                "ChatRoom Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        using var mutex = new Mutex(initiallyOwned: true, name: @"Local\ChatRoomTray", createdNew: out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "ChatRoom 托盘管理器已经在运行。",
                "ChatRoom Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        try
        {
            Application.Run(new TrayApplicationContext());
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(
                error.Message,
                "ChatRoom Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static bool IsConfigEditorCommand(string arg) =>
        string.Equals(arg, "--check", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--roundtrip", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--uismoke", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--traysmoke", StringComparison.OrdinalIgnoreCase);

    private static void EnableDpiAwareness()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        try
        {
            if (SetProcessDpiAwareness(2) == 0) return;
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        try
        {
            SetProcessDPIAware();
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int awareness);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDPIAware();
#endif
}
