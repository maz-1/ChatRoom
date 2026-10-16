import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { readFileSync } from "node:fs";
import http from "node:http";
import https from "node:https";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import tls from "node:tls";
import test from "node:test";
import { createRuntime } from "#js2ray";
import { defaultJs2rayConfig } from "../../src/config/js2ray-config.js";
import { defaultConfig, loadConfig } from "../../src/config/load-config.js";
import { Js2rayController } from "../../src/plugins/js2ray/controller.js";
import { HttpFetcher } from "../../src/plugins/http/http-fetcher.js";
import { httpRequestSchema } from "../../src/plugins/http/types.js";
import type { LogWriter } from "../../src/core/logging/types.js";

const certificate = readFileSync(
  new URL("../fixtures/http-proxy/cert.pem", import.meta.url),
  "utf8",
);
const key = readFileSync(
  new URL("../fixtures/http-proxy/key.pem", import.meta.url),
  "utf8",
);
tls.setDefaultCACertificates([
  ...tls.getCACertificates("default"),
  certificate,
]);
const id = "d342d11e-d424-4583-b36e-524ab1f0afa4";
const limits = {
  defaultTimeoutMs: 3000,
  maxTimeoutMs: 5000,
  maxResponseBytes: 4096,
};
const quiet: LogWriter = { debug() {}, info() {}, warn() {}, error() {} };

async function listen(server: net.Server) {
  const sockets = new Set<net.Socket>();
  server.on("connection", (socket) => {
    sockets.add(socket);
    socket.on("error", () => {});
    socket.once("close", () => sockets.delete(socket));
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  return {
    port: (server.address() as net.AddressInfo).port,
    close: async () => {
      for (const socket of sockets) socket.destroy();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    },
  };
}

test("js2ray uses VLESS/WSS with Undici 8 for HTTP, target HTTPS and redirects without proxy listeners", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "chatroom-js2ray-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const handler: http.RequestListener = (request, response) => {
    if (request.url === "/start") {
      response.writeHead(307, { location: "/end" });
      response.end();
      return;
    }
    const chunks: Buffer[] = [];
    request.on("data", (chunk: Buffer) => chunks.push(chunk));
    request.on("end", () =>
      response.end(
        JSON.stringify({
          method: request.method,
          body: Buffer.concat(chunks).toString(),
          header: request.headers["x-test"],
        }),
      ),
    );
  };
  const plain = await listen(http.createServer(handler));
  t.after(plain.close);
  const secure = await listen(
    https.createServer({ cert: certificate, key }, handler),
  );
  t.after(secure.close);
  const upstream = createRuntime({
    dns: { hosts: { "unresolvable.invalid": "127.0.0.1" } },
    inbounds: [
      {
        tag: "vless",
        protocol: "vless",
        users: [{ id, encryption: "none" }],
        networks: [
          {
            type: "ws",
            address: "127.0.0.1",
            port: 0,
            option: { path: "/tunnel", tls: { cert: certificate, key } },
          },
        ],
      },
    ],
    outbounds: [{ protocol: "freedom" }],
  });
  await upstream.start();
  t.after(() => upstream.close());
  const port = (upstream.listenersInfo[0]!.address as net.AddressInfo).port;
  const settings = defaultJs2rayConfig();
  settings.enabled = true;
  settings.configPath = path.join(root, "runtime.json");
  await writeFile(
    settings.configPath,
    JSON.stringify({
      inbounds: [],
      outbounds: [
        {
          tag: "proxy",
          protocol: "vless",
          users: [{ id, encryption: "none" }],
          networks: [
            {
              type: "ws",
              address: "127.0.0.1",
              port,
              option: {
                path: "/tunnel",
                headers: { Host: "localhost" },
                tls: { ca: certificate, serverName: "localhost" },
              },
            },
          ],
        },
      ],
    }),
  );
  const controller = new Js2rayController(settings, quiet);
  await controller.start();
  t.after(() => controller.stop());
  const fetcher = new HttpFetcher(limits, (timeout) =>
    controller.createDispatcher(timeout),
  );
  for (const [scheme, host, targetPort, proxy] of [
    ["http", "unresolvable.invalid", plain.port, "js2ray"],
    ["https", "localhost", secure.port, "v2ray"],
  ] as const) {
    const result = await fetcher.request(
      httpRequestSchema.parse({
        url: `${scheme}://${host}:${targetPort}/start`,
        proxy,
        method: "POST",
        headers: { "x-test": "preserved" },
        body: "payload",
      }),
    );
    assert.equal(result.status, 200);
    assert.equal(result.url, `${scheme}://${host}:${targetPort}/end`);
    assert.deepEqual(JSON.parse(result.body), {
      method: "POST",
      body: "payload",
      header: "preserved",
    });
  }
  assert.equal(controller.status().listeners, 0);
  await controller.stop();
  await assert.rejects(
    fetcher.request(
      httpRequestSchema.parse({
        url: `http://127.0.0.1:${plain.port}`,
        proxy: "js2ray",
      }),
    ),
    /not running/,
  );
});

