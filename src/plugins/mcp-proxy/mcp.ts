import type { CallToolResult } from "@modelcontextprotocol/server";
import type { PluginMcpRegistrar } from "../../mcp/server/plugin-mcp-registrar.js";
import {
  closedRead,
  openWorldMutation,
} from "../../mcp/server/tool-support.js";
import type { McpProxyService } from "./mcp-proxy-service.js";
import {
  mcpCallInputSchema,
  mcpCallOutputSchema,
  mcpListServersInputSchema,
  mcpListServersOutputSchema,
  mcpRefreshInputSchema,
  mcpRefreshOutputSchema,
  type McpCallOutput,
} from "./types.js";

export function registerMcpProxyTools(
  mcp: PluginMcpRegistrar,
  proxy: McpProxyService,
): void {
  mcp.registerTool(
    "mcp_list_servers",
    {
      title: "List proxied MCP servers",
      description:
        'Discover the MCP servers this device is configured to proxy and the tools each one exposes. Call this before mcp_call to learn server names and tool input schemas. Servers connect lazily on first use; a server that cannot be reached is reported with status "error" and its reason instead of failing the whole call.',
      inputSchema: mcpListServersInputSchema,
      outputSchema: mcpListServersOutputSchema,
      annotations: closedRead,
      action: "list",
    },
    (input) => proxy.list(input),
  );

  mcp.registerTool(
    "mcp_call",
    {
      title: "Call a proxied MCP tool",
      description:
        "Invoke a tool on a configured MCP server through ChatRoom. Discover server and tool names with mcp_list_servers first, then pass arguments matching that tool's input schema. Text, image, and other content blocks are forwarded as the remote server returned them. A tool that reports a problem comes back with isError true and the server's own error content; an unreachable server or a timeout surfaces as a tool error instead.",
      inputSchema: mcpCallInputSchema,
      outputSchema: mcpCallOutputSchema,
      annotations: openWorldMutation,
      action: "call",
      present: (output) => presentCall(output as McpCallOutput),
    },
    async (input, execution) => ({
      ...(await proxy.call(input)),
      operationId: execution.operationId,
    }),
  );

  mcp.registerTool(
    "mcp_refresh",
    {
      title: "Refresh proxied MCP servers",
      description:
        "Reconnect to one or all configured MCP servers and re-read their tool lists. Use this after a server's tools change or when a previous connection failed, to retry discovery.",
      inputSchema: mcpRefreshInputSchema,
      outputSchema: mcpRefreshOutputSchema,
      annotations: openWorldMutation,
      action: "refresh",
    },
    (input) => proxy.refresh(input.server),
  );
}

function presentCall(output: McpCallOutput): CallToolResult {
  return {
    content: output.content as unknown as CallToolResult["content"],
    structuredContent: output as unknown as Record<string, unknown>,
    ...(output.isError ? { isError: true } : {}),
  };
}
