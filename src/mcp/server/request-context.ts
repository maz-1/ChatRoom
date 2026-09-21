import { AsyncLocalStorage } from "node:async_hooks";
import type { ComputerAccessScope } from "#plugins/computer/types";

export interface McpOAuthClientContext {
  clientId: string;
  disabled: boolean;
}

interface McpRequestContext {
  scope: ComputerAccessScope;
  oauthClient: McpOAuthClientContext | null;
}

const storage = new AsyncLocalStorage<McpRequestContext>();

export function runWithMcpAccessScope<T>(
  scope: ComputerAccessScope,
  action: () => T,
  oauthClient: McpOAuthClientContext | null = null,
): T {
  return storage.run({ scope, oauthClient }, action);
}

export function currentMcpAccessScope(): ComputerAccessScope {
  return storage.getStore()?.scope ?? "local";
}

export function currentMcpOAuthClient(): McpOAuthClientContext | null {
  return storage.getStore()?.oauthClient ?? null;
}
