#if LINUX_TRAY
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Eto.Forms;

namespace ChatRoomTray;

/// <summary>
/// Modern Linux tray implementation. Ayatana/AppIndicator exports a
/// StatusNotifierItem + dbusmenu, which is native in KDE Plasma and consumed by
/// GNOME's AppIndicator/KStatusNotifierItem extension.
/// </summary>
internal sealed class LinuxAppIndicatorTrayBackend : ITrayBackend
{
    private const int IndicatorCategoryApplicationStatus = 0;
    private const int IndicatorStatusPassive = 0;
    private const int IndicatorStatusActive = 1;

    private readonly NativeAppIndicator _native;
    private readonly IntPtr _indicator;
    private readonly Gtk.Menu _menu;
    private readonly Gtk.MenuItem _statusItem;
    private readonly Gtk.MenuItem _startItem;
    private readonly Gtk.MenuItem _stopItem;
    private readonly Gtk.MenuItem _restartItem;
    private readonly string _iconDirectory;
    private bool _disposed;

    private LinuxAppIndicatorTrayBackend(TrayActions actions, NativeAppIndicator native)
    {
        _native = native;
        _iconDirectory = CreateIconDirectory();

        _indicator = _native.New(
            "chatroom-tray",
            "chatroom-tray",
            IndicatorCategoryApplicationStatus,
            _iconDirectory);
        if (_indicator == IntPtr.Zero)
            throw new InvalidOperationException("libayatana-appindicator 未能创建 ChatRoom indicator。");

        _menu = new Gtk.Menu();
        _statusItem = Item("状态：已停止", null);
        _statusItem.Sensitive = false;
        _startItem = Item("启动 ChatRoom", actions.Start);
        _stopItem = Item("停止 ChatRoom", actions.Stop);
        _restartItem = Item("重启 ChatRoom", actions.Restart);

        Append(_statusItem);
        Append(new Gtk.SeparatorMenuItem());
        Append(Item("打开 WebUI", actions.OpenWebUi));
        Append(new Gtk.SeparatorMenuItem());
        Append(_startItem);
        Append(_stopItem);
        Append(_restartItem);
        Append(Item("启动参数…", actions.EditStartupArgs));
        Append(new Gtk.SeparatorMenuItem());
        Append(Item("编辑配置 (config.json)", actions.EditConfig));
        Append(Item("打开数据目录", actions.OpenData));
        Append(new Gtk.SeparatorMenuItem());
        Append(Item("退出", actions.Exit));

        _menu.ShowAll();
        _native.SetMenu(_indicator, _menu.Handle);
        _native.SetStatus(_indicator, IndicatorStatusActive);
    }

    public static bool TryCreate(
        TrayActions actions,
        out ITrayBackend backend,
        out string? warning)
    {
        backend = null!;
        warning = null;

        if (!NativeAppIndicator.TryLoad(out var native))
        {
            warning = "未检测到 libayatana-appindicator3/libappindicator3。";
            return false;
        }

        try
        {
            var implementation = new LinuxAppIndicatorTrayBackend(actions, native);
            backend = implementation;

            var watcher = LinuxDesktopSession.HasStatusNotifierWatcher();
            if (watcher != true)
            {
                var desktop = LinuxDesktopSession.Describe();
                if (watcher == false)
                {
                    warning = desktop.IndexOf("GNOME", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "已创建 StatusNotifierItem，但当前 GNOME Shell 未检测到 "
                          + "org.kde.StatusNotifierWatcher。请启用 “AppIndicator and "
                          + "KStatusNotifierItem Support” GNOME Shell 扩展，否则托盘图标不会显示。"
                        : "已创建 StatusNotifierItem，但当前桌面未检测到 "
                          + "org.kde.StatusNotifierWatcher。请确认桌面系统托盘已启用。";
                }
                else
                {
                    warning = "已创建 StatusNotifierItem，但无法确认 "
                              + "org.kde.StatusNotifierWatcher 是否存在。"
                              + "已保留可见控制窗口作为备用入口。";
                }
            }

            return true;
        }
        catch (Exception error)
        {
            native.Dispose();
            warning = "AppIndicator 初始化失败：" + error.Message;
            backend = null!;
            return false;
        }
    }

    public void Update(bool running)
    {
        _statusItem.Label = running ? "状态：运行中" : "状态：已停止";
        _startItem.Sensitive = !running;
        _stopItem.Sensitive = running;
        _restartItem.Sensitive = running;
    }

    public void Notify(string title, string message)
    {
        try
        {
            var notification = new Notification
            {
                Title = title,
                Message = message,
            };
            notification.Show();
        }
        catch
        {
            // Notification support is optional.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _native.SetStatus(_indicator, IndicatorStatusPassive);
        _native.Release(_indicator);
        _menu.Destroy();
        _menu.Dispose();
        _native.Dispose();

        try
        {
            Directory.Delete(_iconDirectory, recursive: true);
        }
        catch
        {
            // Temp icon cleanup is best-effort.
        }
    }

    private void Append(Gtk.Widget item) => _menu.Append(item);

    private static Gtk.MenuItem Item(string text, Action? action)
    {
        var item = new Gtk.MenuItem(text);
        if (action is not null)
            item.Activated += (_, _) => action();
        return item;
    }

    private static string CreateIconDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "chatroom-tray-" + Environment.ProcessId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "chatroom-tray.svg"),
            TraySvg);
        return directory;
    }

    private const string TraySvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"64\" viewBox=\"0 0 64 64\">"
        + "<path fill=\"#ffffff\" d=\"M12 10h40a8 8 0 0 1 8 8v24a8 8 0 0 1-8 8H31L18 59v-9h-6a8 8 0 0 1-8-8V18a8 8 0 0 1 8-8z\"/>"
        + "<circle fill=\"#20242a\" cx=\"21\" cy=\"30\" r=\"4\"/>"
        + "<circle fill=\"#20242a\" cx=\"32\" cy=\"30\" r=\"4\"/>"
        + "<circle fill=\"#20242a\" cx=\"43\" cy=\"30\" r=\"4\"/>"
        + "</svg>";
}

