export interface McpStdioServerConfig {
  type: "stdio";
  command: string;
  args: string[];
  env: Record<string, string>;
  cwd: string | null;
}

export interface McpHttpServerConfig {
  type: "http";
  url: string;
  headers: Record<string, string>;
  proxy: string | null;
}

export type McpServerConfig = McpStdioServerConfig | McpHttpServerConfig;

export interface McpConfig {
  callTimeoutMs: number;
  maxResultBytes: number;
  servers: Record<string, McpServerConfig>;
}

export interface ChatRoomConfig {
  allowedRoots: string[];
  dataDir: string;
  databasePath: string;
  server: {
    host: string;
    port: number;
  };
  auth: {
    localWebAuth: boolean;
    mcpPublicBaseUrl: string | null;
    webPublicBaseUrl: string | null;
    allowedRedirectHosts: string[];
  };
  http: {
    defaultTimeoutMs: number;
    maxTimeoutMs: number;
    maxResponseBytes: number;
  };
  operations: {
    maxPayloadBytes: number;
  };
  mcp: McpConfig;
  process: {
    maxOutputBytes: number;
    defaultTimeoutMs: number;
    maxCompletedProcesses: number;
  };
}
