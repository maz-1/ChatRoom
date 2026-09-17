# ChatRoom 配置编辑器

编辑 ChatRoom 配置文件（`config.json`）的 Windows 桌面应用（C# / WinForms / **.NET Framework 4.8**）。
ChatRoom 的 WebUI 对 MCP 服务只做只读展示，增删改由本应用承担。

## 运行要求

- Windows（.NET Framework 4.8 随 Windows 10/11 内置，通常无需额外安装）
- 构建需要 .NET SDK（含 v4.8 目标包）或 Visual Studio 2022

## 构建与运行

```powershell
cd tools/ChatRoomConfigEditor
dotnet build
dotnet run
```

产物：`bin/Debug/net48/ChatRoomConfigEditor.exe`。构建过程通过 ILRepack 将
`Newtonsoft.Json.dll` 合并进 EXE，因此发布时无需携带独立的依赖 DLL；
`ChatRoomConfigEditor.exe.config` 仍用于 .NET Framework 和高 DPI 设置。

工程为 SDK 风格 csproj 且目标为 `net48`，可直接用 VS 2022 打开；也可在 VS 里改回传统 csproj 格式，源码无需改动。

## 依赖

唯一的 NuGet 依赖是 **Newtonsoft.Json 13.0.3**。net48 上没有 `System.Text.Json` 的多态序列化支持
（`[JsonPolymorphic]` / `[JsonDerivedType]` 属 .NET 7+），因此改用 Newtonsoft，并通过
`McpServerConverter` 按 `type` 字段读写两种 MCP 传输方式——输出键顺序与 ChatRoom 自身写法一致。
**ILRepack.Lib.MSBuild.Task** 仅在构建时使用，不会成为运行时依赖。

## 能编辑的内容

覆盖配置文件里的**全部**选项，按标签页分组：

| 标签页 | 覆盖字段 |
| --- | --- |
| 工作区与路径 | `allowedRoots`（增删改）、`dataDir`、`databasePath` |
| 服务与认证 | `server.host` / `server.port`、`auth.localWebAuth`、`ownerToken`、`mcpPublicBaseUrl`、`webPublicBaseUrl`、`allowedRedirectHosts` |
| 限额 | `http.*`、`operations.maxPayloadBytes`、`process.*`、`mcp.callTimeoutMs`、`mcp.maxResultBytes` |
| MCP 服务 | `mcp.servers` 的**增删改**：`stdio`（命令/参数/环境变量/工作目录）与 `http`（URL/请求头/代理） |

`mcp.servers` 的条目支持两种传输方式，字段与 ChatRoom 的校验规则一一对应。
`stdio` 的“参数”使用列表编辑器，每个列表项对应一个独立 argv 参数，可新建、编辑、删除和调整顺序；“环境变量”用 `KEY=VALUE`；“请求头”用 `Header: value`。

## ownerToken 的处理

按需求，令牌**不会显示在界面上**，任何控件都不包含它的内容（这一点由自检断言保证）。可用的操作：

- **复制**：写入剪贴板，界面只提示“已复制”，状态行显示为 `已设置 · N 个字符`
- **重新生成**：生成与 `chatroom init` 相同的 32 字节 base64url 令牌（43 字符），操作前会明确警告“已授权的 ChatGPT 客户端与 WebUI 会话需要重新授权”
- **清除**：置为 `null`（若启用了需要认证的入口，保存会被校验拦下）

## 安全设计

编辑器不会写出让 ChatRoom 拒绝启动的文件：

1. **校验规则与 ChatRoom 完全一致**：逐条复刻 `src/config/load-config.ts` 的 zod 规则（含各字段取值范围、`mcp` 服务名正则、URL/代理格式）以及 `validateRuntimeSecurity` 的两条运行时约束：
   - 绑定非回环地址时必须开启 `localWebAuth`
   - 启用任何认证入口（`localWebAuth` / 公网地址）时必须存在 `ownerToken`
2. **存在错误则拒绝保存**，错误逐项列在“校验结果”面板中
3. **严格模式**：ChatRoom 的 schema 是 strict 的，未知字段会导致启动失败；编辑器会识别并报错（新增字段时会移除）
4. **原子写入**：先写同目录临时文件，再替换目标文件
5. **自动备份**：覆盖前把原文件另存为 `config.json.<时间戳>.bak`
6. **UTF-8 无 BOM**：Node 的 `JSON.parse` 会把 BOM 当作非法字符，因此写入不带 BOM
7. **外部修改保护**：若文件在打开后被其他程序改动，保存时会提示并允许重新加载
8. **`sse` 传输被拒绝**，并给出与 ChatRoom 相同的指引（改用 `http`）

## 无头模式（便于验证与脚本化）

GUI 子系统程序从控制台运行时不会阻塞，建议用 `Start-Process -Wait`：

```powershell
$exe = "bin/Debug/net48/ChatRoomConfigEditor.exe"

# 校验（默认路径来自 CHATROOM_CONFIG 或 %APPDATA%\ChatRoom\config.json）
Start-Process $exe -ArgumentList '--check','--report','check.txt' -Wait -NoNewWindow

# 载入真实配置 → 另写一份 → 比对是否语义一致（不改动源文件）
Start-Process $exe -ArgumentList '--roundtrip','"C:\...\config.json"','out.json' -Wait -NoNewWindow

# 自检：编码、令牌生成、校验规则、写入不变量、往返一致性
Start-Process $exe -ArgumentList '--selftest','--report','selftest.txt' -Wait -NoNewWindow

# 界面自检：构造窗体与对话框并断言标签对齐、令牌未泄漏
Start-Process $exe -ArgumentList '--uismoke','--report','uismoke.txt' -Wait -NoNewWindow
```

退出码：`0` 通过、`1`/`2` 校验或比对失败、`3` 读取失败、`64` 参数错误。

## 需要知道的几点

- **改完要重启 ChatRoom 才生效**：ChatRoom 只在启动时读取配置。
- **保存会规范化格式**：输出为 2 空格缩进、键顺序固定。手工的制表符缩进/对齐风格会被改写。
- **缺省段会被补齐**：例如原本没有 `http` 段时，保存后会写入 ChatRoom 的默认值（数值与 `defaultConfig()` 一致，行为不变）。
- **`env` / `headers` 里的密钥以明文保存在配置文件中**（与 ChatRoom 本身的做法一致；该文件位于用户配置目录）。编辑器不会把它们显示在令牌那类“脱敏”位置——因为对 MCP 服务而言它们是必要的配置内容。
- **本工具只改配置文件**：不会重启 ChatRoom，也不会碰数据库与 Cloud 状态。
