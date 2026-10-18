using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace ChatRoomTray;

internal static class CodexEnvironmentSmoke
{
    internal static void Run(Action<string, bool, string?> check)
    {
        void Check(string name, bool passed) => check("Codex: " + name, passed, null);
        var windows = CodexPlatform.Windows;
        var unix = CodexPlatform.Linux;
        var source = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Path"] = "C:\\tools;C:\\Shared", ["PATH"] = "C:\\preferred;c:\\shared",
            ["HOME"] = "unchanged", ["UNICODE"] = "中文=值",
        };
        var normalized = CodexEnvironment.CopyEnvironment(source, windows);
        Check("Windows 合并 Path/PATH 并保留原环境",
            normalized.Keys.Count(key => key.Equals("PATH", StringComparison.OrdinalIgnoreCase)) == 1
            && normalized.Keys.Contains("PATH") && normalized["PATH"] == "C:\\preferred;c:\\shared;C:\\tools"
            && normalized["HOME"] == "unchanged" && source["Path"] == "C:\\tools;C:\\Shared");
        var block = CodexEnvironment.WindowsEnvironmentBlock(normalized);
        Check("Windows Unicode 环境块排序、双空字符结尾且不丢失中文",
            block.EndsWith("\0\0", StringComparison.Ordinal) && block.Contains("UNICODE=中文=值\0")
            && block.IndexOf("HOME=", StringComparison.Ordinal) < block.IndexOf("PATH=", StringComparison.Ordinal));
        var unixEnvironment = CodexEnvironment.CopyEnvironment(source, unix);
        Check("Unix 环境变量区分大小写", unixEnvironment["PATH"] == source["PATH"]
            && unixEnvironment["Path"] == source["Path"]);
        Check("Unix PATH 使用冒号并按大小写去重",
            CodexEnvironment.MergePath("/one:/ONE", new[] { "/two", "/one", "" }, unix) == "/one:/ONE:/two");
        Check("Windows PATH 使用分号并忽略大小写去重",
            CodexEnvironment.MergePath("C:\\one", new[] { "c:\\ONE", "C:\\two" }, windows) == "C:\\one;C:\\two");
        Check("shell 输出只读取完整标记包围的 PATH",
            CodexEnvironment.ExtractShellPath("startup noise\n\0CHATROOM_PATH\0/a:/b\0prompt") == "/a:/b"
            && CodexEnvironment.ExtractShellPath("/unmarked/path") is null
            && CodexEnvironment.ExtractShellPath("\0CHATROOM_PATH\0/incomplete") is null);

        var fixture = Path.Combine(Path.GetTempPath(), "chatroom-codex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var platform = CodexEnvironment.Platform;
            var separator = platform == windows ? ";" : ":";
            var fileName = platform == windows ? "codex.exe" : "codex";
            string Candidate(string name)
            {
                var directory = Path.Combine(fixture, name);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, fileName);
                File.WriteAllText(path, "synthetic candidate; never executed");
                return path;
            }
            var preferred = Candidate("preferred CLI");
            var fallback = Candidate("fallback CLI 中文");
            var desktop = Candidate("desktop hash");
            var node = Path.Combine(Path.GetDirectoryName(fallback)!, platform == windows ? "node.exe" : "node");
            var inherited = new Dictionary<string, string>
            {
                ["PATH"] = Path.GetDirectoryName(preferred)!, ["HOME"] = fixture, ["UNCHANGED"] = "a=b",
            };
            var calls = new List<string>();
            var shellCalls = 0;
            string? Shell(IReadOnlyDictionary<string, string> env, int timeout)
            {
                shellCalls++;
                return "/shell/only";
            }
            bool Accept(string candidate, IReadOnlyDictionary<string, string> env, int timeout)
            {
                calls.Add(candidate);
                return timeout > 0 && env["UNCHANGED"] == "a=b";
            }
            Dictionary<string, string> Discover(Func<string, IReadOnlyDictionary<string, string>, int, bool> probe,
                IEnumerable<string>? desktops = null, int timeout = 5000) =>
                CodexEnvironment.Discover(inherited, node, platform,
                    new[] { Path.GetDirectoryName(fallback)! }, desktops ?? new[] { desktop }, probe, Shell, timeout);

