# ChatRoomTray (.NET Framework 4.8)

`ChatRoomTray` replaces `tools/ChatRoomTray.ahk` with a native WinForms tray application and integrates the existing `ChatRoomConfigEditor` UI into the same executable.

## Build

```powershell
cd tools/ChatRoomTray
dotnet build -c Release
```

Output: `bin/Release/net48/ChatRoomTray.exe`.

The build uses `tools/chatgpt.ico` as the executable/tray icon and ILRepack to merge managed dependencies into the EXE. `chatgpt.ico` and `chatroom-tray-launcher.cmd` are also copied beside the EXE for runtime use.

## Runtime layout

Deploy `ChatRoomTray.exe` beside the normal ChatRoom runtime files:

- `chatroom.cmd`
- `node.exe`
- `chatroom-tray-launcher.cmd`
- `chatgpt.ico`

The tray application keeps compatibility with the old `chatroom-tray.ini`; `[ChatRoom] Args` defaults to `serve`.

## Tray functions

- Open WebUI
- Show/hide the ChatRoom console window
- Start / stop / restart ChatRoom
- Edit persisted startup arguments
- Open the integrated `ChatRoomConfigEditor` window
- Monitor unexpected process exits and show tray notifications
- Stop the ChatRoom process tree when the tray application exits

Double-clicking the tray icon toggles the console window. The application is single-instance.

## Configuration editor integration

The project links the source files from `../ChatRoomConfigEditor` at compile time instead of launching `ChatRoomConfigEditor.exe`. This keeps one implementation of the configuration UI while producing a single integrated `ChatRoomTray.exe`.

Run `ChatRoomTray.exe --config` to open only the integrated configuration editor without starting the tray runtime.
