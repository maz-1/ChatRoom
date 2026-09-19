# ChatRoomTray (.NET Framework 4.8)

`ChatRoomTray` replaces `tools/ChatRoomTray.ahk` with a native WinForms tray application. The ChatRoom configuration editor is now part of this project and is compiled directly into the same executable.

## Build

```powershell
cd tools/ChatRoomTray
dotnet build -c Release
```

Output: `bin/Release/net48/ChatRoomTray.exe`.

The project owns `chatgpt.ico` directly under `tools/ChatRoomTray`. The same file is used as the executable icon and embedded into the assembly for the tray/config-editor icon, so no sidecar icon file is required at runtime. ILRepack merges managed dependencies into the EXE.

## Runtime layout

Deploy the tray with the packaged ChatRoom runtime:

- `ChatRoomTray.exe`
- `node.exe`
- `app/dist/cli/index.js`
- `app/node_modules/`
- `app/package.json`

`ChatRoomTray.exe` launches `node.exe app/dist/cli/index.js ...` directly; no `.cmd` or `.bat` launcher is required. The tray application keeps compatibility with the old `chatroom-tray.ini`; `[ChatRoom] Args` defaults to `serve`.

## Tray functions

- Open WebUI
- Show/hide the ChatRoom console window
- Start / stop / restart ChatRoom
- Edit persisted startup arguments
- Open the integrated configuration editor
- Monitor unexpected process exits and show tray notifications
- Stop the ChatRoom process tree when the tray application exits

Double-clicking the tray icon toggles the console window. The application is single-instance.

## Configuration editor integration

The configuration editor source lives under `ConfigEditor/` inside the `ChatRoomTray` project and is compiled by the normal SDK-style project file. There is no separate editor executable or project. Config saves keep only the 10 most recent timestamped automatic backups.

Run `ChatRoomTray.exe --config` to open only the integrated configuration editor without starting the tray runtime.
