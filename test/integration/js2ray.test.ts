import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import http from "node:http";
import type { AddressInfo } from "node:net";
import os from "node:os";
import path from "node:path";
import test, { type TestContext } from "node:test";
import {
  Client,
  StreamableHTTPClientTransport,
} from "@modelcontextprotocol/client";
import { createHttpAgent, createRuntime } from "#js2ray";
import { createTestRuntime } from "../helpers/runtime.js";

const id = "d342d11e-d424-4583-b36e-524ab1f0afa4";

test("http_request selects js2ray through MCP and records the proxy in its audit", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "chatroom-js2ray-mcp-"));
  const file = path.join(root, "runtime.json");
  await writeFile(
    file,
    JSON.stringify({
      inbounds: [],
      outbounds: [{ tag: "direct", protocol: "freedom" }],
    }),
  );
  const runtime = await createTestRuntime({
    configure(config) {
      config.js2ray.enabled = true;
      config.js2ray.configPath = file;
    },
  });
  const target = http.createServer((_request, response) =>
    response.end("js2ray via MCP"),
  );
  await new Promise<void>((resolve) => target.listen(0, "127.0.0.1", resolve));
  const client = new Client({ name: "js2ray-integration", version: "1.0.0" });
  t.after(async () => {
    await client.close();
    await runtime.cleanup();
    target.closeAllConnections();
    await new Promise<void>((resolve) => target.close(() => resolve()));
    await rm(root, { recursive: true, force: true });
  });
  await runtime.components.http.start();
  await runtime.components.js2ray.start();
  const port = runtime.components.http.address()!.port;
  await client.connect(
    new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`)),
  );
  const result = await client.callTool({
    name: "http_request",
    arguments: {
      url: `http://127.0.0.1:${(target.address() as AddressInfo).port}`,
      proxy: "js2ray",
    },
  });
  assert.equal(result.isError, undefined);
  const content = result.structuredContent as {
    body: string;
    operationId: string;
  };
  assert.equal(content.body, "js2ray via MCP");
  assert.equal(
    (
      runtime.components.operations.get(content.operationId)!.input as {
        proxy: string;
      }
    ).proxy,
    "js2ray",
  );
});

