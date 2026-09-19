using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatRoomTray.ConfigEditor;

/// <summary>
/// Reads and writes a single entry of <c>mcp.servers</c>. ChatRoom's schema is
/// strict and the two transports accept different keys, so each variant writes
/// only its own fields — with the type discriminator first.
/// </summary>
public sealed class McpServerConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) =>
        typeof(McpServerConfig).IsAssignableFrom(objectType);

    public override object ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer)
    {
        var json = JObject.Load(reader);
        var type = (string?)json["type"];
        switch (type)
        {
            case "stdio":
                return new StdioMcpServerConfig
                {
                    Command = (string?)json["command"] ?? string.Empty,
                    Args = StringList(json["args"]),
                    Env = StringMap(json["env"]),
                    Cwd = (string?)json["cwd"],
                };

            case "http":
                return new HttpMcpServerConfig
                {
                    Url = (string?)json["url"] ?? string.Empty,
                    Headers = StringMap(json["headers"]),
                    Proxy = (string?)json["proxy"],
                };

            default:
                // Matches ChatRoom's own guidance for the removed sse transport.
                var name = type is null ? "缺少 type" : $"type=\"{type}\"";
                throw new JsonSerializationException(
                    $"MCP 服务的{name}不是受支持的传输方式（只支持 \"stdio\" 与 \"http\"）。");
        }
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        var json = new JObject();
        switch (value)
        {
            case StdioMcpServerConfig stdio:
                json["type"] = "stdio";
                json["command"] = stdio.Command;
                json["args"] = new JArray(stdio.Args.Cast<object>());
                json["env"] = MapToJson(stdio.Env);
                json["cwd"] = stdio.Cwd is null ? JValue.CreateNull() : new JValue(stdio.Cwd);
                break;

            case HttpMcpServerConfig http:
                json["type"] = "http";
                json["url"] = http.Url;
                json["headers"] = MapToJson(http.Headers);
                json["proxy"] = http.Proxy is null ? JValue.CreateNull() : new JValue(http.Proxy);
                break;

            default:
                throw new JsonSerializationException(
                    $"不支持的 MCP 服务类型：{value?.GetType().Name ?? "null"}");
        }

        json.WriteTo(writer);
    }

    private static List<string> StringList(JToken? token)
    {
        if (token is not JArray array) return new List<string>();
        return array.Select(item => (string?)item ?? string.Empty).ToList();
    }

    private static Dictionary<string, string> StringMap(JToken? token)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (token is not JObject json) return map;
        foreach (var property in json.Properties())
            map[property.Name] = (string?)property.Value ?? string.Empty;
        return map;
    }

    private static JObject MapToJson(Dictionary<string, string> map)
    {
        var json = new JObject();
        foreach (var pair in map) json[pair.Key] = pair.Value;
        return json;
    }
}
