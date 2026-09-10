import assert from "node:assert/strict";
import http from "node:http";
import type { AddressInfo } from "node:net";
import test from "node:test";
import { ChatRoomError } from "../../src/core/errors/chatroom-error.js";
import {
  HttpFetcher,
  type HttpFetcherLimits,
} from "../../src/plugins/http/http-fetcher.js";

interface TestServer {
  port: number;
  close(): Promise<void>;
}

function startServer(handler: http.RequestListener): Promise<TestServer> {
  const server = http.createServer(handler);
  return new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address() as AddressInfo;
      resolve({
        port,
        close: () =>
          new Promise<void>((done) => {
            server.closeAllConnections();
            server.close(() => done());
          }),
      });
    });
  });
}

function readBody(request: http.IncomingMessage): Promise<Buffer> {
  return new Promise((resolve) => {
    const chunks: Buffer[] = [];
    request.on("data", (chunk: Buffer) => chunks.push(chunk));
    request.on("end", () => resolve(Buffer.concat(chunks)));
  });
}

function createFetcher(
  overrides: Partial<HttpFetcherLimits> = {},
): HttpFetcher {
  return new HttpFetcher({
    defaultTimeoutMs: 2_000,
    maxTimeoutMs: 10_000,
    maxResponseBytes: 16 * 1024,
    ...overrides,
  });
}

async function assertChatRoomError(
  promise: Promise<unknown>,
  code: string,
  messagePattern: RegExp,
): Promise<void> {
  await assert.rejects(promise, (error: unknown) => {
    assert.ok(error instanceof ChatRoomError, `not a ChatRoomError: ${error}`);
    assert.equal(error.code, code);
    assert.match(error.message, messagePattern);
    return true;
  });
}

test("http fetcher performs GET requests and decodes textual responses", async () => {
  const server = await startServer((_request, response) => {
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ hello: "chatroom" }));
  });
  try {
    const response = await createFetcher().request({
      url: `http://127.0.0.1:${server.port}/`,
      method: "GET",
      headers: undefined,
      body: undefined,
      bodyEncoding: "text",
      timeoutMs: undefined,
      responseFormat: "auto",
    });
    assert.equal(response.status, 200);
    assert.equal(response.bodyEncoding, "text");
    assert.equal(response.truncated, false);
    assert.deepEqual(JSON.parse(response.body), { hello: "chatroom" });
    assert.equal(response.headers["content-type"], "application/json");
    assert.equal(
      response.url,
      `http://127.0.0.1:${server.port}/`,
      "final URL should be reported",
    );
  } finally {
    await server.close();
  }
});

test("http fetcher sends POST bodies with custom headers", async () => {
  const server = await startServer(async (request, response) => {
    const body = (await readBody(request)).toString("utf8");
    response.writeHead(201, { "content-type": "application/json" });
    response.end(
      JSON.stringify({
        method: request.method,
        contentType: request.headers["content-type"],
        authorization: request.headers.authorization ?? null,
        body,
      }),
    );
  });
  try {
    const response = await createFetcher().request({
      url: `http://127.0.0.1:${server.port}/submit`,
      method: "POST",
      headers: {
        "content-type": "application/json",
        authorization: "Bearer test-token",
      },
      body: JSON.stringify({ value: 42 }),
      bodyEncoding: "text",
      timeoutMs: undefined,
      responseFormat: "auto",
    });
    assert.equal(response.status, 201);
    assert.deepEqual(JSON.parse(response.body), {
      method: "POST",
      contentType: "application/json",
      authorization: "Bearer test-token",
      body: JSON.stringify({ value: 42 }),
    });
  } finally {
    await server.close();
  }
});

test("http fetcher sends base64 request bodies byte-exactly", async () => {
  const server = await startServer(async (request, response) => {
    const body = await readBody(request);
    response.writeHead(200, { "content-type": "text/plain" });
    response.end(body.toString("hex"));
  });
  try {
    const payload = Buffer.from([0, 1, 2, 250, 251, 255]);
    const response = await createFetcher().request({
      url: `http://127.0.0.1:${server.port}/binary`,
      method: "PUT",
      headers: undefined,
      body: payload.toString("base64"),
      bodyEncoding: "base64",
      timeoutMs: undefined,
      responseFormat: "text",
    });
    assert.equal(response.status, 200);
    assert.equal(response.body, payload.toString("hex"));
  } finally {
    await server.close();
  }
});

test("http fetcher follows redirects and reports the final URL", async () => {
  const server = await startServer((request, response) => {
    if (request.url === "/start") {
      response.writeHead(302, { location: "/landing" });
      response.end();
      return;
    }
    response.writeHead(200, { "content-type": "text/plain" });
    response.end("arrived");
  });
  try {
    const response = await createFetcher().request({
      url: `http://127.0.0.1:${server.port}/start`,
      method: "GET",
      headers: undefined,
      body: undefined,
      bodyEncoding: "text",
      timeoutMs: undefined,
      responseFormat: "auto",
    });
    assert.equal(response.status, 200);
    assert.equal(response.body, "arrived");
    assert.equal(
      response.url,
      `http://127.0.0.1:${server.port}/landing`,
      "redirect should be followed and the final URL reported",
    );
  } finally {
    await server.close();
  }
});

