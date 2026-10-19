import { parseTOML, traverseNodes, type AST } from "toml-eslint-parser";
import { ChatRoomError } from "#core/errors/chatroom-error";

export type TomlPath = (string | number)[];
export const CODEX_CONFIG_PATH = ".codex/config.toml";

export function invalidConfig(message: string): never {
  throw new ChatRoomError("INVALID_INPUT", `${CODEX_CONFIG_PATH}: ${message}`);
}

export function parseToml(source: string): AST.TOMLProgram {
  try {
    return parseTOML(source.replace(/^\uFEFF/, " "), { tomlVersion: "1.0" });
  } catch (error) {
    invalidConfig(error instanceof Error ? error.message : "Invalid TOML");
  }
}

export function keyPath(node: AST.TOMLKeyValue): TomlPath {
  const parent = node.parent;
  const prefix =
    parent.type === "TOMLTable"
      ? parent.resolvedKey
      : parent.type === "TOMLInlineTable"
        ? contentPath(parent)
        : [];
  return [
    ...prefix,
    ...node.key.keys.map((key) =>
      key.type === "TOMLBare" ? key.name : key.value,
    ),
  ];
}

export function contentPath(node: AST.TOMLContentNode): TomlPath {
  const parent = node.parent;
  return parent.type === "TOMLKeyValue"
    ? keyPath(parent)
    : [...contentPath(parent), parent.elements.indexOf(node)];
}

function isPrefix(prefix: TomlPath, target: TomlPath): boolean {
  return (
    prefix.length <= target.length &&
    prefix.every((key, index) => key === target[index])
  );
}

export function tomlString(value: string): string {
  return JSON.stringify(value).replace(/\u007f/g, "\\u007f");
}

/** Edit one value by source range, retaining unrelated formatting and comments. */
export function editTomlValue(
  source: string,
  target: TomlPath,
  literal: string | null,
): string {
  const ast = parseToml(source);
  const found: {
    entry?: AST.TOMLKeyValue;
    container: AST.TOMLTopLevelTable | AST.TOMLTable | AST.TOMLInlineTable;
    prefix: TomlPath;
  } = { container: ast.body[0], prefix: [] };
  traverseNodes(ast, {
    enterNode(node) {
      if (
        node.type === "TOMLTable" &&
        isPrefix(node.resolvedKey, target.slice(0, -1)) &&
        node.resolvedKey.length > found.prefix.length
      ) {
        found.container = node;
        found.prefix = node.resolvedKey;
      }
      if (node.type !== "TOMLKeyValue") return;
      const keys = keyPath(node);
      if (keys.length === target.length && isPrefix(keys, target))
        found.entry = node;
      if (
        node.value.type === "TOMLInlineTable" &&
        isPrefix(keys, target.slice(0, -1)) &&
        keys.length > found.prefix.length
      ) {
        found.container = node.value;
        found.prefix = keys;
      }
      if (node.value.type === "TOMLArray") {
        node.value.elements.forEach((element, index) => {
          const prefix = [...keys, index];
          if (
            element.type === "TOMLInlineTable" &&
            isPrefix(prefix, target.slice(0, -1)) &&
            prefix.length > found.prefix.length
          ) {
            found.container = element;
            found.prefix = prefix;
          }
        });
      }
    },
    leaveNode() {},
  });
  const entry = found.entry;
  if (entry && literal !== null)
    return (
      source.slice(0, entry.value.range[0]) +
      literal +
      source.slice(entry.value.range[1])
    );
  if (entry) {
    let [start, end] = entry.range;
    if (entry.parent.type === "TOMLInlineTable") {
      const siblings = entry.parent.body;
      const index = siblings.indexOf(entry);
      if (siblings[index + 1]) end = siblings[index + 1]!.range[0];
      else if (index > 0) start = siblings[index - 1]!.range[1];
    }
    return source.slice(0, start) + source.slice(end);
  }
  if (literal === null) return source;
  const key = target
    .slice(found.prefix.length)
    .map((part) => {
      if (typeof part !== "string")
        invalidConfig("Cannot insert an array index as a key");
      return /^[A-Za-z0-9_-]+$/.test(part) ? part : tomlString(part);
    })
    .join(".");
  const assignment = `${key} = ${literal}`;
  const container = found.container;
  if (container.type === "TOMLInlineTable") {
    const offset = container.range[1] - 1;
    return (
      source.slice(0, offset) +
      (container.body.length ? ", " : " ") +
      assignment +
      " " +
      source.slice(offset)
    );
  }
  const newline = source.includes("\r\n") ? "\r\n" : "\n";
  const lineEnd =
    container.type === "TOMLTable"
      ? source.indexOf("\n", container.key.range[1])
      : -1;
  const offset =
    container.type === "TOMLTable"
      ? lineEnd < 0
        ? source.length
        : lineEnd + 1
      : source.startsWith("\uFEFF")
        ? 1
        : 0;
  return (
    source.slice(0, offset) +
    (container.type === "TOMLTable" && lineEnd < 0 ? newline : "") +
    assignment +
    newline +
    source.slice(offset)
  );
}
