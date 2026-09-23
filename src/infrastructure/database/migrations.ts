import type { DatabaseSync } from "node:sqlite";
import { DATABASE_SCHEMA_VERSION } from "./schema.js";

export function migrateDatabase(db: DatabaseSync): void {
  let version = userVersion(db);
  if (version > DATABASE_SCHEMA_VERSION)
    throw new Error(
      `Database schema ${version} is newer than this ChatRoom build (${DATABASE_SCHEMA_VERSION})`,
    );

  while (version < DATABASE_SCHEMA_VERSION) {
    switch (version) {
      case 0:
        migrate0To1(db);
        version = 1;
        break;
      case 1:
        migrate1To2(db);
        version = 2;
        break;
      case 2:
        migrate2To3(db);
        version = 3;
        break;
      case 3:
        migrate3To4(db);
        version = 4;
        break;
      case 4:
        migrate4To5(db);
        version = 5;
        break;
      case 5:
        migrate5To6(db);
        version = 6;
        break;
      default:
        throw new Error(`Unsupported ChatRoom database schema: ${version}`);
    }
  }
}

function migrate0To1(db: DatabaseSync): void {
  db.exec("BEGIN IMMEDIATE;");
  try {
    db.exec(`
      DROP TABLE IF EXISTS workspaces;
      DROP TABLE IF EXISTS audit_events;
      DROP TABLE IF EXISTS agent_runs;
      DROP INDEX IF EXISTS operations_workspace_idx;
    `);

    const operationColumns = db
      .prepare("PRAGMA table_info(operations)")
      .all() as unknown as Array<{ name: string }>;
    if (operationColumns.some((column) => column.name === "workspace_id"))
      db.exec("ALTER TABLE operations DROP COLUMN workspace_id;");

    db.exec("PRAGMA user_version = 1; COMMIT;");
  } catch (error) {
    db.exec("ROLLBACK;");
    throw error;
  }
}

function migrate1To2(db: DatabaseSync): void {
  db.exec("BEGIN IMMEDIATE;");
  try {
    db.exec(`
      CREATE TABLE IF NOT EXISTS mcp_tool_settings (
        tool_name TEXT PRIMARY KEY,
        enabled INTEGER NOT NULL CHECK(enabled IN (0, 1)),
        updated_at TEXT NOT NULL
      );
      PRAGMA user_version = 2;
      COMMIT;
    `);
  } catch (error) {
    db.exec("ROLLBACK;");
    throw error;
  }
}

function migrate2To3(db: DatabaseSync): void {
  db.exec("BEGIN IMMEDIATE;");
  try {
    db.exec(`
      CREATE TABLE IF NOT EXISTS mcp_server_settings (
        server_name TEXT PRIMARY KEY,
        enabled INTEGER NOT NULL CHECK(enabled IN (0, 1)),
        updated_at TEXT NOT NULL
      );
      PRAGMA user_version = 3;
      COMMIT;
    `);
  } catch (error) {
    db.exec("ROLLBACK;");
    throw error;
  }
}

function migrate3To4(db: DatabaseSync): void {
  db.exec("BEGIN IMMEDIATE;");
  try {
    const columns = db
      .prepare("PRAGMA table_info(oauth_clients)")
      .all() as unknown as Array<{ name: string }>;
    if (columns.length > 0) {
      if (!columns.some((column) => column.name === "disabled_at"))
        db.exec("ALTER TABLE oauth_clients ADD COLUMN disabled_at TEXT;");
    }
    db.exec("PRAGMA user_version = 4; COMMIT;");
  } catch (error) {
    db.exec("ROLLBACK;");
    throw error;
  }
}

function migrate4To5(db: DatabaseSync): void {
  db.exec("BEGIN IMMEDIATE;");
  try {
    const columns = db
      .prepare("PRAGMA table_info(oauth_clients)")
      .all() as unknown as Array<{ name: string }>;
    if (columns.length > 0 && !columns.some((column) => column.name === "note"))
      db.exec(
        "ALTER TABLE oauth_clients ADD COLUMN note TEXT NOT NULL DEFAULT '';",
      );
    db.exec("PRAGMA user_version = 5; COMMIT;");
  } catch (error) {
    db.exec("ROLLBACK;");
    throw error;
  }
}

function migrate5To6(db: DatabaseSync): void {
  db.exec("BEGIN IMMEDIATE;");
  try {
    db.exec(`
      CREATE TABLE IF NOT EXISTS workspace_blacklist (
        path_key TEXT PRIMARY KEY,
        root TEXT NOT NULL
      );
      PRAGMA user_version = 6;
      COMMIT;
    `);
  } catch (error) {
    db.exec("ROLLBACK;");
    throw error;
  }
}

function userVersion(db: DatabaseSync): number {
  const row = db.prepare("PRAGMA user_version").get() as
    { user_version?: unknown } | undefined;
  const version = Number(row?.user_version ?? 0);
  if (!Number.isInteger(version) || version < 0)
    throw new Error(`Invalid ChatRoom database schema version: ${version}`);
  return version;
}
