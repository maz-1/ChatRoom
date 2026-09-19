using System;
using Eto.Forms;

namespace ChatRoomTray.ConfigEditor;

/// <summary>
/// Hosts the integrated Eto.Forms editor inside the ChatRoomTray project.
/// Windows uses the existing WinForms backend; Linux uses GTK; macOS uses Eto.Mac64.
/// </summary>
internal static class ConfigEditorHost
{
    public static int Run()
    {
        using var application = CreateApplication();
        application.Run(new MainForm());
        return 0;
    }

    public static T WithApplication<T>(Func<T> action)
    {
        using var application = CreateApplication();
        return action();
    }

    private static Application CreateApplication()
    {
#if WINDOWS_TRAY
        // Windows uses Eto's WinForms backend inside the existing tray process.
        return new Application(new Eto.WinForms.Platform());
#else
        if (OperatingSystem.IsLinux())
            return new Application(Eto.Platforms.Gtk);

        if (OperatingSystem.IsMacOS())
            return new Application(Eto.Platforms.Mac64);

        throw new PlatformNotSupportedException(
            "跨平台配置编辑器当前支持 Windows、Linux 和 macOS。");
#endif
    }
}
