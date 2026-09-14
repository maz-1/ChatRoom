import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { defaultConfig, loadConfig } from "../../src/config/load-config.js";
import { ChatRoomError } from "../../src/core/errors/chatroom-error.js";

async function loadWith(
  mutate: (config: Record<string, unknown>) => void,
): Promise<ReturnType<typeof defaultConfig>> {
  const dir = await mkdtemp(path.join(os.tmpdir(), "chatroom-mcp-config-"));
  const file = path.join(dir, "config.json");
  try {
    const raw = JSON.parse(JSON.stringify(defaultConfig())) as Record<
      string,
      unknown
    >;
    raw.allowedRoots = [dir];
    mutate(raw);
    await writeFile(file, JSON.stringify(raw));
    return await loadConfig(file);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
}

test("mcp config defaults to no proxied servers", async () => {
  const dir = await mkdtemp(path.join(os.tmpdir(), "chatroom-mcp-default-"));
  const file = path.join(dir, "config.json");
  try {
    const raw = JSON.parse(JSON.stringify(defaultConfig())) as Record<
      string,
      unknown
    >;
    delete raw.mcp;
    await writeFile(file, JSON.stringify(raw));
    const config = await loadConfig(file);
    assert.deepEqual(config.mcp.servers, {});
    assert.equal(config.mcp.callTimeoutMs, 60_000);
    assert.equal(config.mcp.maxResultBytes, 1024 * 1024);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("mcp config accepts stdio and http servers with defaults filled in", async () => {
  const config = await loadWith((raw) => {
    raw.mcp = {
      callTimeoutMs: 12_345,
      servers: {
        local: { type: "stdio", command: "node", args: ["server.mjs"] },
        remote: {
          type: "http",
          url: "https://mcp.example.com/mcp",
          headers: { authorization: "Bearer token" },
        },
      },
    };
  });
  assert.deepEqual(config.mcp.servers.local, {
    type: "stdio",
    command: "node",
    args: ["server.mjs"],
    env: {},
    cwd: null,
  });
  assert.deepEqual(config.mcp.servers.remote, {
    type: "http",
    url: "https://mcp.example.com/mcp",
    headers: { authorization: "Bearer token" },
    proxy: null,
  });
  assert.equal(config.mcp.callTimeoutMs, 12_345);
});

test("mcp config rejects the unsupported legacy sse transport with guidance", async () => {
  await assert.rejects(
    loadWith((raw) => {
      raw.mcp = {
        servers: { legacy: { type: "sse", url: "https://example.com/sse" } },
      };
    }),
    (error: unknown) => {
      assert.ok(error instanceof ChatRoomError);
      assert.equal(error.code, "UNSUPPORTED");
      assert.match(error.message, /"legacy"/);
      assert.match(error.message, /sse/);
      assert.match(error.message, /Streamable HTTP/);
      return true;
    },
  );
});

test("mcp config rejects malformed server entries", async () => {
  for (const servers of [
    { bad: { type: "http" } },
    { bad: { type: "stdio", command: "" } },
    { bad: { type: "http", url: "not-a-url" } },
    { "bad name!": { type: "stdio", command: "node" } },
    { bad: { type: "stdio", command: "node", unexpected: true } },
  ])
    await assert.rejects(
      loadWith((raw) => {
        raw.mcp = { servers };
      }),
      (error: unknown) => {
        assert.ok(error instanceof ChatRoomError);
        assert.equal(error.code, "INVALID_INPUT");
        return true;
      },
    );
});
