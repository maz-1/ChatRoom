import type { ChatRoomConfig } from "#config/types";
import { AppDatabase } from "#infrastructure/database/app-database";
import { OperationRepository } from "#infrastructure/database/operation-repository";
import { McpToolSettingsRepository } from "#infrastructure/database/mcp-tool-settings-repository";
import { McpServerSettingsRepository } from "#infrastructure/database/mcp-server-settings-repository";
import { OAuthRepository } from "#infrastructure/database/oauth-repository";
import { PasskeyRepository } from "#infrastructure/database/passkey-repository";
import { WebSessionRepository } from "#infrastructure/database/web-session-repository";
import { RuntimeEventBus } from "./event-bus.js";
import { OperationLog } from "#operations/operation-log";
import { AuthService } from "#auth/auth-service";
import { PasskeyService } from "#auth/passkey-service";
import { ExternalAccessRegistry } from "./external-access-registry.js";
import { ServiceRegistry } from "#plugins/types";
import { PluginManager } from "#plugins/plugin-manager";
import { createWorkspacePlugin } from "#plugins/workspace/plugin";
import { createGitPlugin } from "#plugins/git/plugin";
import { createProcessPlugin, ProcessService } from "#plugins/process/plugin";
import { createHttpPlugin, HttpFetcherToken } from "#plugins/http/plugin";
import {
  createMcpProxyPlugin,
  McpProxyServiceToken,
} from "#plugins/mcp-proxy/plugin";
import { createCloudPlugin, CloudService } from "#plugins/cloud/plugin";
import {
  createComputerPlugin,
  ComputerServiceToken,
} from "#plugins/computer/plugin";
import { createWebPlugin, WebServiceToken } from "#plugins/web/plugin";
import { createChatRoomMcpHandler } from "#mcp/server/create-mcp-server";
import { McpToolControl } from "#mcp/server/tool-control";
import { HttpServer } from "#infrastructure/http/http-server";
import type { LogService } from "#core/logging/types";

export interface RuntimeSecrets {
  ownerToken: string | null;
}

export interface ApplicationComponents {
  database: AppDatabase;
  eventBus: RuntimeEventBus;
  operations: OperationLog;
  plugins: PluginManager;
  application: import("#plugins/web/runtime").WebRuntime;
  processes: import("#plugins/process/process-supervisor").ProcessSupervisor;
  fetcher: import("#plugins/http/http-fetcher").HttpFetcher;
  mcpProxy: import("#plugins/mcp-proxy/mcp-proxy-service").McpProxyService;
  cloud: import("#plugins/cloud/controller").CloudController;
  computer: import("#plugins/computer/computer-service").ComputerService;
  http: HttpServer;
  logs: LogService;
}

export async function createApplication(
  config: ChatRoomConfig,
  logs: LogService,
  secrets: RuntimeSecrets = { ownerToken: null },
): Promise<ApplicationComponents> {
  const database = new AppDatabase(config.databasePath);
  try {
    const eventBus = new RuntimeEventBus();
    const operations = new OperationLog(
      new OperationRepository(database),
      eventBus,
      config.operations.maxPayloadBytes,
    );
    operations.reconcileInterrupted();
    const mcpTools = new McpToolControl(
      new McpToolSettingsRepository(database),
    );
    const externalAccess = new ExternalAccessRegistry(config.auth);
    const auth = new AuthService(
      new OAuthRepository(database),
      new WebSessionRepository(database),
      config.auth,
      secrets.ownerToken,
    );
    const passkeys = new PasskeyService(new PasskeyRepository(database));
    const services = new ServiceRegistry();
    const plugins = new PluginManager(
      {
        config,
        database,
        operations,
        mcpTools,
        events: eventBus,
        externalAccess,
        services,
        logs,
      },
      [
        createWorkspacePlugin(),
        createGitPlugin(),
        createProcessPlugin(),
        createHttpPlugin(),
        createMcpProxyPlugin(new McpServerSettingsRepository(database)),
        createComputerPlugin(),
        createCloudPlugin(),
        createWebPlugin(),
      ],
    );
    await plugins.start();
    const web = services.require(WebServiceToken);
    const processes = services.require(ProcessService);
    const fetcher = services.require(HttpFetcherToken);
    const mcpProxy = services.require(McpProxyServiceToken);
    const cloud = services.require(CloudService);
    const computer = services.require(ComputerServiceToken);
    const mcp = createChatRoomMcpHandler(plugins);
    const http = new HttpServer(
      config,
      web.application,
      eventBus,
      auth,
      passkeys,
      mcp,
      externalAccess,
      cloud,
      logs,
    );
    return {
      database,
      eventBus,
      operations,
      plugins,
      application: web.application,
      processes,
      fetcher,
      mcpProxy,
      cloud,
      computer,
      http,
      logs,
    };
  } catch (error) {
    database.close();
    throw error;
  }
}
