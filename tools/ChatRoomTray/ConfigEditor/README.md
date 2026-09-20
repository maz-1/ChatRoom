# ChatRoomTray 配置编辑器模块

该目录包含 `ChatRoomTray` 内置的 ChatRoom `config.json` 配置编辑器。配置编辑器没有拆成独立工程：Windows 下继续与 WinForms 托盘编译到同一个 `ChatRoomTray.exe`；Linux/macOS 使用同一个 `ChatRoomTray.csproj` 构建配置编辑器、配置 CLI 与跨平台 tray host。

## 平台结构

| 平台 | 目标框架 | Eto 后端 | 托盘 |
| --- | --- | --- | --- |
| Windows | .NET 8 (`net8.0-windows`) | Eto WinForms | 保留现有 WinForms Tray |
| Linux | .NET 8 (`net8.0`) | Eto GTK | AppIndicator/StatusNotifierItem；无 watcher 时 fallback 窗口 |
| macOS | .NET 8 (`net8.0`) | Eto Mac64 | Eto 原生 TrayIndicator |

跨平台目标只排除 Windows 专用的 `StartupArgsDialog.cs` 与 `TrayApplicationContext.cs`；`TraySettings.cs` 已改为纯托管 INI，三平台共用。`ConfigEditor` 源码、配置模型、校验、MCP 服务编辑和 ownerToken 管理也均为同一份代码。

## 默认路径

| 平台 | config.json | Data | State |
| --- | --- | --- | --- |
| Windows | `%APPDATA%\ChatRoom\config.json` | `%LOCALAPPDATA%\ChatRoom\Data` | `%LOCALAPPDATA%\ChatRoom\State` |
| macOS | `~/Library/Application Support/ChatRoom/config.json` | `~/Library/Application Support/ChatRoom/Data` | `~/Library/Application Support/ChatRoom/State` |
| Linux | `$XDG_CONFIG_HOME/chatroom/config.json` 或 `~/.config/chatroom/config.json` | `$XDG_DATA_HOME/chatroom` 或 `~/.local/share/chatroom` | `$XDG_STATE_HOME/chatroom` 或 `~/.local/state/chatroom` |

三个平台都优先使用 `CHATROOM_CONFIG`。Linux 只接受绝对的 XDG 路径，与 `src/config/platform-paths.ts` 保持一致。

## 构建与运行

Windows 默认仍构建完整托盘版本：

```powershell
cd tools/ChatRoomTray
dotnet build
bin\Debug\net8.0-windows\ChatRoomTray.exe --config
```

Linux 上同一个项目默认选择 `net8.0 + Eto GTK`：

```bash
cd tools/ChatRoomTray
dotnet build
./bin/Debug/net8.0/ChatRoomTray --config
```

macOS 使用 `net8.0 + Eto Mac64`，配置了 `osx-x64` 与 `osx-arm64` 两个 RID；`dotnet build` 会生成对应 `.app`。

Linux GUI 需要 GTK3；真实 tray 需要 `libayatana-appindicator3`/`libappindicator3`。KDE Plasma 原生提供 StatusNotifierWatcher；GNOME Shell 需启用 AppIndicator/KStatusNotifierItem 扩展。若 watcher/库不可用，会显示 fallback 控制窗口。认证功能另需 `libsecret-1` 和 Secret Service（如 GNOME Keyring/KWallet）。Visual Studio profile `win-x64-single-file` publishes a framework-dependent single EXE (about 3 MiB in the current build). The target PC must have Microsoft .NET 8 Desktop Runtime x64 installed.

## 依赖

- **Eto.Forms 2.11.0**：统一 UI API。
- **Eto.Platform.Windows 2.11.0**：Windows backend。
- **Eto.Platform.Gtk 2.11.0**：Linux backend。
- **Eto.Platform.Mac64 2.11.0**：macOS backend。
- **Newtonsoft.Json 13.0.3**：配置序列化与多态 MCP transport。

## 能编辑的内容

覆盖配置文件里的**全部**选项，并额外管理系统凭据库中的 `ownerToken`，按标签页分组：

| 标签页 | 覆盖内容 |
| --- | --- |
| 工作区与路径 | `allowedRoots`（增删改）、`dataDir`、`databasePath` |
| 服务与认证 | `server.host` / `server.port`、`auth.localWebAuth`、`mcpPublicBaseUrl`、`webPublicBaseUrl`、`allowedRedirectHosts`，以及系统凭据库中的 `ownerToken` |
| 限额 | `http.*`、`operations.maxPayloadBytes`、`process.*`、`mcp.callTimeoutMs`、`mcp.maxResultBytes` |
| MCP 服务 | `mcp.servers` 的**增删改**：`stdio`（命令/参数/环境变量/工作目录）与 `http`（URL/请求头/代理） |