internal static class LinuxDesktopSession
{
    public static string Describe()
    {
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        if (!string.IsNullOrWhiteSpace(desktop)) return desktop;

        desktop = Environment.GetEnvironmentVariable("DESKTOP_SESSION");
        return string.IsNullOrWhiteSpace(desktop) ? "未知" : desktop;
    }

    public static bool? HasStatusNotifierWatcher()
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "gdbus",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("call");
            start.ArgumentList.Add("--session");
            start.ArgumentList.Add("--dest");
            start.ArgumentList.Add("org.freedesktop.DBus");
            start.ArgumentList.Add("--object-path");
            start.ArgumentList.Add("/org/freedesktop/DBus");
            start.ArgumentList.Add("--method");
            start.ArgumentList.Add("org.freedesktop.DBus.NameHasOwner");
            start.ArgumentList.Add("org.kde.StatusNotifierWatcher");

            using var process = Process.Start(start);
            if (process is null) return null;
            if (!process.WaitForExit(1500))
            {
                try { process.Kill(); } catch { }
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            return output.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class NativeAppIndicator : IDisposable
{
    private static readonly string[] LibraryNames =
    {
        "libayatana-appindicator3.so.1",
        "libayatana-appindicator3.so",
        "libappindicator3.so.1",
        "libappindicator3.so",
    };

    private readonly IntPtr _library;
    private readonly AppIndicatorNewDelegate? _new;
    private readonly AppIndicatorNewWithPathDelegate? _newWithPath;
    private readonly AppIndicatorSetIconThemePathDelegate? _setIconThemePath;
    private readonly AppIndicatorSetStatusDelegate _setStatus;
    private readonly AppIndicatorSetMenuDelegate _setMenu;
    private bool _disposed;

    private NativeAppIndicator(IntPtr library)
    {
        _library = library;
        _newWithPath = GetOptional<AppIndicatorNewWithPathDelegate>("app_indicator_new_with_path");
        _new = GetOptional<AppIndicatorNewDelegate>("app_indicator_new");
        _setIconThemePath = GetOptional<AppIndicatorSetIconThemePathDelegate>(
            "app_indicator_set_icon_theme_path");
        _setStatus = GetRequired<AppIndicatorSetStatusDelegate>("app_indicator_set_status");
        _setMenu = GetRequired<AppIndicatorSetMenuDelegate>("app_indicator_set_menu");

        if (_newWithPath is null && _new is null)
            throw new MissingMethodException("AppIndicator constructor export is missing.");
    }

    public static bool TryLoad(out NativeAppIndicator native)
    {
        foreach (var name in LibraryNames)
        {
            if (!NativeLibrary.TryLoad(name, out var handle)) continue;

            try
            {
                native = new NativeAppIndicator(handle);
                return true;
            }
            catch
            {
                NativeLibrary.Free(handle);
            }
        }

        native = null!;
        return false;
    }

    public IntPtr New(string id, string iconName, int category, string iconThemePath)
    {
        if (_newWithPath is not null)
            return _newWithPath(id, iconName, category, iconThemePath);

        var indicator = _new!(id, iconName, category);
        _setIconThemePath?.Invoke(indicator, iconThemePath);
        return indicator;
    }

    public void SetStatus(IntPtr indicator, int status) =>
        _setStatus(indicator, status);

    public void SetMenu(IntPtr indicator, IntPtr menu) =>
        _setMenu(indicator, menu);

    public void Release(IntPtr indicator)
    {
        if (indicator != IntPtr.Zero) g_object_unref(indicator);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeLibrary.Free(_library);
    }

    private T GetRequired<T>(string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_library, name, out var export))
            throw new MissingMethodException(name);
        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    private T? GetOptional<T>(string name) where T : Delegate
    {
        return NativeLibrary.TryGetExport(_library, name, out var export)
            ? Marshal.GetDelegateForFunctionPointer<T>(export)
            : null;
    }

    [DllImport("libgobject-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_object_unref(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AppIndicatorNewDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string iconName,
        int category);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AppIndicatorNewWithPathDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string iconName,
        int category,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string iconThemePath);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AppIndicatorSetIconThemePathDelegate(
        IntPtr indicator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string iconThemePath);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AppIndicatorSetStatusDelegate(IntPtr indicator, int status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AppIndicatorSetMenuDelegate(IntPtr indicator, IntPtr menu);
}
#endif
