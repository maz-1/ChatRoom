import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { createTestRuntime } from "../helpers/runtime.js";

const STDIO_FIXTURE = fileURLToPath(
  new URL("../fixtures/mcp-stdio-server.mjs", import.meta.url),
);

test("WebUI MCP server view is read-only, reports status, and hides secrets", async () => {
  const runtime = await createTestRuntime({
    configure(config) {
      config.mcp.callTimeoutMs = 15_000;
      config.mcp.servers = {
        local: {
          type: "stdio",
          command: process.execPath,
          args: [STDIO_FIXTURE],
          env: { FIXTURE_API_KEY: "super-secret-value" },
          cwd: null,
        },
        broken: {
          type: "http",
          url: "http://127.0.0.1:1/mcp",
          headers: { authorization: "Bearer another-secret" },
          proxy: null,
        },
      };
    },
  });
  try {
    await runtime.components.http.start();
    const address = runtime.components.http.address();
    assert.ok(address);
    const base = `http://127.0.0.1:${address.port}`;

    const response = await fetch(`${base}/api/mcp/servers`);
    assert.equal(response.status, 200);
    const raw = await response.text();
    assert.ok(
      !raw.includes("super-secret-value"),
      "configured env values must never reach the browser",
    );
    assert.ok(
      !raw.includes("another-secret"),
      "configured headers must never reach the browser",
    );

    const payload = JSON.parse(raw) as {
      configPath: string;
      servers: {
        name: string;
        type: string;
        target: string;
        status: string;
        error: string | null;
        toolCount: number;
        tools: { name: string; inputSchema: unknown }[];
        stderrTail: string;
      }[];
    };
    assert.ok(payload.configPath.endsWith("config.json"));
    const byName = new Map(payload.servers.map((item) => [item.name, item]));
    const local = byName.get("local");
    const broken = byName.get("broken");
    assert.ok(local && broken);
    assert.equal(local.status, "connected", local.error ?? "");
    assert.equal(local.type, "stdio");
    assert.deepEqual(local.tools.map((tool) => tool.name).sort(), [
      "echo",
      "fail",
      "flood",
      "palette",
    ]);
    assert.ok(
      local.tools.find((tool) => tool.name === "echo")?.inputSchema,
      "tool schemas should be available to the view",
    );
    assert.equal(typeof local.stderrTail, "string");

    assert.equal(broken.status, "error");
    assert.ok(broken.error, "an unreachable server reports its reason");

    const scoped = await fetch(`${base}/api/mcp/servers?server=local`);
    const scopedPayload = (await scoped.json()) as {
      servers: { name: string }[];
    };
    assert.deepEqual(
      scopedPayload.servers.map((item) => item.name),
      ["local"],
    );

    const unknown = await fetch(`${base}/api/mcp/servers?server=nope`);
    assert.equal(unknown.status, 404);

    const refreshed = await fetch(`${base}/api/mcp/servers/refresh`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ server: "local" }),
    });
    assert.equal(refreshed.status, 200);
    const refreshedPayload = (await refreshed.json()) as {
      servers: { name: string; status: string }[];
    };
    assert.deepEqual(
      refreshedPayload.servers.map((item) => `${item.name}:${item.status}`),
      ["local:connected"],
    );

    const operations = runtime.components.operations.list({
      pluginId: "mcp-proxy",
    });
    assert.equal(operations[0]?.source, "gui");
    assert.equal(operations[0]?.action, "refresh");
    assert.equal(operations[0]?.status, "success");

    // There is no mutation surface: definition changes are out of scope.
    for (const method of ["POST", "PATCH", "DELETE"]) {
      const attempt = await fetch(`${base}/api/mcp/servers`, { method });
      assert.notEqual(
        attempt.status,
        200,
        `${method} /api/mcp/servers must not be a working mutation route`,
      );
    }
  } finally {
    await runtime.cleanup();
  }
});
