import assert from "node:assert/strict";
import test from "node:test";
import type {
  CallToolResult,
  McpServer,
  RegisteredTool,
} from "@modelcontextprotocol/server";
import { z } from "zod";
import type { OperationLog } from "../../src/operations/operation-log.js";
import { PluginMcpRegistrar } from "../../src/mcp/server/plugin-mcp-registrar.js";
import { McpToolControl } from "../../src/mcp/server/tool-control.js";
import { runWithMcpAccessScope } from "../../src/mcp/server/request-context.js";

test("disabled OAuth client keeps tools listed but tool execution is rejected", async () => {
  let callback:
    ((input: Record<string, never>) => Promise<CallToolResult>) | null = null;

  const registeredState = { enabled: true };
  const registeredTool = {
    get enabled() {
      return registeredState.enabled;
    },
    enable() {
      registeredState.enabled = true;
    },
    disable() {
      registeredState.enabled = false;
    },
  } as unknown as RegisteredTool;

  const server = {
    registerTool(_name: unknown, _config: unknown, value: unknown) {
      callback = value as (
        input: Record<string, never>,
      ) => Promise<CallToolResult>;
      return registeredTool;
    },
  } as unknown as McpServer;

  const toolControl = new McpToolControl({
    disabledTools: () => [],
    setEnabled: () => undefined,
  });
  const registrar = new PluginMcpRegistrar(
    server,
    {} as OperationLog,
    "test-plugin",
    toolControl,
  );

  registrar.registerTool(
    "demo_tool",
    {
      title: "Demo tool",
      description: "Demo",
      inputSchema: z.object({}),
      outputSchema: z.object({ ok: z.boolean() }),
      annotations: {},
      action: "demo",
    },
    async () => ({ ok: true }),
  );

  assert.deepEqual(toolControl.list(), [
    {
      name: "demo_tool",
      pluginId: "test-plugin",
      title: "Demo tool",
      description: "Demo",
      enabled: true,
    },
  ]);
  assert.ok(callback, "tool callback should be registered");

  const result = await runWithMcpAccessScope("remote", () => callback!({}), {
    clientId: "client-disabled",
    disabled: true,
  });

  assert.equal(result.isError, true);
  const payload = JSON.parse(
    result.content[0]?.type === "text" ? result.content[0].text : "{}",
  ) as { code?: string; message?: string };
  assert.equal(payload.code, "FORBIDDEN");
  assert.match(payload.message ?? "", /client-disabled/);

  assert.equal(
    toolControl.list()[0]?.enabled,
    true,
    "client disablement must not hide or disable tools in tools/list",
  );
});
