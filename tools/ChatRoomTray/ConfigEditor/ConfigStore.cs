using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatRoomTray.ConfigEditor;

public sealed class ConfigLoadResult
{
    public ConfigLoadResult(
        ChatRoomConfig config,
        bool existed,
        List<string> unknownKeys,
        DateTime? lastWriteTimeUtc,
        long? length,
        bool hasLegacyOwnerToken,
        string? legacyOwnerToken)
    {
        Config = config;
        Existed = existed;
        UnknownKeys = unknownKeys;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Length = length;
        HasLegacyOwnerToken = hasLegacyOwnerToken;
        LegacyOwnerToken = legacyOwnerToken;
    }

    public ChatRoomConfig Config { get; }

    public bool Existed { get; }

    /// <summary>Keys present in the file that ChatRoom's strict schema rejects.</summary>
    public List<string> UnknownKeys { get; }

    public DateTime? LastWriteTimeUtc { get; }

    public long? Length { get; }

    public bool HasLegacyOwnerToken { get; }

    public string? LegacyOwnerToken { get; }
}

public sealed class ConfigChangedOnDiskException : Exception
{
    public ConfigChangedOnDiskException(string message) : base(message) { }
}

/// <summary>
/// Reads and writes the ChatRoom configuration file without ever producing a
/// file ChatRoom would refuse to start from: JSON is parsed strictly (comments
/// and unknown keys are surfaced, not silently kept), written without a BOM,
/// and replaced behind a timestamped backup.
/// </summary>
public static class ConfigStore
{
    public static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Include,
        MissingMemberHandling = MissingMemberHandling.Ignore,
        // ChatRoom uses JSON.parse: comments and trailing commas are invalid there.
        DateParseHandling = DateParseHandling.None,
    };

    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    public static ConfigLoadResult Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("配置文件不存在：" + path, path);

        var text = File.ReadAllText(path, Encoding.UTF8);

        JObject root;
        try
        {
            root = JObject.Parse(text);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "配置文件不是合法 JSON（ChatRoom 同样会拒绝启动）：" + error.Message, error);
        }

        var unsupported = FindUnsupportedTransport(root);
        if (unsupported is not null) throw new InvalidDataException(unsupported);

        var legacyOwnerToken = ExtractLegacyOwnerToken(root, out var hasLegacyOwnerToken);

        ChatRoomConfig config;
        try
        {
            config = root.ToObject<ChatRoomConfig>(JsonSerializer.Create(JsonSettings))
                ?? throw new InvalidDataException("配置文件内容为空。");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "配置文件字段类型不正确（ChatRoom 同样会拒绝启动）：" + error.Message, error);
        }

        var info = new FileInfo(path);
        return new ConfigLoadResult(
            config,
            true,
            FindUnknownKeys(root),
            info.LastWriteTimeUtc,
            info.Length,
            hasLegacyOwnerToken,
            legacyOwnerToken);
    }

    /// <summary>Builds ChatRoom's own defaults (see defaultConfig() in load-config.ts).</summary>
    public static ChatRoomConfig CreateDefault()
    {
        var config = new ChatRoomConfig();
        config.AllowedRoots.Add(ConfigPaths.DefaultAllowedRoot());
        config.DataDir = ConfigPaths.DataDirectory();
        config.DatabasePath = ConfigPaths.DefaultDatabasePath();
        config.Server = new ServerSection { Host = "127.0.0.1", Port = 8765 };
        config.Auth = new AuthSection
        {
            LocalWebAuth = false,
            McpPublicBaseUrl = null,
            WebPublicBaseUrl = null,
        };
        config.Auth.AllowedRedirectHosts.AddRange(
            new[] { "chatgpt.com", "localhost", "127.0.0.1" });
        config.Http = new HttpSection();
        config.Operations = new OperationsSection();
        config.Mcp = new McpSection();
        config.Process = new ProcessSection();
        return config;
    }

    public static string Serialize(ChatRoomConfig config) =>
        JsonConvert.SerializeObject(config, JsonSettings) + "\n";

    /// <summary>
    /// Writes via a temporary file in the same directory, then replaces the
    /// target. Returns the backup path when an existing file was preserved.
    /// </summary>
    public static string? Save(
        string path,
        ChatRoomConfig config,
        DateTime? expectedLastWriteUtc = null)
    {
        path = Path.GetFullPath(path);
        if (expectedLastWriteUtc.HasValue && File.Exists(path))
        {
            var actual = File.GetLastWriteTimeUtc(path);
            if (actual != expectedLastWriteUtc.Value)
                throw new ConfigChangedOnDiskException(
                    "配置文件在打开之后被其他程序修改过（可能是 ChatRoom 或手工编辑）。请重新加载后再保存。");
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("无法解析目录：" + path);
        Directory.CreateDirectory(directory);

        string? backup = null;
        if (File.Exists(path))
        {
            backup = string.Format(
                "{0}.{1:yyyyMMdd-HHmmss}.bak", path, DateTime.Now);
            File.Copy(path, backup, true);
            PruneBackups(path);
        }

        var temporary = Path.Combine(
            directory,
            Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // No BOM: Node's JSON.parse rejects a leading U+FEFF.
            File.WriteAllText(temporary, Serialize(config), Utf8NoBom);
            // File.Move(overwrite:) does not exist on .NET Framework 4.8.
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return backup;
    }

    private static void PruneBackups(string path)
    {
        const int maxBackupCount = 10;

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("无法解析目录：" + path);
        var fileName = Path.GetFileName(path);
        var prefix = fileName + ".";
        const string suffix = ".bak";

        var backups = Directory.GetFiles(directory, fileName + ".*.bak")
            .Where(candidate =>
            {
                var name = Path.GetFileName(candidate);
                if (!name.StartsWith(prefix, StringComparison.Ordinal)
                    || !name.EndsWith(suffix, StringComparison.Ordinal))
                    return false;

                var timestamp = name.Substring(
                    prefix.Length,
                    name.Length - prefix.Length - suffix.Length);
                return DateTime.TryParseExact(
                    timestamp,
                    "yyyyMMdd-HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out _);
            })
            .OrderByDescending(candidate => Path.GetFileName(candidate), StringComparer.Ordinal)
            .Skip(maxBackupCount)
            .ToList();

        foreach (var oldBackup in backups)
            File.Delete(oldBackup);
    }

    public static void RemoveLegacyOwnerToken(string path, DateTime? expectedLastWriteUtc)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) return;
        if (expectedLastWriteUtc.HasValue && File.GetLastWriteTimeUtc(path) != expectedLastWriteUtc.Value)
            throw new ConfigChangedOnDiskException(
                "配置文件在 ownerToken 迁移期间被其他程序修改过，请重新加载后重试。");

        var root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (!(root["auth"] is JObject auth)) return;
        var property = auth.Property("ownerToken");
        if (property is null) return;
        property.Remove();

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("无法解析目录：" + path);
        var temporary = Path.Combine(
            directory,
            Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, root.ToString(Formatting.Indented) + "\n", Utf8NoBom);
            File.Replace(temporary, path, null);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string? ExtractLegacyOwnerToken(JObject root, out bool present)
    {
        present = false;
        if (!(root["auth"] is JObject auth)) return null;
        var property = auth.Property("ownerToken");
        if (property is null) return null;
        present = true;
        if (property.Value.Type == JTokenType.Null) return null;
        if (property.Value.Type != JTokenType.String)
            throw new InvalidDataException("auth.ownerToken 旧字段必须是字符串或 null。");
        var token = (string?)property.Value;
        if (string.IsNullOrEmpty(token))
            throw new InvalidDataException("auth.ownerToken 旧字段不能是空字符串。");
        return token;
    }

    /// <summary>
    /// Mirrors ChatRoom's own pre-parse check: the legacy SSE transport is
    /// rejected with guidance before the schema reports a vague error.
    /// </summary>
    private static string? FindUnsupportedTransport(JObject root)
    {
        var servers = root["mcp"]?["servers"] as JObject;
        if (servers is null) return null;

        foreach (var server in servers.Properties())
        {
            if (!(server.Value is JObject entry)) continue;
            if ((string?)entry["type"] != "sse") continue;
            return "MCP 服务“" + server.Name + "”使用了已废弃的 \"sse\" 传输方式，ChatRoom 不支持；"
                 + "请改为 \"http\"（Streamable HTTP）。";
        }

        return null;
    }

    private static List<string> FindUnknownKeys(JObject root)
    {
        var unknown = new List<string>();

        foreach (var property in root.Properties())
        {
            if (!KnownRootKeys.Contains(property.Name))
            {
                unknown.Add(property.Name);
                continue;
            }

            HashSet<string> known;
            if (!KnownSections.TryGetValue(property.Name, out known!)) continue;
            if (!(property.Value is JObject section)) continue;
            CollectUnknown(section, known, property.Name, unknown);
            if (property.Name == "mcp") CollectUnknownMcpServers(section, unknown);
        }

        return unknown;
    }

    /// <summary>Every mcp server entry is also a strict object.</summary>
    private static void CollectUnknownMcpServers(JObject mcp, List<string> unknown)
    {
        var servers = mcp["servers"] as JObject;
        if (servers is null) return;

        foreach (var server in servers.Properties())
        {
            if (!(server.Value is JObject entry)) continue;
            var type = (string?)entry["type"];
            HashSet<string>? known = null;
            if (type == "http") known = KnownHttpServerKeys;
            else if (type == "stdio") known = KnownStdioServerKeys;
            if (known is null) continue;
            CollectUnknown(entry, known, "mcp.servers." + server.Name, unknown);
        }
    }

    private static void CollectUnknown(
        JObject section,
        HashSet<string> known,
        string prefix,
        List<string> unknown)
    {
        foreach (var property in section.Properties())
            if (!known.Contains(property.Name))
                unknown.Add(prefix + "." + property.Name);
    }

    private static readonly HashSet<string> KnownRootKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "allowedRoots", "dataDir", "databasePath", "server", "auth",
        "http", "operations", "mcp", "process",
    };

    private static readonly Dictionary<string, HashSet<string>> KnownSections =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["server"] = new HashSet<string>(StringComparer.Ordinal) { "host", "port" },
            ["auth"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "localWebAuth", "ownerToken", "mcpPublicBaseUrl",
                "webPublicBaseUrl", "allowedRedirectHosts",
            },
            ["http"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "defaultTimeoutMs", "maxTimeoutMs", "maxResponseBytes",
            },
            ["operations"] = new HashSet<string>(StringComparer.Ordinal) { "maxPayloadBytes" },
            ["mcp"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "callTimeoutMs", "maxResultBytes", "servers",
            },
            ["process"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "maxOutputBytes", "defaultTimeoutMs", "maxCompletedProcesses",
            },
        };

    private static readonly HashSet<string> KnownStdioServerKeys =
        new HashSet<string>(StringComparer.Ordinal) { "type", "command", "args", "env", "cwd" };

    private static readonly HashSet<string> KnownHttpServerKeys =
        new HashSet<string>(StringComparer.Ordinal) { "type", "url", "headers", "proxy" };
}
