using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ChatRoomConfigEditor;

public enum IssueSeverity
{
    /// <summary>ChatRoom refuses to start: must be fixed before saving.</summary>
    Error,

    /// <summary>Valid, but likely to surprise; saving is allowed after confirmation.</summary>
    Warning,
}

public sealed class ValidationIssue
{
    public ValidationIssue(IssueSeverity severity, string path, string message)
    {
        Severity = severity;
        Path = path;
        Message = message;
    }

    public IssueSeverity Severity { get; }

    public string Path { get; }

    public string Message { get; }
}

/// <summary>
/// Reproduces the rules from src/config/load-config.ts (zod schema plus
/// validateRuntimeSecurity) and the runtime proxy checks, so anything this
/// editor writes is something ChatRoom will actually accept.
/// </summary>
public static class ConfigValidator
{
    public const int MillisecondsMin = 1_000;
    public const int MillisecondsMax = 24 * 60 * 60 * 1_000;
    public const int SmallBytesMin = 4_096;
    public const int PayloadBytesMax = 16 * 1024 * 1024;
    public const int OutputBytesMax = 64 * 1024 * 1024;
    public const int PortMin = 1;
    public const int PortMax = 65_535;
    public const int MaxCompletedProcessesMax = 10_000;

    private static readonly Regex McpServerName = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    public static List<ValidationIssue> Validate(ChatRoomConfig config, IEnumerable<string> unknownKeys)
    {
        var issues = new List<ValidationIssue>();

        ValidateRoots(config, issues);
        ValidatePaths(config, issues);
        ValidateServer(config, issues);
        ValidateAuth(config, issues);
        ValidateHttp(config, issues);
        ValidateOperations(config, issues);
        ValidateProcess(config, issues);
        ValidateMcp(config, issues);
        ValidateRuntimeSecurity(config, issues);

        foreach (var key in unknownKeys)
            issues.Add(new ValidationIssue(
                IssueSeverity.Error,
                key,
                "未知字段：ChatRoom 的配置校验是严格的，保留它会拒绝启动，保存时会被移除。"));

        return issues;
    }

    public static bool HasErrors(IEnumerable<ValidationIssue> issues) =>
        issues.Any(issue => issue.Severity == IssueSeverity.Error);

