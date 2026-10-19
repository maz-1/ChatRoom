import path from "node:path";
import type {
  WorkspaceConfig,
  WorkspaceConfigAccess,
  WorkspaceMcpPolicyUpdate,
} from "#app/workspace-config-access";
import { ChatRoomError } from "#core/errors/chatroom-error";
import type { WorkspaceFs } from "./workspace-fs.js";
import {
  CODEX_CONFIG_PATH,
  parseCodexConfig,
  setCodexMcpSettings,
} from "./codex-config.js";
import { setCodexSkillEnabled } from "./codex-skills-config.js";

export class WorkspaceConfigService implements WorkspaceConfigAccess {
  private readonly writes = new Map<string, Promise<unknown>>();

  constructor(
    private readonly workspaceFs: (root: string) => Promise<WorkspaceFs>,
  ) {}

  async read(root: string): Promise<WorkspaceConfig> {
    const fs = await this.workspaceFs(root);
    await this.writes.get(fs.root)?.catch(() => undefined);
    return this.describe(fs, await this.source(fs));
  }

  async setMcpEnabled(
    root: string,
    server: string,
    enabled: boolean,
  ): Promise<WorkspaceConfig> {
    return this.setMcpSettings(root, server, { enabled });
  }

  async setMcpSettings(
    root: string,
    server: string,
    update: WorkspaceMcpPolicyUpdate,
  ): Promise<WorkspaceConfig> {
    return this.update(root, (source) =>
      setCodexMcpSettings(source, server, update),
    );
  }

  private async update(
    root: string,
    edit: (source: string) => string,
  ): Promise<WorkspaceConfig> {
    const fs = await this.workspaceFs(root);
    const previous = this.writes.get(fs.root) ?? Promise.resolve();
    const pending = previous
      .catch(() => undefined)
      .then(async () => {
        const source = await this.source(fs);
        this.describe(fs, source);
        const content = edit(source);
        const config = this.describe(fs, content);
        if (content !== source) {
          if ((await this.source(fs)) !== source)
            throw new ChatRoomError(
              "CONFLICT",
              "Workspace config changed; reload and try again",
            );
          await fs.write(CODEX_CONFIG_PATH, content);
        }
        return config;
      });
    this.writes.set(fs.root, pending);
    try {
      return await pending;
    } finally {
      if (this.writes.get(fs.root) === pending) this.writes.delete(fs.root);
    }
  }

  private describe(fs: WorkspaceFs, source: string): WorkspaceConfig {
    const { disabledMcpServers, mcpServers, skillsConfig } =
      parseCodexConfig(source);
    return {
      root: fs.root,
      configPath: path.join(fs.root, CODEX_CONFIG_PATH),
      disabledMcpServers,
      mcpServers,
      skillsConfig,
    };
  }

  async setSkillEnabled(
    root: string,
    skillPath: string,
    enabled: boolean,
  ): Promise<WorkspaceConfig> {
    const fs = await this.workspaceFs(root);
    return this.update(fs.root, (source) =>
      setCodexSkillEnabled(source, fs.root, skillPath, enabled),
    );
  }

  private async source(fs: WorkspaceFs): Promise<string> {
    try {
      const file = await fs.read(CODEX_CONFIG_PATH);
      if (file.truncated)
        throw new ChatRoomError(
          "INVALID_INPUT",
          `${CODEX_CONFIG_PATH} exceeds 1 MiB`,
        );
      return file.content;
    } catch (error) {
      if (error instanceof ChatRoomError && error.code === "NOT_FOUND")
        return "";
      throw error;
    }
  }
}
