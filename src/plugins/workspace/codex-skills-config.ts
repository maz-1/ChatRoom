import path from "node:path";
import { traverseNodes, type AST } from "toml-eslint-parser";
import type { WorkspaceSkillOverride } from "#app/workspace-config-access";
import {
  editTomlValue,
  invalidConfig,
  keyPath,
  parseToml,
  tomlString,
} from "./toml-document.js";

export function parseSkillsConfig(
  ast: AST.TOMLProgram,
): WorkspaceSkillOverride[] {
  const entries = new Map<number, Partial<WorkspaceSkillOverride>>();
  const entry = (index: number) => {
    if (!entries.has(index)) entries.set(index, {});
    return entries.get(index)!;
  };
  traverseNodes(ast, {
    enterNode(node) {
      if (node.type === "TOMLTable" && node.resolvedKey[0] === "skills") {
        const keys = node.resolvedKey;
        if (node.key.keys.length === 1 && node.kind === "array")
          invalidConfig("skills must be a table");
        if (keys[1] !== "config") return;
        if (typeof keys[2] !== "number")
          invalidConfig("skills.config must be an array of tables");
        entry(keys[2]);
        if (keys[3] === "path" || keys[3] === "enabled")
          invalidConfig("Invalid skills.config entry");
      }
      if (node.type !== "TOMLKeyValue") return;
      const keys = keyPath(node);
      if (keys[0] !== "skills") return;
      if (keys.length === 1 && node.value.type !== "TOMLInlineTable")
        invalidConfig("skills must be a table");
      if (keys[1] !== "config") return;
      if (keys.length === 2) {
        if (node.value.type !== "TOMLArray")
          invalidConfig("skills.config must be an array of tables");
        node.value.elements.forEach((value, index) => {
          if (value.type !== "TOMLInlineTable")
            invalidConfig("skills.config must contain tables");
          entry(index);
        });
        return;
      }
      if (typeof keys[2] !== "number")
        invalidConfig("skills.config must be an array of tables");
      const field = keys[3];
      if (field !== "path" && field !== "enabled") return;
      if (keys.length !== 4 || node.value.type !== "TOMLValue")
        invalidConfig(`Invalid skills.config ${field}`);
      if (field === "path") {
        if (
          node.value.kind !== "string" ||
          !node.value.value.trim() ||
          node.value.value.includes("\0")
        )
          invalidConfig("Skill path must be a non-empty string");
        entry(keys[2]).path = node.value.value;
      } else {
        if (node.value.kind !== "boolean")
          invalidConfig("Skill enabled must be a boolean");
        entry(keys[2]).enabled = node.value.value;
      }
    },
    leaveNode() {},
  });
  return [...entries]
    .sort(([a], [b]) => a - b)
    .map(([, value]) => {
      if (!value.path) invalidConfig("Every skills.config entry needs a path");
      return { path: value.path, enabled: value.enabled ?? true };
    });
}

function matchesSkill(
  root: string,
  configuredPath: string,
  skillPath: string,
): boolean {
  const configured = path.resolve(root, ".codex", configuredPath);
  const directory =
    path.basename(configured).toLowerCase() === "skill.md"
      ? path.dirname(configured)
      : configured;
  return (
    path.relative(directory, path.dirname(path.resolve(root, skillPath))) === ""
  );
}

export function skillConfigEnabled(
  root: string,
  overrides: WorkspaceSkillOverride[],
  skillPath: string,
): boolean {
  return overrides.reduce(
    (enabled, override) =>
      matchesSkill(root, override.path, skillPath) ? override.enabled : enabled,
    true,
  );
}

export function setCodexSkillEnabled(
  source: string,
  root: string,
  skillPath: string,
  enabled: boolean,
): string {
  const ast = parseToml(source);
  const overrides = parseSkillsConfig(ast);
  const matching = overrides.flatMap((override, index) =>
    matchesSkill(root, override.path, skillPath) ? [index] : [],
  );
  let result = source;
  if (matching.length) {
    for (const index of matching)
      result = editTomlValue(
        result,
        ["skills", "config", index, "enabled"],
        String(enabled),
      );
  } else {
    const found: { array?: AST.TOMLArray } = {};
    traverseNodes(ast, {
      enterNode(node) {
        if (node.type !== "TOMLKeyValue") return;
        const keys = keyPath(node);
        if (
          keys.length === 2 &&
          keys[0] === "skills" &&
          keys[1] === "config" &&
          node.value.type === "TOMLArray"
        )
          found.array = node.value;
      },
      leaveNode() {},
    });
    const literalPath = tomlString(
      path.posix.join("..", skillPath.replaceAll("\\", "/")),
    );
    const literal = `{ path = ${literalPath}, enabled = ${enabled} }`;
    if (found.array) {
      const last = found.array.elements.at(-1);
      const offset = last?.range[1] ?? found.array.range[0] + 1;
      result =
        source.slice(0, offset) +
        (last ? ", " : " ") +
        literal +
        source.slice(offset);
    } else if (overrides.length) {
      const newline = source.includes("\r\n") ? "\r\n" : "\n";
      result =
        source +
        (source.endsWith("\n") ? "" : newline) +
        newline +
        `[[skills.config]]${newline}path = ${literalPath}${newline}enabled = ${enabled}${newline}`;
    } else {
      result = editTomlValue(source, ["skills", "config"], `[${literal}]`);
    }
  }
  parseSkillsConfig(parseToml(result));
  return result;
}
