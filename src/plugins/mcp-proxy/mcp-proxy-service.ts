import {
  Client,
  StreamableHTTPClientTransport,
  type ContentBlock,
  type FetchLike,
  type Tool,
  type Transport,
} from "@modelcontextprotocol/client";
import { StdioClientTransport } from "@modelcontextprotocol/client/stdio";
import type { Readable } from "node:stream";
import type { McpConfig, McpServerConfig } from "../../config/types.js";
import { ChatRoomError } from "../../core/errors/chatroom-error.js";
import { childEnvironment } from "../../core/runtime/child-environment.js";
import {
  CHATROOM_NAME,
  CHATROOM_VERSION,
} from "../../core/runtime/identity.js";
import { createProxyDispatcher } from "../../core/runtime/proxy-dispatcher.js";
import { fetch as undiciFetch, type Dispatcher } from "undici";
import type { RuntimeEventBus } from "../../app/event-bus.js";
import type {
  McpCallInput,
  McpCallOutput,
  McpServerDetail,
  McpServerSummary,
  McpToolSummary,
} from "./types.js";

const STDERR_TAIL_BYTES = 8 * 1024;

interface ServerState {
  readonly name: string;
  readonly config: McpServerConfig;
  client: Client | null;
  tools: Tool[] | null;
  status: "idle" | "connected" | "error";
  error: string | null;
  connecting: Promise<void> | null;
  closing: boolean;
  stderrTail: string;
  dispatcher: Dispatcher | null;
}

export class McpProxyService {
  private readonly servers = new Map<string, ServerState>();

  constructor(
    private readonly config: McpConfig,
    private readonly events: RuntimeEventBus,
  ) {
    for (const [name, serverConfig] of Object.entries(config.servers))
      this.servers.set(name, {
        name,
        config: serverConfig,
        client: null,
        tools: null,
        status: "idle",
        error: null,
        connecting: null,
        closing: false,
        stderrTail: "",
        dispatcher: null,
      });
  }

  async list(options: {
    server?: string | undefined;
    includeSchemas: boolean;
  }): Promise<{ servers: McpServerSummary[] }> {
    const targets = this.resolve(options.server);
    return {
      servers: await Promise.all(
        targets.map((state) => this.describe(state, options.includeSchemas)),
      ),
    };
  }

  async call(input: McpCallInput): Promise<Omit<McpCallOutput, "operationId">> {
    const state = this.require(input.server);
    try {
      await this.ensureConnected(state);
    } catch (error) {
      throw this.unavailable(state, error);
    }

    const client = state.client;
    if (!client) throw this.unavailable(state, null);

    if (!state.tools?.some((tool) => tool.name === input.tool))
      await this.refreshTools(state).catch(() => undefined);
    if (!state.tools?.some((tool) => tool.name === input.tool))
      throw new ChatRoomError(
        "NOT_FOUND",
        `MCP server "${state.name}" has no tool "${input.tool}"`,
        { availableTools: state.tools?.map((tool) => tool.name) ?? [] },
      );

    const timeoutMs = input.timeoutMs ?? this.config.callTimeoutMs;
    let result;
    try {
      result = await client.callTool(
        {
          name: input.tool,
          ...(input.arguments === undefined
            ? {}
            : { arguments: input.arguments }),
        },
        { timeout: timeoutMs },
      );
    } catch (error) {
      this.invalidate(state, describeError(error));
      throw this.unavailable(state, error);
    }

    const bounded = boundContent(result.content, this.config.maxResultBytes);
    return {
      server: state.name,
      tool: input.tool,
      isError: result.isError === true,
      content: bounded.content,
      structuredContent: asRecord(result.structuredContent),
      truncated: bounded.truncated,
    };
  }

