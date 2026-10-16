import { chmodSync } from "node:fs";
import { cp, mkdir, readFile } from "node:fs/promises";

const source = new URL("../vendor/js2ray/", import.meta.url);
const target = new URL("../dist/vendor/js2ray/", import.meta.url);
let upstream;
try {
  upstream = JSON.parse(
    await readFile(new URL("package.json", source), "utf8"),
  );
} catch {
  throw new Error(
    "js2ray submodule is missing. Run git submodule update --init --recursive before building.",
  );
}
await mkdir(target, { recursive: true });
for (const entry of upstream.files) {
  if (
    [
      "cli",
      "examples",
      "docs",
      "README.md",
      "DEVELOPMENT_PLAN.zh-CN.md",
    ].includes(entry)
  )
    continue;
  await cp(new URL(entry, source), new URL(entry, target), { recursive: true });
}
await cp(new URL("package.json", source), new URL("package.json", target));

if (process.platform !== "win32") {
  chmodSync(new URL("../dist/cli/index.js", import.meta.url), 0o755);
}
