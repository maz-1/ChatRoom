import { createHash, randomBytes } from "node:crypto";
import path from "node:path";
import { ChatRoomError } from "../../core/errors/chatroom-error.js";
import { platformSecurityDefaults } from "../../config/platform-paths.js";

const SERVICE = "ChatRoom";
const VERSION = "owner-token:v1";

export interface OwnerTokenStore {
  get(configPath: string): Promise<string | null>;
  set(configPath: string, token: string): Promise<void>;
  delete(configPath: string): Promise<boolean>;
}

export class SystemOwnerTokenStore implements OwnerTokenStore {
  async get(configPath: string): Promise<string | null> {
    try {
      const keytar = await keytarApi();
      return await keytar.getPassword(
        SERVICE,
        ownerTokenCredentialAccount(configPath),
      );
    } catch (error) {
      throw keychainError("read", error);
    }
  }

  async set(configPath: string, token: string): Promise<void> {
    try {
      const keytar = await keytarApi();
      await keytar.setPassword(
        SERVICE,
        ownerTokenCredentialAccount(configPath),
        token,
      );
    } catch (error) {
      throw keychainError("write", error);
    }
  }

  async delete(configPath: string): Promise<boolean> {
    try {
      const keytar = await keytarApi();
      return await keytar.deletePassword(
        SERVICE,
        ownerTokenCredentialAccount(configPath),
      );
    } catch (error) {
      throw keychainError("delete", error);
    }
  }
}

export const systemOwnerTokenStore = new SystemOwnerTokenStore();

export function generateOwnerToken(): string {
  return randomBytes(32).toString("base64url");
}

export function ownerTokenCredentialAccount(configPath: string): string {
  return `${VERSION}:${configPathId(configPath)}`;
}

export function ownerTokenCredentialTarget(configPath: string): string {
  return `${SERVICE}/${ownerTokenCredentialAccount(configPath)}`;
}

function configPathId(configPath: string): string {
  const defaults = platformSecurityDefaults();
  let canonical = path.resolve(configPath);
  if (defaults.caseFoldCredentialPaths) canonical = canonical.toLowerCase();
  return createHash("sha256").update(canonical, "utf8").digest("hex");
}

function keychainError(action: string, error: unknown): ChatRoomError {
  return new ChatRoomError(
    "INTERNAL",
    `Cannot ${action} owner token in the system keychain`,
    undefined,
    { cause: error },
  );
}

interface KeytarApi {
  getPassword(service: string, account: string): Promise<string | null>;
  setPassword(
    service: string,
    account: string,
    password: string,
  ): Promise<void>;
  deletePassword(service: string, account: string): Promise<boolean>;
}

let keytarPromise: Promise<KeytarApi> | null = null;

function keytarApi(): Promise<KeytarApi> {
  keytarPromise ??= import("@github/keytar").then((module) => {
    const candidate = (module as unknown as { default?: KeytarApi }).default;
    return candidate ?? (module as unknown as KeytarApi);
  });
  return keytarPromise;
}