`mcp.servers` 的条目支持 `stdio` 和 `http`。`stdio` 参数使用 argv 列表编辑器；“完整命令…”按当前系统解析：Windows 使用 `CommandLineToArgvW`，Linux/macOS 使用支持单引号、双引号、反斜杠转义与空参数的 shell-like parser。`http` 请求头按键/值编辑，“填写 Auth Token…”会写成 `Authorization: Bearer <token>`。

## ownerToken 的处理

`ownerToken` 不再写入新版本 `config.json`，而是按配置文件路径保存在系统凭据库：

- Windows：Credential Manager
- macOS：Keychain / Security.framework
- Linux：Secret Service / libsecret

凭据 `service=ChatRoom`，`account=owner-token:v1:<sha256(configPath)>`，与 ChatRoom 的 `@github/keytar` 适配器一致。Windows 计算 ID 前对路径转小写；macOS/Linux 保留路径大小写。

令牌不会显示在界面上，可复制、重新生成或清除。旧配置中的 `auth.ownerToken` 会在系统凭据库可用时安全迁移并回读验证；若凭据库暂时不可用，不会删除旧明文字段，纯本地配置仍可继续打开。

重新生成 `ownerToken` 本身**不会级联撤销** SQLite 中已经签发的 OAuth access/refresh token 或现有 WebUI session；它影响的是重启后的 owner-token 登录和新的 OAuth 授权确认。

## 安全设计

编辑器不会写出让 ChatRoom 拒绝启动的文件：

1. **校验规则与 ChatRoom 完全一致**：逐条复刻 `src/config/load-config.ts` 的 zod 规则（含各字段取值范围、`mcp` 服务名正则、URL/代理格式）以及 `validateRuntimeSecurity` 的两条运行时约束：
   - 绑定非回环地址时必须开启 `localWebAuth`
   - 启用任何认证入口（`localWebAuth` / 公网地址）时，系统凭据库中必须存在与当前配置路径对应的 `ownerToken`
2. **存在错误则拒绝保存**，错误逐项列在“校验结果”面板中
3. **严格模式**：ChatRoom 的 schema 是 strict 的，未知字段会导致启动失败；编辑器会识别并报错（新增字段时会移除）
4. **原子写入**：先写同目录临时文件，再替换目标文件
5. **自动备份**：覆盖前把原文件另存为 `config.json.<时间戳>.bak`，仅保留最近 10 个历史备份
6. **UTF-8 无 BOM**：Node 的 `JSON.parse` 会把 BOM 当作非法字符，因此写入不带 BOM
7. **外部修改保护**：若文件在打开后被其他程序改动，保存时会提示并允许重新加载
8. **`sse` 传输被拒绝**，并给出与 ChatRoom 相同的指引（改用 `http`）

## CLI / 无头验证

Windows 的 `net8.0-windows` 仍是 WinExe，建议从控制台用 `Start-Process -Wait`：

```powershell
$exe = "bin/Debug/net8.0-windows/ChatRoomTray.exe"

Start-Process $exe -ArgumentList '--check','--report','check.txt' -Wait -NoNewWindow
Start-Process $exe -ArgumentList '--selftest','--report','selftest.txt' -Wait -NoNewWindow
Start-Process $exe -ArgumentList '--uismoke','--report','uismoke.txt' -Wait -NoNewWindow
```

Linux 的 net8 构建可直接执行 apphost：

```bash
./ChatRoomTray --check
./ChatRoomTray --selftest
./ChatRoomTray --uismoke
./ChatRoomTray --traysmoke
```

macOS bundle 的 CLI 入口位于 `.app/Contents/MacOS/ChatRoomTray`，例如：

```bash
./ChatRoomTray.app/Contents/MacOS/ChatRoomTray --selftest
```

默认配置路径由当前平台决定，也可统一通过 `CHATROOM_CONFIG` 覆盖。退出码：`0` 通过、`1`/`2` 校验或比对失败、`3` 读取失败、`64` 参数错误。

## 已验证的跨平台路径

