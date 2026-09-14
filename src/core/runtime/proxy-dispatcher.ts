import { ProxyAgent, Socks5ProxyAgent, type Dispatcher } from "undici";
import { ChatRoomError } from "../errors/chatroom-error.js";

/**
 * Build an undici dispatcher that routes outbound connections through an
 * unauthenticated HTTP, HTTPS, or SOCKS5 proxy.
 */
export function createProxyDispatcher(
  raw: string,
  timeoutMs: number,
): Dispatcher {
  let proxy: URL;
  try {
    proxy = new URL(raw);
  } catch {
    throw new ChatRoomError("INVALID_INPUT", "Invalid proxy URL");
  }
  if (!["http:", "https:", "socks5:"].includes(proxy.protocol))
    throw new ChatRoomError(
      "UNSUPPORTED",
      "Only http, https, and socks5 proxies are supported",
    );
  if (proxy.username || proxy.password)
    throw new ChatRoomError(
      "INVALID_INPUT",
      "Proxy authentication is not supported",
    );
  if (
    !proxy.hostname ||
    (proxy.pathname !== "" && proxy.pathname !== "/") ||
    proxy.search ||
    proxy.hash
  )
    throw new ChatRoomError(
      "INVALID_INPUT",
      "Proxy URL must contain only a host and optional port",
    );
  if (proxy.port && (Number(proxy.port) < 1 || Number(proxy.port) > 65535))
    throw new ChatRoomError("INVALID_INPUT", "Invalid proxy port");
  return proxy.protocol === "socks5:"
    ? new Socks5ProxyAgent(proxy, { connectTimeout: timeoutMs })
    : new ProxyAgent({ uri: proxy.toString(), connectTimeout: timeoutMs });
}
