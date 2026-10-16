import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import test from "node:test";
import { AppDatabase } from "../../src/infrastructure/database/app-database.js";

test("AppDatabase initializes and reopens the current schema", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "chatroom-db-"));
  const filename = path.join(root, "app.sqlite");
  try {
    const database = new AppDatabase(filename);
    database.raw.exec(
      "INSERT INTO web_sessions VALUES ('persisted', 'expiry', 'created')",
    );
    database.close();

    const reopened = new AppDatabase(filename);
    try {
      assert.equal(
        reopened.raw.prepare("SELECT token_hash FROM web_sessions").get()
          ?.token_hash,
        "persisted",
      );
    } finally {
      reopened.close();
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("AppDatabase upgrades an unversioned upstream database without losing Agent or OAuth data", async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), "chatroom-db-upstream-"));
  const filename = path.join(root, "app.sqlite");
  try {
    const original = new DatabaseSync(filename);
    try {
      original.exec(`
        CREATE TABLE oauth_clients (
          client_id TEXT PRIMARY KEY,
          name TEXT NOT NULL,
          redirect_uris_json TEXT NOT NULL,
          created_at TEXT NOT NULL
        );
        INSERT INTO oauth_clients VALUES ('existing-client', 'Existing', '[]', 'created');
        CREATE TABLE agent_sessions (
          id TEXT PRIMARY KEY,
          workspace_root TEXT NOT NULL,
          provider TEXT NOT NULL,
          status TEXT NOT NULL,
          created_at TEXT NOT NULL,
          updated_at TEXT NOT NULL
        );
        INSERT INTO agent_sessions VALUES ('existing-agent', 'workspace', 'codex', 'idle', 'created', 'updated');
      `);
    } finally {
      original.close();
    }

    const upgraded = new AppDatabase(filename);
    try {
      assert.equal(
        upgraded.raw.prepare("SELECT id FROM agent_sessions").get()?.id,
        "existing-agent",
      );
      const client = upgraded.raw
        .prepare("SELECT client_id, disabled_at, note FROM oauth_clients")
        .get();
      assert.equal(client?.client_id, "existing-client");
      assert.equal(client?.disabled_at, null);
      assert.equal(client?.note, "");
      for (const table of [
        "workspace_blacklist",
        "mcp_server_settings",
        "oauth_registration_settings",
      ]) {
        assert.equal(
          upgraded.raw.prepare(`SELECT COUNT(*) AS count FROM ${table}`).get()
            ?.count,
          0,
        );
      }
      assert.equal(
        upgraded.raw.prepare("PRAGMA user_version").get()?.user_version,
        7,
      );
    } finally {
      upgraded.close();
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
