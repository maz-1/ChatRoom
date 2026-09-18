import assert from "node:assert/strict";
import http from "node:http";
import { readFileSync } from "node:fs";
import { mkdir } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import {
  Client,
  StreamableHTTPClientTransport,
} from "@modelcontextprotocol/client";
import type { SystemLogRecord } from "../../src/core/logging/types.js";
import { createTestRuntime } from "../helpers/runtime.js";

test("HTTP API and real MCP client share the same Application runtime", async () => {
  const runtime = await createTestRuntime();
  let client: Client | null = null;
  try {
    await runtime.components.http.start();
    const address = runtime.components.http.address();
    assert.ok(address);
    const base = `http://127.0.0.1:${address.port}`;

    const packageVersion = (
      JSON.parse(readFileSync("package.json", "utf8")) as { version: string }
    ).version;

    const metadataWrites = await Promise.all(
      [
        [".chatroom/summary.md", "ChatRoom integration workspace"],
        [".chatroom/prompt.md", "Prefer minimal workspace changes."],
      ].map(([filePath, content]) =>
        fetch(`${base}/api/workspace/file`, {
          method: "PUT",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({
            root: runtime.workspaceRoot,
            path: filePath,
            content,
          }),
        }),
      ),
    );
    for (const response of metadataWrites) assert.equal(response.status, 200);

    const workspaceListResponse = await fetch(`${base}/api/workspaces`);
    assert.equal(workspaceListResponse.status, 200);
    const workspaceList = (await workspaceListResponse.json()) as Array<{
      root: string;
      name: string;
      summary: string | null;
    }>;
    assert.deepEqual(
      workspaceList.find((item) => item.root === runtime.workspaceRoot),
      {
        root: runtime.workspaceRoot,
        name: path.basename(runtime.workspaceRoot),
        summary: "ChatRoom integration workspace",
      },
    );

    const createResponse = await fetch(`${base}/api/workspaces`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ parent: runtime.root, name: "created-project" }),
    });
    assert.equal(createResponse.status, 200);
    const createdWorkspace = (await createResponse.json()) as { root: string };
    assert.equal(
      createdWorkspace.root,
      path.join(runtime.root, "created-project"),
    );

    const nested = path.join(runtime.workspaceRoot, "nested");
    await mkdir(nested);
    const nestedInfoResponse = await fetch(
      `${base}/api/workspace?root=${encodeURIComponent(nested)}`,
    );
    assert.equal(nestedInfoResponse.status, 403);

    client = new Client({
      name: "chatroom-integration-test",
      version: "1.0.0",
    });
    const transport = new StreamableHTTPClientTransport(new URL(`${base}/mcp`));
    await client.connect(transport);
    assert.equal(client.getServerVersion()?.version, packageVersion);
    const tools = await client.listTools();
    assert.ok(tools.tools.some((tool) => tool.name === "workspace_info"));
    assert.ok(tools.tools.some((tool) => tool.name === "computer_snapshot"));
    assert.ok(tools.tools.some((tool) => tool.name === "computer_action"));
    const workspace = await client.callTool({
      name: "workspace_info",
      arguments: { root: runtime.workspaceRoot },
    });
    assert.equal(workspace.isError, undefined);
    const workspaceInfo = workspace.structuredContent as {
      root: string;
      summary: string | null;
      presetPrompt: string | null;
    };
    assert.equal(workspaceInfo.root, runtime.workspaceRoot);
    assert.equal(workspaceInfo.summary, "ChatRoom integration workspace");
    assert.equal(
      workspaceInfo.presetPrompt,
      "Prefer minimal workspace changes.",
    );
    const startedProcess = await client.callTool({
      name: "process_start",
      arguments: {
        command: process.execPath,
        args: ["-e", "setTimeout(() => {}, 5000)"],
        cwd: runtime.workspaceRoot,
        timeoutMs: 5000,
      },
    });
    assert.equal(startedProcess.isError, undefined);
    const processSnapshot = startedProcess.structuredContent as {
      processId: string;
      operationId: string;
    };
    const processOperation = runtime.components.operations.get(
      processSnapshot.operationId,
    );
    assert.equal(processOperation?.pluginId, "process");
    assert.equal(processOperation?.source, "mcp");
    assert.equal(processOperation?.action, "start");
    assert.equal(processOperation?.processId, processSnapshot.processId);
    assert.equal(processOperation?.status, "running");
    await client.callTool({
      name: "process_kill",
      arguments: { processId: processSnapshot.processId, force: true },
    });
  } finally {
    await client?.close().catch(() => undefined);
    await runtime.cleanup();
  }
});

