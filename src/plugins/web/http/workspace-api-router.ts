import { randomUUID } from "node:crypto";
import express, { Router, type Request, type Response } from "express";
import type { WorkspaceFs } from "#plugins/workspace/workspace-fs";
import { ChatRoomError } from "#core/errors/chatroom-error";
import {
  asyncRoute,
  bodyRecord,
  boundedIntegerQuery,
  requireString,
} from "#presentation/http/http-utils";
import type { WebRuntime } from "#plugins/web/runtime";
import { workspaceMcpUpdateSchema } from "#plugins/mcp-proxy/types";

const MAX_WRITE_BYTES = 1024 * 1024;
const MAX_ATTACHMENT_BYTES = 10 * 1024 * 1024;
const MAX_IMAGE_PREVIEW_BYTES = 32 * 1024 * 1024;

export function createWorkspaceApiRouter(application: WebRuntime): Router {
  const router = Router();

  router.get(
    "/workspaces",
    asyncRoute(async (_req, res) => {
      res.json(await application.workspaces.list());
    }),
  );

  router.post(
    "/workspaces",
    asyncRoute(async (req, res) => {
      const body = bodyRecord(req.body);
      const parent = requireString(body.parent, "parent");
      const name = requireString(body.name, "name");
      res.json(
        await application.operations.run(
          {
            pluginId: "workspace",
            source: "gui",
            action: "create",
            input: { parent, name },
          },
          () => application.workspaces.createProject(parent, name),
        ),
      );
    }),
  );

  router.get("/workspace/roots", (_req, res) => {
    res.json(application.workspaces.roots());
  });

  router.get(
    "/workspace/skills",
    asyncRoute(async (req, res) => {
      res.json(
        await application.workspaces.skillSettings(
          requireString(req.query.root, "root"),
        ),
      );
    }),
  );

  router.patch(
    "/workspace/skills",
    asyncRoute(async (req, res) => {
      const body = bodyRecord(req.body);
      const root = requireString(body.root, "root");
      const skillPath = requireString(body.path, "path");
      if (typeof body.enabled !== "boolean")
        throw new ChatRoomError("INVALID_INPUT", "enabled must be a boolean");
      res.json(
        await application.operations.run(
          {
            pluginId: "workspace",
            source: "gui",
            action: "skills.set-enabled",
            input: { root, path: skillPath, enabled: body.enabled },
          },
          () =>
            application.workspaces.setSkillEnabled(
              root,
              skillPath,
              body.enabled as boolean,
            ),
        ),
      );
    }),
  );

  router.get(
    "/workspace/mcp/servers",
    asyncRoute(async (req, res) => {
      res.json(
        await application.mcpProxy.workspaceSettings(
          requireString(req.query.root, "root"),
        ),
      );
    }),
  );

  router.patch(
    "/workspace/mcp/servers/:serverName",
    asyncRoute(async (req, res) => {
      const body = bodyRecord(req.body);
      const root = requireString(body.root, "root");
      const server = requireString(req.params.serverName, "serverName");
      const { root: _root, ...fields } = body;
      const update = workspaceMcpUpdateSchema.parse(fields);
      if (Object.keys(update).length === 0)
        throw new ChatRoomError(
          "INVALID_INPUT",
          "At least one MCP setting is required",
        );
      res.json(
        await application.operations.run(
          {
            pluginId: "workspace",
            source: "gui",
            action:
              Object.keys(update).length === 1 && update.enabled !== undefined
                ? "mcp.set-enabled"
                : "mcp.update-settings",
            input: { root, server, ...update },
          },
          () => application.mcpProxy.setWorkspaceSettings(root, server, update),
        ),
      );
    }),
  );

  router.get(
    "/workspace/mcp/servers/:serverName/tools",
    asyncRoute(async (req, res) => {
      res.json(
        await application.mcpProxy.workspaceTools(
          requireString(req.query.root, "root"),
          requireString(req.params.serverName, "serverName"),
        ),
      );
    }),
  );

  router.get("/workspace/blacklist", (_req, res) => {
    res.json(application.workspaces.blockedRoots());
  });

  router.put(
    "/workspace/blacklist",
    asyncRoute(async (req, res) => {
      const body = bodyRecord(req.body);
      const root = requireString(body.root, "root");
      if (typeof body.blocked !== "boolean")
        throw new ChatRoomError("INVALID_INPUT", "blocked must be a boolean");
      const blocked = body.blocked;
      res.json(
        await application.operations.run(
          {
            pluginId: "workspace",
            source: "gui",
            action: blocked ? "block" : "unblock",
            input: { root },
          },
          async () =>
            blocked
              ? application.workspaces.block(root)
              : application.workspaces.unblock(root),
        ),
      );
    }),
  );

  router.get(
    "/workspace",
    asyncRoute(async (req, res) => {
      res.json(
        await application.workspaces.info(
          requireString(req.query.root, "root"),
        ),
      );
    }),
  );

  router.get(
    "/workspace/files",
    asyncRoute(async (req, res) => {
      const fs = await application.workspaces.fs(
        requireString(req.query.root, "root"),
      );
      const filePath =
        typeof req.query.path === "string" ? req.query.path : ".";
      const limit = boundedIntegerQuery(req.query.limit, 300, 1, 500);
      const offset = boundedIntegerQuery(req.query.offset, 0, 0, 100000);
      const page = await fs.list(filePath, {
        recursive: req.query.recursive === "1",
        offset,
        maxEntries: limit + 1,
      });
      const hasMore = page.length > limit;
      res.json({
        items: page.slice(0, limit),
        nextOffset: hasMore ? offset + limit : null,
      });
    }),
  );

  router.get(
    "/workspace/project-files",
    asyncRoute(async (req, res) => {
      res.json(
        await application.workspaces.projectFilesResult(
          requireString(req.query.root, "root"),
        ),
      );
    }),
  );

  router.get(
    "/workspace/file",
    asyncRoute(async (req, res) => {
      const fs = await requestWorkspaceFs(req, application);
      res.json(
        await fs.read(requireString(req.query.path, "path"), {
          maxBytes: 2 * 1024 * 1024,
        }),
      );
    }),
  );

  router.put(
    "/workspace/file",
    asyncRoute(async (req, res) => {
      const body = bodyRecord(req.body);
      const fs = await application.workspaces.fs(
        requireString(body.root, "root"),
      );
      const filePath = requireString(body.path, "path");
      if (typeof body.content !== "string")
        throw new ChatRoomError("INVALID_INPUT", "content must be a string");
      const bytes = Buffer.byteLength(body.content, "utf8");
      if (bytes > MAX_WRITE_BYTES)
        throw new ChatRoomError(
          "INVALID_INPUT",
          `File content exceeds ${MAX_WRITE_BYTES} bytes`,
        );
      res.json(
        await application.operations.run(
          {
            pluginId: "workspace",
            source: "gui",
            action: "file.write",
            input: { root: fs.root, path: filePath, bytes },
          },
          () => fs.write(filePath, body.content as string),
        ),
      );
    }),
  );

  router.post(
    "/workspace/attachment",
    express.raw({
      type: "application/octet-stream",
      limit: MAX_ATTACHMENT_BYTES,
    }),
    asyncRoute(async (req, res) => {
      const root = requireString(req.query.root, "root");
      const sessionId = requireString(req.query.sessionId, "sessionId");
      if (!/^[A-Za-z0-9_-]+$/.test(sessionId))
        throw new ChatRoomError(
          "INVALID_INPUT",
          "Invalid attachment session id",
        );
      const originalName = requireString(req.query.name, "name");
      if (!Buffer.isBuffer(req.body))
        throw new ChatRoomError(
          "INVALID_INPUT",
          "Attachment body must be binary",
        );
      const fs = await application.workspaces.fs(root);
      const session = application.agents.getSession(sessionId);
      if (session.workspaceRoot !== fs.root)
        throw new ChatRoomError(
          "FORBIDDEN",
          "Agent session belongs to another workspace",
        );
      const filePath =
        ".chatroom/agent-attachments/" +
        sessionId +
        "/" +
        randomUUID() +
        "-" +
        attachmentFileName(originalName);
      const file = await application.operations.run(
        {
          pluginId: "workspace",
          source: "gui",
          action: "attachment.upload",
          input: { root: fs.root, path: filePath, bytes: req.body.byteLength },
        },
        () => fs.writeBytes(filePath, req.body),
      );
      res.json({
        ...file,
        name: originalName,
        mimeType:
          typeof req.headers["x-chatroom-mime-type"] === "string"
            ? req.headers["x-chatroom-mime-type"]
            : null,
      });
    }),
  );

  router.get(
    "/workspace/file/download",
    asyncRoute(async (req, res) => {
      const filePath = requireString(req.query.path, "path");
      const fs = await requestWorkspaceFs(req, application);
      await streamWorkspaceFile(
        req,
        res,
        fs,
        filePath,
        "application/octet-stream",
        true,
      );
    }),
  );

  router.get(
    "/workspace/file/preview",
    asyncRoute(async (req, res) => {
      const filePath = requireString(req.query.path, "path");
      const mime = previewMime(filePath);
      if (!mime)
        throw new ChatRoomError(
          "INVALID_INPUT",
          "Unsupported file preview type",
        );
      const fs = await requestWorkspaceFs(req, application);
      await streamWorkspaceFile(req, res, fs, filePath, mime, false);
    }),
  );

  router.get(
    "/workspace/file/image",
    asyncRoute(async (req, res) => {
      const filePath = requireString(req.query.path, "path");
      const mime = imageMime(filePath);
      if (!mime)
        throw new ChatRoomError(
          "INVALID_INPUT",
          "File type is not supported for image preview",
        );
      const fs = await requestWorkspaceFs(req, application);
      const file = await fs.readBytes(filePath, {
        maxBytes: MAX_IMAGE_PREVIEW_BYTES,
      });
      if (file.truncated)
        throw new ChatRoomError(
          "INVALID_INPUT",
          "Image is too large to preview",
        );

      const etag =
        'W/"' +
        file.bytes.toString(16) +
        "-" +
        Date.parse(file.modifiedAt).toString(16) +
        '"';
      const requestedVersion =
        typeof req.query.v === "string" ? req.query.v : null;

      res.setHeader("Content-Type", mime);
      if (mime === "image/svg+xml")
        res.setHeader(
          "Content-Security-Policy",
          "sandbox; default-src 'none'; style-src 'unsafe-inline'",
        );
      res.setHeader("ETag", etag);
      res.setHeader("Last-Modified", file.modifiedAt);
      res.setHeader(
        "Cache-Control",
        requestedVersion === file.modifiedAt
          ? "private, max-age=31536000, immutable"
          : "private, no-cache",
      );
      res.setHeader("X-Content-Type-Options", "nosniff");

      if (req.headers["if-none-match"] === etag) {
        res.status(304).end();
        return;
      }

      res.setHeader("Content-Length", String(file.data.byteLength));
      res.send(file.data);
    }),
  );

  return router;
}

