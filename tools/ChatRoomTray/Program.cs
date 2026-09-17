using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ChatRoomConfigEditor;

namespace ChatRoomTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        EnableDpiAwareness();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Any(arg => string.Equals(arg, "--config", StringComparison.OrdinalIgnoreCase)))
        {
            Application.Run(new MainForm());
            return 0;
        }

        if (args.Any(arg => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                "ChatRoomTray\n\n"
                + "直接启动：常驻托盘并自动启动 ChatRoom。\n"
                + "--config：仅打开集成的配置编辑器。",
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

    private static void EnableDpiAwareness()
    {
        // Prefer Per-Monitor V2 on current Windows. The manifest and App.config
        // declare the same mode; these calls are a runtime fallback for hosts
        // or deployment paths that do not honor one of those declarations.
        try
        {
            if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-Windows 10 1607: fall back to the Windows 8.1 API below.
        }

        try
        {
            if (SetProcessDpiAwareness(2) == 0) return;
        }
        catch (DllNotFoundException)
        {
            // Pre-Windows 8.1: fall back to system-DPI awareness below.
        }
        catch (EntryPointNotFoundException)
        {
            // Same fallback as above.
        }

        try
        {
            SetProcessDPIAware();
        }
        catch (EntryPointNotFoundException)
        {
            // Very old Windows; leave the framework default unchanged.
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
}
