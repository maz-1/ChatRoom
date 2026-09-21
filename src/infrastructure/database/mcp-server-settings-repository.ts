import type { McpServerSettingStore } from "#plugins/mcp-proxy/mcp-proxy-service";
import type { AppDatabase } from "./app-database.js";

export class McpServerSettingsRepository implements McpServerSettingStore {
  constructor(private readonly database: AppDatabase) {}

  disabledServers(): string[] {
    return (
      this.database.raw
        .prepare("SELECT server_name FROM mcp_server_settings WHERE enabled=0")
        .all() as unknown as Array<{ server_name: string }>
    ).map((row) => row.server_name);
  }

  setEnabled(name: string, enabled: boolean): void {
    this.database.raw
      .prepare(
        `
        INSERT INTO mcp_server_settings(server_name, enabled, updated_at)
        VALUES(?,?,?)
        ON CONFLICT(server_name) DO UPDATE SET
          enabled=excluded.enabled,
          updated_at=excluded.updated_at
      `,
      )
      .run(name, enabled ? 1 : 0, new Date().toISOString());
  }
}
