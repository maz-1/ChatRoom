import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, symlink, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { ChatRoomError } from "../../src/core/errors/chatroom-error.js";
import { WorkspaceService } from "../../src/plugins/workspace/workspace-service.js";

test("WorkspaceService lists and resolves directory links under an allowed root", async () => {
  const temp = await mkdtemp(
    path.join(os.tmpdir(), "chatroom-workspace-service-"),
  );
  const allowedRoot = path.join(temp, "projects");
  const target = path.join(temp, "linked-target");
  const link = path.join(allowedRoot, "linked-project");

  try {
    await mkdir(allowedRoot);
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
    const entry = listed.find((item) => item.name === "linked-project");

    assert.ok(entry);
    assert.equal(entry.root, link);
    assert.equal(entry.summary, "Linked summary");

    const info = await service.info(link);
    assert.equal(info.root, link);
    assert.equal(info.name, "linked-project");
    assert.equal(info.summary, "Linked summary");
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

    await assert.rejects(
      () => service.info(outside),
      (error: unknown) =>
        error instanceof ChatRoomError && error.code === "FORBIDDEN",
    );
  } finally {
    await rm(temp, { recursive: true, force: true });
  }
});