            var found = Discover(Accept);
            Check("优先验证现有 PATH，原顺序与其他变量不变",
                calls.SequenceEqual(new[] { preferred }) && found["PATH"].StartsWith(inherited["PATH"] + separator,
                    StringComparison.Ordinal) && shellCalls == 0 && found["UNCHANGED"] == "a=b");
            calls.Clear();
            found = Discover((candidate, env, timeout) =>
            {
                calls.Add(candidate);
                return candidate == fallback && env["PATH"].Contains(Path.GetDirectoryName(node)!);
            });
            Check("跳过损坏候选并将有效 Codex 放到遮蔽项之前",
                calls.SequenceEqual(new[] { preferred, fallback })
                && found["PATH"].StartsWith(Path.GetDirectoryName(fallback)! + separator, StringComparison.Ordinal));
            calls.Clear();
            found = Discover((candidate, env, timeout) => { calls.Add(candidate); return candidate == desktop; });
            Check("桌面应用安装目录可回退且路径支持空格与中文",
                calls.SequenceEqual(new[] { preferred, fallback, desktop })
                && CodexEnvironment.SplitPath(found["PATH"], platform).Contains(Path.GetDirectoryName(desktop)!));
            Check("构造子进程环境不修改传入 PATH", inherited["PATH"] == Path.GetDirectoryName(preferred));

            shellCalls = 0;
            found = Discover((candidate, env, timeout) => false, Array.Empty<string>());
            Check("无安装时继续启动，只有 Unix 读取登录 shell",
                found["UNCHANGED"] == "a=b" && shellCalls == (platform == windows ? 0 : 1));
            // Exercise Unix-specific fallback even on the Windows test host.
            shellCalls = 0;
            var unixEmpty = new Dictionary<string, string> { ["PATH"] = "/missing", ["UNCHANGED"] = "a=b" };
            found = CodexEnvironment.Discover(unixEmpty, "node", unix, Array.Empty<string>(),
                Array.Empty<string>(), Accept, Shell);
            Check("Unix GUI 环境可补充 shell PATH 并保留继承优先级",
                shellCalls == 1 && found["PATH"] == "/missing:/shell/only");
            found = CodexEnvironment.Discover(unixEmpty, "node", CodexPlatform.MacOS, Array.Empty<string>(),
                Array.Empty<string>(), Accept, (env, timeout) => throw new IOException("synthetic shell failure"));
            Check("登录 shell 失败不会中止启动", found["PATH"] == "/missing");

            calls.Clear();
            shellCalls = 0;
            var clock = Stopwatch.StartNew();
            found = Discover((candidate, env, timeout) =>
            {
                calls.Add(candidate);
                Thread.Sleep(30);
                return false;
            }, timeout: 15);
            Check("探测耗尽总预算后不再尝试其他候选或 shell",
                calls.Count == 1 && shellCalls == 0 && clock.ElapsedMilliseconds < 1000);

