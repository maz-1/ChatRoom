import { GitAccessToken } from "#app/git-access";
import { WorkspaceAccessToken } from "#app/workspace-access";
import { WorkspaceConfigAccessToken } from "#app/workspace-config-access";
import type { InternalPlugin } from "#plugins/types";
import { createServiceToken } from "#app/service-registry";
import { registerWorkspaceTools } from "./mcp.js";
import { WorkspaceService } from "./workspace-service.js";
import { WorkspaceBlacklistRepository } from "./workspace-blacklist-repository.js";

export const WorkspaceServiceToken =
  createServiceToken<WorkspaceService>("workspace");

export function createWorkspacePlugin(): InternalPlugin {
  let service: WorkspaceService | null = null;
  let webBaseUrl: () => string | null = () => null;
  return {
    id: "workspace",
    async activate(context) {
      webBaseUrl = () => context.externalAccess.publicWebBaseUrl();
      service = await WorkspaceService.create(
        context.config.allowedRoots,
        context.services.require(GitAccessToken),
        new WorkspaceBlacklistRepository(context.database),
      );
      context.services.provide(WorkspaceServiceToken, service);
      context.services.provide(WorkspaceAccessToken, service);
      context.services.provide(WorkspaceConfigAccessToken, service.config);
    },
    registerMcp(mcp) {
      if (!service) throw new Error("Workspace plugin is not active");
      registerWorkspaceTools(mcp, service, webBaseUrl);
    },
    deactivate() {
      service = null;
      webBaseUrl = () => null;
    },
  };
}
