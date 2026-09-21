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
import type { McpConfig, McpServerConfig } from "#config/types";
import { ChatRoomError } from "#core/errors/chatroom-error";
import { childEnvironment } from "#core/runtime/child-environment";
import {
  CHATROOM_NAME,
  CHATROOM_VERSION,
} from "#core/runtime/identity";
import { createProxyDispatcher } from "#core/runtime/proxy-dispatcher";
import { fetch as undiciFetch, type Dispatcher } from "undici";
import type { RuntimeEventBus } from "#app/event-bus";
import type {
  McpCallInput,
  McpCallOutput,
  McpServerDetail,
  McpServerSummary,
  McpToolSummary,
} from "./types.js";

const STDERR_TAIL_BYTES = 8 * 1024;
const QUICK_RETRY_ATTEMPTS = 5;
const QUICK_RETRY_DELAY_MS = 100;
const STEADY_RETRY_DELAY_MS = 1000;

export interface McpServerSettingStore {
  disabledServers(): string[];
  setEnabled(name: string, enabled: boolean): void;
}

interface ServerState {
  readonly name: string;
  readonly config: McpServerConfig;
  enabled: boolean;
  client: Client | null;
  tools: Tool[] | null;
  status: "idle" | "connected" | "error";
  error: string | null;
  lastErrorMessage: string | null;
  errorRepeatCount: number;
  consecutiveFailures: number;
  retryTimer: ReturnType<typeof setTimeout> | null;
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
    private readonly store: McpServerSettingStore,
  ) {
    const disabled = new Set(store.disabledServers());
    for (const [name, serverConfig] of Object.entries(config.servers))
      this.servers.set(name, {
        name,
        config: serverConfig,
        enabled: !disabled.has(name),
        client: null,
        tools: null,
        status: "idle",
        error: null,
        lastErrorMessage: null,
        errorRepeatCount: 0,
        consecutiveFailures: 0,
        retryTimer: null,
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
    if (!state.enabled)
      throw new ChatRoomError(
        "FORBIDDEN",
        `MCP server "${state.name}" is disabled`,
      );
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

  async setEnabled(name: string, enabled: boolean): Promise<McpServerSummary> {
    const state = this.require(name);
    if (state.enabled !== enabled) {
      this.store.setEnabled(name, enabled);
      state.enabled = enabled;
      if (!enabled) {
        await state.connecting?.catch(() => undefined);
        await this.close(state);
      } else {
        state.status = "idle";
        state.stderrTail = "";
        this.resetRetryState(state);
      }
      this.events.emit({ type: "mcp-servers" });
    }
    return await this.describe(state, true);
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
    if (!state.enabled)
      return {
        name: state.name,
        type: state.config.type,
        target: describeTarget(state.config),
        enabled: false,
        status: "disabled",
        error: null,
        toolCount: 0,
        tools: [],
      };

    if (state.status === "idle") {
      try {
        await this.ensureConnected(state);
      } catch {
        // The per-server failure is reported inline instead of failing the call.
      }
    }
    const tools = state.tools ?? [];
    return {
      name: state.name,
      type: state.config.type,
      target: describeTarget(state.config),
      enabled: true,
      status: state.client ? "connected" : "error",
      error: state.error,
      toolCount: tools.length,
      tools: tools.map((tool) => summarizeTool(tool, includeSchemas)),
    };
  }

  private async ensureConnected(
    state: ServerState,
    bypassRetryDelay = false,
  ): Promise<void> {
    if (!state.enabled)
      throw new ChatRoomError(
        "FORBIDDEN",
        `MCP server "${state.name}" is disabled`,
      );
    if (state.client) return;
    if (!bypassRetryDelay && state.retryTimer)
      throw this.unavailable(state, null);
    if (state.connecting) return await state.connecting;
    const pending = this.connect(state).finally(() => {
      state.connecting = null;
      if (!state.client && state.status === "error") this.scheduleRetry(state);
    });
    state.connecting = pending;
    return await pending;
  }

  private async connect(state: ServerState): Promise<void> {
    // Diagnostics belong to the current connection attempt; do not append the
    // same startup stderr forever across retries.
    state.stderrTail = "";
    const transport = this.createTransport(state);
    const client = new Client(
      { name: CHATROOM_NAME, version: CHATROOM_VERSION },
      { capabilities: {} },
    );
    try {
      await client.connect(transport, { timeout: this.config.callTimeoutMs });
      const tools = await this.listTools(client);
      state.client = client;
      state.tools = tools;
      state.status = "connected";
      state.stderrTail = "";
      this.resetRetryState(state);
      client.onerror = (error) =>
        this.invalidate(state, describeError(error), client);
      client.onclose = () =>
        this.invalidate(state, "Connection closed", client);
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

  private invalidate(
    state: ServerState,
    reason: string | null,
    sourceClient?: Client,
  ): void {
    if (state.closing || !state.enabled) return;
    if (sourceClient && state.client !== sourceClient) return;

    const failedClient = sourceClient ?? state.client;
    state.client = null;
    void failedClient?.close().catch(() => undefined);
    state.tools = null;
    state.status = "error";
    state.consecutiveFailures += 1;
    this.recordError(state, reason ?? "Connection closed");
    this.events.emit({ type: "mcp-servers" });
    this.scheduleRetry(state);
  }

  private recordError(state: ServerState, message: string): void {
    if (state.lastErrorMessage === message) state.errorRepeatCount += 1;
    else {
      state.lastErrorMessage = message;
      state.errorRepeatCount = 1;
    }
    state.error =
      state.errorRepeatCount > 1
        ? `${message} x ${state.errorRepeatCount}`
        : message;
  }

  private scheduleRetry(state: ServerState): void {
    if (
      !state.enabled ||
      state.closing ||
      state.client ||
      state.connecting ||
      state.retryTimer
    )
      return;

    const delay =
      state.consecutiveFailures <= QUICK_RETRY_ATTEMPTS
        ? QUICK_RETRY_DELAY_MS
        : STEADY_RETRY_DELAY_MS;
    const timer = setTimeout(() => {
      state.retryTimer = null;
      if (!state.enabled || state.closing || state.client) return;
      void this.retryConnection(state);
    }, delay);
    timer.unref?.();
    state.retryTimer = timer;
  }

  private async retryConnection(state: ServerState): Promise<void> {
    await this.destroyDispatcher(state);
    await this.ensureConnected(state, true).catch(() => undefined);
  }

  private resetRetryState(state: ServerState): void {
    if (state.retryTimer) clearTimeout(state.retryTimer);
    state.retryTimer = null;
    state.consecutiveFailures = 0;
    state.lastErrorMessage = null;
    state.errorRepeatCount = 0;
    state.error = null;
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
    state.stderrTail = "";
    this.resetRetryState(state);
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
