import assert from "node:assert/strict";
import { access, mkdir, readFile, symlink, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import {
  Client,
  StreamableHTTPClientTransport,
} from "@modelcontextprotocol/client";
import type { WorkspaceMcpSettings } from "../../src/plugins/mcp-proxy/types.js";
import { parseCodexConfig } from "../../src/plugins/workspace/codex-config.js";
import { createTestRuntime } from "../helpers/runtime.js";

const STDIO_FIXTURE = fileURLToPath(
  new URL("../fixtures/mcp-stdio-server.mjs", import.meta.url),
);

async function fixture() {
  const runtime = await createTestRuntime({
    configure(config) {
      config.mcp.callTimeoutMs = 15_000;
      config.mcp.servers = Object.fromEntries(
        ["local", "second"].map((name) => [
          name,
          {
            type: "stdio" as const,
            command: process.execPath,
            args: [STDIO_FIXTURE],
            env: { API_KEY: "private-value" },
            cwd: null,
          },
        ]),
      );
    },
  });
  await runtime.components.http.start();
  const base = `http://127.0.0.1:${runtime.components.http.address()!.port}/api`;
  const configPath = path.join(runtime.workspaceRoot, ".codex/config.toml");
  const settings = (root = runtime.workspaceRoot) =>
    fetch(`${base}/workspace/mcp/servers?root=${encodeURIComponent(root)}`);
  const toggle = (
    server: string,
    enabled: unknown,
    root = runtime.workspaceRoot,
  ) =>
    fetch(`${base}/workspace/mcp/servers/${server}`, {
      method: "PATCH",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ root, enabled }),
    });
  const patch = (update: Record<string, unknown>, server = "local") =>
    fetch(`${base}/workspace/mcp/servers/${server}`, {
      method: "PATCH",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ root: runtime.workspaceRoot, ...update }),
    });
  return { ...runtime, base, configPath, settings, toggle, patch };
}

test("workspace tool filters constrain discovery and calls, with deny rules taking precedence", async () => {
  const runtime = await fixture();
  const client = new Client({ name: "tool-filter-test", version: "1.0.0" });
  try {
    await client.connect(
      new StreamableHTTPClientTransport(
        new URL(runtime.base.replace(/\/api$/, "/mcp")),
      ),
    );
    assert.equal(
      (
        await runtime.patch({
          enabledTools: ["echo", "palette", "fail", "missing"],
          disabledTools: ["fail"],
        })
      ).status,
      200,
    );
    for (const name of ["mcp_list_servers", "mcp_refresh"]) {
      const result = await client.callTool({
        name,
        arguments: { root: runtime.workspaceRoot, server: "local" },
      });
      assert.equal(result.isError, undefined);
      const { servers } = result.structuredContent as {
        servers: { toolCount: number; tools: { name: string }[] }[];
      };
      assert.equal(servers[0]?.toolCount, 2);
      assert.deepEqual(servers[0]?.tools.map((tool) => tool.name).sort(), [
        "echo",
        "palette",
      ]);
    }
    const denied = await client.callTool({
      name: "mcp_call",
      arguments: { root: runtime.workspaceRoot, server: "local", tool: "fail" },
    });
    assert.equal(denied.isError, true);
    assert.match(JSON.stringify(denied.content), /FORBIDDEN/);
    await assert.rejects(
      runtime.components.mcpProxy.call({
        root: runtime.workspaceRoot,
        server: "local",
        tool: "missing",
      }),
      (error: unknown) => {
        assert.ok(error instanceof Error && "details" in error);
        assert.deepEqual(
          (error.details as { availableTools: string[] }).availableTools.sort(),
          ["echo", "palette"],
        );
        return true;
      },
    );
    const result = await runtime.components.mcpProxy.call({
      root: runtime.workspaceRoot,
      server: "local",
      tool: "echo",
      arguments: { text: "allowed" },
    });
    assert.equal(result.content[0]?.text, "echo:allowed");
    assert.equal(
      (
        await runtime.components.mcpProxy.list({
          server: "local",
          includeSchemas: false,
        })
      ).servers[0]?.toolCount,
      4,
    );
    const discovery = await fetch(
      `${runtime.base}/workspace/mcp/servers/local/tools?root=${encodeURIComponent(runtime.workspaceRoot)}`,
    );
    assert.equal(discovery.status, 200);
    assert.equal(
      ((await discovery.json()) as { tools: unknown[] }).tools.length,
      4,
    );
    assert.equal((await runtime.patch({ enabledTools: [] })).status, 200);
    assert.equal(
      (
        await runtime.components.mcpProxy.list({
          root: runtime.workspaceRoot,
          server: "local",
          includeSchemas: false,
        })
      ).servers[0]?.toolCount,
      0,
    );
    await assert.rejects(
      runtime.components.mcpProxy.call({
        root: runtime.workspaceRoot,
        server: "local",
        tool: "echo",
      }),
      /disabled in this workspace/,
    );
    assert.equal(
      (await runtime.patch({ enabledTools: null, disabledTools: null })).status,
      200,
    );
    assert.equal(
      (
        await runtime.components.mcpProxy.list({
          root: runtime.workspaceRoot,
          server: "local",
          includeSchemas: false,
        })
      ).servers[0]?.toolCount,
      4,
    );
    const original = await readFile(runtime.configPath, "utf8");
    for (const update of [
      { enabledTools: "echo" },
      { disabledTools: [1] },
      { enabledTools: [""] },
      {},
      { unknown: true },
    ])
      assert.equal((await runtime.patch(update)).status, 400);
    assert.equal(await readFile(runtime.configPath, "utf8"), original);
  } finally {
    await client.close().catch(() => undefined);
    await runtime.cleanup();
  }
});

