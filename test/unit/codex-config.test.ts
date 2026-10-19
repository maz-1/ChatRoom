import assert from "node:assert/strict";
import test from "node:test";
import { ChatRoomError } from "../../src/core/errors/chatroom-error.js";
import {
  parseCodexConfig,
  setCodexMcpEnabled,
  setCodexMcpSettings,
} from "../../src/plugins/workspace/codex-config.js";

test("workspace Codex config reads only MCP enabled flags, with TOML key semantics", () => {
  const source = [
    'model = "example"',
    'notes = """',
    "[mcp_servers.fake]",
    "enabled = false",
    '"""',
    "unrelated = [{ mcp_servers = { fake = { enabled = false } } }]",
    "[mcp_servers.default]",
    'command = "node"',
    "[mcp_servers.active]",
    "enabled = true",
    '[mcp_servers."quoted.name"]',
    "enabled = false # keep this",
    "[mcp_servers.'literal name']",
    "enabled = false",
  ].join("\n");
  assert.deepEqual(parseCodexConfig(source).disabledMcpServers, [
    "quoted.name",
    "literal name",
  ]);
  assert.deepEqual(parseCodexConfig("").disabledMcpServers, []);
});

test("MCP tool filters preserve TOML formatting and distinguish no allowlist from an empty one", () => {
  const original =
    '# keep\r\n[mcp_servers.local]\r\nenabled_tools = ["echo", "fail"] # allow\r\ndisabled_tools = ["fail"] # deny\r\n';
  assert.deepEqual(parseCodexConfig(original).mcpServers.local, {
    enabledTools: ["echo", "fail"],
    disabledTools: ["fail"],
  });
  const edited = setCodexMcpSettings(original, "local", { enabledTools: [] });
  assert.equal(edited, original.replace('["echo", "fail"]', "[]"));
  const cleared = setCodexMcpSettings(edited, "local", {
    enabledTools: null,
    disabledTools: null,
  });
  assert.deepEqual(parseCodexConfig(cleared).mcpServers, {});
  assert.ok(cleared.includes("# allow") && cleared.includes("# deny"));
  for (const source of [
    'mcp_servers = { local = { enabled_tools = ["echo"], command = "keep" } }',
    'mcp_servers.local = { command = "keep", enabled_tools = [] }',
    "mcp_servers.local = { enabled_tools = [] }",
  ]) {
    const removed = setCodexMcpSettings(source, "local", {
      enabledTools: null,
    });
    assert.equal(
      parseCodexConfig(removed).mcpServers.local?.enabledTools,
      undefined,
    );
    assert.ok(!source.includes('"keep"') || removed.includes('"keep"'));
  }
  for (const value of ["false", "[1]", '[" "]', "{}"]) {
    assert.throws(
      () => parseCodexConfig(`[mcp_servers.local]\nenabled_tools = ${value}`),
      ChatRoomError,
    );
  }
});

test("workspace MCP changes preserve existing comments, strings, values and CRLF", () => {
  const original =
    '# settings\r\nmodel = "keep"\r\n[mcp_servers.local] # server\r\nenabled   = true # switch\r\ncommand = "node"\r\n[mcp_servers.local.env]\r\nSECRET = "keep"\r\n';
  const changed = setCodexMcpEnabled(original, "local", false);
  assert.equal(
    changed,
    original.replace("= true # switch", "= false # switch"),
  );
  assert.equal(setCodexMcpEnabled(changed, "local", true), original);
});

test("MCP timeout config supports fractional seconds and removal while rejecting invalid timer ranges", () => {
  const source = "[mcp_servers.local]\ntool_timeout_sec = 12.5 # keep\n";
  assert.equal(parseCodexConfig(source).mcpServers.local?.toolTimeoutSec, 12.5);
  assert.equal(
    setCodexMcpSettings(source, "local", { toolTimeoutSec: 0.05 }),
    source.replace("12.5", "0.05"),
  );
  const reset = setCodexMcpSettings(source, "local", { toolTimeoutSec: null });
  assert.equal(
    parseCodexConfig(reset).mcpServers.local?.toolTimeoutSec,
    undefined,
  );
  assert.ok(reset.includes("# keep"));
  for (const value of [
    '"5"',
    "0",
    "-1",
    "nan",
    "inf",
    "0.0001",
    "2147484",
    "[]",
  ])
    assert.throws(
      () =>
        parseCodexConfig(`[mcp_servers.local]\ntool_timeout_sec = ${value}`),
      ChatRoomError,
    );
});

test("workspace MCP switches can be inserted in standard, dotted and inline tables", () => {
  for (const original of [
    "",
    '\uFEFFmodel = "keep"\r\n',
    '# keep\nmodel = "keep"\n',
    "[mcp_servers.local] # no trailing newline",
    '[mcp_servers.local]\ncommand = "keep"\n[other]\nkeep = true\n',
    '[mcp_servers]\nlocal.command = "keep"\n',
    'mcp_servers.local.command = "keep"\n[other]\nkeep = true\n',
    '[mcp_servers.local.env]\nKEEP = "yes"\n',
    "mcp_servers = {} # keep\n",
    'mcp_servers = { local = { command = "keep" } } # keep\n',
    'mcp_servers = { local.command = "keep" } # keep\n',
    "[mcp_servers]\nlocal = {} # keep\n",
    'mcp_servers.local = { enabled = true, command = "keep" } # keep\n',
  ]) {
    const disabled = setCodexMcpEnabled(original, "local", false);
    assert.deepEqual(
      parseCodexConfig(disabled).disabledMcpServers,
      ["local"],
      original,
    );
    assert.ok(!original.includes('"keep"') || disabled.includes('"keep"'));
    assert.ok(!original.includes("# keep") || disabled.includes("# keep"));
    const enabled = setCodexMcpEnabled(disabled, "local", true);
    assert.deepEqual(
      parseCodexConfig(enabled).disabledMcpServers,
      [],
      original,
    );
    assert.equal(setCodexMcpEnabled(disabled, "local", false), disabled);
  }
});

test("workspace MCP names are quoted safely and do not modify object prototypes", () => {
  for (const name of [
    "has.dots",
    'has"quotes',
    "中文服务",
    "__proto__",
    "constructor",
    "new\nline",
  ]) {
    const source = setCodexMcpEnabled("", name, false);
    assert.deepEqual(parseCodexConfig(source).disabledMcpServers, [name]);
  }
  assert.equal(Object.hasOwn(Object.prototype, "enabled"), false);
});

test("invalid TOML or invalid MCP enabled types are rejected instead of ignored", () => {
  for (const source of [
    "[broken",
    'model = "a"\nmodel = "b"',
    "mcp_servers = false",
    "mcp_servers.local = []",
    '[mcp_servers.local]\nenabled = "false"',
    "[[mcp_servers.local]]\nenabled = false",
    "[mcp_servers.local.enabled]\nvalue = false",
    "[mcp_servers.local]\nenabled.value = false",
  ]) {
    for (const run of [
      () => parseCodexConfig(source),
      () => setCodexMcpEnabled(source, "local", false),
    ])
      assert.throws(
        run,
        (error: unknown) =>
          error instanceof ChatRoomError && error.code === "INVALID_INPUT",
        source,
      );
  }
});
