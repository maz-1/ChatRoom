using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChatRoomTray;

internal enum CodexPlatform { Windows, MacOS, Linux }

/// <summary>Discovers Codex using the environment that the Node child will receive.</summary>
internal static class CodexEnvironment
{
    private const int DiscoveryTimeoutMs = 5000;
    private const string PathMarker = "\0CHATROOM_PATH\0";

    internal static CodexPlatform Platform => OperatingSystem.IsWindows()
        ? CodexPlatform.Windows
        : OperatingSystem.IsMacOS() ? CodexPlatform.MacOS : CodexPlatform.Linux;

    internal static Dictionary<string, string> Create(string nodeExecutable)
    {
        var inherited = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) inherited[key] = value;

        return Discover(inherited, nodeExecutable, Platform,
            FallbackDirectories(inherited, Platform), DesktopCandidates(inherited, Platform),
            Probe, ReadLoginShellPath);
    }

    internal static Dictionary<string, string> Discover(
        IReadOnlyDictionary<string, string> inherited,
        string nodeExecutable,
        CodexPlatform platform,
        IEnumerable<string> fallbackDirectories,
        IEnumerable<string> desktopCandidates,
        Func<string, IReadOnlyDictionary<string, string>, int, bool> probe,
        Func<IReadOnlyDictionary<string, string>, int, string?> shellPath,
        int timeoutMs = DiscoveryTimeoutMs)
    {
        var clock = Stopwatch.StartNew();
        var environment = CopyEnvironment(inherited, platform);
        var originalPath = Value(environment, "PATH");
        var support = fallbackDirectories.Where(Directory.Exists).ToList();
        if (Path.IsPathFullyQualified(nodeExecutable))
        {
            var nodeDirectory = Path.GetDirectoryName(nodeExecutable);
            if (nodeDirectory is not null) support.Insert(0, nodeDirectory);
        }

        environment["PATH"] = MergePath(originalPath, support, platform);
        var seen = new HashSet<string>(PathComparer(platform));
        var executableName = platform == CodexPlatform.Windows ? "codex.exe" : "codex";

        bool TryCandidate(string candidate)
        {
            var remaining = timeoutMs - (int)clock.ElapsedMilliseconds;
            if (remaining <= 0 || !Path.IsPathFullyQualified(candidate)
                || !File.Exists(candidate) || !seen.Add(candidate)) return false;
            var directory = Path.GetDirectoryName(candidate);
            if (directory is null || directory.Contains(Separator(platform))) return false;
            var candidateEnvironment = CopyEnvironment(environment, platform);
            // Preserve PATH order unless a rejected executable would shadow this one.
            candidateEnvironment["PATH"] = seen.Count == 1
                ? MergePath(environment["PATH"], new[] { directory }, platform)
                : MergePath(directory, SplitPath(environment["PATH"], platform), platform);
            try
            {
                if (!probe(candidate, candidateEnvironment, Math.Min(1000, remaining))) return false;
            }
            catch (Exception error) when (IsDiscoveryError(error)) { return false; }
            environment["PATH"] = candidateEnvironment["PATH"];
            return true;
        }

        bool TryDirectories(IEnumerable<string> directories)
        {
            foreach (var directory in directories)
            {
                if (clock.ElapsedMilliseconds >= timeoutMs) break;
                if (Path.IsPathFullyQualified(directory)
                    && TryCandidate(Path.Combine(directory, executableName))) return true;
            }
            return false;
        }

        try
        {
            if (TryDirectories(SplitPath(originalPath, platform))) return environment;
            if (TryDirectories(support)) return environment;
            foreach (var candidate in desktopCandidates)
            {
                if (clock.ElapsedMilliseconds >= timeoutMs) break;
                if (TryCandidate(candidate)) return environment;
            }

            var remaining = timeoutMs - (int)clock.ElapsedMilliseconds;
            if (platform != CodexPlatform.Windows && remaining > 0)
            {
                var loginPath = shellPath(environment, Math.Min(1500, remaining));
                if (!string.IsNullOrEmpty(loginPath))
                {
                    // Keep the inherited PATH first, including user-selected CLI versions.
                    environment["PATH"] = MergePath(environment["PATH"],
                        SplitPath(loginPath, platform), platform);
                    // A previously rejected npm shim may now have its Node dependency.
                    seen.Clear();
                    TryDirectories(SplitPath(environment["PATH"], platform));
                }
            }
        }
        catch (Exception error) when (IsDiscoveryError(error))
        {
            // Codex is optional; an inaccessible installation must not prevent startup.
        }
        return environment;
    }

    internal static Dictionary<string, string> CopyEnvironment(
        IReadOnlyDictionary<string, string> source, CodexPlatform platform)
    {
        var result = new Dictionary<string, string>(PathComparer(platform));
        foreach (var entry in source)
            if (platform != CodexPlatform.Windows
                || !entry.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase))
                result[entry.Key] = entry.Value;
        // Node's existing child-environment allowlist expects exactly "PATH".
        result["PATH"] = platform == CodexPlatform.Windows
            ? MergePath(null, source.Where(entry => entry.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Key == "PATH" ? 0 : 1)
                .SelectMany(entry => SplitPath(entry.Value, platform)), platform)
            : Value(source, "PATH") ?? string.Empty;
        return result;
    }

    internal static string MergePath(string? first, IEnumerable<string> additions, CodexPlatform platform)
    {
        var seen = new HashSet<string>(PathComparer(platform));
        return string.Join(Separator(platform), SplitPath(first, platform)
            .Concat(additions).Where(path => path.Length != 0 && seen.Add(path)));
    }

    internal static IEnumerable<string> SplitPath(string? path, CodexPlatform platform) =>
        (path ?? string.Empty).Split(Separator(platform), StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => platform == CodexPlatform.Windows ? entry.Trim('"') : entry);

    internal static string WindowsEnvironmentBlock(IReadOnlyDictionary<string, string> environment) =>
        string.Join('\0', CopyEnvironment(environment, CodexPlatform.Windows)
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Key + "=" + entry.Value)) + "\0\0";

    internal static string ResolveRuntimeExecutable(string executable, IReadOnlyDictionary<string, string> environment)
    {
        if (Path.IsPathFullyQualified(executable) || executable.Contains(Path.DirectorySeparatorChar)) return executable;
        // Process.Start's executable lookup need not use ProcessStartInfo.Environment.
        // Resolve the unbundled Node fallback against the same PATH as Codex.
        foreach (var directory in SplitPath(Value(environment, "PATH"), Platform))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }
        return executable;
    }

    internal static IEnumerable<string> FallbackDirectories(
        IReadOnlyDictionary<string, string> environment, CodexPlatform platform)
    {
        var home = Value(environment, platform == CodexPlatform.Windows ? "USERPROFILE" : "HOME");
        var install = Value(environment, "CODEX_INSTALL_DIR");
        if (!string.IsNullOrEmpty(install)) yield return ExpandHome(install, home);
        if (platform == CodexPlatform.Windows)
        {
            foreach (var executable in WindowsNpmCandidates(environment)) yield return executable;
            var local = Value(environment, "LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local))
                yield return Path.Combine(local, "Programs", "OpenAI", "Codex", "bin");
            yield break;
        }

        if (!string.IsNullOrEmpty(home))
        {
            foreach (var relative in new[] { ".local/bin", "bin", ".npm-global/bin", ".volta/bin", ".asdf/shims" })
                yield return Path.Combine(home, relative);
        }
        foreach (var key in new[] { "NVM_BIN", "VOLTA_HOME", "ASDF_DATA_DIR" })
        {
            var root = Value(environment, key);
            if (!string.IsNullOrEmpty(root))
                yield return key == "NVM_BIN" ? root : Path.Combine(root, key == "VOLTA_HOME" ? "bin" : "shims");
        }
        foreach (var directory in platform == CodexPlatform.MacOS
                     ? new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin" }
                     : new[] { "/usr/local/bin", "/usr/bin", "/bin", "/home/linuxbrew/.linuxbrew/bin" })
            yield return directory;
    }

    internal static IEnumerable<string> WindowsNpmCandidates(IReadOnlyDictionary<string, string> environment)
    {
        // Node cannot directly spawn npm's Windows .cmd shim without a shell.
        // Locate the package's native executable instead of changing the Agent code.
        var directories = SplitPath(Value(environment, "PATH"), CodexPlatform.Windows).ToList();
        var roaming = Value(environment, "APPDATA");
        if (!string.IsNullOrEmpty(roaming)) directories.Add(Path.Combine(roaming, "npm"));
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(directory) || !File.Exists(Path.Combine(directory, "codex.cmd"))) continue;
            var modules = Path.Combine(directory, "node_modules", "@openai");
            foreach (var (package, target) in new[]
                     {
                         ("codex-win32-x64", "x86_64-pc-windows-msvc"),
                         ("codex-win32-arm64", "aarch64-pc-windows-msvc"),
                     })
                foreach (var root in new[]
                         {
                             Path.Combine(modules, "codex"), Path.Combine(modules, package),
                             Path.Combine(modules, "codex", "node_modules", "@openai", package),
                         })
                    yield return Path.Combine(root, "vendor", target, "codex", "codex.exe");
        }
    }

    internal static IEnumerable<string> DesktopCandidates(
        IReadOnlyDictionary<string, string> environment, CodexPlatform platform)
    {
        if (platform == CodexPlatform.Windows)
        {
            var local = Value(environment, "LOCALAPPDATA");
            if (string.IsNullOrEmpty(local)) yield break;
            var root = Path.Combine(local, "OpenAI", "Codex", "bin");
            foreach (var executable in RunningWindowsCodex(root)) yield return executable;
            foreach (var executable in InstalledWindowsCodex(root)) yield return executable;
        }
        else if (platform == CodexPlatform.MacOS)
        {
            var home = Value(environment, "HOME");
            var roots = new List<string> { "/Applications" };
            if (!string.IsNullOrEmpty(home)) roots.Add(Path.Combine(home, "Applications"));
            foreach (var root in roots)
                foreach (var app in new[] { "Codex.app", "ChatGPT.app" })
                    yield return Path.Combine(root, app, "Contents", "Resources", "codex");
        }
    }

    private static IEnumerable<string> RunningWindowsCodex(string root)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var result = new List<string>();
        try
        {
            foreach (var process in Process.GetProcessesByName("codex"))
                using (process)
                    try
                    {
                        var executable = process.MainModule?.FileName;
                        if (executable is not null && executable.StartsWith(root + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase)) result.Add(executable);
                    }
                    catch (Exception error) when (IsDiscoveryError(error)) { }
        }
        catch (Exception error) when (IsDiscoveryError(error)) { }
        return result;
    }

    internal static IEnumerable<string> InstalledWindowsCodex(string root)
    {
        try
        {
            return Directory.GetDirectories(root).Take(128)
                .Select(directory => Path.Combine(directory, "codex.exe"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception error) when (IsDiscoveryError(error)) { return Array.Empty<string>(); }
    }

    internal static bool Probe(string executable, IReadOnlyDictionary<string, string> environment, int timeoutMs)
    {
        try
        {
            var result = Capture(executable, new[] { "--version" }, environment, timeoutMs);
            return result is not null && (result.TrimStart().StartsWith("codex-cli ", StringComparison.Ordinal)
                || result.TrimStart().StartsWith("codex ", StringComparison.Ordinal));
        }
        catch (Exception error) when (IsDiscoveryError(error)) { return false; }
    }

    internal static string? ReadLoginShellPath(IReadOnlyDictionary<string, string> environment, int timeoutMs)
    {
        var shell = Value(environment, "SHELL")
                    ?? (OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash");
        if (!Path.IsPathFullyQualified(shell)
            || !new[] { "bash", "zsh", "sh", "dash", "ksh", "fish" }.Contains(Path.GetFileName(shell))) return null;
        // Interactive login initialization also covers nvm installed in .bashrc/.zshrc.
        var output = Capture(shell, new[] { "-l", "-i", "-c", "printf '\\0CHATROOM_PATH\\0%s\\0' \"$PATH\"" },
            environment, timeoutMs);
        return output is null ? null : ExtractShellPath(output);
    }

    internal static string? ExtractShellPath(string output)
    {
        var start = output.LastIndexOf(PathMarker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += PathMarker.Length;
        var end = output.IndexOf('\0', start);
        return end < 0 ? null : output.Substring(start, end - start);
    }

    private static string? Capture(string executable, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string> environment, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
        using var process = Process.Start(start);
        if (process is null) return null;
        process.StandardInput.Close();
        var output = ReadBounded(process.StandardOutput);
        var error = ReadBounded(process.StandardError);
        try
        {
            if (!process.WaitForExit(Math.Max(1, timeoutMs - (int)clock.ElapsedMilliseconds))) return null;
            if (!Task.WaitAll(new Task[] { output, error },
                    Math.Max(1, timeoutMs - (int)clock.ElapsedMilliseconds))) return null;
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (IsDiscoveryError(exception)) { }
            // Observe faults if a timed-out process leaves redirected streams open.
            _ = output.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _ = error.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private static async Task<string> ReadBounded(StreamReader reader)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            if (result.Length < 65536) result.Append(buffer, 0, Math.Min(count, 65536 - result.Length));
        return result.ToString();
    }

    private static bool IsDiscoveryError(Exception error) => error is IOException
        or UnauthorizedAccessException or System.ComponentModel.Win32Exception
        or InvalidOperationException or ArgumentException or NotSupportedException
        or System.Security.SecurityException or AggregateException;

    private static string? Value(IReadOnlyDictionary<string, string> environment, string key) =>
        environment.TryGetValue(key, out var value) ? value : null;
    private static string ExpandHome(string path, string? home) =>
        home is not null && path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path.Substring(2)) : path;
    private static char Separator(CodexPlatform platform) => platform == CodexPlatform.Windows ? ';' : ':';
    private static StringComparer PathComparer(CodexPlatform platform) => platform == CodexPlatform.Windows
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
