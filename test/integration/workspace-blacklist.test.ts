import assert from "node:assert/strict";
import { mkdir, rm } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import {
  Client,
  StreamableHTTPClientTransport,
} from "@modelcontextprotocol/client";
import { createApplication } from "../../src/app/application.js";
import { AppDatabase } from "../../src/infrastructure/database/app-database.js";
import { migrateDatabase } from "../../src/infrastructure/database/migrations.js";
import type { WorkspaceEntry } from "../../src/plugins/workspace/types.js";
import { createTestRuntime } from "../helpers/runtime.js";

test("MCP workspace_info rejects blacklisted paths and resumes after restore", async () => {
  const runtime = await createTestRuntime();
  const client = new Client({ name: "blacklist-test", version: "1.0.0" });
  try {
    await runtime.components.http.start();
    const address = runtime.components.http.address();
    assert.ok(address);
    const base = `http://127.0.0.1:${address.port}`;
    await client.connect(
      new StreamableHTTPClientTransport(new URL(`${base}/mcp`)),
    );
    const read = (root: string) =>
      client.callTool({ name: "workspace_info", arguments: { root } });
    assert.equal((await read(runtime.workspaceRoot)).isError, undefined);
    const other = await runtime.components.application.workspaces.createProject(
      runtime.root,
      "other",
    );

    const block = await fetch(`${base}/api/workspace/blacklist`, {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ root: runtime.workspaceRoot, blocked: true }),
    });
    assert.equal(block.status, 200);
    for (const root of [
      runtime.workspaceRoot,
      `${runtime.workspaceRoot}${path.sep}.`,
      ...(process.platform === "win32"
        ? [runtime.workspaceRoot.toUpperCase()]
        : []),
    ]) {
      const result = await read(root);
      assert.equal(result.isError, true);
      assert.match(JSON.stringify(result.content), /FORBIDDEN/);
      assert.equal(result.structuredContent, undefined);
    }
    assert.equal((await read(other.root)).isError, undefined);

    const restore = await fetch(`${base}/api/workspace/blacklist`, {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ root: runtime.workspaceRoot, blocked: false }),
    });
    assert.equal(restore.status, 200);
    const result = await read(runtime.workspaceRoot);
    assert.equal(result.isError, undefined);
    assert.equal(
      (result.structuredContent as { root: string }).root,
      runtime.workspaceRoot,
    );
  } finally {
    await client.close().catch(() => undefined);
    await runtime.cleanup();
  }
});

test("WebUI blacklist hides workspaces, persists after restart, and restores stale entries", async () => {
  const runtime = await createTestRuntime();
  let restarted: Awaited<ReturnType<typeof createApplication>> | null = null;
  try {
    await runtime.components.http.start();
    const address = runtime.components.http.address();
    assert.ok(address);
    let base = `http://127.0.0.1:${address.port}/api`;
    const list = async () => {
      const response = await fetch(`${base}/workspaces`);
      assert.equal(response.status, 200);
      return (await response.json()) as WorkspaceEntry[];
    };
    const setBlocked = (root: string, blocked: boolean) =>
      fetch(`${base}/workspace/blacklist`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ root, blocked }),
      });

    const visible = await list();
    const workspace = visible.find((entry) => entry.name === "workspace");
    assert.ok(workspace);
    for (let attempt = 0; attempt < 2; attempt++) {
      const response = await setBlocked(workspace.root, true);
      assert.equal(response.status, 200);
      assert.deepEqual(await response.json(), [workspace.root]);
    }
    assert.deepEqual(
      await list(),
      visible.filter((entry) => entry.root !== workspace.root),
    );
    const info = await fetch(
      `${base}/workspace?root=${encodeURIComponent(workspace.root)}`,
    );
    assert.equal(
      info.status,
      403,
      "blocked workspace metadata cannot be read by path",
    );
    const operations = runtime.components.operations.list({
      pluginId: "workspace",
    });
    assert.equal(operations[0]?.action, "block");
    assert.equal(operations[0]?.status, "success");

    await runtime.components.http.close();
    await runtime.components.plugins.stop();
    runtime.components.database.close();
    restarted = await createApplication(
      runtime.config,
      runtime.components.logs,
      {
        ownerToken: "test-owner-token",
      },
    );
    await restarted.http.start();
    const newAddress = restarted.http.address();
    assert.ok(newAddress);
    base = `http://127.0.0.1:${newAddress.port}/api`;
    assert.deepEqual(
      await (await fetch(`${base}/workspace/blacklist`)).json(),
      [workspace.root],
    );
    assert.ok(!(await list()).some((entry) => entry.root === workspace.root));

    assert.equal((await setBlocked(workspace.root, false)).status, 200);
    assert.ok((await list()).some((entry) => entry.root === workspace.root));
    assert.equal((await setBlocked(workspace.root, true)).status, 200);
    await rm(workspace.root, { recursive: true });
    const restored = await setBlocked(workspace.root, false);
    assert.equal(restored.status, 200);
    assert.deepEqual(await restored.json(), []);
    await mkdir(workspace.root);
    assert.ok((await list()).some((entry) => entry.root === workspace.root));

    // All entries may be hidden, while blacklist management remains accessible.
    for (const entry of await list()) {
      assert.equal((await setBlocked(entry.root, true)).status, 200);
    }
    assert.deepEqual(await list(), []);
    assert.equal((await setBlocked(workspace.root, false)).status, 200);
    assert.deepEqual(
      (await list()).map((entry) => entry.root),
      [workspace.root],
    );
  } finally {
    if (restarted) {
      await restarted.http.close();
      await restarted.plugins.stop();
      restarted.database.close();
    }
    await runtime.cleanup();
  }
});

test("blacklist rejects invalid input and workspaces outside allowed roots", async () => {
  const runtime = await createTestRuntime();
  try {
    await runtime.components.http.start();
    const address = runtime.components.http.address();
    assert.ok(address);
    const url = `http://127.0.0.1:${address.port}/api/workspace/blacklist`;
    for (const [body, status] of [
      [{ root: runtime.workspaceRoot, blocked: "true" }, 400],
      [{ blocked: true }, 400],
      [{ root: path.dirname(runtime.root), blocked: true }, 403],
      [{ root: path.join(runtime.root, "missing"), blocked: true }, 404],
      [{ root: "relative", blocked: false }, 400],
    ] as const) {
      const response = await fetch(url, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
      });
      assert.equal(response.status, status);
    }
    assert.deepEqual(await (await fetch(url)).json(), []);
  } finally {
    await runtime.cleanup();
  }
});

test("version-5 databases gain an empty blacklist without losing existing settings", () => {
  const database = new AppDatabase(":memory:");
  try {
    database.raw.exec(`
      DROP TABLE workspace_blacklist;
      INSERT INTO mcp_tool_settings VALUES ('workspace_info', 0, '2026-09-23');
      PRAGMA user_version = 5;
    `);
    // Use the same migration entry point as AppDatabase startup.
    migrateDatabase(database.raw);
    assert.equal(
      database.raw
        .prepare("SELECT COUNT(*) AS count FROM workspace_blacklist")
        .get()?.count,
      0,
    );
    assert.equal(
      database.raw
        .prepare(
          "SELECT enabled FROM mcp_tool_settings WHERE tool_name = 'workspace_info'",
        )
        .get()?.enabled,
      0,
    );
    assert.equal(
      database.raw.prepare("PRAGMA user_version").get()?.user_version,
      6,
    );
  } finally {
    database.close();
  }
});
