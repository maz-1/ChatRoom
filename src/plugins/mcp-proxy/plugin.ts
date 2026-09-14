import type { InternalPlugin } from "../types.js";
import { createServiceToken } from "../types.js";
import { McpProxyService } from "./mcp-proxy-service.js";
import { registerMcpProxyTools } from "./mcp.js";

export const McpProxyServiceToken =
  createServiceToken<McpProxyService>("mcp-proxy");

export function createMcpProxyPlugin(): InternalPlugin {
  let service: McpProxyService | null = null;

  return {
    id: "mcp-proxy",
    activate(context) {
      service = new McpProxyService(context.config.mcp);
      context.services.provide(McpProxyServiceToken, service);
    },
    registerMcp(mcp) {
      if (!service) throw new Error("MCP proxy plugin is not active");
      registerMcpProxyTools(mcp, service);
    },
    async deactivate() {
      await service?.shutdown();
      service = null;
    },
  };
}
