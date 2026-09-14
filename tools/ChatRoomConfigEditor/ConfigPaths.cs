using System;
using System.IO;

namespace ChatRoomConfigEditor;

/// <summary>
/// Mirrors src/config/platform-paths.ts so the editor defaults to the same file
/// ChatRoom reads.
/// </summary>
public static class ConfigPaths
{
    public const string ConfigPathVariable = "CHATROOM_CONFIG";

    public static string DefaultConfigPath()
    {
        var overridden = Environment.GetEnvironmentVariable(ConfigPathVariable);
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChatRoom",
            "config.json");
    }

    public static string DataDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatRoom",
            "Data");
    }

    public static string StateDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatRoom",
            "State");
    }

    public static string DefaultDatabasePath() =>
        Path.Combine(StateDirectory(), "chatroom.sqlite");

    public static string DefaultAllowedRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Projects");
}
