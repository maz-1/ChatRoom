using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ChatRoomTray.ConfigEditor;

/// <summary>
/// Stores ownerToken in the same OS credential entry used by ChatRoom's
/// @github/keytar adapter:
/// Windows Credential Manager, macOS Keychain, or Linux Secret Service.
/// </summary>
public static class OwnerTokenStore
{
    private const string Service = "ChatRoom";
    private const string Version = "owner-token:v1";

    // Windows Credential Manager constants.
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistEnterprise = 3;
    private const int ErrorNotFound = 1168;

#if !NETFRAMEWORK
    // macOS Security.framework status.
    private const int ErrSecItemNotFound = -25300;

    private const string SecurityFramework =
        "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // keytar's Linux schema uses two string attributes.
    private const string LinuxSecretLibrary = "libsecret-1.so.0";
    private const string LinuxGlibLibrary = "libglib-2.0.so.0";
    private const string LinuxSchemaName = "org.freedesktop.Secret.Generic";
    private const int SecretSchemaNone = 0;
    private const int SecretSchemaAttributeString = 0;
#endif

    public static string StoreDisplayName
    {
        get
        {
            if (IsWindows()) return "Windows 凭据管理器";
            if (IsMacOS()) return "macOS Keychain";
            if (IsLinux()) return "Secret Service";
            return "系统凭据库";
        }
    }

    public static string? Read(string configPath)
    {
        if (IsWindows()) return ReadWindows(configPath);

#if !NETFRAMEWORK
        if (IsMacOS()) return ReadMac(configPath);
        if (IsLinux()) return ReadLinux(configPath);
#endif

        throw new PlatformNotSupportedException("ownerToken 凭据存储仅支持 Windows、macOS 和 Linux。");
    }

    public static void Write(string configPath, string token)
    {
        if (!OwnerToken.IsPresent(token))
            throw new ArgumentException("ownerToken 不能为空。", nameof(token));

        if (IsWindows())
        {
            WriteWindows(configPath, token);
            return;
        }

#if !NETFRAMEWORK
        if (IsMacOS())
        {
            WriteMac(configPath, token);
            return;
        }

        if (IsLinux())
        {
            WriteLinux(configPath, token);
            return;
        }
#endif

        throw new PlatformNotSupportedException("ownerToken 凭据存储仅支持 Windows、macOS 和 Linux。");
    }

    public static bool Delete(string configPath)
    {
        if (IsWindows()) return DeleteWindows(configPath);

#if !NETFRAMEWORK
        if (IsMacOS()) return DeleteMac(configPath);
        if (IsLinux()) return DeleteLinux(configPath);
#endif

        throw new PlatformNotSupportedException("ownerToken 凭据存储仅支持 Windows、macOS 和 Linux。");
    }

    /// <summary>
    /// Windows keytar stores the generic credential under ChatRoom/account.
    /// This target therefore exactly matches keytar's Windows target name.
    /// </summary>
    public static string CredentialTarget(string configPath) =>
        Service + "/" + CredentialAccount(configPath);

    public static string CredentialAccount(string configPath) =>
        Version + ":" + ConfigPathId(configPath);

