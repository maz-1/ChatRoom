import { chmod, mkdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { z } from "zod";
import { ChatRoomError } from "#core/errors/chatroom-error";
import {
  generateOwnerToken,
  systemOwnerTokenStore,
  type OwnerTokenStore,
} from "#infrastructure/security/owner-token-store";
import { platformPaths } from "./platform-paths.js";
import type { ChatRoomConfig } from "./types.js";

const MCP_SERVER_NAME_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

function rawConfigSchema() {
  const defaults = defaultConfig();
  return z
    .object({
      allowedRoots: z
        .array(z.string().min(1))
        .min(1)
        .default(defaults.allowedRoots),
      dataDir: z.string().min(1).default(defaults.dataDir),
      databasePath: z.string().min(1).optional(),
      server: z
        .object({
          host: z.string().min(1).default(defaults.server.host),
          port: z
            .number()
            .int()
            .min(1)
            .max(65535)
            .default(defaults.server.port),
        })
        .strict()
        .default(defaults.server),
      auth: z
        .object({
          localWebAuth: z.boolean().default(defaults.auth.localWebAuth),
          // Legacy only: accepted so existing installations can migrate the secret to the system keychain.
          ownerToken: z.string().min(1).nullable().optional(),
          mcpPublicBaseUrl: z
            .string()
            .url()
            .nullable()
            .default(defaults.auth.mcpPublicBaseUrl),
          webPublicBaseUrl: z
            .string()
            .url()
            .nullable()
            .default(defaults.auth.webPublicBaseUrl),
          allowedRedirectHosts: z
            .array(z.string().min(1))
            .default(defaults.auth.allowedRedirectHosts),
        })
        .strict()
        .default(defaults.auth),
      http: z
        .object({
          defaultTimeoutMs: z
            .number()
            .int()
            .min(1000)
            .max(24 * 60 * 60 * 1000)
            .default(defaults.http.defaultTimeoutMs),
          maxTimeoutMs: z
            .number()
            .int()
            .min(1000)
            .max(24 * 60 * 60 * 1000)
            .default(defaults.http.maxTimeoutMs),
          maxResponseBytes: z
            .number()
            .int()
            .min(4096)
            .max(16 * 1024 * 1024)
            .default(defaults.http.maxResponseBytes),
        })
        .strict()
        .default(defaults.http),
      operations: z
        .object({
          maxPayloadBytes: z
            .number()
            .int()
            .min(4096)
            .max(16 * 1024 * 1024)
            .default(defaults.operations.maxPayloadBytes),
        })
        .strict()
        .default(defaults.operations),
      mcp: z
        .object({
          callTimeoutMs: z
            .number()
            .int()
            .min(1000)
            .max(24 * 60 * 60 * 1000)
            .default(defaults.mcp.callTimeoutMs),
          maxResultBytes: z
            .number()
            .int()
            .min(4096)
            .max(64 * 1024 * 1024)
            .default(defaults.mcp.maxResultBytes),
          servers: z
            .record(
              z.string().regex(MCP_SERVER_NAME_PATTERN),
              z.discriminatedUnion("type", [
                z
                  .object({
                    type: z.literal("stdio"),
                    command: z.string().min(1),
                    args: z.array(z.string()).default([]),
                    env: z.record(z.string(), z.string()).default({}),
                    cwd: z.string().min(1).nullable().default(null),
                  })
                  .strict(),
                z
                  .object({
                    type: z.literal("http"),
                    url: z.string().url(),
                    headers: z.record(z.string(), z.string()).default({}),
                    proxy: z.string().min(1).nullable().default(null),
                  })
                  .strict(),
              ]),
            )
            .default({}),
        })
        .strict()
        .default(defaults.mcp),
      process: z
        .object({
          maxOutputBytes: z
            .number()
            .int()
            .min(4096)
            .max(64 * 1024 * 1024)
            .default(defaults.process.maxOutputBytes),
          defaultTimeoutMs: z
            .number()
            .int()
            .min(1000)
            .max(24 * 60 * 60 * 1000)
            .default(defaults.process.defaultTimeoutMs),
          maxCompletedProcesses: z
            .number()
            .int()
            .min(0)
            .max(10_000)
            .default(defaults.process.maxCompletedProcesses),
        })
        .strict()
        .default(defaults.process),
    })
    .strict();
}

export function defaultConfigPath(): string {
  return process.env.CHATROOM_CONFIG ?? platformPaths().configFile;
}

export function defaultConfig(): ChatRoomConfig {
  const paths = platformPaths();
  return {
    allowedRoots: [path.join(os.homedir(), "Projects")],
    dataDir: paths.dataDir,
    databasePath: paths.databaseFile,
    server: { host: "127.0.0.1", port: 8765 },
    auth: {
      localWebAuth: false,
      mcpPublicBaseUrl: null,
      webPublicBaseUrl: null,
      allowedRedirectHosts: ["chatgpt.com", "localhost", "127.0.0.1"],
    },
    http: {
      defaultTimeoutMs: 30_000,
      maxTimeoutMs: 120_000,
      maxResponseBytes: 1024 * 1024,
    },
    operations: { maxPayloadBytes: 512 * 1024 },
    mcp: {
      callTimeoutMs: 60_000,
      maxResultBytes: 1024 * 1024,
      servers: {},
    },
    process: {
      maxOutputBytes: 512 * 1024,
      defaultTimeoutMs: 30 * 60 * 1000,
      maxCompletedProcesses: 200,
    },
  };
}

interface ValidatedConfig {
  config: ChatRoomConfig;
  legacyOwnerToken: string | null;
  hasLegacyOwnerToken: boolean;
}

export interface RuntimeConfig {
  config: ChatRoomConfig;
  ownerToken: string | null;
}

export async function loadConfig(
  configPath = defaultConfigPath(),
): Promise<ChatRoomConfig> {
  const parsed = await readConfigDocument(configPath);
  const { config } = validateConfig(parsed);
  validateRuntimeSecurity(config);
  return config;
}

export async function loadRuntimeConfig(
  configPath = defaultConfigPath(),
  tokenStore: OwnerTokenStore = systemOwnerTokenStore,
): Promise<RuntimeConfig> {
  const parsed = await readConfigDocument(configPath);
  const validated = validateConfig(parsed);
  validateRuntimeSecurity(validated.config);
  const tokenRequired = authenticationUsesOwnerToken(validated.config);

  if (validated.hasLegacyOwnerToken) {
    try {
      await migrateLegacyOwnerToken(
        configPath,
        parsed,
        validated.legacyOwnerToken,
        tokenStore,
      );
    } catch (error) {
      if (
        tokenRequired ||
        !(error instanceof ChatRoomError && error.code === "INTERNAL")
      )
        throw error;
      // A purely local, unauthenticated runtime can continue if the OS secret
      // service is temporarily unavailable. The legacy value stays untouched
      // and will be migrated on a later successful start.
      return { config: validated.config, ownerToken: null };
    }
  }

  let ownerToken: string | null = null;
  try {
    ownerToken = await tokenStore.get(configPath);
  } catch (error) {
    if (tokenRequired) throw error;
  }
  validateOwnerTokenAvailability(validated.config, ownerToken);
  return { config: validated.config, ownerToken };
}

export async function initializeConfig(
  tokenStore: OwnerTokenStore = systemOwnerTokenStore,
): Promise<{
  config: ChatRoomConfig;
  configPath: string;
}> {
  const configPath = defaultConfigPath();
  try {
    await readFile(configPath, "utf8");
    throw new ChatRoomError("CONFLICT", `Config already exists: ${configPath}`);
  } catch (error) {
    if (error instanceof ChatRoomError) throw error;
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
  }

  const config = defaultConfig();
  await mkdir(config.allowedRoots[0]!, { recursive: true, mode: 0o700 });
  await mkdir(path.dirname(configPath), { recursive: true, mode: 0o700 });

  const ownerToken = generateOwnerToken();
  await tokenStore.set(configPath, ownerToken);
  const verified = await tokenStore.get(configPath);
  if (verified !== ownerToken)
    throw new ChatRoomError(
      "INTERNAL",
      "Owner token could not be verified after writing it to the system keychain",
    );

  await writeConfigDocument(configPath, config);
  return { config, configPath };
}

async function readConfigDocument(configPath: string): Promise<unknown> {
  try {
    return JSON.parse(await readFile(configPath, "utf8")) as unknown;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT")
      throw new ChatRoomError(
        "NOT_FOUND",
        `ChatRoom is not initialized: ${configPath}`,
      );
    throw new ChatRoomError(
      "INVALID_INPUT",
      `Cannot read config: ${configPath}`,
      undefined,
      { cause: error },
    );
  }
}

function validateConfig(value: unknown): ValidatedConfig {
  assertSupportedMcpTransports(value);
  const result = rawConfigSchema().safeParse(value);
  if (!result.success)
    throw new ChatRoomError("INVALID_INPUT", "Invalid ChatRoom configuration", {
      issues: result.error.issues.map((issue) => ({
        path: issue.path.join("."),
        message: issue.message,
      })),
    });

  const dataDir = path.resolve(result.data.dataDir);
  const { ownerToken: legacyOwnerToken, ...auth } = result.data.auth;
  return {
    config: {
      ...result.data,
      auth,
      allowedRoots: result.data.allowedRoots.map((root) => path.resolve(root)),
      dataDir,
      databasePath: path.resolve(
        result.data.databasePath ?? path.join(dataDir, "chatroom.sqlite"),
      ),
    },
    legacyOwnerToken: legacyOwnerToken ?? null,
    hasLegacyOwnerToken: Object.prototype.hasOwnProperty.call(
      result.data.auth,
      "ownerToken",
    ),
  };
}

async function migrateLegacyOwnerToken(
  configPath: string,
  parsed: unknown,
  legacyOwnerToken: string | null,
  tokenStore: OwnerTokenStore,
): Promise<void> {
  if (legacyOwnerToken) {
    const stored = await tokenStore.get(configPath);
    if (stored && stored !== legacyOwnerToken)
      throw new ChatRoomError(
        "CONFLICT",
        "The owner token in config.json differs from the owner token already stored in the system keychain; refusing to overwrite either value",
      );
    if (!stored) {
      await tokenStore.set(configPath, legacyOwnerToken);
      const verified = await tokenStore.get(configPath);
      if (verified !== legacyOwnerToken)
        throw new ChatRoomError(
          "INTERNAL",
          "Owner token migration could not be verified in the system keychain",
        );
    }
  }

  const sanitized = removeLegacyOwnerToken(parsed);
  await writeConfigDocument(configPath, sanitized);
}

function removeLegacyOwnerToken(value: unknown): unknown {
  if (!value || typeof value !== "object" || Array.isArray(value)) return value;
  const root = structuredClone(value as Record<string, unknown>);
  const auth = root.auth;
  if (auth && typeof auth === "object" && !Array.isArray(auth))
    delete (auth as Record<string, unknown>).ownerToken;
  return root;
}

async function writeConfigDocument(
  configPath: string,
  value: unknown,
): Promise<void> {
  const temporary = `${configPath}.${process.pid}.${Date.now()}.tmp`;
  await writeFile(temporary, `${JSON.stringify(value, null, 2)}\n`, { mode: 0o600 });
  await chmod(temporary, 0o600).catch(() => undefined);
  try {
    await rename(temporary, configPath);
  } finally {
    await rm(temporary, { force: true }).catch(() => undefined);
  }
  await chmod(configPath, 0o600).catch(() => undefined);
}

function assertSupportedMcpTransports(value: unknown): void {
  const servers = serverEntries(value);
  for (const [name, entry] of Object.entries(servers)) {
    if (
      entry &&
      typeof entry === "object" &&
      (entry as { type?: unknown }).type === "sse"
    )
      throw new ChatRoomError(
        "UNSUPPORTED",
        `MCP server "${name}" uses the legacy "sse" transport, which ChatRoom does not support yet; configure it as "http" (Streamable HTTP) instead`,
      );
  }
}

function serverEntries(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object") return {};
  const servers = (value as { mcp?: unknown }).mcp;
  if (!servers || typeof servers !== "object") return {};
  const entries = (servers as { servers?: unknown }).servers;
  if (!entries || typeof entries !== "object" || Array.isArray(entries))
    return {};
  return entries as Record<string, unknown>;
}

function validateRuntimeSecurity(config: ChatRoomConfig): void {
  validatePublicBaseUrl(config.auth.mcpPublicBaseUrl, "auth.mcpPublicBaseUrl");
  validatePublicBaseUrl(config.auth.webPublicBaseUrl, "auth.webPublicBaseUrl");

  const loopback =
    config.server.host === "127.0.0.1" ||
    config.server.host === "::1" ||
    config.server.host === "localhost";
  if (!loopback && !config.auth.localWebAuth)
    throw new ChatRoomError(
      "FORBIDDEN",
      "auth.localWebAuth must be enabled when ChatRoom binds beyond loopback",
    );
}

function authenticationUsesOwnerToken(config: ChatRoomConfig): boolean {
  return (
    config.auth.localWebAuth ||
    Boolean(config.auth.mcpPublicBaseUrl) ||
    Boolean(config.auth.webPublicBaseUrl)
  );
}

function validateOwnerTokenAvailability(
  config: ChatRoomConfig,
  ownerToken: string | null,
): void {
  if (authenticationUsesOwnerToken(config) && !ownerToken)
    throw new ChatRoomError(
      "INVALID_INPUT",
      "An owner token is required in the system keychain when any authenticated ingress is configured",
    );
}

function validatePublicBaseUrl(value: string | null, name: string): void {
  if (!value) return;
  const url = new URL(value);
  if (url.protocol !== "http:" && url.protocol !== "https:")
    throw new ChatRoomError("INVALID_INPUT", name + " must use HTTP or HTTPS");
  if (
    url.username ||
    url.password ||
    url.pathname !== "/" ||
    url.search ||
    url.hash
  )
    throw new ChatRoomError(
      "INVALID_INPUT",
      name + " must be an origin without credentials, path, query, or fragment",
    );
}
