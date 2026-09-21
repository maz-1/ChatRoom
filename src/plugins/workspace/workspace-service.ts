import { mkdir, readdir, realpath, stat } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { ChatRoomError } from "#core/errors/chatroom-error";
import {
  readInstructions,
  readPresetPrompt,
  readSkills,
  readSummary,
} from "./metadata.js";
import type { WorkspaceEntry, WorkspaceInfo } from "./types.js";
import { WorkspaceFs } from "./workspace-fs.js";

export class WorkspaceService {
  private constructor(private readonly allowedRoots: string[]) {}

  static async create(allowedRoots: string[]): Promise<WorkspaceService> {
    const canonicalRoots = await Promise.all(
      allowedRoots.map(async (root) => {
        const canonical = await realpath(expandHome(root)).catch(() => {
          throw new ChatRoomError(
            "NOT_FOUND",
            `Allowed root does not exist: ${root}`,
          );
        });
        if (!(await stat(canonical)).isDirectory())
          throw new ChatRoomError(
            "INVALID_INPUT",
            `Allowed root is not a directory: ${root}`,
          );
        return canonical;
      }),
    );
    return new WorkspaceService([...new Set(canonicalRoots)]);
  }

  roots(): string[] {
    return [...this.allowedRoots];
  }

  async list(): Promise<WorkspaceEntry[]> {
    const roots = new Set<string>();
    for (const allowedRoot of this.allowedRoots) {
      let entries;
      try {
        entries = await readdir(allowedRoot, { withFileTypes: true });
      } catch {
        continue;
      }
      for (const entry of entries) {
        if (entry.name.startsWith(".")) continue;
        const candidate = path.join(allowedRoot, entry.name);
        const info = await stat(candidate).catch(() => null);
        if (info?.isDirectory()) roots.add(candidate);
      }
    }

    return Promise.all(
      [...roots]
        .sort((a, b) => a.localeCompare(b))
        .map(async (root) => {
          const fs = await WorkspaceFs.create(root);
          return {
            root,
            name: path.basename(root),
            summary: await readSummary(fs),
          };
        }),
    );
  }

  async resolve(input: string): Promise<string> {
    if (typeof input !== "string" || !input.trim())
      throw new ChatRoomError("INVALID_INPUT", "Workspace root is required");
    const requested = path.resolve(expandHome(input));
    const parent = await realpath(path.dirname(requested)).catch(() => null);
    if (!parent || !this.allowedRoots.includes(parent))
      throw new ChatRoomError(
        "FORBIDDEN",
        "Workspace must be a direct child of a configured allowed root",
        { root: requested },
      );
    const workspaceRoot = path.join(parent, path.basename(requested));
    const canonical = await realpath(workspaceRoot).catch(() => {
      throw new ChatRoomError(
        "NOT_FOUND",
        `Workspace root does not exist: ${input}`,
      );
    });
    if (!(await stat(canonical)).isDirectory())
      throw new ChatRoomError(
        "INVALID_INPUT",
        `Workspace root is not a directory: ${input}`,
      );
    return workspaceRoot;
  }

  async createProject(
    parentInput: string,
    nameInput: string,
  ): Promise<WorkspaceEntry> {
    const parent = await this.resolveAllowedRoot(parentInput);
    const name = validateProjectName(nameInput);
    const target = path.join(parent, name);
    try {
      await mkdir(target, { recursive: false });
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "EEXIST")
        throw new ChatRoomError(
          "CONFLICT",
          `Workspace already exists: ${target}`,
        );
      throw error;
    }
    await mkdir(path.join(target, ".chatroom"));
    const root = await realpath(target);
    return { root, name: path.basename(root), summary: null };
  }

  async info(input: string): Promise<WorkspaceInfo> {
    const root = await this.resolve(input);
    const fs = await WorkspaceFs.create(root);
    const [summary, presetPrompt, instructions, skills] = await Promise.all([
      readSummary(fs),
      readPresetPrompt(fs),
      readInstructions(fs),
      readSkills(fs),
    ]);
    return {
      root,
      name: path.basename(root),
      summary,
      presetPrompt,
      instructions,
      skills,
    };
  }

  async fs(input: string): Promise<WorkspaceFs> {
    return WorkspaceFs.create(await this.resolve(input));
  }

  private async resolveAllowedRoot(input: string): Promise<string> {
    if (typeof input !== "string" || !input.trim())
      throw new ChatRoomError("INVALID_INPUT", "Allowed root is required");
    const canonical = await realpath(expandHome(input)).catch(() => {
      throw new ChatRoomError(
        "NOT_FOUND",
        `Allowed root does not exist: ${input}`,
      );
    });
    if (!this.allowedRoots.includes(canonical))
      throw new ChatRoomError(
        "FORBIDDEN",
        "Project can only be created directly under a configured allowed root",
      );
    return canonical;
  }
}

function validateProjectName(input: string): string {
  if (typeof input !== "string")
    throw new ChatRoomError("INVALID_INPUT", "Project name is required");
  const name = input.trim();
  if (
    !name ||
    name === "." ||
    name === ".." ||
    name.startsWith(".") ||
    name.includes("/") ||
    name.includes("\\") ||
    name.includes("\0")
  )
    throw new ChatRoomError("INVALID_INPUT", "Invalid project name");
  return name;
}

function expandHome(input: string): string {
  if (input === "~") return os.homedir();
  if (input.startsWith("~/") || input.startsWith("~\\"))
    return path.join(os.homedir(), input.slice(2));
  return path.resolve(input);
}
