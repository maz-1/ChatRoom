import path from "node:path";
import { platformSecurityDefaults } from "#config/platform-paths";
import type { AppDatabase } from "#infrastructure/database/app-database";

export class WorkspaceBlacklistRepository {
  constructor(private readonly database: AppDatabase) {}

  list(): string[] {
    const rows = this.database.raw
      .prepare("SELECT root FROM workspace_blacklist ORDER BY root")
      .all() as { root: string }[];
    return rows.map((row) => row.root);
  }

  add(root: string): void {
    this.database.raw
      .prepare(
        "INSERT INTO workspace_blacklist(path_key, root) VALUES (?, ?) ON CONFLICT(path_key) DO NOTHING",
      )
      .run(this.key(root), root);
  }

  has(root: string): boolean {
    return (
      this.database.raw
        .prepare("SELECT 1 FROM workspace_blacklist WHERE path_key = ?")
        .get(this.key(root)) !== undefined
    );
  }

  remove(root: string): void {
    this.database.raw
      .prepare("DELETE FROM workspace_blacklist WHERE path_key = ?")
      .run(this.key(root));
  }

  key(root: string): string {
    const normalized = path.resolve(root);
    return platformSecurityDefaults().caseFoldCredentialPaths
      ? normalized.toLowerCase()
      : normalized;
  }
}
