import { z } from "zod";

const workspaceRootSchema = z
  .string()
  .min(1)
  .optional()
  .describe(
    "Workspace root from workspace_list. Pass it for workspace operations to apply .codex/config.toml MCP restrictions; omit for global access.",
  );

const toolSummarySchema = z.object({
  name: z.string(),
  title: z.string().nullable(),
  description: z.string().nullable(),
  inputSchema: z.record(z.string(), z.unknown()).nullable(),
});

const serverSummarySchema = z.object({
  name: z.string(),
  type: z.enum(["stdio", "http"]),
  target: z.string(),
  enabled: z.boolean(),
  status: z.enum(["connected", "disabled", "error"]),
  error: z.string().nullable(),
  toolCount: z.number().int().nonnegative(),
  tools: z.array(toolSummarySchema),
});

export const mcpListServersInputSchema = z.object({
  root: workspaceRootSchema,
  server: z
    .string()
    .min(1)
    .optional()
    .describe("Optional server name; omit to report every configured server."),
  includeSchemas: z
    .boolean()
    .default(true)
    .describe(
      "Include each tool's JSON input schema so calls can be built correctly. Set false to save context.",
    ),
});

export const mcpListServersOutputSchema = z.object({
  servers: z.array(serverSummarySchema),
});

export const mcpCallInputSchema = z.object({
  root: workspaceRootSchema,
  server: z.string().min(1).describe("Configured server name."),
  tool: z
    .string()
    .min(1)
    .describe("Tool name as reported by mcp_list_servers."),
  arguments: z
    .record(z.string(), z.unknown())
    .optional()
    .describe(
      "Tool arguments matching the tool's input schema. Omit for tools without parameters.",
    ),
  timeoutMs: z
    .number()
    .int()
    .positive()
    .optional()
    .describe(
      "Optional per-call timeout. Overrides the workspace server's tool_timeout_sec; otherwise defaults to mcp.callTimeoutMs.",
    ),
});

export const mcpCallOutputSchema = z.object({
  server: z.string(),
  tool: z.string(),
  isError: z.boolean(),
  content: z.array(z.record(z.string(), z.unknown())),
  structuredContent: z.record(z.string(), z.unknown()).nullable(),
  truncated: z.boolean(),
  operationId: z.string(),
});

export const mcpRefreshInputSchema = z.object({
  root: workspaceRootSchema,
  server: z
    .string()
    .min(1)
    .optional()
    .describe(
      "Optional server name; omit to reconnect every configured server.",
    ),
});

export const mcpRefreshOutputSchema = mcpListServersOutputSchema;

export type McpToolSummary = z.infer<typeof toolSummarySchema>;
export type McpServerSummary = z.infer<typeof serverSummarySchema>;
export type McpCallInput = z.infer<typeof mcpCallInputSchema>;
export type McpCallOutput = z.infer<typeof mcpCallOutputSchema>;

/** WebUI projection of a configured server, including stdio diagnostics. */
export interface McpServerDetail extends McpServerSummary {
  stderrTail: string;
}

export interface WorkspaceMcpServerSetting {
  name: string;
  type: "stdio" | "http";
  enabled: boolean;
  globallyEnabled: boolean;
  effectiveEnabled: boolean;
  enabledTools: string[] | null;
  disabledTools: string[];
  toolTimeoutSec: number | null;
  effectiveToolTimeoutSec: number;
}

const toolNamesSchema = z.array(
  z
    .string()
    .min(1)
    .refine((name) => !!name.trim()),
);
export const workspaceMcpUpdateSchema = z
  .object({
    enabled: z.boolean().optional(),
    enabledTools: toolNamesSchema.nullable().optional(),
    disabledTools: toolNamesSchema.nullable().optional(),
    toolTimeoutSec: z
      .number()
      .min(0.001)
      .max(2_147_483.647)
      .nullable()
      .optional(),
  })
  .strict();

export interface WorkspaceMcpSettings {
  root: string;
  configPath: string;
  defaultToolTimeoutSec: number;
  servers: WorkspaceMcpServerSetting[];
}
