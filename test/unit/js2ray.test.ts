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

function captureLogs() {
  const records: Array<{
    level: string;
    event: string;
    message: string;
    data: Record<string, unknown> | undefined;
  }> = [];
  const writer =
    (level: string): LogWriter["warn"] =>
    (_module, event, message, data) =>
      records.push({ level, event, message, data });
  const logs: LogWriter = {
    debug: writer("debug"),
    info: writer("info"),
    warn: writer("warn"),
    error: writer("error"),
  };
  return { records, logs };
}

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
  const records: Array<{ code?: unknown }> = [];
  const capture: LogWriter["debug"] = (_module, _event, _message, data) => {
    if (data) records.push(data);
  };
  const controller = new Js2rayController(settings, {
    debug: capture,
    info: capture,
    warn: capture,
    error: capture,
  });
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
  assert.ok(!records.some((record) => record.code === "ERR_CANCELLED"));
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
  const { records, logs } = captureLogs();
  const controller = new Js2rayController(settings, logs);
  await controller.start();
  t.after(() => controller.stop());
  const fetcher = new HttpFetcher(
    limits,
    (timeout) => controller.createDispatcher(timeout),
    logs,
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
  const failures = records.filter(
    (record) => record.event === "http.request_failed",
  );
  assert.equal(failures.length, 1);
  assert.equal(failures[0]!.level, "warn");
  assert.equal(failures[0]!.data?.code, "ERR_TIMEOUT");
  assert.equal(failures[0]!.data?.proxy, "js2ray");
  await controller.stop();
  const disabled = new Js2rayController(defaultJs2rayConfig(), quiet);
  assert.throws(() => disabled.createDispatcher(100), /not running/);
});

test("js2ray logs body timeouts and network failures while successful cleanup remains silent", async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), "chatroom-js2ray-body-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const target = await listen(
    http.createServer((request, response) => {
      if (request.url?.startsWith("/slow")) {
        response.writeHead(200, { "content-type": "text/plain" });
        response.write("partial");
      } else response.end("ok");
    }),
  );
  t.after(target.close);
  const settings = defaultJs2rayConfig();
  settings.enabled = true;
  settings.configPath = path.join(root, "runtime.json");
  await writeFile(
    settings.configPath,
    JSON.stringify({ outbounds: [{ protocol: "freedom" }] }),
  );
  const { records, logs } = captureLogs();
  const controller = new Js2rayController(settings, logs);
  await controller.start();
  t.after(() => controller.stop());
  const fetcher = new HttpFetcher(
    limits,
    (timeout) => controller.createDispatcher(timeout),
    logs,
  );
  await Promise.all([
    assert.rejects(
      fetcher.request(
        httpRequestSchema.parse({
          url: `http://127.0.0.1:${target.port}/slow?token=private-query`,
          headers: { authorization: "Bearer private-header" },
          proxy: "v2ray",
          timeoutMs: 200,
        }),
      ),
      /timed out after 200ms/,
    ),
    (async () => {
      const result = await fetcher.request(
        httpRequestSchema.parse({
          url: `http://127.0.0.1:${target.port}/ok`,
          proxy: "js2ray",
        }),
      );
      assert.equal(result.body, "ok");
    })(),
  ]);
  const failures = records.filter(
    (record) => record.event === "http.request_failed",
  );
  assert.equal(failures.length, 1);
  assert.equal(failures[0]!.data?.code, "ERR_TIMEOUT");
  assert.equal(failures[0]!.data?.proxy, "js2ray");
  assert.ok(!records.some((record) => record.data?.code === "ERR_CANCELLED"));
  assert.doesNotMatch(
    JSON.stringify(records),
    /private-query|private-header|\/slow/,
  );
  const reserved = await listen(net.createServer());
  await reserved.close();
  await assert.rejects(
    fetcher.request(
      httpRequestSchema.parse({
        url: `http://127.0.0.1:${reserved.port}/`,
        proxy: "js2ray",
      }),
    ),
    /HTTP request failed/,
  );
  assert.ok(
    records.some(
      (record) =>
        record.level === "warn" &&
        record.event === "js2ray.diagnostic" &&
        record.data?.code === "ERR_TRANSPORT",
    ),
  );
  assert.equal(
    records.filter((record) => record.event === "http.request_failed").length,
    2,
  );
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
