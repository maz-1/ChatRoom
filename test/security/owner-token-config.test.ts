import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import {
  defaultConfig,
  loadRuntimeConfig,
} from "../../src/config/load-config.js";
import { ChatRoomError } from "../../src/core/errors/chatroom-error.js";
import type { OwnerTokenStore } from "../../src/infrastructure/security/owner-token-store.js";

class MemoryOwnerTokenStore implements OwnerTokenStore {
  private readonly values = new Map<string, string>();

  async get(configPath: string): Promise<string | null> {
    return this.values.get(configPath) ?? null;
  }

  async set(configPath: string, token: string): Promise<void> {
    this.values.set(configPath, token);
  }

  async delete(configPath: string): Promise<boolean> {
    return this.values.delete(configPath);
  }
}

function legacyConfig(ownerToken?: string) {
  const config = defaultConfig() as ReturnType<typeof defaultConfig> & {
    auth: ReturnType<typeof defaultConfig>["auth"] & { ownerToken?: string };
  };
  config.auth.webPublicBaseUrl = "https://chatroom.example.com";
  if (ownerToken !== undefined) config.auth.ownerToken = ownerToken;
  return config;
}

test("legacy ownerToken migrates to the secret store and is removed from config.json", async () => {
  const directory = await mkdtemp(
    path.join(os.tmpdir(), "chatroom-owner-token-"),
  );
  const configPath = path.join(directory, "config.json");
  const store = new MemoryOwnerTokenStore();
  const legacyToken = "legacy-owner-token-value";
  try {
    await writeFile(configPath, JSON.stringify(legacyConfig(legacyToken)));

    const runtime = await loadRuntimeConfig(configPath, store);
    assert.equal(runtime.ownerToken, legacyToken);
    assert.equal(await store.get(configPath), legacyToken);
    assert.equal("ownerToken" in runtime.config.auth, false);

    const saved = JSON.parse(await readFile(configPath, "utf8")) as {
      auth: Record<string, unknown>;
    };
    assert.equal("ownerToken" in saved.auth, false);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("legacy migration refuses to overwrite a different keychain ownerToken", async () => {
  const directory = await mkdtemp(
    path.join(os.tmpdir(), "chatroom-owner-token-"),
  );
  const configPath = path.join(directory, "config.json");
  const store = new MemoryOwnerTokenStore();
  try {
    await writeFile(configPath, JSON.stringify(legacyConfig("legacy-token")));
    await store.set(configPath, "existing-keychain-token");

    await assert.rejects(
      loadRuntimeConfig(configPath, store),
      (error: unknown) =>
        error instanceof ChatRoomError && error.code === "CONFLICT",
    );

    const saved = JSON.parse(await readFile(configPath, "utf8")) as {
      auth: Record<string, unknown>;
    };
    assert.equal(saved.auth.ownerToken, "legacy-token");
    assert.equal(await store.get(configPath), "existing-keychain-token");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("authenticated ingress requires an ownerToken in the secret store", async () => {
  const directory = await mkdtemp(
    path.join(os.tmpdir(), "chatroom-owner-token-"),
  );
  const configPath = path.join(directory, "config.json");
  const store = new MemoryOwnerTokenStore();
  try {
    await writeFile(configPath, JSON.stringify(legacyConfig()));
    await assert.rejects(
      loadRuntimeConfig(configPath, store),
      (error: unknown) =>
        error instanceof ChatRoomError && error.code === "INVALID_INPUT",
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("local unauthenticated runtime can start when the secret store is unavailable", async () => {
  const directory = await mkdtemp(
    path.join(os.tmpdir(), "chatroom-owner-token-"),
  );
  const configPath = path.join(directory, "config.json");
  const config = defaultConfig();
  const unavailableStore: OwnerTokenStore = {
    async get() {
      throw new ChatRoomError("INTERNAL", "secret store unavailable");
    },
    async set() {
      throw new ChatRoomError("INTERNAL", "secret store unavailable");
    },
    async delete() {
      throw new ChatRoomError("INTERNAL", "secret store unavailable");
    },
  };
  try {
    await writeFile(configPath, JSON.stringify(config));
    const runtime = await loadRuntimeConfig(configPath, unavailableStore);
    assert.equal(runtime.ownerToken, null);
    assert.equal(runtime.config.auth.localWebAuth, false);
    assert.equal(runtime.config.auth.mcpPublicBaseUrl, null);
    assert.equal(runtime.config.auth.webPublicBaseUrl, null);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
