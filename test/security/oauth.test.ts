import assert from "node:assert/strict";
import test from "node:test";
import { createHash } from "node:crypto";
import { AppDatabase } from "../../src/infrastructure/database/app-database.js";
import { OAuthRepository } from "../../src/infrastructure/database/oauth-repository.js";
import { WebSessionRepository } from "../../src/infrastructure/database/web-session-repository.js";
import { AuthService } from "../../src/auth/auth-service.js";
import { mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";

test("OAuth authorization code uses PKCE, hashing, expiry model and one-time consumption", async () => {
  const dir = await mkdtemp(path.join(os.tmpdir(), "chatroom-oauth-"));
  const db = new AppDatabase(path.join(dir, "auth.sqlite"));
  try {
    const repository = new OAuthRepository(db);
    const auth = new AuthService(
      repository,
      new WebSessionRepository(db),
      {
        localWebAuth: false,
        mcpPublicBaseUrl: null,
        webPublicBaseUrl: null,
        allowedRedirectHosts: ["127.0.0.1"],
      },
      "owner-secret",
    );
    assert.throws(() =>
      auth.registerClient("Invalid Scheme", ["file://127.0.0.1/callback"]),
    );
    assert.throws(() =>
      auth.registerClient("Fragment Redirect", [
        "http://127.0.0.1/callback#fragment",
      ]),
    );
    const client = auth.registerClient("Test MCP", [
      "http://127.0.0.1/callback",
    ]);
    const verifier = "A".repeat(64);
    const challenge = createHash("sha256").update(verifier).digest("base64url");
    const request = {
      clientId: client.client_id,
      redirectUri: "http://127.0.0.1/callback",
      codeChallenge: challenge,
      codeChallengeMethod: "S256",
      scopes: ["mcp"],
    };
    const code = auth.approveAuthorization(request, "owner-secret");
    assert.equal(
      JSON.stringify(repository.getClient(client.client_id)).includes(
        "owner-secret",
      ),
      false,
    );
    const wrongVerifier = `${verifier.slice(0, -1)}B`;
    assert.throws(() =>
      auth.exchangeCode({
        code,
        clientId: client.client_id,
        redirectUri: request.redirectUri,
        codeVerifier: wrongVerifier,
      }),
    );
    const token = auth.exchangeCode({
      code,
      clientId: client.client_id,
      redirectUri: request.redirectUri,
      codeVerifier: verifier,
    });
    const refreshToken = "refresh_token" in token ? token.refresh_token : null;
    assert.equal(typeof refreshToken, "string");
    assert.equal(
      auth.verifyMcpToken(token.access_token)?.clientId,
      client.client_id,
    );
    const refreshed = auth.exchangeRefreshToken({
      refreshToken: refreshToken!,
      clientId: client.client_id,
    });
    assert.equal(
      auth.verifyMcpToken(refreshed.access_token)?.clientId,
      client.client_id,
    );
    assert.equal(
      "refresh_token" in refreshed && typeof refreshed.refresh_token,
      "string",
    );
    assert.throws(() =>
      auth.exchangeRefreshToken({
        refreshToken: refreshToken!,
        clientId: client.client_id,
      }),
    );
    const metadata = auth.authorizationServerMetadata(
      "https://chatroom.example.com",
    );
    assert.deepEqual(metadata.grant_types_supported, [
      "authorization_code",
      "refresh_token",
    ]);
    assert.deepEqual(metadata.scopes_supported, ["mcp"]);
    assert.throws(() =>
      auth.exchangeCode({
        code,
        clientId: client.client_id,
        redirectUri: request.redirectUri,
        codeVerifier: verifier,
      }),
    );
  } finally {
    db.close();
    await rm(dir, { recursive: true, force: true });
  }
});

test("OAuth client management tracks runtime access, disables calls without invalidating tokens, and revokes credentials", async () => {
  const dir = await mkdtemp(
    path.join(os.tmpdir(), "chatroom-oauth-management-"),
  );
  const db = new AppDatabase(path.join(dir, "auth.sqlite"));
  try {
    const repository = new OAuthRepository(db);
    const sessions = new WebSessionRepository(db);
    const config = {
      localWebAuth: false,
      mcpPublicBaseUrl: null,
      webPublicBaseUrl: null,
      allowedRedirectHosts: ["127.0.0.1"],
    };
    const auth = new AuthService(repository, sessions, config, "owner-secret");
    const client = auth.registerClient("Managed MCP", [
      "http://127.0.0.1/callback",
    ]);

    const verifier = "B".repeat(64);
    const challenge = createHash("sha256").update(verifier).digest("base64url");
    const request = {
      clientId: client.client_id,
      redirectUri: "http://127.0.0.1/callback",
      codeChallenge: challenge,
      codeChallengeMethod: "S256",
      scopes: ["mcp"],
    };
    const code = auth.approveAuthorization(request, "owner-secret");
    const tokens = auth.exchangeCode({
      code,
      clientId: client.client_id,
      redirectUri: request.redirectUri,
      codeVerifier: verifier,
    });

    assert.equal(auth.listOAuthClients()[0]?.lastAccessAt, null);
    assert.equal(
      auth.verifyMcpToken(tokens.access_token)?.clientId,
      client.client_id,
    );
    assert.ok(auth.listOAuthClients()[0]?.lastAccessAt);

    const restartedAuth = new AuthService(
      repository,
      new WebSessionRepository(db),
      config,
      "owner-secret",
    );
    assert.equal(
      restartedAuth.listOAuthClients()[0]?.lastAccessAt,
      null,
      "recent access is runtime-only and is not persisted",
    );
    assert.equal(restartedAuth.listOAuthClients()[0]?.note, "");

    const noted = auth.setOAuthClientNote(
      client.client_id,
      "  Primary desktop client  ",
    );
    assert.equal(noted.note, "Primary desktop client");
    assert.equal(
      new AuthService(
        repository,
        new WebSessionRepository(db),
        config,
        "owner-secret",
      ).listOAuthClients()[0]?.note,
      "Primary desktop client",
      "client notes are persisted",
    );
    assert.throws(() =>
      auth.setOAuthClientNote(client.client_id, "x".repeat(1001)),
    );

    const disabled = auth.setOAuthClientDisabled(client.client_id, true);
    assert.ok(disabled.disabledAt);
    assert.equal(auth.isOAuthClientDisabled(client.client_id), true);
    assert.equal(
      auth.verifyMcpToken(tokens.access_token)?.clientId,
      client.client_id,
      "disabling a client keeps its access token valid for MCP discovery",
    );

    const enabled = auth.setOAuthClientDisabled(client.client_id, false);
    assert.equal(enabled.disabledAt, null);
    assert.equal(auth.isOAuthClientDisabled(client.client_id), false);

    const refreshToken =
      "refresh_token" in tokens ? tokens.refresh_token : undefined;
    assert.equal(typeof refreshToken, "string");

    const revoked = auth.revokeOAuthClient(client.client_id);
    assert.deepEqual(revoked, { clientId: client.client_id });
    assert.equal(repository.getClient(client.client_id), null);
    assert.equal(auth.verifyMcpToken(tokens.access_token), null);
    assert.throws(() =>
      auth.exchangeRefreshToken({
        refreshToken: refreshToken!,
        clientId: client.client_id,
      }),
    );
    assert.throws(() => auth.validateAuthorizationRequest(request));
    assert.throws(() => auth.revokeOAuthClient(client.client_id));
  } finally {
    db.close();
    await rm(dir, { recursive: true, force: true });
  }
});

test("database migration preserves OAuth clients and adds management columns", async () => {
  const dir = await mkdtemp(
    path.join(os.tmpdir(), "chatroom-oauth-migration-"),
  );
  const databasePath = path.join(dir, "auth.sqlite");
  const legacy = new DatabaseSync(databasePath);
  try {
    legacy.exec(`
      CREATE TABLE oauth_clients (
        client_id TEXT PRIMARY KEY,
        name TEXT NOT NULL,
        redirect_uris_json TEXT NOT NULL,
        created_at TEXT NOT NULL
      );
      INSERT INTO oauth_clients(client_id,name,redirect_uris_json,created_at)
      VALUES(
        'legacy_client',
        'Legacy Client',
        '["http://127.0.0.1/callback"]',
        '2026-09-19T12:00:00.000Z'
      );
      PRAGMA user_version = 3;
    `);
  } finally {
    legacy.close();
  }

  const migrated = new AppDatabase(databasePath);
  try {
    const repository = new OAuthRepository(migrated);
    assert.deepEqual(repository.getClient("legacy_client"), {
      clientId: "legacy_client",
      name: "Legacy Client",
      redirectUris: ["http://127.0.0.1/callback"],
      createdAt: "2026-09-19T12:00:00.000Z",
      disabledAt: null,
      note: "",
    });
    const columns = migrated.raw
      .prepare("PRAGMA table_info(oauth_clients)")
      .all() as unknown as Array<{ name: string }>;
    assert.ok(columns.some((column) => column.name === "disabled_at"));
    assert.ok(columns.some((column) => column.name === "note"));
    assert.equal(
      columns.some((column) => column.name === "revoked_at"),
      false,
    );
    const version = migrated.raw.prepare("PRAGMA user_version").get() as {
      user_version: number;
    };
    assert.equal(version.user_version, 6);
  } finally {
    migrated.close();
    await rm(dir, { recursive: true, force: true });
  }
});
