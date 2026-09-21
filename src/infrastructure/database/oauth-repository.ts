import type {
  OAuthClientRecord,
  OAuthCodeRecord,
  OAuthStateRepository,
  OAuthTokenRecord,
} from "#core/auth/repository";
import type { AppDatabase } from "./app-database.js";

export class OAuthRepository implements OAuthStateRepository {
  constructor(private readonly database: AppDatabase) {}

  createClient(client: OAuthClientRecord): void {
    this.database.raw
      .prepare(
        "INSERT INTO oauth_clients(client_id,name,redirect_uris_json,created_at,disabled_at,note) VALUES(?,?,?,?,?,?)",
      )
      .run(
        client.clientId,
        client.name,
        JSON.stringify(client.redirectUris),
        client.createdAt,
        client.disabledAt,
        client.note,
      );
  }

  getClient(clientId: string): OAuthClientRecord | null {
    const row = this.database.raw
      .prepare("SELECT * FROM oauth_clients WHERE client_id=?")
      .get(clientId) as OAuthClientRow | undefined;
    return row ? clientFromRow(row) : null;
  }

  listClients(): OAuthClientRecord[] {
    return (
      this.database.raw
        .prepare("SELECT * FROM oauth_clients ORDER BY created_at DESC")
        .all() as unknown as OAuthClientRow[]
    ).map(clientFromRow);
  }

  setClientDisabled(
    clientId: string,
    disabledAt: string | null,
  ): OAuthClientRecord | null {
    const updated = this.database.raw
      .prepare("UPDATE oauth_clients SET disabled_at=? WHERE client_id=?")
      .run(disabledAt, clientId);
    return Number(updated.changes) === 1 ? this.getClient(clientId) : null;
  }

  setClientNote(clientId: string, note: string): OAuthClientRecord | null {
    const updated = this.database.raw
      .prepare("UPDATE oauth_clients SET note=? WHERE client_id=?")
      .run(note, clientId);
    return Number(updated.changes) === 1 ? this.getClient(clientId) : null;
  }

  deleteClient(clientId: string): boolean {
    const deleted = this.database.raw
      .prepare("DELETE FROM oauth_clients WHERE client_id=?")
      .run(clientId);
    return Number(deleted.changes) === 1;
  }

  createCode(code: OAuthCodeRecord): void {
    this.database.raw
      .prepare(
        "INSERT INTO oauth_codes(code_hash,client_id,redirect_uri,code_challenge,scope,expires_at,used_at) VALUES(?,?,?,?,?,?,NULL)",
      )
      .run(
        code.codeHash,
        code.clientId,
        code.redirectUri,
        code.codeChallenge,
        code.scope,
        code.expiresAt,
      );
  }

  getCode(codeHash: string): OAuthCodeRecord | null {
    const row = this.database.raw
      .prepare(
        "SELECT * FROM oauth_codes WHERE code_hash=? AND used_at IS NULL",
      )
      .get(codeHash) as OAuthCodeRow | undefined;
    return row ? codeFromRow(row) : null;
  }

  consumeCode(codeHash: string, now: string): OAuthCodeRecord | null {
    this.database.raw.exec("BEGIN IMMEDIATE");
    try {
      const row = this.database.raw
        .prepare(
          "SELECT * FROM oauth_codes WHERE code_hash=? AND used_at IS NULL AND expires_at > ?",
        )
        .get(codeHash, now) as OAuthCodeRow | undefined;
      if (!row) {
        this.database.raw.exec("COMMIT");
        return null;
      }
      const update = this.database.raw
        .prepare(
          "UPDATE oauth_codes SET used_at=? WHERE code_hash=? AND used_at IS NULL AND expires_at > ?",
        )
        .run(now, codeHash, now);
      if (Number(update.changes) !== 1) {
        this.database.raw.exec("ROLLBACK");
        return null;
      }
      this.database.raw.exec("COMMIT");
      return codeFromRow({ ...row, used_at: now });
    } catch (error) {
      this.database.raw.exec("ROLLBACK");
      throw error;
    }
  }

  createToken(
    tokenHash: string,
    clientId: string,
    scope: string,
    expiresAt: string,
  ): void {
    this.database.raw
      .prepare(
        "INSERT INTO oauth_tokens(token_hash,client_id,scope,expires_at,revoked_at,created_at) VALUES(?,?,?,?,NULL,?)",
      )
      .run(tokenHash, clientId, scope, expiresAt, new Date().toISOString());
  }

