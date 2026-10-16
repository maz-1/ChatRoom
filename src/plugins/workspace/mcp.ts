import path from "node:path";
import { ChatRoomError } from "#core/errors/chatroom-error";
import { z } from "zod";
import type { PluginMcpRegistrar } from "#mcp/server/plugin-mcp-registrar";
import { closedRead } from "#mcp/server/tool-support";
import type { WorkspaceService } from "./workspace-service.js";

const workspaceEntrySchema = z.object({
  root: z.string(),
  name: z.string(),
  summary: z.string().nullable(),
});

const workspaceListSchema = z.object({
  workspaces: z.array(workspaceEntrySchema),
});

const workspaceInfoSchema = z.object({
  root: z.string(),
  name: z.string(),
  summary: z.string().nullable(),
  presetPrompt: z.string().nullable(),
  instructions: z.string().nullable(),
  skills: z.array(
    z.object({
      name: z.string(),
      description: z.string(),
      path: z.string(),
    }),
  ),
});

export function registerWorkspaceTools(
  mcp: PluginMcpRegistrar,
  workspaces: WorkspaceService,
  webBaseUrl: () => string | null,
): void {
  mcp.registerTool(
    "workspace_list",
    {
      title: "List workspaces",
      description:
        "List available projects with their workspace roots, names, and project summaries from the workspace prompt metadata. Use this first to identify the relevant project before calling workspace_info for its full prompt, instructions, and skills.",
      inputSchema: z.object({}),
      outputSchema: workspaceListSchema,
      annotations: closedRead,
      action: "list",
    },
    async () => ({ workspaces: await workspaces.list() }),
  );

  mcp.registerTool(
    "workspace_info",
    {
      title: "Workspace info",
      description:
        "Read the complete workspace context for a project, including its summary, preset prompt, project instructions, and skill metadata. Workspaces on the blacklist cannot be read.",
      inputSchema: z.object({ root: z.string() }),
      outputSchema: workspaceInfoSchema,
      annotations: closedRead,
      action: "info",
    },
    (input) => workspaces.info(input.root),
  );

  mcp.registerTool(
    "workspace_file_get",
    {
      title: "Get workspace file",
      description:
        "Read a file from a workspace in bounded chunks, or return a browser download link protected by the normal ChatRoom Web login. " +
        "Use mode=content with encoding=utf8 for text and encoding=base64 for binary files; advance offset with nextOffset for large files. " +
        "Use mode=link to give the user a download link when the Web service has a configured public address. " +
        "Links do not contain credentials and the user must log in to ChatRoom Web to download.",
      inputSchema: z.object({
        root: z.string(),
        path: z.string(),
        mode: z.enum(["content", "link"]).default("content"),
        encoding: z.enum(["utf8", "base64"]).default("utf8"),
        offset: z.number().int().nonnegative().default(0),
        maxBytes: z.number().int().min(1).max(262144).default(65536),
      }),
      outputSchema: z.object({
        mode: z.enum(["content", "link"]),
        path: z.string(),
        name: z.string(),
        bytes: z.number(),
        modifiedAt: z.string(),
        encoding: z.enum(["utf8", "base64"]).optional(),
        content: z.string().optional(),
        offset: z.number().optional(),
        nextOffset: z.number().nullable().optional(),
        url: z.string().optional(),
      }),
      annotations: closedRead,
      action: "file.get",
      audit: {
        input: (input) => ({
          root: input.root,
          path: input.path,
          mode: input.mode,
          offset: input.mode === "content" ? input.offset : undefined,
        }),
        output: (output) => {
          const result = output as {
            mode: string;
            path: string;
            bytes: number;
            nextOffset?: number | null;
          };
          return {
            mode: result.mode,
            path: result.path,
            bytes: result.bytes,
            nextOffset: result.nextOffset,
          };
        },
      },
    },
    async (input) => {
      const fs = await workspaces.fs(input.root);
      const name = path.posix.basename(input.path.replaceAll("\\", "/"));
      if (input.mode === "link") {
        const base = webBaseUrl();
        if (!base)
          throw new ChatRoomError(
            "INVALID_INPUT",
            "No public Web URL is configured; download link is unavailable",
          );
        const info = await fs.fileInfo(input.path);
        const url = new URL("/download", base);
        url.searchParams.set("workspace", path.basename(fs.root));
        url.searchParams.set("path", input.path);
        return {
          mode: "link" as const,
          path: input.path,
          name,
          ...info,
          url: url.toString(),
        };
      }
      const chunk = await fs.readChunk(
        input.path,
        input.offset,
        input.maxBytes,
      );
      let content: string;
      let consumed = chunk.data.byteLength;
      if (input.encoding === "utf8") {
        const decoder = new TextDecoder("utf-8", { fatal: true });
        let decoded: string | null = null;
        for (
          let trim = 0;
          trim < 4 && trim < chunk.data.byteLength;
          trim += 1
        ) {
          if (trim > 0 && input.offset + chunk.data.byteLength >= chunk.bytes)
            break;
          try {
            consumed = chunk.data.byteLength - trim;
            decoded = decoder.decode(chunk.data.subarray(0, consumed));
            break;
          } catch {
            // Only incomplete trailing UTF-8 sequences can be repaired.
          }
        }
        if (decoded === null) {
          if (chunk.data.byteLength === 0) decoded = "";
          else
            throw new ChatRoomError(
              "INVALID_INPUT",
              "Invalid UTF-8 data; use base64",
            );
        }
        content = decoded;
      } else {
        content = chunk.data.toString("base64");
      }
      const next = input.offset + consumed;
      return {
        mode: "content" as const,
        path: input.path,
        name,
        bytes: chunk.bytes,
        modifiedAt: chunk.modifiedAt,
        encoding: input.encoding,
        content,
        offset: input.offset,
        nextOffset: next < chunk.bytes ? next : null,
      };
    },
  );
}
