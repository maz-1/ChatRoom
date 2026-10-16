import {
  createServer,
  type IncomingMessage,
  type RequestListener,
} from "node:http";
import { duplexPair, type Duplex } from "node:stream";
import type { ChatRoomConfig } from "#config/types";
import { ChatRoomError } from "#core/errors/chatroom-error";

/** HTTP over an in-process stream; this server never calls listen(). */
export class ReverseIngress {
  private closed = false;
  private readonly streams = new Set<Duplex>();
  private readonly server;

  constructor(handler: RequestListener, auth: ChatRoomConfig["auth"]) {
    if (!auth.mcpPublicBaseUrl && !auth.webPublicBaseUrl)
      throw new ChatRoomError(
        "INVALID_INPUT",
        "js2ray reverse requires a public MCP or WebUI origin",
      );
    this.server = createServer((request, response) => {
      const origin = reverseOrigin(request, auth);
      if (!origin) {
        response.writeHead(404, {
          "content-type": "text/plain",
          connection: "close",
        });
        response.end("Not found");
        return;
      }
      // The stream's ingress establishes remote identity, never its original
      // Host/Forwarded headers. Existing OAuth, Web auth and remote tool scopes
      // consequently apply even to Host: localhost requests.
      for (const name of Object.keys(request.headers))
        if (name === "forwarded" || name.startsWith("x-forwarded-"))
          delete request.headers[name];
      request.headers.host = new URL(origin).host;
      request.rawHeaders = Object.entries(request.headers).flatMap(
        ([name, value]) =>
          value === undefined
            ? []
            : [name, ...(Array.isArray(value) ? [value.join(", ")] : [value])],
      );
      handler(request, response);
    });
    this.server.on("clientError", (_error, socket) => socket.destroy());
    this.server.timeout = 120_000;
    this.server.on("upgrade", (_request, socket) =>
      socket.end("HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\n"),
    );
  }

  createStream(): Duplex {
    if (this.closed)
      throw new ChatRoomError("PROCESS_FAILED", "Reverse ingress is closed");
    const [client, server] = duplexPair();
    for (const stream of [client, server]) {
      this.streams.add(stream);
      stream.on("error", () => {});
      stream.once("close", () => this.streams.delete(stream));
    }
    client.once("close", () => server.destroy());
    server.once("close", () => client.destroy());
    this.server.emit("connection", httpSocket(server));
    return client;
  }

  close(): void {
    this.closed = true;
    for (const stream of this.streams) stream.destroy();
    this.streams.clear();
  }
}

function reverseOrigin(
  request: IncomingMessage,
  auth: ChatRoomConfig["auth"],
): string | null {
  const raw = request.url ?? "";
  if (!raw.startsWith("/") || raw.startsWith("//")) return null;
  let url: URL;
  try {
    const path = raw.split("?", 1)[0]!;
    if (path.includes("\\") || /%2f|%5c|%00/i.test(path)) return null;
    url = new URL(raw, "http://reverse.invalid");
    const decoded = decodeURIComponent(url.pathname);
    if (decoded.includes("\\") || decoded.includes("\0")) return null;
  } catch {
    return null;
  }
  request.url = url.pathname + url.search;
  // Express routes are case-insensitive by default. Classification must use
  // the same semantics so /MCP cannot be treated as a WebUI/local MCP request.
  const path = url.pathname.toLowerCase();
  const mcp =
    path === "/mcp" ||
    path === "/mcp/" ||
    path === "/oauth" ||
    path.startsWith("/oauth/") ||
    path.startsWith("/.well-known/oauth-");
  return mcp ? auth.mcpPublicBaseUrl : auth.webPublicBaseUrl;
}

/** Supply the socket methods used by Node's HTTP parser and keep-alive timer. */
function httpSocket(stream: Duplex): Duplex {
  let timeoutMs = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const touch = () => {
    clearTimeout(timer);
    if (timeoutMs > 0 && !stream.destroyed) {
      timer = setTimeout(() => stream.emit("timeout"), timeoutMs);
      timer.unref();
    }
  };
  const push = stream.push.bind(stream);
  const write = stream._write.bind(stream);
  stream.push = (chunk, encoding) => {
    touch();
    return push(chunk, encoding);
  };
  stream._write = (chunk, encoding, callback) => {
    touch();
    write(chunk, encoding, callback);
  };
  stream.once("close", () => clearTimeout(timer));
  Object.assign(stream, {
    setTimeout(milliseconds: number, callback?: () => void) {
      timeoutMs = milliseconds;
      if (callback) stream.once("timeout", callback);
      touch();
      return stream;
    },
    setNoDelay() {
      return stream;
    },
    setKeepAlive() {
      return stream;
    },
    ref() {
      return stream;
    },
    unref() {
      return stream;
    },
  });
  return stream;
}