  createRefreshToken(
    tokenHash: string,
    clientId: string,
    scope: string,
    expiresAt: string,
  ): void {
    this.database.raw
      .prepare(
        "INSERT INTO oauth_refresh_tokens(token_hash,client_id,scope,expires_at,revoked_at,created_at) VALUES(?,?,?,?,NULL,?)",
      )
      .run(tokenHash, clientId, scope, expiresAt, new Date().toISOString());
  }

  consumeRefreshToken(
    tokenHash: string,
    clientId?: string,
  ): { clientId: string; scope: string } | null {
    this.database.raw.exec("BEGIN IMMEDIATE");
    try {
      const now = new Date().toISOString();
      const row = this.database.raw
        .prepare(
          clientId
            ? "SELECT client_id,scope FROM oauth_refresh_tokens WHERE token_hash=? AND client_id=? AND revoked_at IS NULL AND expires_at > ?"
            : "SELECT client_id,scope FROM oauth_refresh_tokens WHERE token_hash=? AND revoked_at IS NULL AND expires_at > ?",
        )
        .get(...(clientId ? [tokenHash, clientId, now] : [tokenHash, now])) as
        { client_id: string; scope: string } | undefined;
      if (!row) {
        this.database.raw.exec("COMMIT");
        return null;
      }
      const update = this.database.raw
        .prepare(
          "UPDATE oauth_refresh_tokens SET revoked_at=? WHERE token_hash=? AND revoked_at IS NULL",
        )
        .run(now, tokenHash);
      if (Number(update.changes) !== 1) {
        this.database.raw.exec("ROLLBACK");
        return null;
      }
      this.database.raw.exec("COMMIT");
      return { clientId: row.client_id, scope: row.scope };
    } catch (error) {
      this.database.raw.exec("ROLLBACK");
      throw error;
    }
  }

  tokenInfo(tokenHash: string): OAuthTokenRecord | null {
    const row = this.database.raw
      .prepare(
        "SELECT client_id,scope,expires_at FROM oauth_tokens WHERE token_hash=? AND revoked_at IS NULL AND expires_at > ?",
      )
      .get(tokenHash, new Date().toISOString()) as
      { client_id: string; scope: string; expires_at: string } | undefined;
    return row
      ? { clientId: row.client_id, scope: row.scope, expiresAt: row.expires_at }
      : null;
  }

  revokeToken(tokenHash: string): void {
    const now = new Date().toISOString();
    this.database.raw
      .prepare("UPDATE oauth_tokens SET revoked_at=? WHERE token_hash=?")
      .run(now, tokenHash);
    this.database.raw
      .prepare(
        "UPDATE oauth_refresh_tokens SET revoked_at=? WHERE token_hash=?",
      )
      .run(now, tokenHash);
  }

  prune(now: string): number {
    let deleted = 0;
    deleted += Number(
      this.database.raw
        .prepare(
          "DELETE FROM oauth_codes WHERE expires_at <= ? OR used_at IS NOT NULL",
        )
        .run(now).changes,
    );
    deleted += Number(
      this.database.raw
        .prepare(
          "DELETE FROM oauth_tokens WHERE expires_at <= ? OR revoked_at IS NOT NULL",
        )
        .run(now).changes,
    );
    deleted += Number(
      this.database.raw
        .prepare(
          "DELETE FROM oauth_refresh_tokens WHERE expires_at <= ? OR revoked_at IS NOT NULL",
        )
        .run(now).changes,
    );
    return deleted;
  }
}

interface OAuthClientRow {
  client_id: string;
  name: string;
  redirect_uris_json: string;
  created_at: string;
  disabled_at: string | null;
  note: string;
}

interface OAuthCodeRow {
  code_hash: string;
  client_id: string;
  redirect_uri: string;
  code_challenge: string;
  scope: string;
  expires_at: string;
  used_at: string | null;
}

function clientFromRow(row: OAuthClientRow): OAuthClientRecord {
  return {
    clientId: row.client_id,
    name: row.name,
    redirectUris: JSON.parse(row.redirect_uris_json) as string[],
    createdAt: row.created_at,
    disabledAt: row.disabled_at,
    note: row.note,
  };
}

function codeFromRow(row: OAuthCodeRow): OAuthCodeRecord {
  return {
    codeHash: row.code_hash,
    clientId: row.client_id,
    redirectUri: row.redirect_uri,
    codeChallenge: row.code_challenge,
    scope: row.scope,
    expiresAt: row.expires_at,
    usedAt: row.used_at,
  };
}
