import assert from "node:assert/strict";
import { setImmediate } from "node:timers/promises";
import test from "node:test";
import { RuntimeEventBus } from "../../src/app/event-bus.js";
import { McpProxyService } from "../../src/plugins/mcp-proxy/mcp-proxy-service.js";

test("MCP failures aggregate across five quick retries and subsequent one-second retries", async (context) => {
  context.mock.timers.enable({ apis: ["setTimeout"] });
  const events = new RuntimeEventBus();
  let changes = 0;
  events.subscribe(() => changes++);
  const service = new McpProxyService(
    {
      callTimeoutMs: 15_000,
      maxResultBytes: 1024,
      servers: {
        unreachable: {
          type: "http",
          // Fetch rejects this forbidden port immediately without network I/O.
          url: "http://127.0.0.1:1/mcp",
          headers: {},
          proxy: null,
        },
      },
    },
    events,
    { disabledServers: () => [], setEnabled: () => {} },
  );
  const failureCount = async () => {
    const { servers } = await service.list({ includeSchemas: false });
    assert.equal(servers[0]?.status, "error");
    const error = servers[0]?.error;
    assert.ok(error);
    const match = / x (\d+)$/.exec(error);
    return match ? Number(match[1]) : 1;
  };
  try {
    assert.equal(await failureCount(), 1);
    for (let retry = 1; retry <= 5; retry++) {
      context.mock.timers.tick(99);
      assert.equal(await failureCount(), retry);
      context.mock.timers.tick(1);
      // Drain the asynchronous SDK connection failure before advancing time.
      await setImmediate();
      assert.equal(await failureCount(), retry + 1);
    }
    for (let retry = 6; retry <= 7; retry++) {
      context.mock.timers.tick(999);
      assert.equal(await failureCount(), retry);
      context.mock.timers.tick(1);
      await setImmediate();
      assert.equal(await failureCount(), retry + 1);
    }
    await service.shutdown();
    const changesBeforeShutdown = changes;
    context.mock.timers.tick(10_000);
    await setImmediate();
    assert.equal(
      changes,
      changesBeforeShutdown,
      "shutdown cancels pending retries",
    );
  } finally {
    await service.shutdown();
    context.mock.timers.reset();
  }
});
