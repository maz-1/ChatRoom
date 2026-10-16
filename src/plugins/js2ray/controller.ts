import { readFile } from "node:fs/promises";
import path from "node:path";
import type { Duplex } from "node:stream";
import type { Dispatcher } from "undici";
import { createRuntime, type Js2rayError, type Runtime } from "#js2ray";
import type { Js2rayConfig } from "#config/js2ray-config";
import { ChatRoomError } from "#core/errors/chatroom-error";
import type { LogWriter } from "#core/logging/types";
import { prepareRuntimeConfig } from "./runtime-config.js";
import type { Js2rayStatus } from "./types.js";
import { logJs2rayDiagnostic, safeIdentifier } from "./diagnostics.js";
import { createHttpDispatcher } from "./http-dispatcher.js";

export class Js2rayController {
  private runtime: Runtime | null = null;
  private starting: Promise<void> | null = null;
  private stopping: Promise<void> | null = null;
  private reverseHandler: (() => Duplex) | null = null;
  private lastError: { code: string; phase: string } | null = null;

  constructor(
    private readonly settings: Js2rayConfig,
    private readonly logs: LogWriter,
  ) {}

  setReverseHandler(handler: () => Duplex): void {
    this.reverseHandler = handler;
  }

  status(): Js2rayStatus {
    return {
      enabled: this.settings.enabled,
      state: !this.settings.enabled
        ? "disabled"
        : (this.runtime?.state ?? (this.lastError ? "error" : "idle")),
      listeners: this.runtime?.listenersInfo.length ?? 0,
      reverse: this.runtime?.stats.reverse ?? [],
      lastError: this.lastError,
    };
  }

  async start(): Promise<void> {
    if (this.stopping) await this.stopping;
    if (!this.settings.enabled) return;
    if (this.starting) return this.starting;
    if (this.runtime?.state === "running") return;
    const starting = this.startInternal();
    this.starting = starting;
    try {
      await starting;
    } finally {
      this.starting = null;
    }
  }

  private async startInternal(): Promise<void> {
    try {
      if (!this.settings.configPath)
        throw new ChatRoomError(
          "INVALID_INPUT",
          "js2ray.configPath is required",
        );
      if (this.settings.reverse.enabled && !this.reverseHandler)
        throw new ChatRoomError(
          "INVALID_INPUT",
          "The js2ray reverse ingress is not initialized",
        );
      const raw: unknown = JSON.parse(
        await readFile(this.settings.configPath, "utf8"),
      );
      const config = prepareRuntimeConfig(
        raw,
        this.settings,
        path.dirname(this.settings.configPath),
      );
      this.runtime = createRuntime(config, {
        baseDir: path.dirname(this.settings.configPath),
        // Serialized logger records omit the cause; use diagnostics below.
        logger: () => {},
        services: {
          chatroom: () => {
            if (!this.reverseHandler)
              throw new Error("Reverse ingress unavailable");
            return this.reverseHandler();
          },
        },
      });
      const runtime = this.runtime;
      runtime.on("diagnostic", (error: Js2rayError) =>
        logJs2rayDiagnostic(
          this.logs,
          error,
          !!this.stopping ||
            runtime.state === "closing" ||
            runtime.state === "closed",
        ),
      );
      this.runtime.on("reverse", (event: { state: string }) =>
        this.logs.info(
          "js2ray",
          "js2ray.reverse",
          "js2ray reverse connection changed",
          { state: event.state },
        ),
      );
      await this.runtime.start();
      this.lastError = null;
      this.logs.info("js2ray", "js2ray.started", "js2ray runtime started", {
        reverse: this.settings.reverse.enabled,
        listeners: this.runtime.listenersInfo.length,
      });
    } catch (error) {
      await this.runtime?.close().catch(() => undefined);
      this.runtime = null;
      const failure = error as { code?: unknown; phase?: unknown };
      // Never expose raw config, exception messages, causes or node credentials.
      this.lastError = {
        code: safeIdentifier(failure?.code, "START_FAILED"),
        phase: safeIdentifier(failure?.phase, "startup"),
      };
      this.logs.error(
        "js2ray",
        "js2ray.start_failed",
        "js2ray runtime could not start",
        this.lastError,
      );
      if (error instanceof ChatRoomError) throw error;
      throw new ChatRoomError(
        "INVALID_INPUT",
        "Cannot start js2ray; check its runtime configuration and resources",
        this.lastError,
      );
    }
  }

  createDispatcher(timeoutMs: number): Dispatcher {
    if (
      !this.settings.enabled ||
      this.runtime?.state !== "running" ||
      this.stopping
    )
      throw new ChatRoomError(
        "PROCESS_FAILED",
        "The js2ray runtime is not running",
      );
    return createHttpDispatcher(
      this.runtime,
      timeoutMs,
      this.settings.outboundTag,
    );
  }

  async stop(): Promise<void> {
    if (this.stopping) return this.stopping;
    const stopping = this.stopInternal();
    this.stopping = stopping;
    try {
      await stopping;
    } finally {
      this.stopping = null;
    }
  }

  private async stopInternal(): Promise<void> {
    await this.starting?.catch(() => undefined);
    const runtime = this.runtime;
    this.runtime = null;
    await runtime?.close({ drainTimeoutMs: 1000 });
  }
}
