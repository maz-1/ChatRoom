import { McpServer } from "@modelcontextprotocol/server";
import { StdioServerTransport } from "@modelcontextprotocol/server/stdio";
import { z } from "zod";

// Minimal stdio MCP server used by the mcp-proxy integration test. It must
// never write to stdout outside the MCP protocol stream.
const server = new McpServer({ name: "fixture-stdio", version: "1.0.0" });

server.registerTool(
  "echo",
  {
    title: "Echo",
    description: "Echo the provided text back.",
    inputSchema: z.object({ text: z.string() }),
    outputSchema: z.object({ echoed: z.string() }),
  },
  ({ text }) => ({
    content: [{ type: "text", text: `echo:${text}` }],
    structuredContent: { echoed: text },
  }),
);

const PNG_1X1 =
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==";

server.registerTool(
  "palette",
  {
    title: "Palette",
    description: "Return a 1x1 PNG image.",
  },
  () => ({
    content: [{ type: "image", data: PNG_1X1, mimeType: "image/png" }],
  }),
);

server.registerTool(
  "fail",
  {
    title: "Fail",
    description: "Always reports a tool-level failure.",
  },
  () => ({
    isError: true,
    content: [{ type: "text", text: "fixture failure" }],
  }),
);

server.registerTool(
  "flood",
  {
    title: "Flood",
    description: "Return more text than the configured result limit.",
    inputSchema: z.object({ size: z.number().int().positive() }),
  },
  ({ size }) => ({
    content: [{ type: "text", text: "y".repeat(size) }],
  }),
);

await server.connect(new StdioServerTransport());
