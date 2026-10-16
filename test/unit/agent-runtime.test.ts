import assert from "node:assert/strict";
import {
  access,
  mkdir,
  mkdtemp,
  rm,
  symlink,
  writeFile,
} from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { z } from "zod";
import type { PluginMcpRegistrar } from "../../src/mcp/server/plugin-mcp-registrar.js";
import { registerAgentTools } from "../../src/plugins/agent/mcp.js";
import { RuntimeEventBus } from "../../src/app/event-bus.js";
import { ChatRoomError } from "../../src/core/errors/chatroom-error.js";
import { AppDatabase } from "../../src/infrastructure/database/app-database.js";
import type {
  AgentProvider,
  AgentProviderContext,
  ProviderEvent,
  ProviderSession,
  ProviderTurnInput,
} from "../../src/plugins/agent/provider.js";
import { AgentRepository } from "../../src/plugins/agent/repository.js";
import { AgentRuntime } from "../../src/plugins/agent/runtime.js";
import type {
  AgentInteractionResponse,
  AgentItem,
  AgentModel,
  AgentProviderId,
  AgentProviderProbe,
  AgentProviderDetails,
  AgentReviewTarget,
  AgentSessionConfig,
  AgentTokenUsage,
} from "../../src/plugins/agent/types.js";
import { WorkspaceService } from "../../src/plugins/workspace/workspace-service.js";
import { WorkspaceBlacklistRepository } from "../../src/plugins/workspace/workspace-blacklist-repository.js";

const defaultConfig: AgentSessionConfig = {
  model: null,
  reasoningEffort: null,
  reasoningSummary: null,
  serviceTier: null,
  approvalPolicy: null,
  approvalsReviewer: null,
  permissionMode: null,
};

function fakeModel(id: string, efforts: string[], tiers: string[]): AgentModel {
  return {
    id,
    model: id,
    displayName: id,
    description: "test model",
    modelSpecialty: null,
    isDefault: id === "test-model",
    upgrade: null,
    upgradeInfo: null,
    availabilityMessage: null,
    reasoningEfforts: efforts.map((id) => ({ id, description: id })),
    defaultReasoningEffort: efforts[0] ?? null,
    reasoningSummaries: efforts.length
      ? ["auto", "concise", "detailed", "none"]
      : [],
    defaultReasoningSummary: null,
    inputModalities: ["text", "image", "audio"],
    multiAgentVersion: null,
    serviceTiers: tiers.map((id) => ({ id, name: id, description: id })),
    defaultServiceTier: tiers[0] ?? null,
    availableAccessPrograms: null,
  };
}

function tokenUsage(inputTokens: number): AgentTokenUsage {
  const last = {
    totalTokens: inputTokens + 10,
    inputTokens,
    cachedInputTokens: 2,
    cacheWriteInputTokens: 0,
    outputTokens: 10,
    reasoningOutputTokens: 3,
  };
  return {
    total: {
      ...last,
      totalTokens: last.totalTokens * 2,
      inputTokens: inputTokens * 2,
    },
    last,
    modelContextWindow: 128_000,
  };
}

class FakeSession implements ProviderSession {
  readonly providerSessionId = "fake-provider-session";
  private interrupted = false;
  private releaseTurn: (() => void) | null = null;

  runTurn(_input: ProviderTurnInput): AsyncIterable<ProviderEvent> {
    const self = this;
    return (async function* () {
      yield { type: "turn.started", providerTurnId: "fake-turn" };
      await new Promise<void>((resolve) => {
        self.releaseTurn = resolve;
        if (self.interrupted) resolve();
      });
      yield {
        type: "turn.completed",
        status: self.interrupted ? "interrupted" : "completed",
        error: null,
      };
    })();
  }

  readonly steering: Array<Pick<ProviderTurnInput, "text" | "attachments">> =
    [];
  readonly reviews: AgentReviewTarget[] = [];

  async steer(
    input: Pick<ProviderTurnInput, "text" | "attachments">,
  ): Promise<void> {
    this.steering.push(input);
  }

  runReview(target: AgentReviewTarget): AsyncIterable<ProviderEvent> {
    this.reviews.push(target);
    return this.runTurn({
      text: "",
      attachments: [],
      outputSchema: null,
    });
  }

  async interrupt(): Promise<void> {
    this.interrupted = true;
    this.releaseTurn?.();
  }

  readonly configurations: AgentSessionConfig[] = [];

  async configure(config: AgentSessionConfig): Promise<void> {
    this.configurations.push(config);
  }

  async resolveInteraction(
    _interactionId: string,
    _response: AgentInteractionResponse,
  ): Promise<void> {}

  async close(): Promise<void> {
    await this.interrupt();
  }
}

class FakeProvider implements AgentProvider {
  readonly id: AgentProviderId;
  readonly displayName: string;
  readonly icon = null;
  readonly features: AgentProvider["features"];
  readonly nativeSettings: AgentProvider["nativeSettings"];

  constructor(
    id: AgentProviderId = "codex",
    features: AgentProvider["features"] = {
      nativeReview: id === "codex",
      steering: id === "codex",
    },
  ) {
    this.id = id;
    this.displayName = id;
    this.features = features;
    this.nativeSettings =
      id === "codex"
        ? [
            {
              id: "approvalPolicy",
              defaultValue: "on-request",
              options: [
                { id: "untrusted", description: null },
                { id: "on-request", description: null },
                { id: "granular", description: null },
                { id: "never", description: null },
              ],
              granularFields: [
                { id: "sandbox_approval", description: null },
                { id: "rules", description: null },
                { id: "skill_approval", description: null },
                { id: "request_permissions", description: null },
                { id: "mcp_elicitations", description: null },
              ],
            },
            {
              id: "permissionMode",
              defaultValue: "workspace-write",
              options: [
                { id: "read-only", description: null },
                { id: "workspace-write", description: null },
                { id: "danger-full-access", description: null },
              ],
              granularFields: [],
            },
            {
              id: "approvalsReviewer",
              defaultValue: "user",
              options: [
                { id: "user", description: null },
                { id: "auto_review", description: null },
                { id: "guardian_subagent", description: null },
              ],
              granularFields: [],
            },
          ]
        : [];
  }
  readonly session = new FakeSession();

