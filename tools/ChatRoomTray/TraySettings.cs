using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ChatRoomTray;

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
        if (!WritePrivateProfileString(Section, ArgsKey, value, _path))
            throw new InvalidOperationException("无法写入启动参数设置：" + _path);
    }

    private string Read(string key, string defaultValue)
    {
        var buffer = new StringBuilder(4096);
        GetPrivateProfileString(Section, key, defaultValue, buffer, buffer.Capacity, _path);
        return buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetPrivateProfileString(
        string lpAppName,
        string lpKeyName,
        string lpDefault,
        StringBuilder lpReturnedString,
        int nSize,
        string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileString(
        string lpAppName,
        string lpKeyName,
        string lpString,
        string lpFileName);
}