    private static string ConfigPathId(string configPath)
    {
        var canonical = Path.GetFullPath(configPath);
        // Mirrors platformSecurityDefaults(). Windows is case-insensitive for
        // credential identity; macOS/Linux deliberately preserve path case.
        if (IsWindows()) canonical = canonical.ToLowerInvariant();

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var value in hash) builder.Append(value.ToString("x2"));
        return builder.ToString();
    }

    // ---------------------------------------------------------------- Windows

    private static string? ReadWindows(string configPath)
    {
        var target = CredentialTarget(configPath);
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw StoreError("无法从 Windows 凭据管理器读取 ownerToken。", new Win32Exception(error));
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

    private static void WriteWindows(string configPath, string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var account = CredentialAccount(configPath);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = CredentialTarget(configPath),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistEnterprise,
                UserName = account,
                Comment = "ChatRoom owner token for " + Path.GetFullPath(configPath),
            };

            if (!CredWrite(ref credential, 0))
            {
                throw StoreError(
                    "无法写入 Windows 凭据管理器中的 ownerToken。",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    private static bool DeleteWindows(string configPath)
    {
        if (CredDelete(CredentialTarget(configPath), CredTypeGeneric, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw StoreError("无法删除 Windows 凭据管理器中的 ownerToken。", new Win32Exception(error));
    }

#if !NETFRAMEWORK
    // ------------------------------------------------------------------ macOS

    private static string? ReadMac(string configPath)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(CredentialAccount(configPath));

        var status = SecKeychainFindGenericPasswordData(
            IntPtr.Zero,
            (uint)service.Length,
            service,
            (uint)account.Length,
            account,
            out var length,
            out var data,
            IntPtr.Zero);

        if (status == ErrSecItemNotFound) return null;
        EnsureMacSuccess(status, "读取");

        try
        {
            if (data == IntPtr.Zero || length == 0) return string.Empty;
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            if (data != IntPtr.Zero)
                SecKeychainItemFreeContent(IntPtr.Zero, data);
        }
    }

    private static void WriteMac(string configPath, string token)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(CredentialAccount(configPath));
        var password = Encoding.UTF8.GetBytes(token);

        var status = SecKeychainFindGenericPasswordItem(
            IntPtr.Zero,
            (uint)service.Length,
            service,
            (uint)account.Length,
            account,
            IntPtr.Zero,
            IntPtr.Zero,
            out var item);

        if (status == ErrSecItemNotFound)
        {
            status = SecKeychainAddGenericPassword(
                IntPtr.Zero,
                (uint)service.Length,
                service,
                (uint)account.Length,
                account,
                (uint)password.Length,
                password,
                out item);
            try
            {
                EnsureMacSuccess(status, "写入");
            }
            finally
            {
                if (item != IntPtr.Zero) CFRelease(item);
            }

            return;
        }

        EnsureMacSuccess(status, "查找");
        try
        {
            status = SecKeychainItemModifyAttributesAndData(
                item,
                IntPtr.Zero,
                (uint)password.Length,
                password);
            EnsureMacSuccess(status, "更新");
        }
        finally
        {
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    private static bool DeleteMac(string configPath)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(CredentialAccount(configPath));

        var status = SecKeychainFindGenericPasswordItem(
            IntPtr.Zero,
            (uint)service.Length,
            service,
            (uint)account.Length,
            account,
            IntPtr.Zero,
            IntPtr.Zero,
            out var item);

        if (status == ErrSecItemNotFound) return false;
        EnsureMacSuccess(status, "查找");

        try
        {
            status = SecKeychainItemDelete(item);
            EnsureMacSuccess(status, "删除");
            return true;
        }
        finally
        {
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    private static void EnsureMacSuccess(int status, string action)
    {
        if (status == 0) return;
        throw StoreError($"无法在 macOS Keychain 中{action} ownerToken（OSStatus {status}）。");
    }

    // ------------------------------------------------------------------ Linux

    private static string? ReadLinux(string configPath)
    {
        var schema = CreateLinuxSchema();
        try
        {
            var password = secret_password_lookup_sync(
                schema,
                IntPtr.Zero,
                out var error,
                "service",
                Service,
                "account",
                CredentialAccount(configPath),
                IntPtr.Zero);

            ThrowLinuxError(error, "读取");
            if (password == IntPtr.Zero) return null;

            try
            {
                return Marshal.PtrToStringUTF8(password) ?? string.Empty;
            }
            finally
            {
                secret_password_free(password);
            }
        }
        catch (DllNotFoundException error)
        {
            throw LinuxDependencyError(error);
        }
        finally
        {
            if (schema != IntPtr.Zero) secret_schema_unref(schema);
        }
    }

    private static void WriteLinux(string configPath, string token)
    {
        var schema = CreateLinuxSchema();
        try
        {
            var ok = secret_password_store_sync(
                schema,
                IntPtr.Zero,
                CredentialTarget(configPath),
                token,
                IntPtr.Zero,
                out var error,
                "service",
                Service,
                "account",
                CredentialAccount(configPath),
                IntPtr.Zero);

            ThrowLinuxError(error, "写入");
            if (ok == 0)
                throw StoreError("Secret Service 未能保存 ownerToken。");
        }
        catch (DllNotFoundException error)
        {
            throw LinuxDependencyError(error);
        }
        finally
        {
            if (schema != IntPtr.Zero) secret_schema_unref(schema);
        }
    }

    private static bool DeleteLinux(string configPath)
    {
        var schema = CreateLinuxSchema();
        try
        {
            var removed = secret_password_clear_sync(
                schema,
                IntPtr.Zero,
                out var error,
                "service",
                Service,
                "account",
                CredentialAccount(configPath),
                IntPtr.Zero);

            ThrowLinuxError(error, "删除");
            return removed != 0;
        }
        catch (DllNotFoundException error)
        {
            throw LinuxDependencyError(error);
        }
        finally
        {
            if (schema != IntPtr.Zero) secret_schema_unref(schema);
        }
    }

    private static IntPtr CreateLinuxSchema()
    {
        try
        {
            var schema = secret_schema_new(
                LinuxSchemaName,
                SecretSchemaNone,
                "service",
                SecretSchemaAttributeString,
                "account",
                SecretSchemaAttributeString,
                IntPtr.Zero);

            if (schema == IntPtr.Zero)
                throw StoreError("无法创建 Secret Service schema。");

            return schema;
        }
        catch (DllNotFoundException error)
        {
            throw LinuxDependencyError(error);
        }
    }

    private static void ThrowLinuxError(IntPtr error, string action)
    {
        if (error == IntPtr.Zero) return;

        try
        {
            var native = Marshal.PtrToStructure<GError>(error);
            var message = native.Message == IntPtr.Zero
                ? "未知错误"
                : Marshal.PtrToStringUTF8(native.Message) ?? "未知错误";
            throw StoreError($"无法通过 Linux Secret Service {action} ownerToken：{message}");
        }
        finally
        {
            g_error_free(error);
        }
    }

    private static Exception LinuxDependencyError(Exception inner) =>
        StoreError(
            "无法加载 Linux libsecret。请安装 libsecret-1，并确保桌面 Secret Service（例如 GNOME Keyring 或 KWallet）可用。",
            inner);
#endif

    private static OwnerTokenStoreException StoreError(string message, Exception? inner = null) =>
        new OwnerTokenStoreException(message, inner);

    // -------------------------------------------------------------- platform

    private static bool IsWindows() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static bool IsMacOS() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private static bool IsLinux() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    // --------------------------------------------------------------- Windows P/Invoke

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

#if !NETFRAMEWORK
    // --------------------------------------------------------------- macOS P/Invoke

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int SecKeychainFindGenericPasswordData(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        [In] byte[] serviceName,
        uint accountNameLength,
        [In] byte[] accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        IntPtr itemRef);

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int SecKeychainFindGenericPasswordItem(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        [In] byte[] serviceName,
        uint accountNameLength,
        [In] byte[] accountName,
        IntPtr passwordLength,
        IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        [In] byte[] serviceName,
        uint accountNameLength,
        [In] byte[] accountName,
        uint passwordLength,
        [In] byte[] passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef,
        IntPtr attrList,
        uint length,
        [In] byte[] data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr value);

    // --------------------------------------------------------------- Linux P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public IntPtr Message;
    }

    [DllImport(LinuxSecretLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr secret_schema_new(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int flags,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute1,
        int attributeType1,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute2,
        int attributeType2,
        IntPtr terminator);

    [DllImport(LinuxSecretLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void secret_schema_unref(IntPtr schema);

    [DllImport(LinuxSecretLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr secret_password_lookup_sync(
        IntPtr schema,
        IntPtr cancellable,
        out IntPtr error,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute1,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value1,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute2,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value2,
        IntPtr terminator);

    [DllImport(LinuxSecretLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int secret_password_store_sync(
        IntPtr schema,
        IntPtr collection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        IntPtr cancellable,
        out IntPtr error,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute1,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value1,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute2,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value2,
        IntPtr terminator);

    [DllImport(LinuxSecretLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int secret_password_clear_sync(
        IntPtr schema,
        IntPtr cancellable,
        out IntPtr error,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute1,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value1,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attribute2,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value2,
        IntPtr terminator);

    [DllImport(LinuxSecretLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport(LinuxGlibLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_error_free(IntPtr error);
#endif
}


public sealed class OwnerTokenStoreException : Exception
{
    public OwnerTokenStoreException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
