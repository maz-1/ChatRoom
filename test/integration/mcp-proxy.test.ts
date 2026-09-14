import assert from "node:assert/strict";
import http from "node:http";
import type { AddressInfo } from "node:net";
import { fileURLToPath } from "node:url";
import test from "node:test";
import {
  Client,
  StreamableHTTPClientTransport,
} from "@modelcontextprotocol/client";
import { toNodeHandler } from "@modelcontextprotocol/node";
import { createMcpHandler, McpServer } from "@modelcontextprotocol/server";
import { z } from "zod";
import { createTestRuntime } from "../helpers/runtime.js";

const STDIO_FIXTURE = fileURLToPath(
  new URL("../fixtures/mcp-stdio-server.mjs", import.meta.url),
);

function createUpstreamServer(): McpServer {
  const server = new McpServer({ name: "fixture-http", version: "1.0.0" });
  server.registerTool(
    "echo",
    {
      title: "Echo",
      description: "Echo the provided text back.",
      inputSchema: z.object({ text: z.string() }),
    },
    ({ text }) => ({
      content: [{ type: "text", text: `http-echo:${text}` }],
      structuredContent: { echoed: text },
    }),
  );
  server.registerTool(
    "fail",
    { title: "Fail", description: "Always reports a tool-level failure." },
    () => ({
      isError: true,
      content: [{ type: "text", text: "http fixture failure" }],
    }),
  );
  return server;
}

async function startUpstreamHttp(): Promise<{
  url: string;
  close: () => Promise<void>;
}> {
  // Mirrors how ChatRoom serves its own /mcp endpoint.
  const handler = toNodeHandler(
    createMcpHandler(() => createUpstreamServer(), {
      legacy: "stateless",
      responseMode: "auto",
    }),
  );
  const server = http.createServer((request, response) => {
    // node:http types `method` as optional while the adapter requires it under
    // exactOptionalPropertyTypes; production passes express's structurally
    // compatible req/res instead.
    void handler(
      request as unknown as Parameters<typeof handler>[0],
      response as unknown as Parameters<typeof handler>[1],
    );
  });
  await new Promise<void>((resolve) =>
    server.listen(0, "127.0.0.1", () => resolve()),
  );
  const { port } = server.address() as AddressInfo;
  return {
    url: `http://127.0.0.1:${port}/mcp`,
    close: async () => {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    },
  };
}

function toolError(result: { content: unknown }): {
  code: string;
  message: string;
  details?: unknown;
} {
  const blocks = result.content as { type: string; text?: string }[];
  const text = blocks.find((block) => block.type === "text")?.text;
  assert.ok(text, "tool error results carry a text block");
  return JSON.parse(text) as {
    code: string;
    message: string;
    details?: unknown;
  };
}

