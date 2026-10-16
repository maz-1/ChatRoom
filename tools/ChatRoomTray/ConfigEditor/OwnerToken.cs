using System;
using System.Security.Cryptography;

namespace ChatRoomTray.ConfigEditor;

/// <summary>
/// Owns the owner token: it is generated exactly like `chatroom init` does
/// (32 random bytes, base64url, no padding) and is never rendered in the UI.
/// </summary>
public static class OwnerToken
{
    public static string Generate()
    {
        var bytes = new byte[32];
        using (var generator = RandomNumberGenerator.Create())
            generator.GetBytes(bytes);

        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>Human-readable hint that reveals nothing about the value.</summary>
    public static string Describe(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "未设置";
        return "已设置 · " + token!.Length + " 个字符";
    }

    public static bool IsPresent(string? token) => !string.IsNullOrEmpty(token);
}
