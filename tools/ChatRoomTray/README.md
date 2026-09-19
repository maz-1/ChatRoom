# ChatRoomTray

ChatRoomTray is the integrated desktop host for ChatRoom. The configuration editor stays in the same project; there is no separate editor project or executable.

## Platform backends

| Platform | Runtime | Tray backend |
| --- | --- | --- |
| Windows | .NET 8 (`net8.0-windows`) | Existing WinForms `NotifyIcon` |
| Linux | .NET 8 + Eto GTK | Ayatana/AppIndicator → StatusNotifierItem + DBusMenu |
| macOS | .NET 8 + Eto Mac64 | Eto native `TrayIndicator` |

Linux deliberately does **not** rely on legacy `Gtk.StatusIcon` as the primary tray implementation.

### Linux: KDE and GNOME

The Linux backend dynamically loads one of:

- `libayatana-appindicator3.so.1`
- `libayatana-appindicator3.so`
- `libappindicator3.so.1`
- `libappindicator3.so`

AppIndicator publishes the tray item as a StatusNotifierItem. KDE Plasma provides `org.kde.StatusNotifierWatcher` natively. GNOME Shell normally requires the **AppIndicator and KStatusNotifierItem Support** extension.

At startup ChatRoomTray probes the session D-Bus owner of `org.kde.StatusNotifierWatcher`:

- watcher present → normal tray menu;
- AppIndicator available but watcher absent → keep the StatusNotifierItem and also show a visible fallback control window;
- AppIndicator library absent → do not depend on invisible legacy tray behavior; show the fallback control window.

The fallback window exposes the important tray actions, so a GNOME/Wayland user is never left with a running background process and no UI entry point.

## Tray menu

The cross-platform menu provides:

- current ChatRoom state;
- Open WebUI;
- Start / Stop / Restart ChatRoom;
- Edit persisted startup arguments;
- Open the integrated configuration editor;
- Open the ChatRoom data directory;
- Exit and terminate the ChatRoom process tree.

Windows keeps its existing console-show/hide behavior. Linux/macOS do not create a dedicated ChatRoom console window.

## Build

### Windows

```powershell
cd tools/ChatRoomTray
dotnet build -c Release
```

Visual Studio profile `win-x64-single-file` publishes a framework-dependent single EXE (about 3 MiB in the current build). The target PC must have Microsoft .NET 8 Desktop Runtime x64 installed.

For a self-contained single-file Windows package, use the .NET 8 publisher:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
```

### Linux

On Linux the project selects `net8.0 + Eto GTK` automatically:

```bash
cd tools/ChatRoomTray
dotnet build -c Release
./bin/Release/net8.0/ChatRoomTray
```

Runtime GUI dependencies:

- GTK3;
- Ayatana AppIndicator/AppIndicator for the real tray;
- on GNOME, an AppIndicator/KStatusNotifierItem Shell extension;
- `libsecret-1` plus a Secret Service only when ownerToken-backed authentication is used.

### macOS

macOS uses Eto Mac64 with `osx-x64` and `osx-arm64` bundle targets. Launching the app without arguments starts the tray host.

## Commands

- no arguments: start tray host and ChatRoom;
- `--tray`: explicitly start tray host;
- `--config`: open only the integrated configuration editor;
- `--check [path]`: validate configuration;
- `--roundtrip <src> <dst>`: configuration round-trip check;
- `--selftest`: configuration and tray logic self-test;
- `--uismoke`: Eto configuration UI smoke test;
- `--traysmoke`: initialize the Linux/macOS tray backend or fallback window without starting ChatRoom, then exit automatically.

## Runtime discovery

The cross-platform tray looks for ChatRoom's Node runtime in the application directory, macOS `Contents/Resources`, and parent deployment directories. It accepts either:

- `app/dist/cli/index.js`, or
- `dist/cli/index.js`.

A bundled `node` / `node.exe` in the same runtime root is preferred; otherwise the tray uses `node` from `PATH`.

The existing `chatroom-tray.ini` format is kept. `[ChatRoom] Args` defaults to `serve`, and the INI reader/writer is now fully managed so it works on all three platforms.

On Linux/macOS the tray host also registers SIGTERM/SIGINT cleanup. The tracked ChatRoom process tree is terminated before the operating system performs its normal signal termination, preventing an orphaned Node process.

## Icons

- Windows executable / WinForms tray: `chatgpt.ico`;
- Eto/macOS image resource: `chatgpt.png`;
- Linux AppIndicator: a temporary SVG icon registered through the AppIndicator icon theme path.

## Configuration editor

The integrated editor lives under `ConfigEditor/` and is compiled by this project on every platform. Config saves retain the 10 most recent timestamped automatic backups.
