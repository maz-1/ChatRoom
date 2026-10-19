import { createServiceToken } from "./service-registry.js";

export interface WorkspaceMcpPolicy {
  enabled?: boolean;
  enabledTools?: string[];
  disabledTools?: string[];
  toolTimeoutSec?: number;
}

export interface WorkspaceMcpPolicyUpdate {
  enabled?: boolean | undefined;
  enabledTools?: string[] | null | undefined;
  disabledTools?: string[] | null | undefined;
  toolTimeoutSec?: number | null | undefined;
}

export interface WorkspaceConfig {
  root: string;
  configPath: string;
  disabledMcpServers: string[];
  mcpServers: Record<string, WorkspaceMcpPolicy>;
  skillsConfig: WorkspaceSkillOverride[];
}

export interface WorkspaceSkillOverride {
  path: string;
  enabled: boolean;
}

export interface WorkspaceConfigAccess {
  read(root: string): Promise<WorkspaceConfig>;
  setMcpEnabled(
    root: string,
    server: string,
    enabled: boolean,
  ): Promise<WorkspaceConfig>;
  setMcpSettings(
    root: string,
    server: string,
    update: WorkspaceMcpPolicyUpdate,
  ): Promise<WorkspaceConfig>;
  setSkillEnabled(
    root: string,
    skillPath: string,
    enabled: boolean,
  ): Promise<WorkspaceConfig>;
}

export const WorkspaceConfigAccessToken =
  createServiceToken<WorkspaceConfigAccess>("workspace-config-access");
