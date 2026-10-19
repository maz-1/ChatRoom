import assert from "node:assert/strict";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import {
  Client,
  StreamableHTTPClientTransport,
} from "@modelcontextprotocol/client";
import type { WorkspaceSkillSettings } from "../../src/app/workspace-access.js";
import { parseCodexConfig } from "../../src/plugins/workspace/codex-config.js";
import { createTestRuntime } from "../helpers/runtime.js";

const SKILL = ".agents/skills/example/SKILL.md";
async function addSkill(root: string, relative = SKILL) {
  await mkdir(path.dirname(path.join(root, relative)), { recursive: true });
  await writeFile(
    path.join(root, relative),
    "---\nname: example\ndescription: Test skill\n---\n# Instructions\n",
  );
}

test("WebUI skill switches persist without deleting files and filter workspace_info immediately", async () => {
  const runtime = await createTestRuntime();
  const client = new Client({ name: "skill-settings-test", version: "1.0.0" });
  try {
    await addSkill(runtime.workspaceRoot);
    await addSkill(runtime.workspaceRoot, ".claude/skills/second/SKILL.md");
    const sibling = path.join(runtime.root, "sibling");
    await addSkill(sibling);
    await runtime.components.http.start();
    const base = `http://127.0.0.1:${runtime.components.http.address()!.port}`;
    const settingsUrl = `${base}/api/workspace/skills?root=${encodeURIComponent(runtime.workspaceRoot)}`;
    const toggle = (
      enabled: unknown,
      skillPath = SKILL,
      root = runtime.workspaceRoot,
    ) =>
      fetch(`${base}/api/workspace/skills`, {
        method: "PATCH",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ root, path: skillPath, enabled }),
      });
    const initial = (await (
      await fetch(settingsUrl)
    ).json()) as WorkspaceSkillSettings;
    assert.equal(initial.skills.length, 2);
    assert.ok(initial.skills.every((skill) => skill.enabled));
    const configFile = path.join(runtime.workspaceRoot, ".codex/config.toml");
    const [disabled, mcp] = await Promise.all([
      toggle(false),
      runtime.components.mcpProxy.workspaceSettings(runtime.workspaceRoot),
    ]);
    assert.equal(disabled.status, 200);
    assert.equal(mcp.servers.length, 0);
    const disabledSettings = (await disabled.json()) as WorkspaceSkillSettings;
    assert.equal(
      disabledSettings.skills.find((skill) => skill.path === SKILL)?.enabled,
      false,
    );
    assert.match(
      await readFile(path.join(runtime.workspaceRoot, SKILL), "utf8"),
      /# Instructions/,
    );
    assert.deepEqual(
      parseCodexConfig(await readFile(configFile, "utf8")).skillsConfig,
      [{ path: `../${SKILL}`, enabled: false }],
    );
    await client.connect(
      new StreamableHTTPClientTransport(new URL(`${base}/mcp`)),
    );
    const info = await client.callTool({
      name: "workspace_info",
      arguments: { root: runtime.workspaceRoot },
    });
    assert.equal(info.isError, undefined);
    assert.deepEqual(
      (info.structuredContent as { skills: { path: string }[] }).skills.map(
        (skill) => skill.path,
      ),
      [".claude/skills/second/SKILL.md"],
    );
    const other = await client.callTool({
      name: "workspace_info",
      arguments: { root: sibling },
    });
    assert.equal(
      (other.structuredContent as { skills: unknown[] }).skills.length,
      1,
    );
    assert.equal((await toggle(true)).status, 200);
    assert.equal(
      (
        (await (await fetch(settingsUrl)).json()) as WorkspaceSkillSettings
      ).skills.filter((skill) => skill.enabled).length,
      2,
    );
    assert.equal((await toggle(false, "../outside/SKILL.md")).status, 403);
    assert.equal(
      (await toggle(false, ".agents/skills/missing/SKILL.md")).status,
      404,
    );
    assert.equal((await toggle("false")).status, 400);
    const nested = path.join(runtime.workspaceRoot, "nested");
    await mkdir(nested);
    assert.equal((await toggle(false, SKILL, nested)).status, 403);
    const operations = runtime.components.operations.list({
      pluginId: "workspace",
    });
    assert.ok(
      operations.some(
        (operation) =>
          operation.action === "skills.set-enabled" &&
          operation.status === "success",
      ),
    );
  } finally {
    await client.close().catch(() => undefined);
    await runtime.cleanup();
  }
});

test("skill overrides preserve other entries and config while concurrent MCP and skill writes survive", async () => {
  const runtime = await createTestRuntime({
    configure(config) {
      config.mcp.servers.local = {
        type: "http",
        url: "http://127.0.0.1:1/mcp",
        headers: {},
        proxy: null,
      };
    },
  });
  try {
    await addSkill(runtime.workspaceRoot);
    await addSkill(runtime.workspaceRoot, ".chatroom/skills/second/SKILL.md");
    const configFile = path.join(runtime.workspaceRoot, ".codex/config.toml");
    await mkdir(path.dirname(configFile));
    const original =
      '# keep\r\nmodel = "keep"\r\n[[skills.config]]\r\npath = "../.agents/skills/example/SKILL.md"\r\nenabled = true # keep switch\r\n[[skills.config]]\r\npath = "/external/unavailable"\r\nenabled = false\r\n';
    await writeFile(configFile, original);
    await runtime.components.http.start();
    const base = `http://127.0.0.1:${runtime.components.http.address()!.port}/api`;
    const toggle = (skillPath: string) =>
      fetch(`${base}/workspace/skills`, {
        method: "PATCH",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          root: runtime.workspaceRoot,
          path: skillPath,
          enabled: false,
        }),
      });
    const responses = await Promise.all([
      toggle(SKILL),
      toggle(".chatroom/skills/second/SKILL.md"),
      runtime.components.mcpProxy.setWorkspaceSettings(
        runtime.workspaceRoot,
        "local",
        { disabledTools: ["delete"] },
      ),
    ]);
    assert.equal((responses[0] as Response).status, 200);
    assert.equal((responses[1] as Response).status, 200);
    const text = await readFile(configFile, "utf8");
    assert.ok(
      text.includes('model = "keep"') &&
        text.includes("enabled = false # keep switch"),
    );
    const parsed = parseCodexConfig(text);
    assert.equal(parsed.skillsConfig.length, 3);
    assert.ok(parsed.skillsConfig.every((override) => !override.enabled));
    assert.deepEqual(parsed.mcpServers.local?.disabledTools, ["delete"]);
    // Invalid existing config must never be rewritten by the skill API.
    await writeFile(configFile, '[mcp_servers.local]\nenabled = "invalid"\n');
    assert.equal((await toggle(SKILL)).status, 400);
    assert.equal(
      await readFile(configFile, "utf8"),
      '[mcp_servers.local]\nenabled = "invalid"\n',
    );
  } finally {
    await runtime.cleanup();
  }
});
