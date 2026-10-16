import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import http from "node:http";
import https from "node:https";
import net, { type AddressInfo, type Socket } from "node:net";
import tls from "node:tls";
import test from "node:test";
import { HttpFetcher } from "../../src/plugins/http/http-fetcher.js";
import { httpRequestSchema } from "../../src/plugins/http/types.js";

// Test-only certificate; trust it without disabling certificate verification.
const cert = readFileSync(
  new URL("../fixtures/http-proxy/cert.pem", import.meta.url),
  "utf8",
);
const key = readFileSync(
  new URL("../fixtures/http-proxy/key.pem", import.meta.url),
  "utf8",
);
tls.setDefaultCACertificates([...tls.getCACertificates("default"), cert]);

const fetcher = new HttpFetcher({
  defaultTimeoutMs: 2000,
  maxTimeoutMs: 10000,
  maxResponseBytes: 1024,
});

async function listen(server: net.Server) {
  const sockets = new Set<Socket>();
  server.on("connection", (socket: Socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
    socket.on("error", () => {});
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  return {
    port: (server.address() as AddressInfo).port,
    close: async () => {
      for (const socket of sockets) socket.destroy();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    },
  };
}

function tunnel(
  client: Socket,
  host: string,
  port: number,
  onConnected: () => void,
) {
  const upstream = net.connect(port, host, () => {
    onConnected();
    client.pipe(upstream).pipe(client);
  });
  upstream.on("error", () => client.destroy());
  client.on("close", () => upstream.destroy());
}

for (const protocol of ["http", "https", "socks5"] as const) {
  test(`http fetcher uses ${protocol} proxy for HTTP/HTTPS targets and redirects`, async () => {
    const observed: string[] = [];
    const handler: http.RequestListener = (request, response) => {
      if (request.url === "/start") {
        response.writeHead(307, { location: "/end" });
        response.end();
      } else {
        const chunks: Buffer[] = [];
        request.on("data", (chunk: Buffer) => chunks.push(chunk));
        request.on("end", () => {
          response.setHeader("content-type", "application/json");
          response.end(
            JSON.stringify({
              method: request.method,
              body: Buffer.concat(chunks).toString(),
              header: request.headers["x-test"],
            }),
          );
        });
      }
    };
    const plain = await listen(http.createServer(handler));
    const secure = await listen(https.createServer({ key, cert }, handler));
    let proxyServer: net.Server;
    if (protocol === "socks5") {
      proxyServer = net.createServer((socket) => {
        let buffered = Buffer.alloc(0);
        let greeting = true;
        const receive = (chunk: Buffer) => {
          buffered = Buffer.concat([buffered, chunk]);
          if (greeting) {
            if (buffered.length < 2 || buffered.length < 2 + buffered[1]!)
              return;
            assert.equal(buffered[0], 5);
            assert.ok(buffered.subarray(2, 2 + buffered[1]!).includes(0));
            buffered = buffered.subarray(2 + buffered[1]!);
            greeting = false;
            socket.write(Buffer.from([5, 0]));
          }
          if (buffered.length < 5) return;
          const addressType = buffered[3];
          const addressLength = addressType === 1 ? 4 : buffered[4]!;
          const offset = addressType === 1 ? 4 : 5;
          if (buffered.length < offset + addressLength + 2) return;
          assert.equal(buffered[1], 1);
          const host =
            addressType === 1
              ? [...buffered.subarray(offset, offset + addressLength)].join(".")
              : buffered.toString("utf8", offset, offset + addressLength);
          const port = buffered.readUInt16BE(offset + addressLength);
          observed.push(`${host}:${port}`);
          socket.off("data", receive);
          const pending = buffered.subarray(offset + addressLength + 2);
          if (pending.length) socket.unshift(pending);
          // Test domains deliberately do not resolve locally.
          tunnel(socket, "127.0.0.1", port, () =>
            socket.write(Buffer.from([5, 0, 0, 1, 127, 0, 0, 1, 0, 0])),
          );
        };
        socket.on("data", receive);
      });
    } else {
      const forward: http.RequestListener = (request, response) => {
        const url = new URL(request.url!);
        observed.push(url.toString());
        const upstream = http.request(
          {
            hostname: "127.0.0.1",
            port: url.port,
            path: url.pathname,
            method: request.method,
            headers: request.headers,
          },
          (result) => {
            response.writeHead(result.statusCode!, result.headers);
            result.pipe(response);
          },
        );
        upstream.on("error", () => response.destroy());
        request.pipe(upstream);
      };
      const server =
        protocol === "https"
          ? https.createServer({ key, cert }, forward)
          : http.createServer(forward);
      server.on("connect", (request, socket, head) => {
        observed.push(request.url!);
        const url = new URL(`http://${request.url}`);
        if (head.length) socket.unshift(head);
        tunnel(socket as Socket, "127.0.0.1", Number(url.port), () =>
          socket.write("HTTP/1.1 200 Connection Established\r\n\r\n"),
        );
      });
      proxyServer = server;
    }
    const proxy = await listen(proxyServer);
    try {
      for (const [scheme, port, host] of [
        ["http", plain.port, "unresolvable.invalid"],
        ["https", secure.port, "localhost"],
      ] as const) {
        const result = await fetcher.request(
          httpRequestSchema.parse({
            url: `${scheme}://${host}:${port}/start`,
            proxy: `${protocol}://127.0.0.1:${proxy.port}`,
            method: "POST",
            body: "proxied payload",
            headers: { "x-test": "preserved" },
          }),
        );
        assert.equal(result.status, 200);
        assert.equal(result.url, `${scheme}://${host}:${port}/end`);
        assert.deepEqual(JSON.parse(result.body), {
          method: "POST",
          body: "proxied payload",
          header: "preserved",
        });
      }
      assert.ok(
        observed.some((value) =>
          value.includes(`unresolvable.invalid:${plain.port}`),
        ),
      );
      assert.ok(
        observed.some((value) => value.includes(`localhost:${secure.port}`)),
      );
    } finally {
      await proxy.close();
      await plain.close();
      await secure.close();
    }
  });
}

test("http fetcher rejects malformed, authenticated, and unsupported proxies", async () => {
  for (const proxy of [
    "not a url",
    "http://user:pass@localhost:8080",
    "https://localhost/path",
    "socks5://localhost:0",
    "socks5://localhost:65536",
    "ftp://localhost:21",
  ]) {
    await assert.rejects(
      fetcher.request(
        httpRequestSchema.parse({ url: "http://127.0.0.1:1/", proxy }),
      ),
      (error: unknown) => {
        assert.ok(error instanceof Error);
        assert.match(error.message, /proxy|proxies/i);
        return true;
      },
    );
  }
});

for (const protocol of ["http", "https", "socks5"]) {
  test(`http fetcher times out during ${protocol} proxy negotiation`, async () => {
    const proxy = await listen(net.createServer(() => {}));
    try {
      await assert.rejects(
        fetcher.request(
          httpRequestSchema.parse({
            url: "https://example.invalid/",
            proxy: `${protocol}://127.0.0.1:${proxy.port}`,
            timeoutMs: 150,
          }),
        ),
        /timed out after 150ms/,
      );
    } finally {
      await proxy.close();
    }
  });
}
