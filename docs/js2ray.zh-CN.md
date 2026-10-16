# js2ray 代理与反向代理

ChatRoom 内置纯 JavaScript js2ray 运行时，可为 `http_request` 提供 VLESS 等协议的出站连接，也可通过传统 Bridge/Portal 隧道提供远程 MCP 和 WebUI。运行时直接使用连接流，不启动本地 SOCKS/HTTP 代理端口，也不修改系统代理。

## 配置 HTTP 出站

将 `assets/js2ray.config.example.json` 复制到自己的配置目录，填写真实节点地址、UUID、SNI、Host 和路径。示例中的凭据是合成值。

在 ChatRoom 主配置中添加：

```json
{
  "js2ray": {
    "enabled": true,
    "configPath": "./js2ray.json",
    "outboundTag": null
  }
}
```

`configPath` 相对于 ChatRoom 主配置文件所在目录解析；节点配置内的证书及 Geo 资源路径相对于节点配置文件解析。修改配置后重启 ChatRoom。Tray 配置编辑器的 js2ray 页提供相同选项。

调用示例：

```json
{
  "url": "https://example.com/api",
  "proxy": "js2ray",
  "method": "GET"
}
```

`v2ray` 是 `js2ray` 的兼容别名。省略 proxy 仍然直连，已有 HTTP、HTTPS、SOCKS5 代理 URL 的行为保持不变。选择 js2ray 后运行时不可用会返回错误，不回退直连；重定向持续使用同一代理适配器。

`outboundTag: null` 表示遵循节点配置的路由规则；指定 tag 则固定使用该出站并绕过路由。交给 js2ray 不等于强制经远端节点，配置为 direct 的目标仍可直连。

## 配置反向代理

使用 `assets/js2ray-reverse.config.example.json` 作为 Bridge 节点配置，并设置主配置：

```json
{
  "auth": {
    "mcpPublicBaseUrl": "https://mcp.example.com",
    "webPublicBaseUrl": null
  },
  "js2ray": {
    "enabled": true,
    "configPath": "./js2ray-reverse.json",
    "outboundTag": null,
    "reverse": {
      "enabled": true,
      "bridgeTag": "bridge",
      "localOutboundTag": "mcp-local",
      "targetHost": "127.0.0.1",
      "targetPort": 8765
    }
  }
}
```

上面是需合并进现有主配置的字段。公网地址继续使用现有 OAuth/WebUI 认证，并要求配置路径对应的系统凭据库中存在 owner token。WebUI 公网地址为 null 时，隧道仅开放 MCP、OAuth 和发现端点，管理 API 不对外开放。

反代导入规则如下：

- 节点配置必须没有代理入站和 Portal，且只包含一个与 bridgeTag 一致的 Bridge。ChatRoom 托管本机 Bridge，Portal 独立部署。
- 普通出站连接及 Bridge 控制承载共享同一运行时。控制承载使用主配置的固定 outboundTag；未指定时使用节点配置的第一个出站，通常为 proxy。
- ChatRoom 将 localOutboundTag 对应的出站替换为进程内 HTTP 服务，并在原路由之前固定 Bridge 控制和业务规则。其他出站、DNS 和普通业务分流规则保留。
- targetHost 和 targetPort 是 Portal 发来的业务目标，必须完全一致。它们不是新增监听地址，也不需要等于 ChatRoom 实际监听端口。其他目标会被拒绝。
- 不要从原 Freedom 出站的 settings.redirect 推断 Portal 业务目标。例如 Portal 发送 127.0.0.1:1985、原出站再重定向到 127.0.0.1:8765 时，应将 reverse.targetPort 设为 1985。ChatRoom 替换该出站后直接处理服务流，不执行原 TCP 重定向。
- 反代流量进入独立的进程内 HTTP 入口，固定公网 Host，清理外部转发头，继续执行 MCP OAuth、远程工具权限、WebUI 登录及 Origin 校验。伪造 localhost Host 无法获得本地免认证权限。
- 普通出站 mux.enabled 为 false 不影响 reverse 内部的 Mux。承载故障会终止受影响的业务流，后续新连接随 Bridge 重连恢复，不重放应用数据。

### Portal 对接示例

以下是部署在公网服务器上的传统 Portal 配置骨架，不作为 ChatRoom 的节点配置加载。假设公网 HTTPS 前端将 `proxy.example.com/tunnel` 的 WebSocket 流量转发到 `127.0.0.1:10000`，将 `mcp.example.com` 的 HTTP 流量转发到 `127.0.0.1:18080`：

```json
{
  "reverse": { "portals": [{ "tag": "portal", "domain": "reverse.internal.example" }] },
  "inbounds": [
    {
      "tag": "carrier",
      "listen": "127.0.0.1",
      "port": 10000,
      "protocol": "vless",
      "settings": { "clients": [{ "id": "d342d11e-d424-4583-b36e-524ab1f0afa4" }], "decryption": "none" },
      "streamSettings": { "network": "ws", "wsSettings": { "path": "/tunnel" } }
    },
    {
      "tag": "business",
      "listen": "127.0.0.1",
      "port": 18080,
      "protocol": "dokodemo-door",
      "settings": { "address": "127.0.0.1", "port": 8765, "network": "tcp" }
    }
  ],
  "outbounds": [{ "tag": "block", "protocol": "blackhole" }],
  "routing": { "rules": [
    { "type": "field", "inboundTag": ["carrier", "business"], "outboundTag": "portal" }
  ] }
}
```

两端控制域名和节点身份必须一致。HTTPS 前端为节点 WSS 和公网 MCP 分别提供正确证书，并允许 SSE/长连接、关闭响应缓冲；如启用远程 WebUI，再为其配置公网域名及主配置中的 webPublicBaseUrl。使用与实际 Portal 版本匹配的传统 reverse 配置，不把更新的协议变体视为自动兼容。

## 状态与诊断

WebUI 的 js2ray 页面及已认证的 `GET /api/js2ray` 显示运行状态、代理监听数、反代承载和会话数。active 表示控制通道可用，不能据此认定公网域名或 HTTPS 前端已经配置正确。

日志仅保留运行阶段、错误代码与连接状态，不返回节点配置、UUID、密钥或原始异常。启用功能时，配置或资源无效会使 ChatRoom 启动失败；修正配置或关闭 js2ray 后重新启动。普通用户关闭状态无需节点配置文件。

参考旧配置时，应显式移除 SOCKS 入站和悬空的 api 路由。GeoIP/Geosite 文件按 js2ray 要求提供固定的资源版本及 SHA256；节点文件仍由 js2ray 严格校验，ChatRoom 不静默丢弃未知字段。

## 源码构建与打包

```sh
git submodule update --init --recursive
npm ci
npm run check
```

`vendor/js2ray` 跟踪 maz-1/js2ray 的 `codex/js2ray-release-acceptance` 分支，但构建使用 Git 记录的固定提交。更新时先更新 submodule，再重新运行整合测试。

构建将运行所需源码及 MIT 许可证复制到 `dist/vendor/js2ray`。npm 包使用此快照，不依赖用户安装目录中存在 Git、submodule 或外部内核，也不使用本机绝对路径及 file 依赖。根依赖固定 js2ray 所需的 re2js 和 ws 版本。

整合测试包含 VLESS/WSS、目标 HTTPS、重定向、取消、无代理监听、Bridge/Portal、OAuth/MCP、远程 WebUI/Origin 和 SSE。测试使用本机合成节点，不连接真实代理；上游自身的验收记录不替代 ChatRoom 整合测试。
