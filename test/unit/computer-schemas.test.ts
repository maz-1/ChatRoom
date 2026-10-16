import assert from "node:assert/strict";
import test from "node:test";
import { computerScreenshotSchema } from "../../src/plugins/computer/computer-schemas.js";

test("Computer screenshot schema accepts WebP", () => {
  const screenshot = computerScreenshotSchema.parse({
    mimeType: "image/webp",
    data: "UklGRg==",
  });

  assert.equal(screenshot.mimeType, "image/webp");
});
