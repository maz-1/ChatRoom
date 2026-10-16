import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { access, mkdir, mkdtemp, readFile, rm } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../", import.meta.url));
const temporaryRoot = path.join(root, ".chatroom");
await mkdir(temporaryRoot, { recursive: true });
const temporary = await mkdtemp(path.join(temporaryRoot, "package-test-"));
try {
  if (!process.env.npm_execpath)
    throw new Error("Run this check with npm run verify:package");
  const result = JSON.parse(
    execFileSync(
      process.execPath,
      [
        process.env.npm_execpath,
        "pack",
        "--ignore-scripts",
        "--json",
        "--pack-destination",
        temporary,
      ],
      { cwd: root, encoding: "utf8", maxBuffer: 16 * 1024 * 1024 },
    ),
  );
  const archive = path.join(temporary, result[0].filename);
  execFileSync("tar", ["-xzf", archive, "-C", temporary]);
  const unpacked = path.join(temporary, "package");
  for (const file of [
    "dist/vendor/js2ray/index.mjs",
    "dist/vendor/js2ray/index.d.ts",
    "dist/vendor/js2ray/LICENSE",
  ])
    await access(path.join(unpacked, file));
  const packageJson = JSON.parse(
    await readFile(path.join(unpacked, "package.json"), "utf8"),
  );
  assert.ok(
    !Object.values(packageJson.dependencies).some((entry) =>
      String(entry).startsWith("file:"),
    ),
  );
  for (const name of Object.keys(packageJson.dependencies))
    assert.ok(
      !(name in packageJson.devDependencies),
      `${name} must not also be a devDependency: production pruning can remove it`,
    );
  // Resolve the extracted package's own import map and snapshot. Installed
  // third-party dependencies are inherited from the surrounding workspace.
  execFileSync(
    process.execPath,
    [
      "--input-type=module",
      "-e",
      `
    import assert from 'node:assert/strict';
    const { createRuntime } = await import('#js2ray');
    const { Js2rayController } = await import('./dist/plugins/js2ray/controller.js');
    const { createApplication } = await import('./dist/app/application.js');
    const runtime = createRuntime({ inbounds: [], outbounds: [{protocol:'freedom'}] });
    await runtime.start();
    assert.equal(runtime.listenersInfo.length, 0);
    await runtime.close();
    assert.equal(typeof Js2rayController, 'function');
    assert.equal(typeof createApplication, 'function');
  `,
    ],
    { cwd: unpacked, stdio: "inherit", timeout: 15000 },
  );
  console.log(
    "Published archive loads its js2ray snapshot and host adapter without a submodule checkout.",
  );
} finally {
  const resolved = path.resolve(temporary);
  if (!resolved.startsWith(path.resolve(temporaryRoot) + path.sep))
    throw new Error("Invalid package test cleanup path");
  await rm(resolved, { recursive: true, force: true });
}