test("http fetcher truncates responses beyond maxResponseBytes", async () => {
  const server = await startServer((_request, response) => {
    response.writeHead(200, { "content-type": "text/plain" });
    response.end("x".repeat(64 * 1024));
  });
  try {
    const response = await createFetcher({
      maxResponseBytes: 1024,
    }).request({
      url: `http://127.0.0.1:${server.port}/large`,
      method: "GET",
      headers: undefined,
      body: undefined,
      bodyEncoding: "text",
      timeoutMs: undefined,
      responseFormat: "auto",
    });
    assert.equal(response.truncated, true);
    assert.equal(response.bodyBytes, 1024);
    assert.equal(response.body.length, 1024);
  } finally {
    await server.close();
  }
});

test("http fetcher returns binary responses as base64 in auto mode", async () => {
  const bytes = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10, 0, 255]);
  const server = await startServer((_request, response) => {
    response.writeHead(200, { "content-type": "image/png" });
    response.end(bytes);
  });
  try {
    const response = await createFetcher().request({
      url: `http://127.0.0.1:${server.port}/image.png`,
      method: "GET",
      headers: undefined,
      body: undefined,
      bodyEncoding: "text",
      timeoutMs: undefined,
      responseFormat: "auto",
    });
    assert.equal(response.bodyEncoding, "base64");
    assert.equal(
      Buffer.from(response.body, "base64").toString("hex"),
      bytes.toString("hex"),
    );
  } finally {
    await server.close();
  }
});

test("http fetcher skips the body entirely with responseFormat omit", async () => {
  const server = await startServer((_request, response) => {
    response.writeHead(200, { "content-type": "text/plain" });
    response.end("x".repeat(64 * 1024));
  });
  try {
    const response = await createFetcher({
      maxResponseBytes: 1024,
    }).request({
      url: `http://127.0.0.1:${server.port}/large`,
      method: "GET",
      headers: undefined,
      body: undefined,
      bodyEncoding: "text",
      timeoutMs: undefined,
      responseFormat: "omit",
    });
    assert.equal(response.status, 200);
    assert.equal(response.body, "");
    assert.equal(response.bodyBytes, 0);
    assert.equal(response.truncated, false);
  } finally {
    await server.close();
  }
});

test("http fetcher reports timeouts as PROCESS_FAILED", async () => {
  const server = await startServer((_request, _response) => {
    // Deliberately never respond.
  });
  try {
    await assertChatRoomError(
      createFetcher().request({
        url: `http://127.0.0.1:${server.port}/slow`,
        method: "GET",
        headers: undefined,
        body: undefined,
        bodyEncoding: "text",
        timeoutMs: 300,
        responseFormat: "auto",
      }),
      "PROCESS_FAILED",
      /timed out after 300ms/,
    );
  } finally {
    await server.close();
  }
});

test("http fetcher reports connection failures as PROCESS_FAILED", async () => {
  const reserved = await startServer((_request, response) => {
    response.end("unused");
  });
  const port = reserved.port;
  await reserved.close();

  await assertChatRoomError(
    createFetcher().request({
      url: `http://127.0.0.1:${port}/nowhere`,
      method: "GET",
      headers: undefined,
      body: undefined,
      bodyEncoding: "text",
      timeoutMs: undefined,
      responseFormat: "auto",
    }),
    "PROCESS_FAILED",
    /HTTP request failed/,
  );
});

test("http fetcher rejects unsupported URL schemes and malformed URLs", async () => {
  const fetcher = createFetcher();
  const base = {
    method: "GET",
    headers: undefined,
    body: undefined,
    bodyEncoding: "text",
    timeoutMs: undefined,
    responseFormat: "auto",
  } as const;
  await assertChatRoomError(
    fetcher.request({ url: "ftp://example.com/file", ...base }),
    "UNSUPPORTED",
    /Only http and https/,
  );
  await assertChatRoomError(
    fetcher.request({ url: "not a url", ...base }),
    "INVALID_INPUT",
    /Invalid request URL/,
  );
  await assertChatRoomError(
    fetcher.request({ url: "https://user:pass@example.com/", ...base }),
    "INVALID_INPUT",
    /URL-embedded credentials/,
  );
});

test("http fetcher rejects invalid base64 bodies and GET request bodies", async () => {
  const fetcher = createFetcher();
  await assertChatRoomError(
    fetcher.request({
      url: "http://127.0.0.1:1/",
      method: "POST",
      headers: undefined,
      body: "not*base64!",
      bodyEncoding: "base64",
      timeoutMs: undefined,
      responseFormat: "auto",
    }),
    "INVALID_INPUT",
    /not valid standard base64/,
  );
  await assertChatRoomError(
    fetcher.request({
      url: "http://127.0.0.1:1/",
      method: "GET",
      headers: undefined,
      body: "payload",
      bodyEncoding: "text",
      timeoutMs: undefined,
      responseFormat: "auto",
    }),
    "INVALID_INPUT",
    /cannot be sent with GET/,
  );
});