async function requestWithHost(
  port: number,
  requestPath: string,
  options: {
    method: string;
    host: string;
    headers?: Record<string, string>;
    body?: string;
  },
): Promise<{
  status: number;
  headers: http.IncomingHttpHeaders;
  body: string;
}> {
  return await new Promise((resolve, reject) => {
    const request = http.request(
      {
        host: "127.0.0.1",
        port,
        path: requestPath,
        method: options.method,
        headers: { host: options.host, ...(options.headers ?? {}) },
      },
      (response) => {
        const chunks: Buffer[] = [];
        response.on("data", (chunk: Buffer) => chunks.push(Buffer.from(chunk)));
        response.on("end", () =>
          resolve({
            status: response.statusCode ?? 0,
            headers: response.headers,
            body: Buffer.concat(chunks).toString("utf8"),
          }),
        );
      },
    );
    request.on("error", reject);
    if (options.body) request.end(options.body);
    else request.end();
  });
}

test("remote WebUI mutations require same-origin while loopback WebUI stays local", async () => {
  const runtime = await createTestRuntime({
    configure(config) {
      config.auth.webPublicBaseUrl = "https://chatroom.example.com";
    },
  });
  try {
    await runtime.components.http.start();
    const address = runtime.components.http.address();
    assert.ok(address);
    const base = `http://127.0.0.1:${address.port}`;

    const local = await fetch(`${base}/api/auth/login`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ownerToken: "not-required-locally" }),
    });
    assert.equal(local.status, 200);

    const rejected = await requestWithHost(address.port, "/api/auth/login", {
      method: "POST",
      host: "chatroom.example.com",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ownerToken: "test-owner-token" }),
    });
    assert.equal(rejected.status, 403);

    const accepted = await requestWithHost(address.port, "/api/auth/login", {
      method: "POST",
      host: "chatroom.example.com",
      headers: {
        "content-type": "application/json",
        origin: "https://chatroom.example.com",
      },
      body: JSON.stringify({ ownerToken: "test-owner-token" }),
    });
    assert.equal(accepted.status, 200);
    const cookie = accepted.headers["set-cookie"]?.[0];
    assert.ok(cookie);
    assert.match(cookie, /Secure/);

    runtime.components.computer.setSettings({
      enabled: true,
      remoteAccess: false,
    });
    const blockedComputerPreview = await requestWithHost(
      address.port,
      "/api/computer/preview",
      {
        method: "GET",
        host: "chatroom.example.com",
        headers: { cookie },
      },
    );
    assert.equal(
      blockedComputerPreview.status,
      403,
      "remote WebUI must not read Computer screenshots while remote access is disabled",
    );

    runtime.components.computer.setSettings({ remoteAccess: true });
    const allowedComputerPreview = await requestWithHost(
      address.port,
      "/api/computer/preview",
      {
        method: "GET",
        host: "chatroom.example.com",
        headers: { cookie },
      },
    );
    assert.equal(allowedComputerPreview.status, 200);
    assert.equal(allowedComputerPreview.body, "null");

    const wrongOrigin = await requestWithHost(address.port, "/api/operations", {
      method: "DELETE",
      host: "chatroom.example.com",
      headers: { cookie, origin: "https://evil.example.com" },
    });
    assert.equal(wrongOrigin.status, 403);

    const sameOrigin = await requestWithHost(address.port, "/api/operations", {
      method: "DELETE",
      host: "chatroom.example.com",
      headers: { cookie, origin: "https://chatroom.example.com" },
    });
    assert.equal(sameOrigin.status, 200);
  } finally {
    await runtime.cleanup();
  }
});