function requestWorkspaceFs(req: Request, application: WebRuntime) {
  if (req.query.workspace !== undefined)
    return application.workspaces.fsByAlias(
      requireString(req.query.workspace, "workspace"),
    );
  return application.workspaces.fs(requireString(req.query.root, "root"));
}

function imageMime(filePath: string): string | null {
  switch (filePath.split(".").pop()?.toLowerCase()) {
    case "png":
      return "image/png";
    case "jpg":
    case "jpeg":
      return "image/jpeg";
    case "gif":
      return "image/gif";
    case "webp":
      return "image/webp";
    case "avif":
      return "image/avif";
    case "bmp":
      return "image/bmp";
    case "ico":
      return "image/x-icon";
    case "svg":
      return "image/svg+xml";
    default:
      return null;
  }
}

function attachmentFileName(value: string): string {
  const name = value.split(/[\\/]/).pop()?.trim() ?? "";
  const safe = name.replace(/[^A-Za-z0-9._-]+/g, "-").replace(/^-+|-+$/g, "");
  return (safe || "attachment").slice(0, 160);
}

function previewMime(filePath: string): string | null {
  switch (filePath.split(".").pop()?.toLowerCase()) {
    case "pdf":
      return "application/pdf";
    case "mp4":
    case "m4v":
      return "video/mp4";
    case "webm":
      return "video/webm";
    case "ogv":
      return "video/ogg";
    case "mov":
      return "video/quicktime";
    case "mp3":
      return "audio/mpeg";
    case "m4a":
      return "audio/mp4";
    case "aac":
      return "audio/aac";
    case "wav":
      return "audio/wav";
    case "flac":
      return "audio/flac";
    case "oga":
    case "opus":
    case "ogg":
      return "audio/ogg";
    default:
      return null;
  }
}

