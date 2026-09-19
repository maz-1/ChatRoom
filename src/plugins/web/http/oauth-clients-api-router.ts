import { Router } from "express";
import type { Router as ExpressRouter } from "express";
import type { AuthService } from "../../../auth/auth-service.js";
import { ChatRoomError } from "../../../core/errors/chatroom-error.js";
import { bodyRecord } from "../../../presentation/http/http-utils.js";

export function createOAuthClientsApiRouter(auth: AuthService): ExpressRouter {
  const router = Router();

  router.get("/oauth-clients", (_req, res) => {
    res.json({ clients: auth.listOAuthClients() });
  });

  router.patch("/oauth-clients/:clientId", (req, res) => {
    const clientId = req.params.clientId;
    if (!clientId)
      throw new ChatRoomError("INVALID_INPUT", "clientId is required");
    const body = bodyRecord(req.body);
    const hasDisabled = "disabled" in body;
    const hasNote = "note" in body;
    if (!hasDisabled && !hasNote)
      throw new ChatRoomError(
        "INVALID_INPUT",
        "disabled or note must be provided",
      );
    if (hasDisabled && typeof body.disabled !== "boolean")
      throw new ChatRoomError("INVALID_INPUT", "disabled must be a boolean");
    if (hasNote && typeof body.note !== "string")
      throw new ChatRoomError("INVALID_INPUT", "note must be a string");
    if (typeof body.note === "string" && body.note.length > 1000)
      throw new ChatRoomError(
        "INVALID_INPUT",
        "note must be 1000 characters or fewer",
      );

    let updated = hasDisabled
      ? auth.setOAuthClientDisabled(clientId, body.disabled as boolean)
      : auth.listOAuthClients().find((client) => client.clientId === clientId);
    if (!updated)
      throw new ChatRoomError("NOT_FOUND", "OAuth client not found");
    if (hasNote)
      updated = auth.setOAuthClientNote(clientId, body.note as string);
    res.json(updated);
  });

  router.post("/oauth-clients/:clientId/revoke", (req, res) => {
    const clientId = req.params.clientId;
    if (!clientId)
      throw new ChatRoomError("INVALID_INPUT", "clientId is required");
    res.json(auth.revokeOAuthClient(clientId));
  });

  return router;
}
