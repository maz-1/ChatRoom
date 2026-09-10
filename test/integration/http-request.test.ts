import assert from "node:assert/strict";
import http from "node:http";
import type { AddressInfo } from "node:net";
import test from "node:test";
import {
  Client,
  StreamableHTTPClientTransport,
} from "@modelcontextprotocol/client";
import { createTestRuntime } from "../helpers/runtime.js";

test("http_request tool serves direct HTTP requests with audited operations", async () => {
  const target = http.createServer((request, response) => {
    if (request.url !== "/api/echo") {
      response.writeHead(404, { "content-type": "text/plain" });
      response.end("not found");
      return;
    }
    const chunks: Buffer[] = [];
    request.on("data", (chunk: Buffer) => chunks.push(chunk));
    request.on("end", () => {
      response.writeHead(200, { "content-type": "application/json" });
      response.end(
        JSON.stringify({
          method: request.method,
          authorization: request.headers.authorization ?? null,
          body: Buffer.concat(chunks).toString("utf8"),
        }),
      );
    });
  });
  await new Promise<void>((resolve) => target.listen(0, "127.0.0.1", resolve));
  const targetPort = (target.address() as AddressInfo).port;

  const runtime = await createTestRuntime();
  let client: Client | null = null;
  try {
    await runtime.components.http.start();
    const address = runtime.components.http.address();
    assert.ok(address);
    const base = `http://127.0.0.1:${address.port}`;

    client = new Client({
      name: "chatroom-http-request-test",
      version: "1.0.0",
    });
    await client.connect(
      new StreamableHTTPClientTransport(new URL(`${base}/mcp`)),
    );

    const tools = await client.listTools();
    assert.ok(
      tools.tools.some((tool) => tool.name === "http_request"),
      "http_request should be listed",
    );

    const posted = await client.callTool({
      name: "http_request",
      arguments: {
        url: `http://127.0.0.1:${targetPort}/api/echo`,
        method: "POST",
        headers: {
          "content-type": "application/json",
          authorization: "Bearer secret-token",
        },
        body: JSON.stringify({ hello: "chatroom" }),
      },
    });
    assert.equal(posted.isError, undefined);
    const result = posted.structuredContent as {
      status: number;
      body: string;
      operationId: string;
    };
    assert.equal(result.status, 200);
    assert.deepEqual(JSON.parse(result.body), {
      method: "POST",
      authorization: "Bearer secret-token",
      body: JSON.stringify({ hello: "chatroom" }),
    });

    const operation = runtime.components.operations.get(result.operationId);
    assert.ok(operation, "operation should be recorded");
    assert.equal(operation.pluginId, "http");
    assert.equal(operation.source, "mcp");
    assert.equal(operation.action, "post");
    assert.equal(operation.status, "success");
    const input = operation.input as {
      headers?: Record<string, string>;
    } | null;
    assert.equal(
      input?.headers?.authorization,
      "[redacted]",
      "authorization header must be redacted in the audit log",
    );
    assert.equal(input?.headers?.["content-type"], "application/json");

    const failed = await client.callTool({
      name: "http_request",
      arguments: {
        url: `http://127.0.0.1:${targetPort}/missing`,
      },
    });
    assert.equal(failed.isError, undefined);
    assert.equal(
      (failed.structuredContent as { status: number }).status,
      404,
      "HTTP error statuses are tool results, not tool errors",
    );
  } finally {
    await client?.close().catch(() => undefined);
    await runtime.cleanup();
    target.closeAllConnections();
    await new Promise<void>((done) => target.close(() => done()));
  }
});