  async probe(): Promise<AgentProviderProbe> {
    return {
      installed: true,
      authenticated: true,
      version: "test",
      error: null,
      capabilities: {
        namespaceTools: true,
        imageGeneration: true,
        webSearch: false,
      },
    };
  }

  readonly providerDetails: AgentProviderDetails = {
    configRequirements: {
      allowedApprovalPolicies: ["on-request"],
      allowedSandboxModes: ["workspace-write"],
    },
    rateLimits: { primary: { usedPercent: 25 }, credits: { hasCredits: true } },
    accountUsage: null,
  };

  async details(): Promise<AgentProviderDetails> {
    return this.providerDetails;
  }

  async listModels(): Promise<AgentModel[]> {
    return [
      fakeModel("test-model", ["low", "high"], ["default", "fast"]),
      fakeModel("other-model", ["medium"], ["default"]),
      fakeModel("plain-model", [], []),
    ];
  }

  async createSession(
    _context: AgentProviderContext,
  ): Promise<ProviderSession> {
    return this.session;
  }

  async resumeSession(
    _context: AgentProviderContext,
    _providerSessionId: string,
  ): Promise<ProviderSession> {
    return this.session;
  }

  async close(): Promise<void> {
    await this.session.close();
  }
}

class StreamingSession extends FakeSession {
  readonly inputs: ProviderTurnInput[] = [];
  readonly responses: Array<{
    interactionId: string;
    response: AgentInteractionResponse;
  }> = [];

  override async resolveInteraction(
    interactionId: string,
    response: AgentInteractionResponse,
  ): Promise<void> {
    this.responses.push({ interactionId, response });
  }
  private deliver: ((event: ProviderEvent) => void) | null = null;
  private processed: (() => void) | null = null;

  override runTurn(input: ProviderTurnInput): AsyncIterable<ProviderEvent> {
    this.inputs.push(input);
    return this.events();
  }

  override runReview(target: AgentReviewTarget): AsyncIterable<ProviderEvent> {
    this.reviews.push(target);
    return this.events();
  }

  private events(): AsyncIterable<ProviderEvent> {
    const self = this;
    return (async function* () {
      while (true) {
        const event = await new Promise<ProviderEvent>((resolve) => {
          self.deliver = resolve;
        });
        try {
          yield event;
        } finally {
          self.processed?.();
          self.processed = null;
        }
        if (event.type === "turn.completed") return;
      }
    })();
  }

  async send(event: ProviderEvent): Promise<void> {
    assert.ok(this.deliver, "runtime must be waiting for the next event");
    await new Promise<void>((resolve) => {
      this.processed = resolve;
      const deliver = this.deliver!;
      this.deliver = null;
      deliver(event);
    });
  }

  override async interrupt(): Promise<void> {
    if (this.deliver)
      await this.send({
        type: "turn.completed",
        status: "interrupted",
        error: null,
      });
  }
}

class StreamingProvider extends FakeProvider {
  readonly sessions: StreamingSession[] = [];
  readonly contexts: AgentProviderContext[] = [];

  override async createSession(
    context: AgentProviderContext,
  ): Promise<ProviderSession> {
    this.contexts.push(context);
    const session = new StreamingSession();
    this.sessions.push(session);
    return session;
  }

  override async resumeSession(
    context: AgentProviderContext,
  ): Promise<ProviderSession> {
    return this.createSession(context);
  }
}

async function createRuntimeFixture(
  provider: AgentProvider = new FakeProvider(),
  additionalProviders: AgentProvider[] = [],
  presetPrompt: string | null = null,
) {
  const root = await mkdtemp(path.join(os.tmpdir(), "chatroom-agent-"));
  const workspace = path.join(root, "workspace");
  await mkdir(workspace);
  if (presetPrompt !== null) {
    await mkdir(path.join(workspace, ".chatroom"));
    await writeFile(
      path.join(workspace, ".chatroom", "prompt.md"),
      presetPrompt,
    );
  }
  const database = new AppDatabase(path.join(root, "data", "chatroom.sqlite"));
  const events = new RuntimeEventBus();
  const repository = new AgentRepository(database);
  const runtime = new AgentRuntime(
    repository,
    await WorkspaceService.create(
      [root],
      { projectFiles: async () => null },
      new WorkspaceBlacklistRepository(database),
    ),
    events,
    [provider, ...additionalProviders],
  );
  const session = await runtime.createSession({
    workspaceRoot: workspace,
    provider: "codex",
    ...defaultConfig,
    model: "test-model",
  });
  return {
    root,
    workspace,
    database,
    repository,
    events,
    runtime,
    session,
    async cleanup() {
      await runtime.close();
      database.close();
      await rm(root, { recursive: true, force: true });
    },
  };
}

test("AgentRepository startup reconciliation keeps session timestamps monotonic", async () => {
  const root = await mkdtemp(
    path.join(os.tmpdir(), "chatroom-agent-reconcile-"),
  );
  const database = new AppDatabase(path.join(root, "app.sqlite"));
  try {
    const repository = new AgentRepository(database);
    const current = "2026-01-01T00:00:00.125Z";
    const candidate = "2026-01-01T00:00:00.123Z";
    database.raw
      .prepare(
        "INSERT INTO agent_sessions(id, workspace_root, provider, status, created_at, updated_at) VALUES(?,?,?,?,?,?)",
      )
      .run(
        "agent_reconcile",
        "/workspace",
        "codex",
        "running",
        current,
        current,
      );

    repository.reconcileInterrupted(candidate);

    const row = database.raw
      .prepare(
        "SELECT status, updated_at FROM agent_sessions WHERE id='agent_reconcile'",
      )
      .get() as { status: string; updated_at: string };
    assert.equal(row.status, "idle");
    assert.ok(row.updated_at > current);
  } finally {
    database.close();
    await rm(root, { recursive: true, force: true });
  }
});

