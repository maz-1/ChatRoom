import { ChatRoomError } from "../../core/errors/chatroom-error.js";
import type { HttpRequestInput, HttpResponse } from "./types.js";
import {
  fetch,
  ProxyAgent,
  Socks5ProxyAgent,
  type Dispatcher,
  type RequestInit,
  type Response,
} from "undici";

export interface HttpFetcherLimits {
  defaultTimeoutMs: number;
  maxTimeoutMs: number;
  maxResponseBytes: number;
}

export class HttpFetcher {
  constructor(private readonly limits: HttpFetcherLimits) {}

  async request(input: HttpRequestInput): Promise<HttpResponse> {
    const url = parseTargetUrl(input.url);
    if (
      input.body !== undefined &&
      (input.method === "GET" || input.method === "HEAD")
    )
      throw new ChatRoomError(
        "INVALID_INPUT",
        `A request body cannot be sent with ${input.method}; use POST, PUT, or PATCH instead`,
      );
    const timeoutMs = Math.min(
      input.timeoutMs ?? this.limits.defaultTimeoutMs,
      this.limits.maxTimeoutMs,
    );
    const body = encodeRequestBody(input);
    const headers = normalizeHeaders(input.headers);
    const dispatcher =
      input.proxy === undefined
        ? undefined
        : createProxy(input.proxy, timeoutMs);
    try {
      const options: RequestInit & { dispatcher?: Dispatcher } = {
        method: input.method,
        ...(headers === undefined ? {} : { headers }),
        ...(body === undefined ? {} : { body }),
        redirect: "follow",
        signal: AbortSignal.timeout(timeoutMs),
        ...(dispatcher === undefined ? {} : { dispatcher }),
      };
      const response = await fetch(url, options);
      return await readResponse(
        response,
        url,
        input.responseFormat,
        timeoutMs,
        this.limits.maxResponseBytes,
      );
    } catch (error) {
      throw translateRequestError(error, timeoutMs);
    } finally {
      await dispatcher?.destroy();
    }
  }
}

function createProxy(raw: string, timeoutMs: number): Dispatcher {
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

function parseTargetUrl(raw: string): URL {
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    throw new ChatRoomError("INVALID_INPUT", `Invalid request URL: ${raw}`);
  }
  if (url.protocol !== "http:" && url.protocol !== "https:")
    throw new ChatRoomError(
      "UNSUPPORTED",
      `Only http and https URLs are supported, got: ${url.protocol}`,
    );
  if (url.username || url.password)
    throw new ChatRoomError(
      "INVALID_INPUT",
      "URL-embedded credentials are not allowed; pass an authorization header instead",
    );
  return url;
}

function normalizeHeaders(
  headers: Record<string, string> | undefined,
): Record<string, string> | undefined {
  if (!headers) return undefined;
  const normalized: Record<string, string> = {};
  for (const [name, value] of Object.entries(headers)) {
    if (name.trim() === "")
      throw new ChatRoomError(
        "INVALID_INPUT",
        "Header names must not be empty",
      );
    normalized[name.trim()] = value;
  }
  return normalized;
}

function encodeRequestBody(input: HttpRequestInput): RequestInit["body"] {
  if (input.body === undefined) return undefined;
  if (input.bodyEncoding === "text") return input.body;
  const compact = input.body.replace(/\s+/g, "");
  if (compact.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(compact))
    throw new ChatRoomError(
      "INVALID_INPUT",
      "Request body is not valid standard base64",
    );
  return new Uint8Array(Buffer.from(compact, "base64"));
}

async function readResponse(
  response: Response,
  requestedUrl: URL,
  format: "auto" | "text" | "base64" | "omit",
  timeoutMs: number,
  maxResponseBytes: number,
): Promise<HttpResponse> {
  const headers: Record<string, string> = {};
  for (const [name, value] of response.headers) {
    headers[name] =
      headers[name] === undefined ? value : `${headers[name]}, ${value}`;
  }

  if (format === "omit") {
    await response.body?.cancel().catch(() => undefined);
    return {
      status: response.status,
      statusText: response.statusText,
      url: response.url || requestedUrl.toString(),
      headers,
      body: "",
      bodyEncoding: "text",
      bodyBytes: 0,
      truncated: false,
    };
  }

  const chunks: Uint8Array[] = [];
  let received = 0;
  let truncated = false;
  try {
    if (response.body) {
      const reader = response.body.getReader();
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        const remaining = maxResponseBytes - received;
        if (value.byteLength > remaining) {
          if (remaining > 0) chunks.push(value.slice(0, remaining));
          received = maxResponseBytes;
          truncated = true;
          await reader.cancel().catch(() => undefined);
          break;
        }
        chunks.push(value);
        received += value.byteLength;
      }
    }
  } catch (error) {
    throw translateRequestError(error, timeoutMs);
  }

  const buffer = Buffer.concat(chunks);
  const asText =
    format === "text" ||
    (format === "auto" &&
      isTextualContentType(response.headers.get("content-type")));
  return {
    status: response.status,
    statusText: response.statusText,
    url: response.url || requestedUrl.toString(),
    headers,
    body: asText
      ? new TextDecoder("utf-8").decode(buffer)
      : buffer.toString("base64"),
    bodyEncoding: asText ? "text" : "base64",
    bodyBytes: received,
    truncated,
  };
}

function isTextualContentType(contentType: string | null): boolean {
  if (!contentType) return true;
  const mime = contentType.split(";")[0]!.trim().toLowerCase();
  return (
    mime.startsWith("text/") ||
    mime === "application/json" ||
    mime === "application/xml" ||
    mime === "application/javascript" ||
    mime === "application/yaml" ||
    mime === "application/x-yaml" ||
    mime === "application/toml" ||
    mime === "application/graphql" ||
    mime.endsWith("+json") ||
    mime.endsWith("+xml")
  );
}

function translateRequestError(
  error: unknown,
  timeoutMs: number,
): ChatRoomError {
  if (error instanceof ChatRoomError) return error;
  if (error instanceof DOMException && error.name === "TimeoutError")
    return new ChatRoomError(
      "PROCESS_FAILED",
      `HTTP request timed out after ${timeoutMs}ms`,
    );
  if (error instanceof Error) {
    const cause = error.cause as { code?: string } | undefined;
    const detail = cause?.code
      ? `${error.message} (${cause.code})`
      : error.message;
    return new ChatRoomError(
      "PROCESS_FAILED",
      `HTTP request failed: ${detail}`,
      undefined,
      { cause: error },
    );
  }
  return new ChatRoomError("PROCESS_FAILED", "HTTP request failed");
}
