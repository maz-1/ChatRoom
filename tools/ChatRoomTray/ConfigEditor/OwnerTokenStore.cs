using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ChatRoomTray.ConfigEditor;

/// <summary>
/// Stores the owner token in Windows Credential Manager using the exact target
/// naming convention used by ChatRoom's Node keychain adapter.
/// </summary>
public static class OwnerTokenStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistEnterprise = 3;
    private const int ErrorNotFound = 1168;
    private const string Service = "ChatRoom";
    private const string Version = "owner-token:v1";

    public static string? Read(string configPath)
    {
        var target = CredentialTarget(configPath);
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error, "无法从 Windows 凭据管理器读取 ownerToken。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return string.Empty;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Write(string configPath, string token)
    {
        if (!OwnerToken.IsPresent(token))
            throw new ArgumentException("ownerToken 不能为空。", nameof(token));

        var bytes = Encoding.UTF8.GetBytes(token);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var id = ConfigPathId(configPath);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = CredentialTarget(configPath),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistEnterprise,
                UserName = Version + ":" + id,
                Comment = "ChatRoom owner token for " + Path.GetFullPath(configPath),
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法写入 Windows 凭据管理器中的 ownerToken。");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static bool Delete(string configPath)
    {
        if (CredDelete(CredentialTarget(configPath), CredTypeGeneric, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw new Win32Exception(error, "无法删除 Windows 凭据管理器中的 ownerToken。");
    }

    public static string CredentialTarget(string configPath) =>
        Service + "/" + Version + ":" + ConfigPathId(configPath);

    private static string ConfigPathId(string configPath)
    {
        var canonical = Path.GetFullPath(configPath).ToLowerInvariant();
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var value in hash) builder.Append(value.ToString("x2"));
        return builder.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        int reservedFlag,
        out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
