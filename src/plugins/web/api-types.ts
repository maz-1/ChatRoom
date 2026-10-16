import type { Operation as DomainOperation } from "#core/operations/types";
import type {
  GitBranch,
  GitChange,
  GitCommit,
  GitDiff,
  GitStatus,
} from "#app/git-access";
import type { ProcessSnapshot, ProcessSummary } from "#plugins/process/types";
import type {
  AgentAttachment,
  AgentHistory,
  AgentHistoryUpdate,
  AgentInteractionResponse,
  AgentItem,
  AgentModel,
  AgentProviderId,
  AgentProviderDetails,
  AgentReviewTarget,
  AgentProviderStatus,
  AgentSession,
  AgentTurn,
  AgentTurnSource,
} from "#plugins/agent/types";
import type { McpToolSummary as DomainMcpToolSummary } from "#mcp/server/tool-control";
import type {
  CloudServiceId,
  CloudStatus as DomainCloudStatus,
} from "#plugins/cloud/types";
import type {
  McpServerDetail,
  McpToolSummary as McpProxyToolSummary,
} from "#plugins/mcp-proxy/types";
import type {
  ComputerDisplay,
  ComputerPermission,
  ComputerStatus,
} from "#plugins/computer/types";
import type {
  WorkspaceEntry,
  WorkspaceInfo,
  WorkspaceSkill,
} from "#app/workspace-access";
import type { WorkspaceFile } from "#plugins/workspace/types";

export type {
  AgentAttachment,
  AgentHistory,
  AgentHistoryUpdate,
  AgentInteractionResponse,
  AgentItem,
  AgentModel,
  AgentProviderId,
  AgentProviderDetails,
  AgentReviewTarget,
  AgentProviderStatus,
  AgentSession,
  AgentTurn,
  AgentTurnSource,
  ComputerDisplay,
  ComputerPermission,
  ComputerStatus,
  GitBranch,
  GitChange,
  GitCommit,
  GitDiff,
  GitStatus,
  McpProxyToolSummary,
  McpServerDetail,
  ProcessSnapshot,
  ProcessSummary,
  WorkspaceEntry,
  WorkspaceFile,
  WorkspaceInfo,
  WorkspaceSkill,
};

export interface ComputerPreviewView {
  snapshotId: string;
  revision: number;
  capturedAt: string | null;
  display: ComputerDisplay | null;
  activeApp: string | null;
  activeWindow: string | null;
  cursor: { x: number; y: number } | null;
  elementCount: number;
  screenshot: { mimeType: "image/jpeg" | "image/png" } | null;
}

export type Operation = DomainOperation;

export interface AuthStatus {
  authenticated: boolean;
  passkeyAvailable: boolean;
  passkeyRegistered: boolean;
}

export interface OAuthClientSummary {
  clientId: string;
  name: string;
  redirectUris: string[];
  createdAt: string;
  disabledAt: string | null;
  note: string;
  lastAccessAt: string | null;
}

export interface PasskeySummary {
  id: string;
  name: string;
  lastUsedAt: string;
}

export interface RuntimeStatus {
  version: string;
  mcpRequests: number;
  uptimeMinutes: number;
}

export type { Js2rayStatus } from "#plugins/js2ray/types";

export interface UpdateStatus {
  latestVersion: string | null;
  updateAvailable: boolean;
  releaseUrl: string | null;
}

export type McpToolSummary = DomainMcpToolSummary;
export type CloudService = CloudServiceId;
export type CloudStatus = DomainCloudStatus;

/** MCP bridge view: definitions live in config; enabled state is stored locally. */
export interface McpServersView {
  configPath: string;
  servers: McpServerDetail[];
}

export interface WorkspaceFileContent {
  content: string;
  bytes: number;
  truncated: boolean;
  modifiedAt: string;
}

export interface WorkspaceFilePage {
  items: WorkspaceFile[];
  nextOffset: number | null;
}

export interface WorkspaceProjectFiles {
  paths: string[];
  truncated: boolean;
}

export interface CloudManagementSession {
  url: string;
}

export interface CloudRestoreResult {
  status: CloudStatus;
}

export type { LogLevel, LogPage, LogRecord } from "#core/logging/types";
