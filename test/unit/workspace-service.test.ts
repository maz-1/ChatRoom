import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, symlink, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { ChatRoomError } from "../../src/core/errors/chatroom-error.js";
import { WorkspaceService } from "../../src/plugins/workspace/workspace-service.js";

function forbidden(operation: () => Promise<unknown>): Promise<void> {
  return assert.rejects(
    operation,
    (error: unknown) =>
      error instanceof ChatRoomError && error.code === "FORBIDDEN",
  );
}

test("WorkspaceService lists real projects but ignores directory links under an allowed root", async () => {
  const temp = await mkdtemp(
    path.join(os.tmpdir(), "chatroom-workspace-service-"),
  );
  const allowedRoot = path.join(temp, "projects");
  const realProject = path.join(allowedRoot, "real-project");
  const target = path.join(temp, "linked-target");
  const link = path.join(allowedRoot, "linked-project");

  try {
    await mkdir(allowedRoot);
    await mkdir(path.join(realProject, ".chatroom"), { recursive: true });
    await writeFile(
      path.join(realProject, ".chatroom", "summary.md"),
      "Real summary",
    );
    await mkdir(path.join(target, ".chatroom"), { recursive: true });
    await writeFile(
      path.join(target, ".chatroom", "summary.md"),
      "Linked summary",
    );
    await symlink(
      target,
      link,
      process.platform === "win32" ? "junction" : "dir",
    );

    const service = await WorkspaceService.create([allowedRoot]);

    const listed = await service.list();
    const realEntry = listed.find((item) => item.name === "real-project");
    assert.ok(realEntry);
    assert.equal(realEntry.root, realProject);
    assert.equal(realEntry.summary, "Real summary");
    assert.ok(!listed.some((item) => item.name === "linked-project"));

    const info = await service.info(realProject);
    assert.equal(info.root, realProject);
    assert.equal(info.summary, "Real summary");

    await forbidden(() => service.info(link));
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});

test("WorkspaceService still rejects paths that are not entries under an allowed root", async () => {
  const temp = await mkdtemp(
    path.join(os.tmpdir(), "chatroom-workspace-service-"),
  );
  const allowedRoot = path.join(temp, "projects");
  const outside = path.join(temp, "outside");

  try {
    await mkdir(allowedRoot);
    await mkdir(outside);
    const service = await WorkspaceService.create([allowedRoot]);

    await forbidden(() => service.info(outside));
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});