test("workspace MCP settings create config on demand, serialize writes and respect global switches", async () => {
  const runtime = await fixture();
  try {
    const initialResponse = await runtime.settings();
    assert.equal(initialResponse.status, 200);
    const initial = (await initialResponse.json()) as WorkspaceMcpSettings;
    assert.equal(initial.configPath, runtime.configPath);
    assert.ok(
      initial.servers.every(
        (server) => server.enabled && server.effectiveEnabled,
      ),
    );
    assert.ok(!JSON.stringify(initial).includes("private-value"));
    await assert.rejects(access(runtime.configPath));

    const responses = await Promise.all([
      runtime.toggle("local", false),
      runtime.toggle("second", false),
    ]);
    assert.deepEqual(
      responses.map((response) => response.status),
      [200, 200],
    );
    assert.deepEqual(
      parseCodexConfig(
        await readFile(runtime.configPath, "utf8"),
      ).disabledMcpServers.sort(),
      ["local", "second"],
    );
    await runtime.components.mcpProxy.setEnabled("second", false);
    const enabledResponse = await runtime.toggle("second", true);
    assert.equal(enabledResponse.status, 200);
    const enabled = (await enabledResponse.json()) as WorkspaceMcpSettings;
    const second = enabled.servers.find((server) => server.name === "second")!;
    assert.equal(second.enabled, true);
    assert.equal(second.globallyEnabled, false);
    assert.equal(second.effectiveEnabled, false);
    await assert.rejects(
      runtime.components.mcpProxy.call({
        root: runtime.workspaceRoot,
        server: "second",
        tool: "echo",
      }),
      /disabled/,
    );
    assert.equal((await runtime.toggle("unknown", false)).status, 404);
    assert.equal((await runtime.toggle("local", "false")).status, 400);
    const operations = runtime.components.operations.list({
      pluginId: "workspace",
    });
    assert.ok(
      operations.some(
        (operation) =>
          operation.action === "mcp.set-enabled" &&
          operation.status === "success",
      ),
    );
  } finally {
    await runtime.cleanup();
  }
});

test("workspace tool timeout applies per call, supports overrides and leaves shared requests connected", async () => {
  const runtime = await fixture();
  try {
    const response = await runtime.patch({ toolTimeoutSec: 0.05 });
    assert.equal(response.status, 200);
    const settings = (await response.json()) as WorkspaceMcpSettings;
    assert.equal(settings.defaultToolTimeoutSec, 15);
    assert.equal(settings.servers[0]?.effectiveToolTimeoutSec, 0.05);
    await runtime.components.mcpProxy.list({
      server: "local",
      includeSchemas: false,
    });
    const sibling = path.join(runtime.root, "sibling");
    await mkdir(sibling);
    const call = {
      server: "local",
      tool: "echo",
      arguments: { text: "wait", delayMs: 200 },
    };
    const results = await Promise.allSettled([
      runtime.components.mcpProxy.call({
        ...call,
        root: runtime.workspaceRoot,
      }),
      runtime.components.mcpProxy.call({ ...call, root: sibling }),
    ]);
    assert.equal(results[0]?.status, "rejected");
    if (results[0]?.status === "rejected") {
      assert.match(String(results[0].reason), /timed out after 50 ms/);
      assert.equal(results[0].reason.details.timeoutMs, 50);
    }
    assert.equal(
      results[1]?.status,
      "fulfilled",
      "a workspace timeout must not close the shared connection",
    );
    const explicit = await runtime.components.mcpProxy.call({
      ...call,
      root: runtime.workspaceRoot,
      timeoutMs: 1000,
    });
    assert.equal(explicit.content[0]?.text, "echo:wait");
    assert.equal((await runtime.patch({ toolTimeoutSec: null })).status, 200);
    const inherited = await runtime.components.mcpProxy.call({
      ...call,
      root: runtime.workspaceRoot,
    });
    assert.equal(inherited.content[0]?.text, "echo:wait");
    const current = (await (
      await runtime.settings()
    ).json()) as WorkspaceMcpSettings;
    assert.equal(current.servers[0]?.toolTimeoutSec, null);
    assert.equal(current.servers[0]?.effectiveToolTimeoutSec, 15);
    for (const value of [0, -1, "20", 0.0001, 2147484])
      assert.equal(
        (await runtime.patch({ toolTimeoutSec: value })).status,
        400,
      );
  } finally {
    await runtime.cleanup();
  }
});