  /**
   * WebUI projection: like {@link list} but also reports the captured stdio
   * stderr tail. Never returns configured env values or HTTP headers.
   */
  async inspect(
    serverName?: string | undefined,
  ): Promise<{ servers: McpServerDetail[] }> {
    const targets = this.resolve(serverName);
    return {
      servers: await Promise.all(
        targets.map(async (state) => ({
          ...(await this.describe(state, true)),
          stderrTail: state.stderrTail.trim(),
        })),
      ),
    };
  }

  async refresh(
    serverName?: string | undefined,
  ): Promise<{ servers: McpServerSummary[] }> {
    const targets = this.resolve(serverName);
    for (const state of targets) await this.close(state);
    return await this.list({ server: serverName, includeSchemas: true });
  }

  async shutdown(): Promise<void> {
    for (const state of this.servers.values()) await this.close(state);
  }

  private resolve(serverName?: string | undefined): ServerState[] {
    if (serverName === undefined) return [...this.servers.values()];
    const state = this.servers.get(serverName);
    if (!state)
      throw new ChatRoomError(
        "NOT_FOUND",
        `Unknown MCP server: ${serverName}`,
        { availableServers: [...this.servers.keys()] },
      );
    return [state];
  }

  private require(serverName: string): ServerState {
    return this.resolve(serverName)[0]!;
  }

  private async describe(
    state: ServerState,
    includeSchemas: boolean,
  ): Promise<McpServerSummary> {
    try {
      await this.ensureConnected(state);
    } catch {
      // The per-server failure is reported inline instead of failing the call.
    }
    const tools = state.tools ?? [];
    return {
      name: state.name,
      type: state.config.type,
      target: describeTarget(state.config),
      status: state.client ? "connected" : "error",
      error: state.error,
      toolCount: tools.length,
      tools: tools.map((tool) => summarizeTool(tool, includeSchemas)),
    };
  }

  private async ensureConnected(state: ServerState): Promise<void> {
    if (state.client) return;
    if (state.connecting) return await state.connecting;
    const pending = this.connect(state).finally(() => {
      state.connecting = null;
    });
    state.connecting = pending;
    return await pending;
  }

  private async connect(state: ServerState): Promise<void> {
    const transport = this.createTransport(state);
    const client = new Client(
      { name: CHATROOM_NAME, version: CHATROOM_VERSION },
      { capabilities: {} },
    );
    client.onerror = (error) => this.invalidate(state, describeError(error));
    client.onclose = () => this.invalidate(state, "Connection closed");
    try {
      await client.connect(transport, { timeout: this.config.callTimeoutMs });
      state.client = client;
      state.tools = await this.listTools(client);
      state.status = "connected";
      state.error = null;
      this.events.emit({ type: "mcp-servers" });
    } catch (error) {
      await client.close().catch(() => undefined);
      await this.destroyDispatcher(state);
      this.invalidate(state, describeError(error));
      throw error;
    }
  }

  private async listTools(client: Client): Promise<Tool[]> {
    const result = await client.listTools(undefined, {
      timeout: this.config.callTimeoutMs,
    });
    return result.tools;
  }

  private async refreshTools(state: ServerState): Promise<void> {
    if (!state.client) return;
    state.tools = await this.listTools(state.client);
  }

  private createTransport(state: ServerState): Transport {
    if (state.config.type === "stdio") {
      const transport = new StdioClientTransport({
        command: state.config.command,
        args: state.config.args,
        env: resolveEnvironment(state.config.env),
        ...(state.config.cwd === null ? {} : { cwd: state.config.cwd }),
        stderr: "pipe",
      });
      const stderr = transport.stderr as unknown as Readable | null;
      if (stderr) {
        stderr.setEncoding("utf8");
        stderr.on("data", (chunk: string) => {
          state.stderrTail = tailOf(
            state.stderrTail + chunk,
            STDERR_TAIL_BYTES,
          );
        });
      }
      return transport;
    }

    const dispatcher =
      state.config.proxy === null
        ? null
        : createProxyDispatcher(state.config.proxy, this.config.callTimeoutMs);
    state.dispatcher = dispatcher;
    return new StreamableHTTPClientTransport(new URL(state.config.url), {
      ...(Object.keys(state.config.headers).length === 0
        ? {}
        : { requestInit: { headers: state.config.headers } }),
      ...(dispatcher === null ? {} : { fetch: proxiedFetch(dispatcher) }),
    });
  }

