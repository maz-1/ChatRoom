using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChatRoomTray.ConfigEditor;

internal static class PlatformShell
{
    public static void OpenDirectory(string directory) => OpenTarget(directory);

    public static void OpenUri(string uri) => OpenTarget(uri);

    private static void OpenTarget(string target)
    {
#if NETFRAMEWORK
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        });
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var start = new ProcessStartInfo
            {
                FileName = "open",
                UseShellExecute = false,
            };
            start.ArgumentList.Add(target);
            Process.Start(start);
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var start = new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
            };
            start.ArgumentList.Add(target);
            Process.Start(start);
            return;
        }

        throw new PlatformNotSupportedException(
            "系统打开操作仅支持 Windows、macOS 和 Linux。");
#endif
    }
}