async function setup(t: TestContext, web = false) {
  const root = await mkdtemp(path.join(os.tmpdir(), "chatroom-reverse-test-"));
  const portal = createRuntime({
    inbounds: [
      {
        tag: "carrier",
        protocol: "vless",
        users: [{ id, encryption: "none" }],
        networks: [{ type: "tcp", address: "127.0.0.1", port: 0 }],
      },
    ],
    outbounds: [{ tag: "block", protocol: "blackhole" }],
    reverse: { portals: [{ tag: "portal", domain: "control.test" }] },
    routing: { rules: [{ inboundTag: ["carrier"], outboundTag: "portal" }] },
  });
  await portal.start();
  const port = (portal.listenersInfo[0]!.address as AddressInfo).port;
  const file = path.join(root, "runtime.json");
  await writeFile(
    file,
    JSON.stringify({
      inbounds: [],
      outbounds: [
        {
          tag: "proxy",
          protocol: "vless",
          users: [{ id, encryption: "none" }],
          networks: [{ type: "tcp", address: "127.0.0.1", port }],
        },
        { tag: "mcp-local", protocol: "freedom", redirect: "127.0.0.1:1" },
      ],
      reverse: { bridges: [{ tag: "bridge", domain: "control.test" }] },
      routing: {
        rules: [
          {
            inboundTag: ["bridge"],
            domain: ["full:control.test"],
            outboundTag: "proxy",
          },
          { inboundTag: ["bridge"], outboundTag: "mcp-local" },
        ],
      },
    }),
  );
  const runtime = await createTestRuntime({
    configure(config) {
      config.js2ray.enabled = true;
      config.js2ray.configPath = file;
      config.js2ray.reverse.enabled = true;
      config.auth.mcpPublicBaseUrl = "https://mcp.example.com";
      config.auth.webPublicBaseUrl = web ? "https://web.example.com" : null;
    },
  });
  const agent = createHttpAgent(portal, {
    outboundTag: "portal",
    keepAlive: true,
  });
  t.after(async () => {
    agent.destroy();
    await runtime.cleanup();
    await portal.close();
    await rm(root, { recursive: true, force: true });
  });
  await runtime.components.http.start();
  await runtime.components.js2ray.start();
  const deadline = Date.now() + 5000;
  while (!portal.stats.reverse[0]?.active) {
    if (Date.now() >= deadline) throw new Error("Bridge did not become active");
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  async function request(
    url: string,
    options: http.RequestOptions = {},
    body?: string,
  ) {
    return new Promise<{
      status: number;
      headers: http.IncomingHttpHeaders;
      body: string;
    }>((resolve, reject) => {
      const req = http.request(
        `http://127.0.0.1:8765${url}`,
        { agent, ...options },
        (res) => {
          const chunks: Buffer[] = [];
          res.on("data", (chunk: Buffer) => chunks.push(chunk));
          res.once("error", reject);
          res.once("end", () =>
            resolve({
              status: res.statusCode!,
              headers: res.headers,
              body: Buffer.concat(chunks).toString(),
            }),
          );
        },
      );
      req.once("error", reject);
      req.setTimeout(5000, () =>
        req.destroy(new Error("Reverse request timed out")),
      );
      req.end(body);
    });
  }
  return { runtime, portal, agent, request };
}

test("js2ray reverse pins remote identity, completes OAuth/MCP and isolates MCP-only ingress", async (t) => {
  const { runtime, portal, request } = await setup(t);
  const metadata = await request("/.well-known/oauth-authorization-server", {
    headers: {
      Host: "localhost",
      "X-Forwarded-Host": "localhost",
      "X-Forwarded-Proto": "http",
    },
  });
  assert.equal(metadata.status, 200);
  assert.equal(JSON.parse(metadata.body).issuer, "https://mcp.example.com");
  const denied = await request(
    "/mcp",
    {
      method: "POST",
      headers: { Host: "localhost", "content-type": "application/json" },
    },
    "{}",
  );
  assert.equal(denied.status, 401);
  assert.match(
    denied.headers["www-authenticate"]!,
    /https:\/\/mcp.example.com/,
  );
  for (const resource of [
    "/api/runtime",
    "/api/auth/status",
    "/",
    "/api%2fauth/status",
  ])
    assert.equal(
      (await request(resource, { headers: { Host: "localhost" } })).status,
      404,
    );

  const registered = await request(
    "/oauth/register",
    { method: "POST", headers: { "content-type": "application/json" } },
    JSON.stringify({
      client_name: "reverse-test",
      redirect_uris: ["http://localhost/callback"],
    }),
  );
  assert.equal(registered.status, 201);
  const clientId = JSON.parse(registered.body).client_id as string;
  const verifier = "v".repeat(43);
  const authorization = new URLSearchParams({
    response_type: "code",
    client_id: clientId,
    redirect_uri: "http://localhost/callback",
    code_challenge: createHash("sha256").update(verifier).digest("base64url"),
    code_challenge_method: "S256",
    scope: "mcp",
    owner_token: "test-owner-token",
  });
  const approved = await request(
    "/oauth/authorize",
    {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
    },
    authorization.toString(),
  );
  assert.equal(approved.status, 303);
  const code = new URL(approved.headers.location!).searchParams.get("code")!;
  const exchanged = await request(
    "/oauth/token",
    {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
    },
    new URLSearchParams({
      grant_type: "authorization_code",
      client_id: clientId,
      redirect_uri: "http://localhost/callback",
      code_verifier: verifier,
      code,
    }).toString(),
  );
  assert.equal(exchanged.status, 200);
  const token = JSON.parse(exchanged.body).access_token as string;
  const tools = await request(
    "/mcp",
    {
      method: "POST",
      headers: {
        Host: "127.0.0.1",
        authorization: `Bearer ${token}`,
        "content-type": "application/json",
        accept: "application/json, text/event-stream",
      },
    },
    JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/list", params: {} }),
  );
  assert.equal(tools.status, 200);
  assert.match(tools.body, /http_request/);
  assert.equal(runtime.components.js2ray.status().listeners, 0);

  await assert.rejects(
    portal
      .dial({ host: "other-private.test", port: 80, outboundTag: "portal" })
      .then(async (stream) => {
        stream.on("error", () => {});
        stream.write("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
        for await (const _chunk of stream)
          assert.fail("Out-of-scope service received data");
        throw new Error("Out-of-scope service closed");
      }),
  );
});

test("js2ray reverse WebUI requires login and public Origin and streams authenticated events", async (t) => {
  const { runtime, agent, request } = await setup(t, true);
  assert.equal(
    (await request("/api/runtime", { headers: { Host: "localhost" } })).status,
    401,
  );
  assert.equal(
    (
      await request(
        "/MCP",
        {
          method: "POST",
          headers: {
            Host: "localhost",
            "content-type": "application/json",
            accept: "application/json, text/event-stream",
          },
        },
        JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/list" }),
      )
    ).status,
    401,
  );
  const loginBody = JSON.stringify({ ownerToken: "test-owner-token" });
  assert.equal(
    (
      await request(
        "/api/auth/login",
        {
          method: "POST",
          headers: {
            Host: "localhost",
            "content-type": "application/json",
            origin: "http://localhost",
          },
        },
        loginBody,
      )
    ).status,
    403,
  );
  const login = await request(
    "/api/auth/login",
    {
      method: "POST",
      headers: {
        Host: "localhost",
        "content-type": "application/json",
        origin: "https://web.example.com",
      },
    },
    loginBody,
  );
  assert.equal(login.status, 200);
  const cookies = login.headers["set-cookie"]!;
  assert.match(cookies[0]!, /; Secure/);
  const cookie = cookies[0]!.split(";", 1)[0]!;
  const status = await request("/api/js2ray", { headers: { cookie } });
  assert.equal(status.status, 200);
  assert.equal(JSON.parse(status.body).state, "running");
  assert.deepEqual(JSON.parse(status.body).listenerEndpoints, []);
  assert.doesNotMatch(status.body, new RegExp(id));
  await new Promise<void>((resolve, reject) => {
    const req = http.get(
      "http://127.0.0.1:8765/api/events",
      { agent, headers: { cookie, Host: "localhost" } },
      (res) => {
        assert.equal(res.statusCode, 200);
        let received = "";
        res.on("data", (chunk: Buffer) => {
          received += chunk.toString();
          if (received.includes("event: runtime")) {
            req.destroy();
            resolve();
          }
        });
        runtime.components.eventBus.emit({ type: "mcp-servers" });
      },
    );
    req.once("error", reject);
    req.setTimeout(5000, () =>
      req.destroy(new Error("SSE event was buffered")),
    );
  });
});