- Windows `net8.0-windows`：0 warning / 0 error，`--selftest` 与 `--uismoke` 均通过；self-contained single-file 发布目录可缩为唯一 `ChatRoomTray.exe`，其 selftest/uismoke 也均通过。
- .NET 8 + GTK：交叉编译 0 warning / 0 error。
- Linux .NET 8 x64 self-contained：在 WSL2 Ubuntu 上真实运行 `--selftest` 通过，POSIX 命令解析和 XDG 路径生效。
- Linux WSLg + GTK3：真实运行 Eto `--uismoke` 通过。
- Linux WSLg 托盘：缺少 AppIndicator 库时 fallback window 路径可启动；`--traysmoke` exit 0 且不启动 ChatRoom。
- Linux 托盘 SIGTERM：实测托盘和其 Node 子进程树都会结束，不遗留后台 Node。
- Linux 缺少 `libsecret-1` 时：纯本地配置 `--check` 为 0 error + 1 warning，不会被错误阻止。
- macOS `.NET 8 + Mac64`：已交叉构建 `osx-x64` / `osx-arm64` 两个 `.app`，0 warning / 0 error；最终原生运行仍需在 macOS 机器上验证。

## 需要知道的几点

- **改完要重启 ChatRoom 才生效**：ChatRoom 只在启动时读取配置。
- **保存会规范化格式**：输出为 2 空格缩进、键顺序固定，并补齐缺省段。
- **`env` / `headers` 中的密钥以明文保存在配置文件中**，与 ChatRoom 本身的设计一致。
- **打开所在文件夹**：Windows 使用系统 Shell，macOS 使用 `open`，Linux 使用 `xdg-open`。
- **Linux 凭据依赖**：认证功能需要 `libsecret-1` 和 Secret Service；若二者不可用，纯本地配置仍可打开/校验并给出 warning。
- **除 Windows Tray 外，ConfigEditor 不依赖 WinForms API**。

## 高 DPI 布局

- Windows 保留 `PerMonitorV2`。窗口初始客户区尺寸随 WinForms 设备 DPI 放大，并限制在屏幕工作区以内；不使用位图拉伸或降低 DPI 感知的兼容模式。
- `EditorLayout.VerticalScroll` 将 Windows 滚动内容的宽度约束到可用客户区。Eto WinForms 的 `ExpandContentWidth` 仅设置最小宽度，单独使用时长说明文字仍可能撑出横向滚动条。
- MCP 编辑器移除命令、工作目录、环境变量、URL 和容器的固定宽度。参数与请求头的操作按钮横排；表单允许纵向滚动，错误提示和确定／取消按钮位于独立底栏。
- stdio 环境变量使用“名称／值”两列表格，与 HTTP 请求头一致提供下方横排的添加／编辑／删除按钮，支持双击编辑及 Insert／F2／Delete。单项弹窗校验名称和重复项；值允许为空，保留空白、等号、引号与换行，不再解析 `KEY=VALUE` 文本。长值在弹窗内滚动，保存格式仍为 `mcp.servers.<name>.env` 对象。
- Windows `--uismoke` 增加 100%、125%、150%、200%、250% 的字号与客户区尺寸模拟，检查各页横向溢出、说明文字高度、底部按钮及缩小后恢复；150% 额外检查 stdio/http 切换。该测试使用隔离配置路径和隐藏的原生控件，不显示窗口、不修改系统 DPI，也不替代真实多显示器切换验收。

- 环境变量回归测试使用合成数据，检查表格列与按钮状态、增删改、重复名称、空值和特殊字符保真、stdio/http 切换、取消安全以及确定后 JSON 往返；Windows 同时验证首次原生行绘制，新建／编辑环境变量弹窗也加入五档布局模拟。

### MCP 表单可视区回归

- Windows 在原生 `Load` 完成后一次性应用初始客户区尺寸，避免 Eto 初始化覆盖构造时设置的 DPI 尺寸；后续加载事件不覆盖用户调整后的窗口大小。
- MCP 字段直接使用 `DynamicLayout` 的横向行，避免嵌套 `StackLayout` 的对齐占位空间被计入最小高度。参数列表按最多三行分配紧凑高度；WinForms 后端同时限制原生列表高度，更多参数在列表内部滚动。
- 环境变量和 HTTP 请求头表格使用剩余垂直空间，操作按钮和工作目录保持可见；无错误时不占用错误提示区域。窗口小到无法容纳最小布局时仍可纵向滚动，并能访问底部字段。
- `DialogViewportSmoke` 在原生初始化之后检查初始尺寸，并使用 150% 字号下的 870×544 客户区检查每个交互控件与滚动可视区的交集，覆盖空参数、30 个参数、50 个环境变量、错误提示、传输切换、放大/缩小和极小窗口恢复。测试不展示桌面窗口，不修改用户配置。
