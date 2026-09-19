import type { Request } from "express";
import type { AuthInfo } from "@modelcontextprotocol/server";
import type { AuthService } from "../../auth/auth-service.js";
import type { McpOAuthClientContext } from "../../mcp/server/request-context.js";

export function oauthClientContextForRequest(
  req: Request,
  auth: AuthService,
): McpOAuthClientContext | null {
  const clientId = (req as unknown as { auth?: AuthInfo }).auth?.clientId;
  return clientId
    ? { clientId, disabled: auth.isOAuthClientDisabled(clientId) }
    : null;
}
