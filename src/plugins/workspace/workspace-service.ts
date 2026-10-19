import { mkdir, readdir, realpath } from "node:fs/promises";
import path from "node:path";
import type { GitAccess } from "#app/git-access";
import type {
  WorkspaceAccess,
  WorkspaceEntry,
  WorkspaceInfo,
  WorkspaceSkillSettings,
} from "#app/workspace-access";
import { ChatRoomError } from "#core/errors/chatroom-error";
import { WorkspaceRootPolicy } from "./workspace-root-policy.js";
import {
  readInstructions,
  readPresetPrompt,
  readSkills,
  readSummary,
} from "./metadata.js";
import { WorkspaceFs } from "./workspace-fs.js";
import type { WorkspaceBlacklistRepository } from "./workspace-blacklist-repository.js";
import { normalizeRelativePath } from "#core/fs/safe-directory";
import { WorkspaceConfigService } from "./workspace-config-service.js";
import { skillConfigEnabled } from "./codex-skills-config.js";

export class WorkspaceService implements WorkspaceAccess {
  readonly config: WorkspaceConfigService;
  private constructor(
    private readonly rootsPolicy: WorkspaceRootPolicy,
    private readonly git: Pick<GitAccess, "projectFiles">,
    private readonly blacklist: WorkspaceBlacklistRepository,
  ) {
    this.config = new WorkspaceConfigService((root) => this.fs(root));
  }

  static async create(
    allowedRoots: string[],
    git: Pick<GitAccess, "projectFiles">,
    blacklist: WorkspaceBlacklistRepository,
  ): Promise<WorkspaceService> {
    return new WorkspaceService(
      await WorkspaceRootPolicy.create(allowedRoots),
      git,
      blacklist,
    );
  }

  roots(): string[] {
    return this.rootsPolicy.roots();
  }

  async list(): Promise<WorkspaceEntry[]> {
    const blocked = new Set(
      this.blacklist.list().map((root) => this.blacklist.key(root)),
    );
    const roots = new Set<string>();
    for (const allowedRoot of this.rootsPolicy.roots()) {
      let entries;
      try {
        entries = await readdir(allowedRoot, { withFileTypes: true });
      } catch {
        continue;
      }
      for (const entry of entries) {
        if (
          !entry.isDirectory() ||
          entry.isSymbolicLink() ||
          entry.name.startsWith(".")
        )
          continue;
        const candidate = await realpath(
          path.join(allowedRoot, entry.name),
        ).catch(() => null);
        if (
          candidate &&
          this.rootsPolicy.isWorkspaceRoot(candidate) &&
          !blocked.has(this.blacklist.key(candidate))
        )
          roots.add(candidate);
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

  blockedRoots(): string[] {
    return this.blacklist.list();
  }

  async block(input: string): Promise<string[]> {
    this.blacklist.add(await this.resolve(input));
    return this.blockedRoots();
  }

  unblock(input: string): string[] {
    if (typeof input !== "string" || !input.trim() || !path.isAbsolute(input))
      throw new ChatRoomError(
        "INVALID_INPUT",
        "An absolute workspace root is required",
      );
    // Stale entries must remain removable after their directories are deleted.
    this.blacklist.remove(input);
    return this.blockedRoots();
  }

  async resolve(input: string): Promise<string> {
    return this.rootsPolicy.resolveWorkspace(input);
  }
  async resolveAlias(alias: string): Promise<string> {
    const name = validateProjectName(alias);
    const matches: string[] = [];
    for (const parent of this.rootsPolicy.roots()) {
      const candidate = path.join(parent, name);
      const canonical = await realpath(candidate).catch(() => null);
      if (canonical === candidate) matches.push(candidate);
    }
    if (matches.length === 0)
      throw new ChatRoomError("NOT_FOUND", "Workspace not found");
    if (matches.length > 1)
      throw new ChatRoomError("CONFLICT", "Workspace alias is ambiguous");
    return this.resolve(matches[0]!);
  }

  async fsByAlias(alias: string): Promise<WorkspaceFs> {
    return WorkspaceFs.create(await this.resolveAlias(alias));
  }

  async createProject(
    parentInput: string,
    nameInput: string,
  ): Promise<WorkspaceEntry> {
    const parent = await this.rootsPolicy.resolveAllowedRoot(parentInput);
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

  async projectFiles(input: string, maxFiles = 5000): Promise<string[]> {
    return (await this.projectFilesResult(input, maxFiles)).paths;
  }

  async projectFilesResult(
    input: string,
    maxFiles = 5000,
  ): Promise<{ paths: string[]; truncated: boolean }> {
    const fs = await this.fs(input);
    const limit = Math.min(Math.max(Math.trunc(maxFiles), 1), 9999);
    const gitFiles = await this.git.projectFiles(fs.root, limit + 1);
    if (gitFiles)
      return {
        paths: gitFiles.slice(0, limit),
        truncated: gitFiles.length > limit,
      };

    const entries = await fs.list(".", {
      recursive: true,
      maxEntries: 10_000,
      excludeDirectoryNames: [".chatroom", ".git"],
    });
    const files = entries
      .filter((entry) => entry.type === "file")
      .map((entry) => entry.path);
    return {
      paths: files.slice(0, limit),
      truncated: entries.length >= 10_000 || files.length > limit,
    };
  }

  async info(input: string): Promise<WorkspaceInfo> {
    const root = await this.resolve(input);
    if (this.blacklist.has(root))
      throw new ChatRoomError("FORBIDDEN", "Workspace is blocked");
    const fs = await WorkspaceFs.create(root);
    const [summary, presetPrompt, instructions, skillSettings] =
      await Promise.all([
        readSummary(fs),
        readPresetPrompt(fs),
        readInstructions(fs),
        this.skillSettings(root),
      ]);
    return {
      root,
      name: path.basename(root),
      summary,
      presetPrompt,
      instructions,
      skills: skillSettings.skills
        .filter((skill) => skill.enabled)
        .map(({ enabled: _enabled, ...skill }) => skill),
    };
  }

  async skillSettings(input: string): Promise<WorkspaceSkillSettings> {
    const root = await this.resolve(input);
    if (this.blacklist.has(root))
      throw new ChatRoomError("FORBIDDEN", "Workspace is blocked");
    const fs = await WorkspaceFs.create(root);
    const [skills, config] = await Promise.all([
      readSkills(fs),
      this.config.read(root),
    ]);
    return {
      root,
      configPath: config.configPath,
      skills: skills.map((skill) => ({
        ...skill,
        enabled: skillConfigEnabled(root, config.skillsConfig, skill.path),
      })),
    };
  }

  async setSkillEnabled(
    input: string,
    inputPath: string,
    enabled: boolean,
  ): Promise<WorkspaceSkillSettings> {
    const skillPath = normalizeRelativePath(inputPath);
    const settings = await this.skillSettings(input);
    if (!settings.skills.some((skill) => skill.path === skillPath))
      throw new ChatRoomError("NOT_FOUND", "Workspace skill not found");
    await this.config.setSkillEnabled(settings.root, skillPath, enabled);
    return this.skillSettings(settings.root);
  }

  async fs(input: string): Promise<WorkspaceFs> {
    return WorkspaceFs.create(await this.resolve(input));
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
