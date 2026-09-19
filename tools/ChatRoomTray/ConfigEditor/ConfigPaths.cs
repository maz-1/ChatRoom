using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ChatRoomTray.ConfigEditor;

/// <summary>
/// Mirrors src/config/platform-paths.ts so the editor defaults to the same
/// config/data/state locations as the ChatRoom Node process.
/// </summary>
public static class ConfigPaths
{
    public const string ConfigPathVariable = "CHATROOM_CONFIG";

    public static string DefaultConfigPath()
    {
        var overridden = Environment.GetEnvironmentVariable(ConfigPathVariable);
        if (overridden is not null) return overridden;

        var home = HomeDirectory();
        if (IsWindows())
        {
            var roaming = Environment.GetEnvironmentVariable("APPDATA");
            if (roaming is null)
                roaming = Path.Combine(home, "AppData", "Roaming");
            return Path.Combine(roaming, "ChatRoom", "config.json");
        }

        if (IsMacOS())
        {
            return Path.Combine(
                home,
                "Library",
                "Application Support",
                "ChatRoom",
                "config.json");
        }

        var configHome = AbsoluteEnvironmentPath("XDG_CONFIG_HOME")
                         ?? Path.Combine(home, ".config");
        return Path.Combine(configHome, "chatroom", "config.json");
    }

    public static string DataDirectory()
    {
        var home = HomeDirectory();
        if (IsWindows())
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (local is null)
                local = Path.Combine(home, "AppData", "Local");
            return Path.Combine(local, "ChatRoom", "Data");
        }

        if (IsMacOS())
        {
            return Path.Combine(
                home,
                "Library",
                "Application Support",
                "ChatRoom",
                "Data");
        }

        var dataHome = AbsoluteEnvironmentPath("XDG_DATA_HOME")
                       ?? Path.Combine(home, ".local", "share");
        return Path.Combine(dataHome, "chatroom");
    }

    public static string StateDirectory()
    {
        var home = HomeDirectory();
        if (IsWindows())
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (local is null)
                local = Path.Combine(home, "AppData", "Local");
            return Path.Combine(local, "ChatRoom", "State");
        }

        if (IsMacOS())
        {
            return Path.Combine(
                home,
                "Library",
                "Application Support",
                "ChatRoom",
                "State");
        }

        var stateHome = AbsoluteEnvironmentPath("XDG_STATE_HOME")
                        ?? Path.Combine(home, ".local", "state");
        return Path.Combine(stateHome, "chatroom");
    }

    public static string DefaultDatabasePath() =>
        Path.Combine(StateDirectory(), "chatroom.sqlite");

    public static string DefaultAllowedRoot() =>
        Path.Combine(HomeDirectory(), "Projects");

    private static string? AbsoluteEnvironmentPath(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value)
            ? value
            : null;
    }

    private static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home)) return home;

        home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home)) return home;

        throw new InvalidOperationException("无法确定当前用户的主目录。");
    }

    private static bool IsWindows() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static bool IsMacOS() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
}