            var versions = Path.Combine(fixture, "versions");
            var older = Path.Combine(versions, "zz-old", "codex.exe");
            var newer = Path.Combine(versions, "aa-new", "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(older)!);
            Directory.CreateDirectory(Path.GetDirectoryName(newer)!);
            File.WriteAllText(older, "old"); File.WriteAllText(newer, "new");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));
            Check("Windows 升级候选按文件时间选择而非哈希目录名称",
                CodexEnvironment.InstalledWindowsCodex(versions).SequenceEqual(new[] { newer, older }));
            Check("缺失安装目录不会报错",
                !CodexEnvironment.InstalledWindowsCodex(Path.Combine(fixture, "absent")).Any());
            var npmRoot = Path.Combine(fixture, "npm global");
            Directory.CreateDirectory(npmRoot);
            File.WriteAllText(Path.Combine(npmRoot, "codex.cmd"), "never executed");
            var nativeNpm = Path.Combine(npmRoot, "node_modules", "@openai", "codex-win32-x64",
                "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
            Check("Windows npm shim 可解析到包内原生可执行文件",
                CodexEnvironment.WindowsNpmCandidates(new Dictionary<string, string> { ["PATH"] = npmRoot })
                    .Contains(nativeNpm));
            var directories = CodexEnvironment.FallbackDirectories(new Dictionary<string, string>
            {
                ["HOME"] = fixture, ["CODEX_INSTALL_DIR"] = Path.Combine(fixture, "custom"),
            }, CodexPlatform.MacOS).ToArray();
            Check("macOS 包含独立安装器与两种 Homebrew 目录",
                directories.Contains(Path.Combine(fixture, ".local/bin"))
                && directories.Contains("/opt/homebrew/bin") && directories.Contains("/usr/local/bin")
                && directories[0] == Path.Combine(fixture, "custom"));

            if (!OperatingSystem.IsWindows()) UnixProcesses(fixture, check);
        }
        catch (Exception error)
        {
            check("Codex: 自检执行无异常", false, error.ToString());
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static void UnixProcesses(string fixture, Action<string, bool, string?> check)
    {
        if (OperatingSystem.IsWindows()) return;
        var executable = Path.Combine(fixture, "real codex probe");
        File.WriteAllText(executable, "#!/bin/sh\nprintf 'codex-cli test-version\\n'\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var environment = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };
        check("Codex: Unix 可执行脚本探测", CodexEnvironment.Probe(executable, environment, 1000), null);
        var link = Path.Combine(fixture, "codex symlink");
        File.CreateSymbolicLink(link, executable);
        check("Codex: Unix 符号链接探测", CodexEnvironment.Probe(link, environment, 1000), null);
        File.WriteAllText(executable, "#!/bin/sh\nprintf 'another-tool 1.0\\n'\n");
        check("Codex: 拒绝同名其他工具", !CodexEnvironment.Probe(executable, environment, 1000), null);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        check("Codex: Unix 无执行权限候选被跳过", !CodexEnvironment.Probe(executable, environment, 1000), null);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(executable, "#!/bin/sh\nsleep 10\n");
        var clock = Stopwatch.StartNew();
        check("Codex: Unix 真实子进程超时会被终止",
            !CodexEnvironment.Probe(executable, environment, 100) && clock.ElapsedMilliseconds < 2000, null);

        var nodeDirectory = Path.Combine(fixture, "bundled node");
        var codexDirectory = Path.Combine(fixture, "npm bin");
        Directory.CreateDirectory(nodeDirectory); Directory.CreateDirectory(codexDirectory);
        var node = Path.Combine(nodeDirectory, "node");
        File.WriteAllText(node, "#!/bin/sh\nprintf 'codex-cli npm-test\\n'\n");
        File.SetUnixFileMode(node, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var npmCodex = Path.Combine(codexDirectory, "codex");
        File.WriteAllText(npmCodex, "#!/usr/bin/env node\n");
        File.SetUnixFileMode(npmCodex, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var resolved = CodexEnvironment.Discover(new Dictionary<string, string> { ["PATH"] = "/missing" },
            node, CodexEnvironment.Platform, new[] { codexDirectory }, Array.Empty<string>(),
            CodexEnvironment.Probe, (env, timeout) => null);
        check("Codex: npm 启动脚本可通过最终 PATH 找到 Node",
            CodexEnvironment.Probe(npmCodex, resolved, 1000), null);
        check("Codex: 非捆绑 Node 使用子进程 PATH 解析",
            CodexEnvironment.ResolveRuntimeExecutable("node", resolved) == node, null);
        var afterShell = CodexEnvironment.Discover(new Dictionary<string, string> { ["PATH"] = codexDirectory },
            "node", CodexEnvironment.Platform, Array.Empty<string>(), Array.Empty<string>(),
            CodexEnvironment.Probe, (env, timeout) => nodeDirectory);
        check("Codex: shell 补全 Node 后重新验证原 npm 候选",
            CodexEnvironment.Probe(npmCodex, afterShell, 1000), null);
        if (File.Exists("/bin/bash"))
        {
            File.WriteAllText(Path.Combine(fixture, ".bash_profile"),
                "printf 'profile banner\\n'\nexport PATH=/shell/profile/bin\n");
            var loginPath = CodexEnvironment.ReadLoginShellPath(new Dictionary<string, string>
            {
                ["SHELL"] = "/bin/bash", ["HOME"] = fixture, ["PATH"] = "/usr/bin:/bin",
            }, 1500);
            check("Codex: 真实登录 shell 可读取 profile PATH 并忽略横幅",
                loginPath == "/shell/profile/bin", loginPath);
        }
    }
}