test("mcp proxy discovers and calls upstream tools over stdio and http", async () => {
  const upstream = await startUpstreamHttp();
  const runtime = await createTestRuntime({
    configure(config) {
      config.mcp.callTimeoutMs = 15_000;
      config.mcp.servers = {
        local: {
          type: "stdio",
          command: process.execPath,
          args: [STDIO_FIXTURE],
          env: {},
          cwd: null,
        },
        remote: {
          type: "http",
          url: upstream.url,
          headers: {},
          proxy: null,
        },
      };
    },
  });
  let client: Client | null = null;
  try {
    await runtime.components.http.start();
    const address = runtime.components.http.address();
    assert.ok(address);
    client = new Client({
      name: "chatroom-mcp-proxy-test",
      version: "1.0.0",
    });
    await client.connect(
      new StreamableHTTPClientTransport(
        new URL(`http://127.0.0.1:${address.port}/mcp`),
      ),
    );

    const tools = await client.listTools();
    for (const name of ["mcp_list_servers", "mcp_call", "mcp_refresh"])
      assert.ok(
        tools.tools.some((tool) => tool.name === name),
        `${name} should be exposed`,
      );

    const discovered = await client.callTool({
      name: "mcp_list_servers",
      arguments: {},
    });
    assert.equal(discovered.isError, undefined);
    const summary = discovered.structuredContent as {
      servers: {
        name: string;
        type: string;
        status: string;
        error: string | null;
        tools: { name: string; inputSchema: unknown }[];
      }[];
    };
    const byName = new Map(summary.servers.map((s) => [s.name, s]));
    assert.equal(summary.servers.length, 2);
    const local = byName.get("local");
    const remote = byName.get("remote");
    assert.ok(local && remote);
    assert.equal(local.status, "connected", local.error ?? "");
    assert.equal(local.type, "stdio");
    assert.equal(remote.status, "connected", remote.error ?? "");
    assert.equal(remote.type, "http");
    const localTools = local.tools.map((tool) => tool.name).sort();
    assert.deepEqual(localTools, ["echo", "fail", "flood", "palette"]);
    assert.ok(
      local.tools.find((tool) => tool.name === "echo")?.inputSchema,
      "tool input schemas should be reported so calls can be built",
    );

    const echoed = await client.callTool({
      name: "mcp_call",
      arguments: { server: "local", tool: "echo", arguments: { text: "hi" } },
    });
    assert.equal(echoed.isError, undefined);
    const echoResult = echoed.structuredContent as {
      isError: boolean;
      content: { type: string; text?: string }[];
      structuredContent: { echoed?: string } | null;
      operationId: string;
    };
    assert.equal(echoResult.isError, false);
    assert.equal(echoResult.content[0]?.text, "echo:hi");
    assert.equal(echoResult.structuredContent?.echoed, "hi");

    const operation = runtime.components.operations.get(echoResult.operationId);
    assert.equal(operation?.pluginId, "mcp-proxy");
    assert.equal(operation?.source, "mcp");
    assert.equal(operation?.action, "call");
    assert.equal(operation?.status, "success");

    const image = await client.callTool({
      name: "mcp_call",
      arguments: { server: "local", tool: "palette" },
    });
    const imageResult = image.structuredContent as {
      content: { type: string; mimeType?: string; data?: string }[];
    };
    assert.equal(imageResult.content[0]?.type, "image");
    assert.equal(imageResult.content[0]?.mimeType, "image/png");
    assert.ok(imageResult.content[0]?.data);

    const remoteEcho = await client.callTool({
      name: "mcp_call",
      arguments: { server: "remote", tool: "echo", arguments: { text: "hi" } },
    });
    assert.equal(
      (remoteEcho.structuredContent as { content: { text?: string }[] })
        .content[0]?.text,
      "http-echo:hi",
    );

    const remoteFailure = await client.callTool({
      name: "mcp_call",
      arguments: { server: "local", tool: "fail" },
    });
    assert.equal(
      remoteFailure.isError,
      true,
      "a remote tool-level failure is passed through as a tool error result",
    );
    const failure = remoteFailure.structuredContent as {
      isError: boolean;
      content: { text?: string }[];
    };
    assert.equal(failure.isError, true);
    assert.equal(failure.content[0]?.text, "fixture failure");

    const flooded = await client.callTool({
      name: "mcp_call",
      arguments: { server: "local", tool: "flood", arguments: { size: 64000 } },
    });
    const floodResult = flooded.structuredContent as {
      truncated: boolean;
      content: { text?: string }[];
    };
    assert.equal(floodResult.truncated, true);
    assert.match(floodResult.content.at(-1)?.text ?? "", /truncated/i);

    const unknownServer = await client.callTool({
      name: "mcp_call",
      arguments: { server: "nope", tool: "echo" },
    });
    assert.equal(unknownServer.isError, true);
    const unknownServerError = toolError(unknownServer);
    assert.equal(unknownServerError.code, "NOT_FOUND");
    assert.match(unknownServerError.message, /Unknown MCP server: nope/);
    assert.deepEqual(
      (unknownServerError.details as { availableServers: string[] })
        .availableServers,
      ["local", "remote"],
    );

    const unknownTool = await client.callTool({
      name: "mcp_call",
      arguments: { server: "local", tool: "missing" },
    });
    assert.equal(unknownTool.isError, true);
    const unknownToolError = toolError(unknownTool);
    assert.equal(unknownToolError.code, "NOT_FOUND");
    assert.match(unknownToolError.message, /has no tool "missing"/);
    assert.deepEqual(
      (unknownToolError.details as { availableTools: string[] }).availableTools,
      ["echo", "palette", "fail", "flood"],
    );

    const refreshed = await client.callTool({
      name: "mcp_refresh",
      arguments: { server: "remote" },
    });
    assert.equal(refreshed.isError, undefined);
    const refreshedServers = (
      refreshed.structuredContent as { servers: { name: string }[] }
    ).servers;
    assert.deepEqual(
      refreshedServers.map((entry) => entry.name),
      ["remote"],
    );
  } finally {
    await client?.close().catch(() => undefined);
    await runtime.cleanup();
    await upstream.close();
  }
});
