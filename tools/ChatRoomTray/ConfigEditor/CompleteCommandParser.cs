using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ChatRoomTray.ConfigEditor;

/// <summary>Splits a pasted Windows command line into executable and argv items.</summary>
internal static class CompleteCommandParser
{
    public static bool TryParse(
        string text,
        out string command,
        out List<string> args,
        out string error)
    {
        command = string.Empty;
        args = new List<string>();
        error = string.Empty;

        var input = text.Trim();
        if (input.Length == 0)
        {
            error = "完整命令不能为空。";
            return false;
        }

        var argv = CommandLineToArgvW(input, out var count);
        if (argv == IntPtr.Zero || count == 0)
        {
            error = "无法解析完整命令。";
            return false;
        }

        try
        {
            command = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv)) ?? string.Empty;
            if (command.Length == 0)
            {
                error = "未解析到启动命令。";
                return false;
            }

            for (var index = 1; index < count; index++)
            {
                var value = Marshal.PtrToStringUni(
                    Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty;
                args.Add(value);
            }

            return true;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