test("AgentRuntime forwards the Workspace preset prompt to the Agent provider", async () => {
  const provider = new StreamingProvider();
  const fixture = await createRuntimeFixture(
    provider,
    [],
    "Prefer the project-local conventions from this Workspace.",
  );
  try {
    assert.equal(
      provider.contexts[0]?.workspacePrompt,
      "Prefer the project-local conventions from this Workspace.",
    );
  } finally {
    await fixture.cleanup();
  }
});

test("AgentRuntime rejects attachment symlinks that escape the workspace", async () => {
  const fixture = await createRuntimeFixture();
  try {
    const outside = path.join(fixture.root, "outside.txt");
    await writeFile(outside, "secret");
    await symlink(outside, path.join(fixture.workspace, "escape.txt"));

    await assert.rejects(
      () =>
        fixture.runtime.startTurn(
          fixture.session.id,
          {
            text: "inspect",
            outputSchema: null,
            attachments: [
              {
                kind: "file",
                name: "escape.txt",
                path: "escape.txt",
                mimeType: "text/plain",
              },
            ],
          },
          "human",
        ),
      (error: unknown) =>
        error instanceof ChatRoomError && error.code === "FORBIDDEN",
    );

    assert.equal(fixture.runtime.history(fixture.session.id).turns.length, 0);
  } finally {
    await fixture.cleanup();
  }
});

