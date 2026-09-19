using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ChatRoomTray.ConfigEditor;

/// <summary>
/// Splits a pasted command line into executable + argv using the current OS rules.
/// Windows delegates to CommandLineToArgvW; Linux/macOS use shell-like quoting.
/// </summary>
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

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? TryParseWindows(input, out command, out args, out error)
            : TryParsePosix(input, out command, out args, out error);
    }

    private static bool TryParseWindows(
        string input,
        out string command,
        out List<string> args,
        out string error)
    {
        command = string.Empty;
        args = new List<string>();
        error = string.Empty;

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

    private static bool TryParsePosix(
        string input,
        out string command,
        out List<string> args,
        out string error)
    {
        command = string.Empty;
        args = new List<string>();
        error = string.Empty;

        var tokens = new List<string>();
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var tokenStarted = false;

        for (var index = 0; index < input.Length; index++)
        {
            var ch = input[index];

            if (ch == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                tokenStarted = true;
                continue;
            }

            if (ch == '"' && !inSingle)
            {
                inDouble = !inDouble;
                tokenStarted = true;
                continue;
            }

            if (ch == '\\' && !inSingle)
            {
                if (index + 1 >= input.Length)
                {
                    error = "完整命令末尾存在未完成的转义符。";
                    return false;
                }

                current.Append(input[++index]);
                tokenStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inSingle && !inDouble)
            {
                if (!tokenStarted) continue;
                tokens.Add(current.ToString());
                current.Clear();
                tokenStarted = false;
                continue;
            }

            current.Append(ch);
            tokenStarted = true;
        }

        if (inSingle || inDouble)
        {
            error = "完整命令包含未闭合的引号。";
            return false;
        }

        if (tokenStarted) tokens.Add(current.ToString());
        if (tokens.Count == 0 || tokens[0].Length == 0)
        {
            error = "未解析到启动命令。";
            return false;
        }

        command = tokens[0];
        for (var index = 1; index < tokens.Count; index++)
            args.Add(tokens[index]);

        return true;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
