import { createServiceToken } from "#app/service-registry";

export interface WorkspaceEntry {
  root: string;
  name: string;
  summary: string | null;
}

export interface WorkspaceSkill {
  name: string;
  description: string;
  path: string;
}

export interface WorkspaceSkillSetting extends WorkspaceSkill {
  enabled: boolean;
}

export interface WorkspaceSkillSettings {
  root: string;
  configPath: string;
  skills: WorkspaceSkillSetting[];
}

export interface WorkspaceInfo extends WorkspaceEntry {
  presetPrompt: string | null;
  instructions: string | null;
  skills: WorkspaceSkill[];
}

export interface WorkspaceAccess {
  roots(): string[];
  list(): Promise<WorkspaceEntry[]>;
  resolve(input: string): Promise<string>;
  createProject(
    parentInput: string,
    nameInput: string,
  ): Promise<WorkspaceEntry>;
  projectFiles(input: string, maxFiles?: number): Promise<string[]>;
  info(input: string): Promise<WorkspaceInfo>;
}

export const WorkspaceAccessToken =
  createServiceToken<WorkspaceAccess>("workspace-access");