function parseByteRange(
  header: string,
  size: number,
): { start: number; end: number } | null {
  const match = /^bytes=(\d*)-(\d*)$/.exec(header.trim());
  if (!match || size === 0 || (!match[1] && !match[2])) return null;

  let start: number;
  let end: number;
  if (!match[1]) {
    const suffix = Number(match[2]);
    if (!Number.isSafeInteger(suffix) || suffix <= 0) return null;
    start = Math.max(0, size - suffix);
    end = size - 1;
  } else {
    start = Number(match[1]);
    end = match[2] ? Number(match[2]) : size - 1;
    if (
      !Number.isSafeInteger(start) ||
      !Number.isSafeInteger(end) ||
      start >= size ||
      end < start
    )
      return null;
    end = Math.min(end, size - 1);
  }
  return { start, end };
}

async function streamWorkspaceFile(
  req: Request,
  res: Response,
  fs: WorkspaceFs,
  relativePath: string,
  mime: string,
  download: boolean,
): Promise<void> {
  const { handle, bytes } = await fs.openReadable(relativePath);
  const isRange = typeof req.headers.range === "string";
  const range = isRange ? parseByteRange(req.headers.range!, bytes) : null;
  res.setHeader("Content-Type", mime);
  res.setHeader("Cache-Control", "private, no-store, no-transform");
  res.setHeader("X-Content-Type-Options", "nosniff");
  res.setHeader("Accept-Ranges", "bytes");
  if (download) {
    const name =
      relativePath.replaceAll("\\", "/").split("/").pop() || "download";
    const fallback = name.replace(/[^\x20-\x7e]/g, "_").replace(/["\\]/g, "_");
    res.setHeader(
      "Content-Disposition",
      `attachment; filename="${fallback}"; filename*=UTF-8''${encodeURIComponent(name)}`,
    );
  } else {
    res.setHeader("Content-Disposition", "inline");
  }

  if (isRange && !range) {
    res.setHeader("Content-Range", `bytes */${bytes}`);
    await handle.close();
    res.status(416).end();
    return;
  }
  const start = range?.start ?? 0;
  const end = range?.end ?? bytes - 1;
  if (range) {
    res.status(206);
    res.setHeader("Content-Range", `bytes ${start}-${end}/${bytes}`);
  }
  res.setHeader("Content-Length", String(Math.max(0, end - start + 1)));
  if (req.method === "HEAD" || bytes === 0) {
    await handle.close();
    res.end();
    return;
  }

  const stream = handle.createReadStream({ start, end, autoClose: true });
  res.on("close", () => stream.destroy());
  stream.on("error", (error) => res.destroy(error));
  stream.pipe(res);
}