test("OAuth client registration logs accepted and rejected remote requests", async () => {
  const runtime = await createTestRuntime({
    configure(config) {
      config.auth.mcpPublicBaseUrl = "https://mcp.example.com";
    },
  });
  const registrationLogs: SystemLogRecord[] = [];
  const unsubscribeLogs = runtime.components.logger.subscribe((record) => {
    if (
      record.event === "oauth.registration_accepted" ||
      record.event === "oauth.registration_rejected"
    )
      registrationLogs.push(record);
  });
  try {
    await runtime.components.http.start();
    const address = runtime.components.http.address();
    assert.ok(address);

    const accepted = await requestWithHost(address.port, "/oauth/register", {
      method: "POST",
      host: "mcp.example.com",
      headers: {
        "content-type": "application/json",
        "user-agent": "remote-mcp-test/1.0",
      },
      body: JSON.stringify({
        client_name: "Remote MCP Test",
        redirect_uris: ["http://127.0.0.1/callback"],
      }),
    });
    assert.equal(accepted.status, 201);
    const registered = JSON.parse(accepted.body) as { client_id: string };
    const authorizeParams = new URLSearchParams({
      response_type: "code",
      client_id: registered.client_id,
      redirect_uri: "http://127.0.0.1/callback",
      code_challenge: "A".repeat(43),
      code_challenge_method: "S256",
      scope: "mcp",
    });
    const authorization = await requestWithHost(
      address.port,
      `/oauth/authorize?${authorizeParams}`,
      { method: "GET", host: "mcp.example.com" },
    );
    assert.equal(authorization.status, 200);
    assert.match(authorization.body, /addEventListener\("paste"/);
    assert.match(authorization.body, /event\.preventDefault\(\)/);
    assert.match(authorization.body, /navigator\.clipboard\.writeText\(""\)/);

    const acceptedLog = registrationLogs.find(
      (record) => record.event === "oauth.registration_accepted",
    );
    assert.ok(acceptedLog);
    assertRegistrationLog({
      value: acceptedLog.data,
      clientName: "Remote MCP Test",
      redirectUris: ["http://127.0.0.1/callback"],
      host: "mcp.example.com",
      userAgent: "remote-mcp-test/1.0",
      externalMcp: true,
    });

    const rejected = await requestWithHost(address.port, "/oauth/register", {
      method: "POST",
      host: "mcp.example.com",
      headers: {
        "content-type": "application/json",
        "user-agent": "rejected-mcp-test/1.0",
      },
      body: JSON.stringify({
        client_name: "Rejected MCP Test",
        redirect_uris: ["http://remote.example.com/callback"],
      }),
    });
    assert.equal(rejected.status, 400);
    const rejectedLog = registrationLogs.find(
      (record) => record.event === "oauth.registration_rejected",
    );
    assert.ok(rejectedLog);
    const rejectedDetails = rejectedLog.data ?? {};
    assert.equal(rejectedDetails.clientName, "Rejected MCP Test");
    assert.deepEqual(rejectedDetails.redirectUris, [
      "http://remote.example.com/callback",
    ]);
    assert.equal(rejectedDetails.host, "mcp.example.com");
    assert.equal(rejectedDetails.userAgent, "rejected-mcp-test/1.0");
    assert.equal(rejectedDetails.externalMcp, true);
    assert.equal(rejectedDetails.error, "FORBIDDEN");
    assert.equal(
      rejectedDetails.errorDescription,
      "OAuth redirects must use HTTPS unless loopback",
    );
    assert.match(String(rejectedDetails.timestamp), /^\d{4}-\d{2}-\d{2}T/);
    assert.equal(typeof rejectedDetails.sourceAddress, "string");
    assert.equal(typeof rejectedDetails.sourcePort, "number");
  } finally {
    unsubscribeLogs();
    await runtime.cleanup();
  }
});

function assertRegistrationLog(input: {
  value: unknown;
  clientName: string;
  redirectUris: string[];
  host: string;
  userAgent: string;
  externalMcp: boolean;
}): void {
  const value = input.value as Record<string, unknown>;
  assert.equal(value.clientName, input.clientName);
  assert.deepEqual(value.redirectUris, input.redirectUris);
  assert.equal(value.host, input.host);
  assert.equal(value.userAgent, input.userAgent);
  assert.equal(value.externalMcp, input.externalMcp);
  assert.match(String(value.clientId), /^client_/);
  assert.match(String(value.timestamp), /^\d{4}-\d{2}-\d{2}T/);
  assert.equal(typeof value.sourceAddress, "string");
  assert.equal(typeof value.sourcePort, "number");
}
