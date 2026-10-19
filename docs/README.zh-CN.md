<p align="center">
  <img src="../assets/chatroom.svg" alt="ChatRoom" width="88" height="88" />
</p>

<h1 align="center">ChatRoom</h1>

<p align="center">
  <a href="../README.md">English</a> | 简体中文 | <a href="https://doc.chatroomcp.com/zh/">文档</a>
</p>

ChatRoom 是一个本地 MCP Runtime，让 ChatGPT 可以处理本地设备上的项目，运行进程、直接发送 HTTP/HTTPS 请求或执行电脑控制。它也可以作为 MCP 客户端代理其他 MCP 服务（stdio 与 Streamable HTTP），让 ChatGPT 经 ChatRoom 调用它们的工具。WebUI 用于浏览工作区、管理 Git 仓库和进程、使用电脑控制、查看已代理的 MCP 服务、管理 ChatRoom Cloud，并按插件查看操作日志。

## 安装

需要 Node.js 24.15.0 或更高版本以及 Git。

```bash
npm install -g @zhanguym/chatroom
chatroom init
chatroom serve
```
Windows用户也可以使用[ChatRoom-Windows-Portable](https://github.com/gymZJ/ChatRoom-Windows-Portable)，无需自行配置任何依赖。

默认本地地址：

```text
WebUI  http://127.0.0.1:8765/
MCP    http://127.0.0.1:8765/mcp
```

`chatroom init` 会创建[配置文件](https://doc.chatroomcp.com/zh/configuration)，并默认允许访问 `~/Projects` 下的工作区。

## 与 ChatGPT 配合使用

ChatGPT 通过 MCP Endpoint 连接 ChatRoom。需要远程访问时，可以直接在 WebUI 中启用 ChatRoom Cloud，也可以自行通过 HTTPS 将 ChatRoom 暴露到公网，并配置对应的公网地址。

在 ChatGPT Developer Mode 中创建自定义 MCP App，填写 ChatRoom 的 `/mcp` 地址，并使用系统凭据库中保存的 owner token 完成 OAuth 授权。只有在明确需要查看令牌时才运行 `chatroom auth token`。

当前 ChatGPT 接入流程见：[在 ChatGPT 中使用 ChatRoom](https://doc.chatroomcp.com/zh/chatgpt)。

## 工作区 MCP 设置

在 WebUI 的 **工作区 → 设置** 中，可以单独启用或禁用当前项目内的桥接 MCP 服务。点击 **配置工具** 可编辑允许/禁用的工具和服务调用超时，修改后保存。设置存储在工作区的 `.codex/config.toml`：

```toml
[mcp_servers.example]
enabled = false
enabled_tools = ["search", "read"]
disabled_tools = ["delete"]
tool_timeout_sec = 120
```

服务名对应 ChatRoom 全局配置中的 `mcp.servers`。未配置时默认启用，全局禁用优先。省略 `enabled_tools` 表示允许全部工具，空数组表示不允许任何工具；`disabled_tools` 在允许列表之后生效。筛选同时约束工具查询与调用。超时优先级为：单次调用的 `timeoutMs` → 工作区的 `tool_timeout_sec` → 全局的 `mcp.callTimeoutMs`；清空工作区超时可恢复继承。某次调用超时不会断开其他工作区共用的服务连接。

在 **工作区 → 技能** 中，可启用或禁用已发现的技能。覆盖项使用 `[[skills.config]]`（或等价的内联数组），包含 `path` 和 `enabled`。WebUI 保存相对于 `.codex/` 目录的 `SKILL.md` 路径，例如 `../.agents/skills/example/SKILL.md`；同时兼容绝对路径和技能目录路径。禁用的技能仍保留在 WebUI 中供重新启用，但不会出现在 `workspace_info` 中。已有 Codex 会话需重新启动以加载技能配置。

编辑时保留原有注释及其他配置，手动修改后在下一次 ChatRoom 请求中生效。工作区配置不导入 MCP 服务定义或其他 Codex 设置。

执行工作区内的 MCP 桥接操作时，向 `mcp_list_servers`、`mcp_call` 和 `mcp_refresh` 传入该工作区的 `root`。禁用的服务不返回工具，并拒绝该工作区内的调用。不传 `root` 时沿用全局设置；其他工作区不受影响。

## ChatRoom Cloud

ChatRoom Cloud 已内置于 ChatRoom，用于提供可选的 Remote MCP 和 Remote WebUI 公网访问。在 WebUI 的 **Cloud** 页面即可购买、恢复和管理服务。

## js2ray 代理

ChatRoom 可使用进程内 js2ray 发送 HTTP 请求，并通过 Bridge/Portal 提供远程访问，无须新增本地代理监听端口。配置运行时后，`http_request` 设置 `proxy: "js2ray"` 即可使用。详见 [配置与反代说明](js2ray.zh-CN.md)。

源码构建前先运行 `git submodule update --init --recursive`；npm 发布包已包含固定版本的运行时快照。

## 官方文档

[官方文档](https://doc.chatroomcp.com/zh/)

## License

Apache-2.0