  private invalidate(state: ServerState, reason: string | null): void {
    if (state.closing) return;
    // Records the failure reason even when no client was ever established, so
    // a server that cannot be reached reports why instead of an empty error.
    state.client = null;
    state.tools = null;
    state.status = "error";
    state.error = reason ?? "Connection closed";
    this.events.emit({ type: "mcp-servers" });
  }

  private unavailable(state: ServerState, cause: unknown): ChatRoomError {
    const detail = state.error ?? "unknown error";
    const stderr = state.stderrTail.trim();
    return new ChatRoomError(
      "PROCESS_FAILED",
      `MCP server "${state.name}" is unavailable: ${detail}${stderr ? `\n${stderr}` : ""}`,
      undefined,
      cause === null ? undefined : { cause },
    );
  }

  private async close(state: ServerState): Promise<void> {
    state.closing = true;
    const client = state.client;
    state.client = null;
    state.tools = null;
    state.status = "idle";
    state.error = null;
    state.stderrTail = "";
    await client?.close().catch(() => undefined);
    await this.destroyDispatcher(state);
    state.closing = false;
  }

  private async destroyDispatcher(state: ServerState): Promise<void> {
    const dispatcher = state.dispatcher;
    state.dispatcher = null;
    await dispatcher?.destroy().catch(() => undefined);
  }
}

function describeTarget(config: McpServerConfig): string {
  return config.type === "stdio"
    ? [config.command, ...config.args].join(" ")
    : config.url;
}

function summarizeTool(tool: Tool, includeSchemas: boolean): McpToolSummary {
  return {
    name: tool.name,
    title: tool.title ?? null,
    description: tool.description ?? null,
    inputSchema: includeSchemas ? asRecord(tool.inputSchema) : null,
  };
}

function resolveEnvironment(
  overrides: Record<string, string>,
): Record<string, string> {
  const environment: Record<string, string> = {};
  for (const [key, value] of Object.entries(childEnvironment(overrides)))
    if (value !== undefined) environment[key] = value;
  return environment;
}

function proxiedFetch(dispatcher: Dispatcher): FetchLike {
  return (url, init) =>
    undiciFetch(url as string, {
      ...(init as Record<string, unknown>),
      dispatcher,
    }) as unknown as Promise<Response>;
}

function boundContent(
  content: ContentBlock[],
  maxBytes: number,
): { content: Record<string, unknown>[]; truncated: boolean } {
  const blocks: Record<string, unknown>[] = [];
  for (const block of content) if (isRecord(block)) blocks.push(block);
  const output: Record<string, unknown>[] = [];
  let used = 0;
  for (const block of blocks) {
    const size = Buffer.byteLength(JSON.stringify(block) ?? "");
    if (used + size > maxBytes) {
      if (typeof block.text === "string" && maxBytes - used > 256)
        output.push({
          ...block,
          text: `${block.text.slice(0, Math.max(0, maxBytes - used - 128))}\n[truncated by ChatRoom: response exceeded mcp.maxResultBytes]`,
        });
      output.push({
        type: "text",
        text: `[ChatRoom truncated ${blocks.length - output.length} content block(s): response exceeded mcp.maxResultBytes (${maxBytes})]`,
      });
      return { content: output, truncated: true };
    }
    output.push(block);
    used += size;
  }
  return { content: output, truncated: false };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return isRecord(value) ? value : null;
}

function describeError(error: unknown): string {
  if (error instanceof Error) return error.message;
  return String(error);
}

function tailOf(value: string, maxBytes: number): string {
  if (Buffer.byteLength(value) <= maxBytes) return value;
  return value.slice(-maxBytes);
}
