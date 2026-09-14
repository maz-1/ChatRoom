using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ChatRoomConfigEditor;

/// <summary>
/// Mirrors <c>ChatRoomConfig</c> from src/config/types.ts. The <c>Order</c> values
/// fix the serialized key order so the output is stable and matches what
/// ChatRoom itself writes.
/// </summary>
public sealed class ChatRoomConfig
{
    [JsonProperty("allowedRoots", Order = 1)]
    public List<string> AllowedRoots { get; set; } = new List<string>();

    [JsonProperty("dataDir", Order = 2)]
    public string DataDir { get; set; } = string.Empty;

    /// <summary>Optional: when absent ChatRoom derives it from <see cref="DataDir"/>.</summary>
    [JsonProperty("databasePath", Order = 3, NullValueHandling = NullValueHandling.Ignore)]
    public string? DatabasePath { get; set; }

    [JsonProperty("server", Order = 4)]
    public ServerSection Server { get; set; } = new ServerSection();

    [JsonProperty("auth", Order = 5)]
    public AuthSection Auth { get; set; } = new AuthSection();

    [JsonProperty("http", Order = 6)]
    public HttpSection Http { get; set; } = new HttpSection();

    [JsonProperty("operations", Order = 7)]
    public OperationsSection Operations { get; set; } = new OperationsSection();

    [JsonProperty("mcp", Order = 8)]
    public McpSection Mcp { get; set; } = new McpSection();

    [JsonProperty("process", Order = 9)]
    public ProcessSection Process { get; set; } = new ProcessSection();
}

public sealed class ServerSection
{
    [JsonProperty("host", Order = 1)]
    public string Host { get; set; } = "127.0.0.1";

    [JsonProperty("port", Order = 2)]
    public int Port { get; set; } = 8765;
}

public sealed class AuthSection
{
    [JsonProperty("localWebAuth", Order = 1)]
    public bool LocalWebAuth { get; set; }

    /// <summary>Never shown in the editor UI; only copied or regenerated.</summary>
    [JsonProperty("ownerToken", Order = 2)]
    public string? OwnerToken { get; set; }

    [JsonProperty("mcpPublicBaseUrl", Order = 3)]
    public string? McpPublicBaseUrl { get; set; }

    [JsonProperty("webPublicBaseUrl", Order = 4)]
    public string? WebPublicBaseUrl { get; set; }

    [JsonProperty("allowedRedirectHosts", Order = 5)]
    public List<string> AllowedRedirectHosts { get; set; } = new List<string>();
}

public sealed class HttpSection
{
    [JsonProperty("defaultTimeoutMs", Order = 1)]
    public int DefaultTimeoutMs { get; set; } = 30_000;

    [JsonProperty("maxTimeoutMs", Order = 2)]
    public int MaxTimeoutMs { get; set; } = 120_000;

    [JsonProperty("maxResponseBytes", Order = 3)]
    public int MaxResponseBytes { get; set; } = 1024 * 1024;
}

public sealed class OperationsSection
{
    [JsonProperty("maxPayloadBytes", Order = 1)]
    public int MaxPayloadBytes { get; set; } = 512 * 1024;
}

public sealed class ProcessSection
{
    [JsonProperty("maxOutputBytes", Order = 1)]
    public int MaxOutputBytes { get; set; } = 512 * 1024;

    [JsonProperty("defaultTimeoutMs", Order = 2)]
    public int DefaultTimeoutMs { get; set; } = 30 * 60 * 1000;

    [JsonProperty("maxCompletedProcesses", Order = 3)]
    public int MaxCompletedProcesses { get; set; } = 200;
}

public sealed class McpSection
{
    [JsonProperty("callTimeoutMs", Order = 1)]
    public int CallTimeoutMs { get; set; } = 60_000;

    [JsonProperty("maxResultBytes", Order = 2)]
    public int MaxResultBytes { get; set; } = 1024 * 1024;

    [JsonProperty("servers", Order = 3)]
    public Dictionary<string, McpServerConfig> Servers { get; set; } =
        new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
}

[JsonConverter(typeof(McpServerConverter))]
public abstract class McpServerConfig
{
    /// <summary>Editor-only helper; never reaches the file.</summary>
    [JsonIgnore]
    public abstract string TransportType { get; }

    /// <summary>Short description used by the editor's list view.</summary>
    public abstract string Describe();
}

public sealed class StdioMcpServerConfig : McpServerConfig
{
    public string Command { get; set; } = string.Empty;

    public List<string> Args { get; set; } = new List<string>();

    public Dictionary<string, string> Env { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? Cwd { get; set; }

    public override string TransportType => "stdio";

    public override string Describe() =>
        Args.Count == 0 ? Command : Command + " " + string.Join(" ", Args);
}

public sealed class HttpMcpServerConfig : McpServerConfig
{
    public string Url { get; set; } = string.Empty;

    public Dictionary<string, string> Headers { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? Proxy { get; set; }

    public override string TransportType => "http";

    public override string Describe() => Url;
}