test("js2ray cancellation destroys pending tunnel connections and never falls back to direct", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "chatroom-js2ray-cancel-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const stuck = await listen(net.createServer(() => {}));
  t.after(stuck.close);
  const settings = defaultJs2rayConfig();
  settings.enabled = true;
  settings.configPath = path.join(root, "runtime.json");
  await writeFile(
    settings.configPath,
    JSON.stringify({
      outbounds: [
        {
          protocol: "vless",
          users: [{ id, encryption: "none" }],
          networks: [{ type: "tcp", address: "127.0.0.1", port: stuck.port }],
        },
      ],
    }),
  );
  const controller = new Js2rayController(settings, quiet);
  await controller.start();
  t.after(() => controller.stop());
  const fetcher = new HttpFetcher(limits, (timeout) =>
    controller.createDispatcher(timeout),
  );
  await assert.rejects(
    fetcher.request(
      httpRequestSchema.parse({
        url: "https://unresolvable.invalid",
        proxy: "js2ray",
        timeoutMs: 150,
      }),
    ),
    /timed out after 150ms/,
  );
  await controller.stop();
  const disabled = new Js2rayController(defaultJs2rayConfig(), quiet);
  assert.throws(() => disabled.createDispatcher(100), /not running/);
});

test("js2ray rejects local listeners and sanitizes startup failures", async (t) => {
  const root = await mkdtemp(
    path.join(os.tmpdir(), "chatroom-js2ray-invalid-"),
  );
  t.after(() => rm(root, { recursive: true, force: true }));
  const settings = defaultJs2rayConfig();
  settings.enabled = true;
  settings.configPath = path.join(root, "runtime.json");
  await writeFile(
    settings.configPath,
    JSON.stringify({
      inbounds: [
        { protocol: "socks", networks: [{ address: "127.0.0.1", port: 1080 }] },
      ],
    }),
  );
  const controller = new Js2rayController(settings, quiet);
  await assert.rejects(controller.start(), /no inbounds/);
  await writeFile(
    settings.configPath,
    "private-node-uuid-and-address-not-json",
  );
  await assert.rejects(controller.start(), (error: unknown) => {
    assert.ok(error instanceof Error);
    assert.doesNotMatch(JSON.stringify(error), /private-node/);
    return true;
  });
  assert.equal(controller.status().state, "error");
  assert.equal(controller.status().listeners, 0);
  await controller.stop();
});

test("js2ray settings resolve relative to the ChatRoom config and validate reverse ingress", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "chatroom-js2ray-config-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const file = path.join(root, "config.json");
  const config = defaultConfig();
  config.js2ray.enabled = true;
  config.js2ray.configPath = "./runtime.json";
  await writeFile(file, JSON.stringify(config));
  assert.equal(
    (await loadConfig(file)).js2ray.configPath,
    path.join(root, "runtime.json"),
  );
  config.js2ray.reverse.enabled = true;
  await writeFile(file, JSON.stringify(config));
  await assert.rejects(loadConfig(file), /public MCP or WebUI/);
  config.auth.mcpPublicBaseUrl = "https://mcp.example.com";
  await writeFile(file, JSON.stringify(config));
  assert.equal((await loadConfig(file)).js2ray.reverse.enabled, true);
  const old = JSON.parse(await readFile(file, "utf8")) as Record<
    string,
    unknown
  >;
  delete old.js2ray;
  await writeFile(file, JSON.stringify(old));
  assert.equal((await loadConfig(file)).js2ray.enabled, false);
});
