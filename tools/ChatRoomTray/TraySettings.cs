using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ChatRoomTray;

/// <summary>
/// Tiny managed INI reader/writer for tray-only settings. Keeping this managed
/// makes the same chatroom-tray.ini work on Windows, Linux, and macOS.
/// </summary>
internal sealed class TraySettings
{
    private const string Section = "ChatRoom";
    private const string ArgsKey = "Args";
    private readonly string _path;

    public TraySettings(string path)
    {
        _path = path;
        Args = Read(ArgsKey, "serve");
    }

    public string Args { get; private set; }

    public void SaveArgs(string value)
    {
        Args = value;
        var lines = File.Exists(_path)
            ? new List<string>(File.ReadAllLines(_path, Encoding.UTF8))
            : new List<string>();

        var sectionStart = -1;
        var sectionEnd = lines.Count;
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (!trimmed.StartsWith("[", StringComparison.Ordinal)
                || !trimmed.EndsWith("]", StringComparison.Ordinal))
                continue;

            var name = trimmed.Substring(1, trimmed.Length - 2).Trim();
            if (sectionStart >= 0)
            {
                sectionEnd = index;
                break;
            }

            if (string.Equals(name, Section, StringComparison.OrdinalIgnoreCase))
                sectionStart = index;
        }

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[lines.Count - 1].Length != 0) lines.Add(string.Empty);
            lines.Add("[" + Section + "]");
            lines.Add(ArgsKey + "=" + value);
        }
        else
        {
            var replaced = false;
            for (var index = sectionStart + 1; index < sectionEnd; index++)
            {
                var line = lines[index];
                var equals = line.IndexOf('=');
                if (equals < 0) continue;
                var key = line.Substring(0, equals).Trim();
                if (!string.Equals(key, ArgsKey, StringComparison.OrdinalIgnoreCase)) continue;

                lines[index] = ArgsKey + "=" + value;
                replaced = true;
                break;
            }

            if (!replaced) lines.Insert(sectionEnd, ArgsKey + "=" + value);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllLines(_path, lines, new UTF8Encoding(false));
    }

    private string Read(string key, string defaultValue)
    {
        if (!File.Exists(_path)) return defaultValue;

        var inSection = false;
        foreach (var raw in File.ReadAllLines(_path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal)
                                 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("[", StringComparison.Ordinal)
                && line.EndsWith("]", StringComparison.Ordinal))
            {
                var name = line.Substring(1, line.Length - 2).Trim();
                inSection = string.Equals(name, Section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection) continue;
            var equals = line.IndexOf('=');
            if (equals < 0) continue;
            var candidate = line.Substring(0, equals).Trim();
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
                return line.Substring(equals + 1);
        }

        return defaultValue;
    }
}
