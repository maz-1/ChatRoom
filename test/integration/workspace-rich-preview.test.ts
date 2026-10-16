import assert from "node:assert/strict";
import { symlink, writeFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { createTestRuntime } from "../helpers/runtime.js";

test("workspace document and media preview stream full and byte-range responses safely", async () => {
  const runtime = await createTestRuntime();
  try {
    const bytes = Buffer.from("%PDF-1.7\n" + "x".repeat(3 * 1024 * 1024));
    await writeFile(path.join(runtime.workspaceRoot, "sample.pdf"), bytes);
    await writeFile(path.join(runtime.workspaceRoot, "movie.mp4"), bytes);
    await writeFile(path.join(runtime.workspaceRoot, "sound.ogg"), bytes);
    await writeFile(
      path.join(runtime.workspaceRoot, "not-allowed.html"),
      "<script>alert(1)</script>",
    );
    const outside = path.join(runtime.root, "outside.pdf");
    await writeFile(outside, "outside the workspace");
    await symlink(outside, path.join(runtime.workspaceRoot, "outside.pdf"));

    await runtime.components.http.start();
    const address = runtime.components.http.address();
    assert.ok(address);
    const url = (pathName: string) =>
      `http://127.0.0.1:${address.port}/api/workspace/file/preview?root=` +
      encodeURIComponent(runtime.workspaceRoot) +
      "&path=" +
      encodeURIComponent(pathName);

    const pdf = await fetch(url("sample.pdf"));
    assert.equal(pdf.status, 200);
    assert.equal(pdf.headers.get("content-type"), "application/pdf");
    assert.equal(
      pdf.headers.get("cache-control"),
      "private, no-store, no-transform",
    );
    assert.equal(pdf.headers.get("accept-ranges"), "bytes");
    assert.equal(
      Buffer.compare(Buffer.from(await pdf.arrayBuffer()), bytes),
      0,
    );

    const partial = await fetch(url("movie.mp4"), {
      headers: { Range: "bytes=10-19", "Accept-Encoding": "gzip, br" },
    });
    assert.equal(partial.status, 206);
    assert.equal(partial.headers.get("content-encoding"), null);
    assert.equal(partial.headers.get("content-type"), "video/mp4");
    assert.equal(
      partial.headers.get("content-range"),
      `bytes 10-19/${bytes.length}`,
    );
    assert.equal(await partial.text(), bytes.subarray(10, 20).toString());

    const suffix = await fetch(url("sample.pdf"), {
      headers: { Range: "bytes=-7" },
    });
    assert.equal(suffix.status, 206);
    assert.equal(await suffix.text(), bytes.subarray(-7).toString());

    const openEnded = await fetch(url("sample.pdf"), {
      headers: { Range: `bytes=${bytes.length - 4}-` },
    });
    assert.equal(openEnded.status, 206);
    assert.equal(await openEnded.text(), bytes.subarray(-4).toString());

    const invalid = await fetch(url("movie.mp4"), {
      headers: { Range: "bytes=999999999-" },
    });
    assert.equal(invalid.status, 416);
    assert.equal(
      invalid.headers.get("content-range"),
      `bytes */${bytes.length}`,
    );

    const multi = await fetch(url("movie.mp4"), {
      headers: { Range: "bytes=0-2,5-7" },
    });
    assert.equal(multi.status, 416);

    const audio = await fetch(url("sound.ogg"), {
      headers: { Range: "bytes=0-3" },
    });
    assert.equal(audio.status, 206);
    assert.equal(audio.headers.get("content-type"), "audio/ogg");
    await audio.arrayBuffer();

    assert.equal((await fetch(url("not-allowed.html"))).status, 400);
    assert.equal((await fetch(url("../sample.pdf"))).status, 403);
    assert.equal((await fetch(url("outside.pdf"))).status, 403);
  } finally {
    await runtime.cleanup();
  }
});

test("SVG preview is served with restrictive CSP and correct MIME", async () => {
  const runtime = await createTestRuntime();
  try {
    const svg =
      '<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script><circle r="10"/></svg>';
    await writeFile(path.join(runtime.workspaceRoot, "drawing.svg"), svg);
    await runtime.components.http.start();
    const address = runtime.components.http.address();
    assert.ok(address);
    const url =
      `http://127.0.0.1:${address.port}/api/workspace/file/image?root=` +
      encodeURIComponent(runtime.workspaceRoot) +
      "&path=drawing.svg";
    const result = await fetch(url);
    assert.equal(result.status, 200);
    assert.equal(result.headers.get("content-type"), "image/svg+xml");
    assert.match(
      result.headers.get("content-security-policy") ?? "",
      /sandbox/,
    );
    assert.match(
      result.headers.get("content-security-policy") ?? "",
      /default-src 'none'/,
    );
    assert.equal(await result.text(), svg);
  } finally {
    await runtime.cleanup();
  }
});
