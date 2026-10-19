import { traverseNodes, type AST } from "toml-eslint-parser";
import type {
  WorkspaceMcpPolicy,
  WorkspaceMcpPolicyUpdate,
  WorkspaceSkillOverride,
} from "#app/workspace-config-access";
import {
  editTomlValue,
  invalidConfig,
  keyPath,
  parseToml,
  tomlString,
} from "./toml-document.js";
import { parseSkillsConfig } from "./codex-skills-config.js";

export { CODEX_CONFIG_PATH } from "./toml-document.js";

const MCP_FIELDS = {
  enabled: "enabled",
  enabled_tools: "enabledTools",
  disabled_tools: "disabledTools",
  tool_timeout_sec: "toolTimeoutSec",
} as const;

/** Validate supported workspace settings without interpreting other Codex keys. */
export function parseCodexConfig(source: string): {
  ast: AST.TOMLProgram;
  disabledMcpServers: string[];
  mcpServers: Record<string, WorkspaceMcpPolicy>;
  skillsConfig: WorkspaceSkillOverride[];
} {
  const ast = parseToml(source);
  const servers = new Map<string, WorkspaceMcpPolicy>();
  traverseNodes(ast, {
    enterNode(node) {
      if (node.type === "TOMLTable") {
        const keys = node.resolvedKey;
        if (keys[0] !== "mcp_servers") return;
        if (node.key.keys.length <= 2 && node.kind === "array")
          invalidConfig("mcp_servers and its servers must be tables");
        if (Object.hasOwn(MCP_FIELDS, String(keys[2])))
          invalidConfig(`Invalid MCP ${String(keys[2])}`);
      }
      if (node.type !== "TOMLKeyValue") return;
      const keys = keyPath(node);
      if (keys[0] !== "mcp_servers") return;
      if (keys.length <= 2 && node.value.type !== "TOMLInlineTable")
        invalidConfig("mcp_servers and its servers must be tables");
      const field = String(keys[2]);
      if (!Object.hasOwn(MCP_FIELDS, field)) return;
      if (keys.length !== 3) invalidConfig(`Invalid MCP ${field}`);
      const name = String(keys[1]);
      const policy = servers.get(name) ?? {};
      if (field === "enabled") {
        if (node.value.type !== "TOMLValue" || node.value.kind !== "boolean")
          invalidConfig("MCP enabled must be a boolean");
        policy.enabled = node.value.value;
      } else if (field === "tool_timeout_sec") {
        if (
          node.value.type !== "TOMLValue" ||
          !["integer", "float"].includes(node.value.kind) ||
          typeof node.value.value !== "number" ||
          !Number.isFinite(node.value.value) ||
          node.value.value < 0.001 ||
          node.value.value > 2_147_483.647
        )
          invalidConfig(
            "MCP tool_timeout_sec must be between 0.001 and 2147483.647 seconds",
          );
        policy.toolTimeoutSec = node.value.value;
      } else {
        if (node.value.type !== "TOMLArray")
          invalidConfig(`MCP ${field} must be an array of tool names`);
        const tools = node.value.elements.map((element) => {
          if (
            element.type !== "TOMLValue" ||
            element.kind !== "string" ||
            !element.value.trim()
          )
            invalidConfig(
              `MCP ${field} must be an array of non-empty tool names`,
            );
          return element.value;
        });
        policy[field === "enabled_tools" ? "enabledTools" : "disabledTools"] =
          tools;
      }
      servers.set(name, policy);
    },
    leaveNode() {},
  });
  return {
    ast,
    skillsConfig: parseSkillsConfig(ast),
    mcpServers: Object.fromEntries(servers),
    disabledMcpServers: [...servers]
      .filter(([, policy]) => policy.enabled === false)
      .map(([name]) => name),
  };
}

export function setCodexMcpSettings(
  source: string,
  server: string,
  update: WorkspaceMcpPolicyUpdate,
): string {
  parseCodexConfig(source);
  let result = source;
  for (const [tomlKey, key] of Object.entries(MCP_FIELDS)) {
    const value = update[key];
    if (value === undefined) continue;
    const literal =
      value === null
        ? null
        : typeof value === "boolean" || typeof value === "number"
          ? String(value)
          : `[${value.map(tomlString).join(", ")}]`;
    result = editTomlValue(result, ["mcp_servers", server, tomlKey], literal);
  }
  parseCodexConfig(result);
  return result;
}

export function setCodexMcpEnabled(
  source: string,
  server: string,
  enabled: boolean,
): string {
  return setCodexMcpSettings(source, server, { enabled });
}