test("deleting an Agent session cleans only that session's uploaded files", async () => {
  const fixture = await createRuntimeFixture();
  try {
    const otherSession = await fixture.runtime.createSession({
      workspaceRoot: fixture.workspace,
      provider: "codex",
      ...defaultConfig,
      model: "test-model",
    });
    const ownedDirectory = path.join(
      fixture.workspace,
      ".chatroom",
      "agent-attachments",
      fixture.session.id,
    );
    const otherDirectory = path.join(
      fixture.workspace,
      ".chatroom",
      "agent-attachments",
      otherSession.id,
    );
    await mkdir(ownedDirectory, { recursive: true });
    await mkdir(otherDirectory, { recursive: true });

    const owned = path.join(ownedDirectory, "owned.txt");
    const unsent = path.join(ownedDirectory, "unsent.txt");
    const other = path.join(otherDirectory, "kept.txt");
    const userFile = path.join(fixture.workspace, "kept.txt");
    await writeFile(owned, "owned");
    await writeFile(unsent, "unsent");
    await writeFile(other, "other");
    await writeFile(userFile, "user");

    await fixture.runtime.startTurn(
      fixture.session.id,
      {
        text: "inspect both",
        outputSchema: null,
        attachments: [
          {
            kind: "file",
            name: "owned.txt",
            path:
              ".chatroom/agent-attachments/" +
              fixture.session.id +
              "/owned.txt",
            mimeType: "text/plain",
          },
          {
            kind: "file",
            name: "kept.txt",
            path: "kept.txt",
            mimeType: "text/plain",
          },
        ],
      },
      "human",
    );
    await fixture.runtime.interrupt(fixture.session.id);
    for (let attempt = 0; attempt < 100; attempt += 1) {
      if (fixture.runtime.getSession(fixture.session.id).status === "idle")
        break;
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
    assert.equal(fixture.runtime.getSession(fixture.session.id).status, "idle");

    await rm(owned);
    await symlink(userFile, owned);
    await fixture.runtime.deleteSession(fixture.session.id);

    await assert.rejects(() => access(owned));
    await assert.rejects(() => access(unsent));
    await access(other);
    await access(userFile);
  } finally {
    await fixture.cleanup();
  }
});

function sessionConfig(session: AgentSessionConfig) {
  const { model, reasoningEffort, reasoningSummary, serviceTier } = session;
  return { model, reasoningEffort, reasoningSummary, serviceTier };
}

test("AgentRuntime persists independent provider settings and usage across switches and restart", async () => {
  const codex = new StreamingProvider();
  const secondary = new StreamingProvider("secondary");
  const fixture = await createRuntimeFixture(codex, [secondary]);
  let restarted: AgentRuntime | null = null;
  try {
    const codexConfig: AgentSessionConfig = {
      ...defaultConfig,
      model: "test-model",
      reasoningEffort: "high",
      reasoningSummary: "detailed",
      serviceTier: "fast",
      approvalPolicy: "never",
      approvalsReviewer: "auto_review",
      permissionMode: "danger-full-access",
    };
    const secondaryConfig: AgentSessionConfig = {
      ...defaultConfig,
      model: "other-model",
      reasoningEffort: "medium",
      reasoningSummary: "concise",
      serviceTier: "default",
      permissionMode: null,
    };
    await fixture.runtime.configureSession(fixture.session.id, codexConfig);
    await fixture.runtime.startTurn(
      fixture.session.id,
      {
        text: "codex",
        attachments: [],
        outputSchema: null,
      },
      "human",
    );
    const codexUsage = tokenUsage(20);
    await codex.sessions[0]!.send({ type: "token.usage", usage: codexUsage });
    await codex.sessions[0]!.send({
      type: "turn.completed",
      status: "completed",
      error: null,
    });
    const firstSecondary = await fixture.runtime.switchProvider(
      fixture.session.id,
      "secondary",
      {},
    );
    assert.equal(firstSecondary.tokenUsage, null);
    await fixture.runtime.configureSession(fixture.session.id, secondaryConfig);
    await fixture.runtime.startTurn(
      fixture.session.id,
      {
        text: "secondary",
        attachments: [],
        outputSchema: null,
      },
      "human",
    );
    const secondaryUsage = tokenUsage(40);
    await secondary.sessions[0]!.send({
      type: "token.usage",
      usage: secondaryUsage,
    });
    await secondary.sessions[0]!.send({
      type: "turn.completed",
      status: "completed",
      error: null,
    });
    const restoredCodex = await fixture.runtime.switchProvider(
      fixture.session.id,
      "codex",
      {},
    );
    assert.deepEqual(sessionConfig(restoredCodex), sessionConfig(codexConfig));
    assert.equal(restoredCodex.approvalPolicy, "never");
    assert.equal(restoredCodex.approvalsReviewer, "auto_review");
    assert.equal(restoredCodex.permissionMode, "danger-full-access");
    assert.deepEqual(restoredCodex.tokenUsage, codexUsage);
    assert.deepEqual(
      sessionConfig(codex.contexts.at(-1)!),
      sessionConfig(codexConfig),
    );
    const repository = new AgentRepository(fixture.database);
    for (const [provider, config, usage] of [
      ["codex", codexConfig, codexUsage],
      ["secondary", secondaryConfig, secondaryUsage],
    ] as const) {
      const state = repository.getProviderState(fixture.session.id, provider)!;
      assert.deepEqual(sessionConfig(state), sessionConfig(config));
      assert.deepEqual(state.approvalPolicy, config.approvalPolicy);
      assert.equal(state.approvalsReviewer, config.approvalsReviewer);
      assert.equal(state.permissionMode, config.permissionMode);
      assert.deepEqual(state.tokenUsage, usage);
    }
    await fixture.runtime.close();
    const nextCodex = new StreamingProvider();
    const nextSecondary = new StreamingProvider("secondary");
    restarted = new AgentRuntime(
      repository,
      await WorkspaceService.create(
        [fixture.root],
        { projectFiles: async () => null },
        new WorkspaceBlacklistRepository(fixture.database),
      ),
      fixture.events,
      [nextCodex, nextSecondary],
    );
    assert.deepEqual(
      sessionConfig(restarted.getSession(fixture.session.id)),
      sessionConfig(codexConfig),
    );
    assert.deepEqual(
      restarted.getSession(fixture.session.id).tokenUsage,
      codexUsage,
    );
    const restoredSecondary = await restarted.switchProvider(
      fixture.session.id,
      "secondary",
      {},
    );
    assert.deepEqual(
      sessionConfig(restoredSecondary),
      sessionConfig(secondaryConfig),
    );
    assert.equal(restoredSecondary.permissionMode, null);
    assert.deepEqual(restoredSecondary.tokenUsage, secondaryUsage);
    assert.deepEqual(
      sessionConfig(nextSecondary.contexts[0]!),
      sessionConfig(secondaryConfig),
    );
  } finally {
    await restarted?.close();
    await fixture.cleanup();
  }
});

test("AgentRuntime validates advertised settings atomically and resets incompatible settings on model change", async () => {
  const provider = new FakeProvider();
  const fixture = await createRuntimeFixture(provider);
  try {
    const config = {
      model: "test-model",
      reasoningEffort: "high",
      reasoningSummary: "detailed",
      serviceTier: "fast",
    } as const;
    await fixture.runtime.configureSession(fixture.session.id, config);
    assert.deepEqual(provider.session.configurations.at(-1), {
      ...config,
      approvalPolicy: "on-request",
      approvalsReviewer: "user",
      permissionMode: "workspace-write",
    });
    const before = fixture.runtime.getSession(fixture.session.id);
    const calls = provider.session.configurations.length;
    for (const patch of [
      { model: "unknown" },
      { model: "other-model", reasoningEffort: "high" },
      { model: "other-model", serviceTier: "fast" },
      { reasoningEffort: "medium" },
      { serviceTier: "unknown" },
      { reasoningEffort: "low", serviceTier: "unknown" },
    ]) {
      await assert.rejects(
        () => fixture.runtime.configureSession(fixture.session.id, patch),
        (error: unknown) =>
          error instanceof ChatRoomError && error.code === "INVALID_INPUT",
      );
      assert.deepEqual(fixture.runtime.getSession(fixture.session.id), before);
      assert.equal(provider.session.configurations.length, calls);
    }
    const changed = await fixture.runtime.configureSession(fixture.session.id, {
      model: "other-model",
    });
    assert.ok(changed.updatedAt > before.updatedAt);
    assert.deepEqual(sessionConfig(changed), {
      model: "other-model",
      reasoningEffort: "medium",
      reasoningSummary: "detailed",
      serviceTier: "default",
    });
    assert.deepEqual(provider.session.configurations.at(-1), {
      ...sessionConfig(changed),
      approvalPolicy: changed.approvalPolicy,
      approvalsReviewer: changed.approvalsReviewer,
      permissionMode: changed.permissionMode,
    });
    const plain = await fixture.runtime.configureSession(fixture.session.id, {
      model: "plain-model",
    });
    assert.ok(plain.updatedAt > changed.updatedAt);
    assert.deepEqual(sessionConfig(plain), {
      model: "plain-model",
      reasoningEffort: null,
      reasoningSummary: null,
      serviceTier: null,
    });
    await assert.rejects(
      () =>
        fixture.runtime.configureSession(fixture.session.id, {
          reasoningSummary: "detailed",
        }),
      (error: unknown) =>
        error instanceof ChatRoomError && error.code === "INVALID_INPUT",
    );
    await fixture.runtime.configureSession(fixture.session.id, {
      model: "other-model",
      reasoningSummary: "detailed",
    });
    const reset = await fixture.runtime.configureSession(fixture.session.id, {
      reasoningSummary: null,
    });
    assert.equal(reset.reasoningSummary, null);
    assert.equal(reset.model, "other-model");
    assert.deepEqual(
      sessionConfig(
        new AgentRepository(fixture.database).getSession(fixture.session.id)!,
      ),
      sessionConfig(reset),
    );
  } finally {
    await fixture.cleanup();
  }
});

test("AgentRuntime persists item updates, structured plans, and reasoning summary deltas", async () => {
  const provider = new StreamingProvider();
  const fixture = await createRuntimeFixture(provider);
  try {
    await fixture.runtime.startTurn(
      fixture.session.id,
      {
        text: "plan",
        attachments: [],
        outputSchema: null,
      },
      "human",
    );
    const stream = provider.sessions[0]!;
    const readItems = () =>
      fixture.runtime.history(fixture.session.id).turns[0]!.items;
    await stream.send({
      type: "item.started",
      providerItemId: "plan",
      item: {
        type: "plan",
        text: "Plan: ",
        steps: [{ step: "Inspect", status: "pending" }],
      },
    });
    const original = readItems()[1]!;
    await stream.send({
      type: "item.delta",
      providerItemId: "plan",
      field: "text",
      delta: "inspect",
    });
    assert.equal(
      (readItems()[1] as AgentItem & { text: string }).text,
      "Plan: inspect",
    );
    const steps = [
      { step: "Inspect", status: "completed" },
      { step: "Implement", status: "inProgress" },
    ] as const;
    await stream.send({
      type: "item.updated",
      providerItemId: "plan",
      item: { type: "plan", text: "Updated plan", steps: [...steps] },
    });
    const updated = readItems()[1]!;
    assert.equal(updated.id, original.id);
    assert.equal(updated.sequence, original.sequence);
    assert.equal(updated.createdAt, original.createdAt);
    assert.equal(updated.status, "running");
    assert.equal(updated.type, "plan");
    if (updated.type === "plan") {
      assert.equal(updated.text, "Updated plan");
      assert.deepEqual(updated.steps, steps);
    }
    await stream.send({
      type: "item.started",
      providerItemId: "reasoning",
      item: { type: "reasoning_summary", text: "Checking" },
    });
    await stream.send({
      type: "item.delta",
      providerItemId: "reasoning",
      field: "text",
      delta: " constraints",
    });
    const reasoning = readItems()[2]!;
    assert.equal(reasoning.type, "reasoning_summary");
    if (reasoning.type === "reasoning_summary")
      assert.equal(reasoning.text, "Checking constraints");
    await stream.send({
      type: "item.completed",
      providerItemId: "reasoning",
      item: { type: "reasoning_summary", text: "Constraints checked" },
    });
    await stream.send({
      type: "item.completed",
      providerItemId: "plan",
      item: {
        type: "plan",
        text: "Done",
        steps: steps.map(({ step }) => ({ step, status: "completed" })),
      },
    });
    await stream.send({
      type: "turn.completed",
      status: "completed",
      error: null,
    });
    const persisted = readItems();
    assert.equal(persisted.length, 3);
    assert.deepEqual(
      persisted.map((item) => item.sequence),
      [0, 1, 2],
    );
    assert.equal(persisted[1]!.id, original.id);
    assert.equal(persisted[2]!.id, reasoning.id);
    assert.equal(persisted[1]!.status, "completed");
    assert.equal(persisted[2]!.status, "completed");
    if (persisted[1]!.type === "plan")
      assert.deepEqual(
        persisted[1]!.steps,
        steps.map(({ step }) => ({ step, status: "completed" })),
      );
    if (persisted[2]!.type === "reasoning_summary")
      assert.equal(persisted[2]!.text, "Constraints checked");
  } finally {
    await fixture.cleanup();
  }
});

test("AgentRuntime batches streamed persistence and bounds command output", async () => {
  const provider = new StreamingProvider();
  const fixture = await createRuntimeFixture(provider);
  const originalUpdateItem = fixture.repository.updateItem.bind(
    fixture.repository,
  );
  let itemWrites = 0;
  fixture.repository.updateItem = (item) => {
    itemWrites += 1;
    originalUpdateItem(item);
  };

  try {
    await fixture.runtime.startTurn(
      fixture.session.id,
      { text: "run", attachments: [], outputSchema: null },
      "human",
    );
    const stream = provider.sessions[0]!;
    await stream.send({
      type: "item.started",
      providerItemId: "command",
      item: {
        type: "command",
        command: "generate-output",
        cwd: fixture.workspace,
        output: "",
        exitCode: null,
      },
    });

    await stream.send({
      type: "item.delta",
      providerItemId: "command",
      field: "output",
      delta: "H".repeat(400 * 1024),
    });
    await stream.send({
      type: "item.delta",
      providerItemId: "command",
      field: "output",
      delta: "T".repeat(400 * 1024),
    });

    assert.equal(
      itemWrites,
      0,
      "streaming deltas should stay in memory briefly",
    );

    const current = fixture.runtime.history(fixture.session.id);
    assert.equal(
      itemWrites,
      1,
      "a read should flush the latest coalesced item",
    );
    const command = current.turns[0]!.items.find(
      (item): item is Extract<AgentItem, { type: "command" }> =>
        item.type === "command",
    );
    assert.ok(command);
    assert.ok(Buffer.byteLength(command.output) < 513 * 1024);
    assert.ok(command.output.startsWith("H"));
    assert.ok(command.output.endsWith("T"));
    assert.match(command.output, /bytes truncated/);

    await stream.send({
      type: "item.delta",
      providerItemId: "command",
      field: "output",
      delta: "stale-delta",
    });
    await stream.send({
      type: "item.completed",
      providerItemId: "command",
      item: {
        type: "command",
        command: "generate-output",
        cwd: fixture.workspace,
        output: "final-output",
        exitCode: 0,
      },
    });
    await new Promise((resolve) => setTimeout(resolve, 180));

    const persisted = new AgentRepository(fixture.database)
      .history(fixture.session.id)!
      .turns[0]!.items.find(
        (item): item is Extract<AgentItem, { type: "command" }> =>
          item.type === "command",
      );
    assert.ok(persisted);
    assert.equal(persisted.output, "final-output");
    assert.equal(persisted.exitCode, 0);
    assert.equal(
      itemWrites,
      2,
      "completion must replace, not replay, pending delta",
    );
  } finally {
    await fixture.cleanup();
  }
});

test("AgentRuntime persists usage, actual rerouted model, and structured failure without changing session configuration", async () => {
  const provider = new StreamingProvider();
  const fixture = await createRuntimeFixture(provider);
  try {
    await fixture.runtime.startTurn(
      fixture.session.id,
      {
        text: "run",
        attachments: [],
        outputSchema: null,
      },
      "human",
    );
    const stream = provider.sessions[0]!;
    await stream.send({ type: "turn.started", providerTurnId: "native-turn" });
    await stream.send({ type: "token.usage", usage: tokenUsage(5) });
    const usage = tokenUsage(30);
    await stream.send({ type: "token.usage", usage });
    const repository = new AgentRepository(fixture.database);
    assert.deepEqual(
      repository.getSession(fixture.session.id)!.tokenUsage,
      usage,
    );
    assert.deepEqual(
      repository.history(fixture.session.id)!.turns[0]!.turn.usage,
      usage.last,
    );
    await stream.send({
      type: "model.rerouted",
      fromModel: "test-model",
      toModel: "fallback-model",
      reason: "capacity",
    });
    assert.equal(
      repository.history(fixture.session.id)!.turns[0]!.turn.model,
      "fallback-model",
    );
    assert.equal(
      fixture.runtime.getSession(fixture.session.id).model,
      "test-model",
    );
    const error = {
      message: "Provider failed",
      details: "retry later",
      providerData: {
        errorInfo: { httpConnectionFailed: { httpStatusCode: 503 } },
        misalignment: { expected: "tool", actual: "text" },
      },
    };
    await stream.send({ type: "turn.completed", status: "failed", error });
    const history = repository.history(fixture.session.id)!;
    assert.equal(history.turns[0]!.turn.providerTurnId, "native-turn");
    assert.equal(history.turns[0]!.turn.status, "failed");
    assert.deepEqual(history.turns[0]!.turn.error, error);
    assert.deepEqual(history.turns[0]!.turn.usage, usage.last);
    assert.equal(history.turns[0]!.turn.model, "fallback-model");
    assert.equal(history.session.model, "test-model");
    assert.deepEqual(history.session.tokenUsage, usage);
    const state = repository.getProviderState(fixture.session.id, "codex")!;
    assert.equal(state.model, "test-model");
    assert.deepEqual(state.tokenUsage, usage);
    assert.ok(history.turns[0]!.turn.completedAt);
  } finally {
    await fixture.cleanup();
  }
});

interface RegisteredAgentTool {
  config: {
    action: string;
    inputSchema: z.ZodType;
    outputSchema: z.ZodType;
  };
  handler: (input: unknown) => unknown;
}

function registeredAgentTools(runtime: AgentRuntime) {
  const tools = new Map<string, RegisteredAgentTool>();
  // Capture the actual plugin registration, retaining its schemas and handlers.
  const registrar = {
    registerTool(
      name: string,
      config: RegisteredAgentTool["config"],
      handler: RegisteredAgentTool["handler"],
    ) {
      assert.equal(tools.has(name), false);
      tools.set(name, { config, handler });
    },
  } as unknown as PluginMcpRegistrar;
  registerAgentTools(registrar, runtime);
  return {
    tools,
    async call(name: string, input: unknown): Promise<unknown> {
      const tool = tools.get(name);
      assert.ok(tool, `Missing MCP tool: ${name}`);
      return tool.config.outputSchema.parse(
        await tool.handler(tool.config.inputSchema.parse(input)),
      );
    },
  };
}

test("Agent MCP schemas and actions retain rich capabilities, settings, turns, and string approvals", async () => {
  const provider = new StreamingProvider();
  const fixture = await createRuntimeFixture(provider);
  try {
    const mcp = registeredAgentTools(fixture.runtime);
    for (const [name, action] of [
      ["agent_configure", "configure"],
      ["agent_send", "send"],
      ["agent_respond", "respond"],
      ["agent_models", "models"],
      ["agent_history", "history"],
    ]) {
      assert.equal(mcp.tools.get(name!)?.config.action, action);
    }
    const models = await mcp.call("agent_models", { provider: "codex" });
    assert.deepEqual(models, { models: await provider.listModels() });
    const providers = await mcp.call("agent_providers", {});
    assert.deepEqual(providers, {
      providers: await fixture.runtime.providerStatuses(),
    });
    assert.equal(
      mcp.tools.get("agent_create")!.config.inputSchema.safeParse({
        workspaceRoot: fixture.workspace,
        provider: "custom-agent",
      }).success,
      true,
    );
    const config = {
      model: "test-model",
      reasoningEffort: "high",
      reasoningSummary: "detailed",
      serviceTier: "default",
      approvalPolicy: "never",
      permissionMode: "danger-full-access",
    } as const;
    const created = (await mcp.call("agent_create", {
      workspaceRoot: fixture.workspace,
      provider: "codex",
      ...config,
    })) as { id: string };
    assert.deepEqual(created, fixture.runtime.getSession(created.id));
    // A partial patch must preserve fields omitted by the MCP caller.
    const configured = await mcp.call("agent_configure", {
      sessionId: created.id,
      reasoningEffort: "low",
    });
    assert.deepEqual(configured, fixture.runtime.getSession(created.id));
    assert.deepEqual(sessionConfig(fixture.runtime.getSession(created.id)), {
      model: config.model,
      reasoningEffort: "low",
      reasoningSummary: config.reasoningSummary,
      serviceTier: config.serviceTier,
    });
    assert.equal(
      fixture.runtime.getSession(created.id).permissionMode,
      "danger-full-access",
    );
    assert.equal(
      fixture.runtime.getSession(created.id).approvalPolicy,
      "never",
    );
    const reset = await mcp.call("agent_configure", {
      sessionId: created.id,
      reasoningSummary: null,
    });
    assert.deepEqual(reset, fixture.runtime.getSession(created.id));
    assert.equal(fixture.runtime.getSession(created.id).reasoningSummary, null);
    for (const name of ["clip.wav", "SKILL.md"])
      await writeFile(path.join(fixture.workspace, name), "test");
    const attachments = [
      {
        kind: "audio",
        name: "clip.wav",
        path: "clip.wav",
        mimeType: "audio/wav",
      },
      {
        kind: "skill",
        name: "SKILL.md",
        path: "SKILL.md",
        mimeType: "text/markdown",
      },
    ];
    const sent = await mcp.call("agent_send", {
      sessionId: created.id,
      text: "inspect",
      attachments,
      outputSchema: null,
    });
    const stream = provider.sessions[1]!;
    assert.deepEqual(stream.inputs[0], {
      text: "inspect",
      attachments,
      outputSchema: null,
    });
    assert.deepEqual(sent, fixture.runtime.history(created.id).turns[0]!.turn);
    await stream.send({
      type: "item.completed",
      providerItemId: "plan",
      item: {
        type: "plan",
        text: "Inspect",
        steps: [{ step: "Inspect", status: "completed" }],
      },
    });
    await stream.send({
      type: "item.completed",
      providerItemId: "reasoning",
      item: { type: "reasoning_summary", text: "Reviewed" },
    });
    await stream.send({ type: "token.usage", usage: tokenUsage(10) });
    const decision = "acceptForSession";
    await stream.send({
      type: "interaction.requested",
      providerItemId: "approval",
      interaction: {
        id: "native-approval",
        kind: "approval",
        title: "Proceed?",
        description: null,
        questions: [],
        approvalOptions: [
          { id: decision, label: "Allow for session", description: null },
        ],
      },
    });
    const pendingHistory = fixture.runtime.history(created.id);
    const pendingTurn = pendingHistory.turns[0]!.turn;
    const pending = pendingHistory.turns[0]!.items.at(-1)!;
    assert.deepEqual(fixture.runtime.getSession(created.id).attention, {
      kind: "approval",
      count: 1,
    });
    assert.deepEqual(
      fixture.runtime.historyUpdate(created.id, [pendingTurn.id], [pending.id]),
      {
        session: fixture.runtime.getSession(created.id),
        turns: [pendingTurn],
        items: [pending],
      },
    );
    const response = { type: "approval", decision };
    const answered = await mcp.call("agent_respond", {
      sessionId: created.id,
      itemId: pending.id,
      response,
    });
    assert.deepEqual(stream.responses, [
      { interactionId: "native-approval", response },
    ]);
    assert.deepEqual(
      answered,
      fixture.runtime.history(created.id).turns[0]!.items.at(-1),
    );
    assert.equal(fixture.runtime.getSession(created.id).attention, null);
    assert.equal(fixture.runtime.getSession(created.id).status, "running");
    const error = {
      message: "failed",
      details: "details",
      providerData: {
        errorInfo: { other: "test" },
        misalignment: { kind: "test" },
      },
    };
    await stream.send({ type: "turn.completed", status: "failed", error });
    // Parsing output must retain every rich field, including plan steps and approvals.
    assert.deepEqual(
      await mcp.call("agent_history", { sessionId: created.id }),
      fixture.runtime.history(created.id),
    );
    assert.deepEqual(await mcp.call("agent_sessions", {}), {
      sessions: fixture.runtime.listSessions(),
    });
    const inputSchema = mcp.tools.get("agent_send")!.config.inputSchema;
    assert.equal(
      inputSchema.safeParse({
        sessionId: created.id,
        attachments: [{ ...attachments[0], kind: "unsupported" }],
      }).success,
      false,
    );
    assert.equal(
      mcp.tools.get("agent_respond")!.config.inputSchema.safeParse({
        sessionId: created.id,
        itemId: pending.id,
        response: { type: "approval", decision: "" },
      }).success,
      false,
    );
  } finally {
    await fixture.cleanup();
  }
});

test("Agent invalidations reconstruct history through creation, deltas, usage, completion and deletion", async () => {
  const provider = new StreamingProvider();
  const fixture = await createRuntimeFixture(provider);
  const hints: Array<
    Extract<Parameters<RuntimeEventBus["emit"]>[0], { type: "agent" }>
  > = [];
  const unsubscribe = fixture.events.subscribe((event) => {
    if (event.type === "agent") hints.push(event);
  });
  try {
    const id = fixture.session.id;
    const empty = fixture.runtime.historyUpdate(id, [], []);
    assert.deepEqual(empty.turns, []);
    assert.deepEqual(empty.items, []);
    await fixture.runtime.startTurn(
      id,
      { text: "run", attachments: [], outputSchema: null },
      "human",
    );
    const stream = provider.sessions[0]!;
    await stream.send({ type: "turn.started", providerTurnId: "native" });
    await stream.send({
      type: "item.started",
      providerItemId: "assistant",
      item: { type: "assistant_message", text: "Hi", phase: null },
    });
    await stream.send({
      type: "item.delta",
      providerItemId: "assistant",
      field: "text",
      delta: " there",
    });
    await stream.send({ type: "token.usage", usage: tokenUsage(5) });
    await stream.send({
      type: "interaction.requested",
      providerItemId: "approval",
      interaction: {
        id: "request",
        kind: "approval",
        title: "Allow?",
        description: null,
        questions: [],
        approvalOptions: [{ id: "yes", label: "Yes", description: null }],
      },
    });
    const waiting = fixture.runtime.history(id);
    const item = waiting.turns[0]!.items.at(-1)!;
    const itemOnly = fixture.runtime.historyUpdate(id, [], [item.id]);
    assert.equal(itemOnly.turns[0]!.id, item.turnId);
    await stream.send({
      type: "interaction.resolved",
      providerRequestId: "request",
    });
    await stream.send({
      type: "turn.completed",
      status: "completed",
      error: null,
    });
    const turnIds = [...new Set(hints.flatMap((hint) => hint.turnIds ?? []))];
    const itemIds = [...new Set(hints.flatMap((hint) => hint.itemIds ?? []))];
    const update = fixture.runtime.historyUpdate(id, turnIds, itemIds);
    const full = fixture.runtime.history(id);
    assert.deepEqual(update.session, full.session);
    assert.deepEqual(
      update.turns,
      full.turns.map((entry) => entry.turn),
    );
    assert.deepEqual(
      update.items.sort((a, b) => a.sequence - b.sequence),
      full.turns[0]!.items,
    );
    assert.equal(full.session.attention, null);
    assert.equal(full.session.status, "idle");
    const completion = hints.at(-1)!;
    assert.deepEqual(completion.itemIds, [full.turns[0]!.items[1]!.id]);
    await fixture.runtime.deleteSession(id);
    assert.equal(hints.at(-1)!.deleted, true);
    assert.throws(
      () => fixture.runtime.historyUpdate(id, [], []),
      /Unknown Agent session/,
    );
  } finally {
    unsubscribe();
    await fixture.cleanup();
  }
});

test("Agent interaction submission rejects duplicates and reconciles external resolution, usage and turn completion", async () => {
  const provider = new StreamingProvider();
  const fixture = await createRuntimeFixture(provider);
  try {
    const id = fixture.session.id;
    await fixture.runtime.startTurn(
      id,
      { text: "run", attachments: [], outputSchema: null },
      "human",
    );
    const stream = provider.sessions[0]!;
    const request = async (name: string, kind: "approval" | "input") => {
      await stream.send({
        type: "interaction.requested",
        providerItemId: name,
        interaction: {
          id: name,
          kind,
          title: name,
          description: null,
          questions: [],
          approvalOptions:
            kind === "approval"
              ? [{ id: "yes", label: "Yes", description: null }]
              : [],
        },
      });
      return fixture.runtime.history(id).turns[0]!.items.at(-1)!;
    };
    const approval = await request("approval", "approval");
    const input = await request("input", "input");
    assert.deepEqual(fixture.runtime.getSession(id).attention, {
      kind: "approval",
      count: 2,
    });
    let release!: () => void;
    const delay = new Promise<void>((resolve) => {
      release = resolve;
    });
    let submissions = 0;
    stream.resolveInteraction = async () => {
      submissions++;
      await delay;
    };
    const response = { type: "approval", decision: "yes" } as const;
    const submitting = fixture.runtime.respondInteraction(
      id,
      approval.id,
      response,
    );
    await assert.rejects(
      fixture.runtime.respondInteraction(id, approval.id, response),
      /already being submitted/,
    );
    await stream.send({
      type: "interaction.resolved",
      providerRequestId: "approval",
    });
    assert.deepEqual(fixture.runtime.getSession(id).attention, {
      kind: "input",
      count: 1,
    });
    release();
    await submitting;
    assert.equal(submissions, 1);
    assert.equal(fixture.runtime.getSession(id).status, "waiting_input");
    await fixture.runtime.respondInteraction(id, input.id, {
      type: "input",
      answers: {},
    });
    assert.equal(fixture.runtime.getSession(id).attention, null);
    await stream.send({ type: "token.usage", usage: tokenUsage(10) });
    assert.equal(fixture.runtime.getSession(id).status, "running");
    const late = await request("late", "approval");
    let releaseLate!: () => void;
    stream.resolveInteraction = () =>
      new Promise<void>((resolve) => {
        releaseLate = resolve;
      });
    const lateSubmission = fixture.runtime.respondInteraction(
      id,
      late.id,
      response,
    );
    await stream.send({
      type: "turn.completed",
      status: "interrupted",
      error: null,
    });
    releaseLate();
    const lateItem = await lateSubmission;
    assert.equal(lateItem.status, "interrupted");
    assert.equal(fixture.runtime.getSession(id).status, "idle");
    assert.equal(fixture.runtime.getSession(id).attention, null);
  } finally {
    await fixture.cleanup();
  }
});

test("Human and MCP steering messages preserve their own source within an MCP turn", async () => {
  const provider = new StreamingProvider();
  const fixture = await createRuntimeFixture(provider);
  try {
    await fixture.runtime.startTurn(
      fixture.session.id,
      { text: "MCP instruction", attachments: [], outputSchema: null },
      "mcp",
    );
    await fixture.runtime.steer(
      fixture.session.id,
      { text: "Human follow-up", attachments: [] },
      "human",
    );
    await fixture.runtime.steer(
      fixture.session.id,
      { text: "MCP follow-up", attachments: [] },
      "mcp",
    );
    const entry = fixture.runtime.history(fixture.session.id).turns[0]!;
    assert.equal(entry.turn.source, "mcp");
    assert.deepEqual(
      entry.items
        .filter((item) => item.type === "user_message")
        .map((item) => item.source),
      ["mcp", "human", "mcp"],
    );
    await provider.sessions[0]!.send({
      type: "turn.completed",
      status: "completed",
      error: null,
    });
  } finally {
    await fixture.cleanup();
  }
});