test("workspace restrictions apply to MCP discovery, calls and refresh while other workspaces remain usable", async () => {
  const runtime = await fixture();
  const client = new Client({
    name: "workspace-settings-test",
    version: "1.0.0",
  });
  try {
    const sibling = path.join(runtime.root, "sibling");
    await mkdir(sibling);
    await mkdir(path.dirname(runtime.configPath));
    const original =
      '# preserve\r\nmodel = "keep"\r\n[mcp_servers.local]\r\nenabled = true # switch\r\ncommand = "keep"\r\n';
    await writeFile(runtime.configPath, original);
    assert.equal((await runtime.toggle("local", false)).status, 200);
    assert.equal(
      await readFile(runtime.configPath, "utf8"),
      original.replace("enabled = true", "enabled = false"),
    );
    await client.connect(
      new StreamableHTTPClientTransport(
        new URL(`${runtime.base.replace(/\/api$/, "")}/mcp`),
      ),
    );
    for (const name of ["mcp_list_servers", "mcp_refresh"]) {
      const result = await client.callTool({
        name,
        arguments: { root: runtime.workspaceRoot, server: "local" },
      });
      assert.equal(result.isError, undefined);
      const { servers } = result.structuredContent as {
        servers: { enabled: boolean; status: string; tools: unknown[] }[];
      };
      assert.equal(servers[0]?.enabled, false);
      assert.equal(servers[0]?.status, "disabled");
      assert.deepEqual(servers[0]?.tools, []);
    }
    const denied = await client.callTool({
      name: "mcp_call",
      arguments: {
        root: runtime.workspaceRoot,
        server: "local",
        tool: "echo",
        arguments: { text: "denied" },
      },
    });
    assert.equal(denied.isError, true);
    assert.match(JSON.stringify(denied.content), /FORBIDDEN/);
    for (const root of [undefined, sibling]) {
      const result = await runtime.components.mcpProxy.call({
        ...(root ? { root } : {}),
        server: "local",
        tool: "echo",
        arguments: { text: "allowed" },
      });
      assert.equal(result.content[0]?.text, "echo:allowed");
    }
    // External edits are read on the next request, without restarting ChatRoom.
    await writeFile(runtime.configPath, original);
    const allowed = await client.callTool({
      name: "mcp_call",
      arguments: {
        root: runtime.workspaceRoot,
        server: "local",
        tool: "echo",
        arguments: { text: "updated" },
      },
    });
    assert.equal(allowed.isError, undefined);
    assert.match(JSON.stringify(allowed.content), /echo:updated/);
  } finally {
    await client.close().catch(() => undefined);
    await runtime.cleanup();
  }
});

test("workspace settings reject invalid config and roots without overwriting files", async () => {
  const runtime = await fixture();
  try {
    await mkdir(path.dirname(runtime.configPath));
    const malformed = '[mcp_servers.local]\nenabled = "false"\n';
    await writeFile(runtime.configPath, malformed);
    assert.equal((await runtime.settings()).status, 400);
    assert.equal((await runtime.toggle("local", false)).status, 400);
    assert.equal(await readFile(runtime.configPath, "utf8"), malformed);
    await assert.rejects(
      runtime.components.mcpProxy.call({
        root: runtime.workspaceRoot,
        server: "local",
        tool: "echo",
      }),
      /enabled must be a boolean/,
    );
    const nested = path.join(runtime.workspaceRoot, "nested");
    await mkdir(nested);
    assert.equal((await runtime.settings(nested)).status, 403);
    assert.equal((await runtime.toggle("local", false, nested)).status, 403);
    assert.equal(
      (await fetch(`${runtime.base}/workspace/mcp/servers`)).status,
      400,
    );
  } finally {
    await runtime.cleanup();
  }
});

test("workspace MCP config cannot read or write through escaping directory links", async () => {
  const runtime = await fixture();
  try {
    const outside = path.join(runtime.root, "outside-config");
    await mkdir(outside);
    const outsideFile = path.join(outside, "config.toml");
    const original = "[mcp_servers.local]\nenabled = true\n";
    await writeFile(outsideFile, original);
    await symlink(
      outside,
      path.dirname(runtime.configPath),
      process.platform === "win32" ? "junction" : "dir",
    );
    assert.equal((await runtime.settings()).status, 403);
    assert.equal((await runtime.toggle("local", false)).status, 403);
    assert.equal(await readFile(outsideFile, "utf8"), original);
  } finally {
    await runtime.cleanup();
  }
});
