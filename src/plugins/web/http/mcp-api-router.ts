import { Router } from "express";
import type { Router as ExpressRouter } from "express";
import type { OperationLog } from "../../../operations/operation-log.js";
import { asyncRoute } from "../../../presentation/http/http-utils.js";
import type { McpProxyService } from "../../mcp-proxy/mcp-proxy-service.js";
import type { McpServerDetail } from "../../mcp-proxy/types.js";

export interface McpServersPayload {
  configPath: string;
  servers: McpServerDetail[];
}

/**
 * Read-only MCP server surface for the WebUI. Server definitions are owned by
 * the configuration file and edited outside ChatRoom, so this router exposes
 * discovery plus an explicit reconnect and nothing else.
 */
export function createMcpApiRouter(
  proxy: McpProxyService,
  operations: OperationLog,
  configPath: () => string,
): ExpressRouter {
  const router = Router();

  router.get(
    "/mcp/servers",
    asyncRoute(async (req, res) => {
      res.json(
        await present(proxy, configPath(), optionalString(req.query.server)),
      );
    }),
  );

  router.post(
    "/mcp/servers/refresh",
    asyncRoute(async (req, res) => {
      const server = optionalString(bodyRecord(req.body).server);
      await operations.run(
        {
          pluginId: "mcp-proxy",
          source: "gui",
          action: "refresh",
          input: { server: server ?? null },
        },
        () => proxy.refresh(server),
      );
      res.json(await present(proxy, configPath(), server));
    }),
  );

  return router;
}

async function present(
  proxy: McpProxyService,
  configPath: string,
  server: string | undefined,
): Promise<McpServersPayload> {
  const { servers } = await proxy.inspect(server);
  return { configPath, servers };
}

function bodyRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
}

function optionalString(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}