    private static void ValidateRoots(ChatRoomConfig config, List<ValidationIssue> issues)
    {
        if (config.AllowedRoots.Count == 0)
            issues.Add(Error("allowedRoots", "至少要配置一个工作区根目录。"));
        for (var index = 0; index < config.AllowedRoots.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(config.AllowedRoots[index]))
                issues.Add(Error($"allowedRoots[{index}]", "路径不能为空。"));
        }
    }

    private static void ValidatePaths(ChatRoomConfig config, List<ValidationIssue> issues)
    {
        RequireNonEmpty(config.DataDir, "dataDir", "数据目录", issues);

        if (config.DatabasePath is not null)
            RequireNonEmpty(config.DatabasePath, "databasePath", "数据库文件路径", issues);
        else
            issues.Add(new ValidationIssue(
                IssueSeverity.Warning,
                "databasePath",
                "未设置，ChatRoom 将使用 <dataDir>/chatroom.sqlite。"));
    }

    private static void ValidateServer(ChatRoomConfig config, List<ValidationIssue> issues)
    {
        RequireNonEmpty(config.Server.Host, "server.host", "监听地址", issues);

        if (config.Server.Port is < 1 or > 65_535)
            issues.Add(Error("server.port", "端口必须在 1 到 65535 之间。"));
        else if (config.Server.Port < 1_024)
            issues.Add(new ValidationIssue(
                IssueSeverity.Warning,
                "server.port",
                "低于 1024 的端口通常需要管理员权限。"));
    }

    private static void ValidateAuth(ChatRoomConfig config, List<ValidationIssue> issues)
    {
        if (config.Auth.OwnerToken is { Length: 0 })
            issues.Add(Error("auth.ownerToken", "令牌不能是空字符串；应留空(null)或填写有效值。"));

        ValidateOptionalUrl(config.Auth.McpPublicBaseUrl, "auth.mcpPublicBaseUrl", issues);
        ValidateOptionalUrl(config.Auth.WebPublicBaseUrl, "auth.webPublicBaseUrl", issues);

        foreach (var url in new[]
                 {
                     (Value: config.Auth.McpPublicBaseUrl, Path: "auth.mcpPublicBaseUrl"),
                     (Value: config.Auth.WebPublicBaseUrl, Path: "auth.webPublicBaseUrl"),
                 })
        {
            if (url.Value is not null &&
                url.Value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                issues.Add(new ValidationIssue(
                    IssueSeverity.Warning,
                    url.Path,
                    "使用 http:// 明文地址时，令牌与会话 Cookie 会在网络上以明文传输，建议改用 https://。"));
        }

        for (var index = 0; index < config.Auth.AllowedRedirectHosts.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(config.Auth.AllowedRedirectHosts[index]))
                issues.Add(Error($"auth.allowedRedirectHosts[{index}]", "主机名不能为空。"));
        }
    }

    private static void ValidateHttp(ChatRoomConfig config, List<ValidationIssue> issues)
    {
        ValidateRange(config.Http.DefaultTimeoutMs, "http.defaultTimeoutMs", MillisecondsMin, MillisecondsMax, issues);
        ValidateRange(config.Http.MaxTimeoutMs, "http.maxTimeoutMs", MillisecondsMin, MillisecondsMax, issues);
        ValidateRange(config.Http.MaxResponseBytes, "http.maxResponseBytes", SmallBytesMin, PayloadBytesMax, issues);

        if (config.Http.DefaultTimeoutMs > config.Http.MaxTimeoutMs)
            issues.Add(new ValidationIssue(
                IssueSeverity.Warning,
                "http.defaultTimeoutMs",
                "默认超时大于上限，实际会被上限截断（ChatRoom 取两者较小值）。"));
    }

    private static void ValidateOperations(ChatRoomConfig config, List<ValidationIssue> issues) =>
        ValidateRange(config.Operations.MaxPayloadBytes, "operations.maxPayloadBytes", SmallBytesMin, PayloadBytesMax, issues);

    private static void ValidateProcess(ChatRoomConfig config, List<ValidationIssue> issues)
    {
        ValidateRange(config.Process.MaxOutputBytes, "process.maxOutputBytes", SmallBytesMin, OutputBytesMax, issues);
        ValidateRange(config.Process.DefaultTimeoutMs, "process.defaultTimeoutMs", MillisecondsMin, MillisecondsMax, issues);
        ValidateRange(config.Process.MaxCompletedProcesses, "process.maxCompletedProcesses", 0, 10_000, issues);
    }

    private static void ValidateMcp(ChatRoomConfig config, List<ValidationIssue> issues)
    {
        ValidateRange(config.Mcp.CallTimeoutMs, "mcp.callTimeoutMs", MillisecondsMin, MillisecondsMax, issues);
        ValidateRange(config.Mcp.MaxResultBytes, "mcp.maxResultBytes", SmallBytesMin, OutputBytesMax, issues);

        foreach (var pair in config.Mcp.Servers)
        {
            var name = pair.Key;
            var server = pair.Value;
            var path = "mcp.servers." + name;
            if (!McpServerName.IsMatch(name))
                issues.Add(Error(path, "服务名只能包含字母、数字、下划线和连字符，长度 1-64。"));

            switch (server)
            {
                case StdioMcpServerConfig stdio:
                    RequireNonEmpty(stdio.Command, $"{path}.command", "启动命令", issues);
                    if (stdio.Cwd is { Length: 0 })
                        issues.Add(Error($"{path}.cwd", "工作目录不能是空字符串；应留空(null)或填写路径。"));
                    foreach (var key in stdio.Env.Keys)
                    {
                        if (string.IsNullOrWhiteSpace(key))
                            issues.Add(Error($"{path}.env", "环境变量名不能为空。"));
                    }
                    break;

                case HttpMcpServerConfig http:
                    ValidateOptionalUrl(http.Url, $"{path}.url", issues, required: true);
                    if (http.Proxy is { Length: > 0 } proxy)
                        ValidateProxy(proxy, $"{path}.proxy", issues);
                    else if (http.Proxy is { Length: 0 })
                        issues.Add(Error($"{path}.proxy", "代理不能是空字符串；应留空(null)或填写代理地址。"));
                    break;
            }
        }
    }

    /// <summary>Mirrors validateRuntimeSecurity() and createProxyDispatcher().</summary>
    private static void ValidateRuntimeSecurity(ChatRoomConfig config, List<ValidationIssue> issues)
    {
        var host = config.Server.Host.Trim();
        var loopback = host is "127.0.0.1" or "::1" or "localhost";
        if (!loopback && !config.Auth.LocalWebAuth)
            issues.Add(Error(
                "server.host",
                $"绑定到 {host} 时，auth.localWebAuth 必须为 true，否则 ChatRoom 会以 FORBIDDEN 拒绝启动。"));

        var authenticationUsed = config.Auth.LocalWebAuth
            || !string.IsNullOrEmpty(config.Auth.McpPublicBaseUrl)
            || !string.IsNullOrEmpty(config.Auth.WebPublicBaseUrl);

        if (authenticationUsed && !OwnerToken.IsPresent(config.Auth.OwnerToken))
            issues.Add(Error(
                "auth.ownerToken",
                "启用了需要认证的入口（localWebAuth / 公网地址），必须设置 ownerToken，否则 ChatRoom 会拒绝启动。"));
    }

    private static void ValidateProxy(string proxy, string path, List<ValidationIssue> issues)
    {
        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var uri))
        {
            issues.Add(Error(path, "代理地址不是合法 URL。"));
            return;
        }

        if (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "socks5")
        {
            issues.Add(Error(path, "只支持 http、https 和 socks5 代理。"));
            return;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            issues.Add(Error(path, "不支持带认证信息的代理（user:pass@）。"));
            return;
        }

        if (string.IsNullOrEmpty(uri.Host)
            || (uri.AbsolutePath != string.Empty && uri.AbsolutePath != "/")
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            issues.Add(Error(path, "代理地址只能包含主机和可选端口，不能带路径、查询串或片段。"));
            return;
        }

        if (uri.Port is < 1 or > 65_535)
            issues.Add(Error(path, "代理端口必须在 1 到 65535 之间。"));
    }

    private static void ValidateOptionalUrl(
        string? value,
        string path,
        List<ValidationIssue> issues,
        bool required = false)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (required) issues.Add(Error(path, "URL 不能为空。"));
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
            issues.Add(Error(path, "不是合法的绝对 URL。"));
    }

    private static void RequireNonEmpty(
        string value,
        string path,
        string label,
        List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
            issues.Add(Error(path, $"{label}不能为空。"));
    }

    private static void ValidateRange(
        int value,
        string path,
        int min,
        int max,
        List<ValidationIssue> issues)
    {
        if (value < min || value > max)
            issues.Add(Error(path, $"必须在 {min} 到 {max} 之间（当前 {value}）。"));
    }

    private static ValidationIssue Error(string path, string message) =>
        new(IssueSeverity.Error, path, message);
}
