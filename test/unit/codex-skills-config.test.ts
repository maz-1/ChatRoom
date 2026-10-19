import assert from "node:assert/strict";
import path from "node:path";
import test from "node:test";
import { ChatRoomError } from "../../src/core/errors/chatroom-error.js";
import { parseCodexConfig } from "../../src/plugins/workspace/codex-config.js";
import {
  setCodexSkillEnabled,
  skillConfigEnabled,
} from "../../src/plugins/workspace/codex-skills-config.js";

const root = path.resolve("test-workspace");
const skillPath = ".agents/skills/example/SKILL.md";
const configPath = "../.agents/skills/example/SKILL.md";

test("skill overrides read array tables and inline arrays with enabled defaulting to true", () => {
  for (const source of [
    `[[skills.config]]\npath = "${configPath}"\nenabled = false\n[[skills.config]]\npath = "../.agents/skills/other"\n`,
    `skills.config = [{ path = "${configPath}", enabled = false }, { path = "../.agents/skills/other" }]`,
    `skills = { config = [{ path = "${configPath}", enabled = false }, { path = "../.agents/skills/other" }] }`,
  ]) {
    const parsed = parseCodexConfig(source);
    assert.deepEqual(parsed.skillsConfig, [
      { path: configPath, enabled: false },
      { path: "../.agents/skills/other", enabled: true },
    ]);
    assert.equal(
      skillConfigEnabled(root, parsed.skillsConfig, skillPath),
      false,
    );
    assert.equal(
      skillConfigEnabled(
        root,
        parsed.skillsConfig,
        ".agents/skills/missing/SKILL.md",
      ),
      true,
    );
  }
});

test("skill edits preserve existing array-table comments and unrelated settings", () => {
  const original = `# keep\r\nmodel = "keep"\r\n[[skills.config]] # entry\r\npath = "${configPath}"\r\nenabled = true # flag\r\ncustom = "keep"\r\n`;
  const edited = setCodexSkillEnabled(original, root, skillPath, false);
  assert.equal(edited, original.replace("enabled = true", "enabled = false"));
  assert.equal(setCodexSkillEnabled(edited, root, skillPath, true), original);
});

test("skill edits add entries and missing enabled fields across TOML representations", () => {
  for (const source of [
    "",
    '\uFEFFmodel = "keep"\r\n',
    "[skills]\nmax_context_tokens = 3000\n",
    "skills = {} # keep\n",
    "skills.config = [] # keep\n",
    "skills.config = [ # keep\n]",
    `skills.config = [{ path = "${configPath}" }] # keep`,
    `skills = { config = [{ path = "${configPath}" }] }`,
    'skills.config = [{ path = "../other", enabled = false }, # keep\n]',
    '[[skills.config]]\npath = "../other"\nenabled = false\n[other]\nkeep = true',
    `[[skills.config]]\npath = "${configPath}" # no enabled\n`,
  ]) {
    const edited = setCodexSkillEnabled(source, root, skillPath, false);
    const parsed = parseCodexConfig(edited);
    assert.equal(
      skillConfigEnabled(root, parsed.skillsConfig, skillPath),
      false,
      source,
    );
    assert.equal(
      parsed.skillsConfig.filter((entry) => entry.path === configPath).length,
      1,
      source,
    );
    assert.equal(setCodexSkillEnabled(edited, root, skillPath, false), edited);
    const enabled = setCodexSkillEnabled(edited, root, skillPath, true);
    assert.equal(
      skillConfigEnabled(
        root,
        parseCodexConfig(enabled).skillsConfig,
        skillPath,
      ),
      true,
    );
    assert.ok(!source.includes("# keep") || edited.includes("# keep"));
  }
});

test("skill matching uses the config directory and supports absolute folder and SKILL.md aliases", () => {
  const absolute = path.resolve(root, skillPath);
  const source = `skills.config = [{ path = ${JSON.stringify(absolute)}, enabled = false }, { path = ${JSON.stringify(path.dirname(absolute))}, enabled = true }]`;
  assert.equal(
    skillConfigEnabled(root, parseCodexConfig(source).skillsConfig, skillPath),
    true,
  );
  const edited = setCodexSkillEnabled(source, root, skillPath, false);
  const overrides = parseCodexConfig(edited).skillsConfig;
  assert.equal(overrides.length, 2);
  assert.ok(overrides.every((entry) => !entry.enabled));
  assert.equal(
    skillConfigEnabled(
      root,
      [{ path: ".agents/skills/example", enabled: false }],
      skillPath,
    ),
    true,
    "relative paths are based in .codex, not the workspace root",
  );
});

test("invalid skill shapes and field values are rejected before editing", () => {
  for (const source of [
    "skills = false",
    "[[skills]]\nconfig = []",
    "skills.config = false",
    "skills.config = [1]",
    '[skills.config]\npath = "x"',
    "skills.config = [{ enabled = false }]",
    'skills.config = [{ path = "" }]',
    "skills.config = [{ path = 5 }]",
    'skills.config = [{ path = "x", enabled = "false" }]',
    '[[skills.config]]\npath = "x"\nenabled.value = false',
  ]) {
    assert.throws(() => parseCodexConfig(source), ChatRoomError, source);
    assert.throws(
      () => setCodexSkillEnabled(source, root, skillPath, false),
      ChatRoomError,
      source,
    );
  }
});
