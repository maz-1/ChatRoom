import { Router } from "express";
import type { Router as ExpressRouter } from "express";
import { ChatRoomError } from "../../../core/errors/chatroom-error.js";
import type { OperationLog } from "../../../operations/operation-log.js";
import { asyncRoute } from "../../../presentation/http/http-utils.js";
import type { McpProxyService } from "../../mcp-proxy/mcp-proxy-service.js";
import type { McpServerDetail } from "../../mcp-proxy/types.js";

export interface McpServersPayload {
  configPath: string;
  servers: McpServerDetail[];
}

/**
 * MCP server surface for the WebUI. Server definitions remain owned by the
 * configuration file; only their local enabled/disabled state is mutable.
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

  router.patch(
    "/mcp/servers/:serverName",
    asyncRoute(async (req, res) => {
      const server = optionalString(req.params.serverName);
      if (!server)
        throw new ChatRoomError("INVALID_INPUT", "serverName is required");
      const body = bodyRecord(req.body);
      if (typeof body.enabled !== "boolean")
        throw new ChatRoomError("INVALID_INPUT", "enabled must be a boolean");
      await operations.run(
        {
          pluginId: "mcp-proxy",
          source: "gui",
          action: "set-enabled",
          input: { server, enabled: body.enabled },
        },
        () => proxy.setEnabled(server, body.enabled as boolean),
      );
      const { servers } = await proxy.inspect(server);
      res.json(servers[0]);
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
