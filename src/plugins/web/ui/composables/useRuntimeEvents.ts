import { onBeforeUnmount, shallowRef } from "vue";
import { api, type RuntimeStatus, type UpdateStatus } from "../api.js";
import { createRequestGate } from "../utils/requests.js";

type ConnectionState = "connecting" | "connected" | "reconnecting" | "offline";
type RefreshScope = "processes" | "operations" | "computer" | "agents" | "mcp";

export interface ProcessRefreshHint {
  processId: string;
  output: boolean;
  summary: boolean;
  deleted: boolean;
}

export interface OperationRefreshHint {
  operationId: string;
  pluginId: string;
}

export interface OperationRefreshBatch {
  changes: OperationRefreshHint[];
  clearAll: boolean;
}

export interface ComputerRefreshHint {
  status: boolean;
  preview: boolean;
  previewSnapshotId: string | null;
  operationsReset: boolean;
  operationIds: string[];
}

export interface AgentRefreshHint {
  sessionId: string;
  turnIds: string[];
  itemIds: string[];
  deleted: boolean;
}

type RuntimeEvent =
  | {
      type: "agent";
      sessionId?: string;
      turnIds?: string[];
      itemIds?: string[];
      deleted?: boolean;
    }
  | { type: "operation"; operationId: string; pluginId: string }
  | { type: "operations-cleared" }
  | {
      type: "process";
      processId: string;
      output?: boolean;
      deleted?: boolean;
    }
  | { type: "computer-settings" }
  | { type: "computer-snapshot"; snapshotId: string }
  | { type: "mcp-servers" };

const EVENT_COALESCE_MS = 100;
const RUNTIME_POLL_MS = 15_000;
const UPDATE_POLL_MS = 60 * 60 * 1000;

export function useRuntimeEvents() {
  const runtime = shallowRef<RuntimeStatus | null>(null);
  const updateStatus = shallowRef<UpdateStatus | null>(null);
  const processRevision = shallowRef(0);
  const operationRevision = shallowRef(0);
  const computerRevision = shallowRef(0);
  const agentRevision = shallowRef(0);
  const mcpRevision = shallowRef(0);
  const processChanges = shallowRef<ProcessRefreshHint[] | null>([]);
  const operationChanges = shallowRef<OperationRefreshBatch | null>({
    changes: [],
    clearAll: false,
  });
  const computerChanges = shallowRef<ComputerRefreshHint | null>({
    status: false,
    preview: false,
    previewSnapshotId: null,
    operationsReset: false,
    operationIds: [],
  });
  const agentChanges = shallowRef<AgentRefreshHint[] | null>([]);
  const connectionState = shallowRef<ConnectionState>("connecting");
  const latencyMs = shallowRef<number | null>(null);

  let stream: EventSource | null = null;
  let runtimeTimer: ReturnType<typeof setInterval> | null = null;
  let updateTimer: ReturnType<typeof setInterval> | null = null;
  let flushTimer: ReturnType<typeof setTimeout> | null = null;
  let lifecycle = 0;

  const pendingScopes = new Set<RefreshScope>();
  const pendingProcessChanges = new Map<
    string,
    { output: boolean; summary: boolean; deleted: boolean }
  >();
  const pendingOperationChanges = new Map<string, string>();
  const pendingComputerOperationIds = new Set<string>();
  const pendingAgentChanges = new Map<
    string,
    { turnIds: Set<string>; itemIds: Set<string>; deleted: boolean }
  >();

  let pendingComputerStatus = false;
  let pendingComputerPreview = false;
  let pendingComputerPreviewSnapshotId: string | null = null;
  let pendingComputerOperationsReset = false;
  let pendingGlobalProcessEvent = false;
  let pendingGlobalOperationEvent = false;
  let pendingOperationClear = false;
  let pendingGlobalComputerEvent = false;
  let pendingGlobalAgentEvent = false;

  const runtimeRequests = createRequestGate();

  onBeforeUnmount(stop);

  function start() {
    stop();
    connectionState.value = navigator.onLine ? "connecting" : "offline";
    latencyMs.value = null;
    window.addEventListener("online", handleOnline);
    window.addEventListener("offline", handleOffline);
    connectEvents();
    void loadRuntimeStatus().then(() => void loadUpdateStatus());
    runtimeTimer = setInterval(() => void loadRuntimeStatus(), RUNTIME_POLL_MS);
    updateTimer = setInterval(() => void loadUpdateStatus(), UPDATE_POLL_MS);
  }

  function stop() {
    lifecycle += 1;
    runtimeRequests.invalidate();
    window.removeEventListener("online", handleOnline);
    window.removeEventListener("offline", handleOffline);
    stream?.close();
    stream = null;
    if (runtimeTimer) clearInterval(runtimeTimer);
    if (updateTimer) clearInterval(updateTimer);
    if (flushTimer) clearTimeout(flushTimer);
    runtimeTimer = null;
    updateTimer = null;
    flushTimer = null;
    clearPending();
  }

  function clear() {
    stop();
    runtime.value = null;
    updateStatus.value = null;
    connectionState.value = "offline";
    latencyMs.value = null;
  }

  function connectEvents() {
    const source = new EventSource("/api/events");
    stream = source;
    source.addEventListener("open", () => {
      if (stream !== source) return;
      connectionState.value = "connected";
      scheduleGlobalRefresh();
    });
    source.addEventListener("error", () => {
      if (stream !== source) return;
      connectionState.value = navigator.onLine ? "reconnecting" : "offline";
      latencyMs.value = null;
    });
    source.addEventListener("runtime", (message) => {
      if (stream !== source) return;
      const event = parseRuntimeEvent(message);
      if (!event) return;
      switch (event.type) {
        case "agent":
          if (event.sessionId) mergeAgentChange(event);
          else pendingGlobalAgentEvent = true;
          scheduleRefresh("agents");
          break;
        case "process":
          mergeProcessChange(event);
          scheduleRefresh("processes");
          break;
        case "operation":
          pendingOperationChanges.set(event.operationId, event.pluginId);
          scheduleRefresh("operations");
          if (event.pluginId === "computer") {
            pendingComputerOperationIds.add(event.operationId);
            scheduleRefresh("computer");
          }
          break;
        case "operations-cleared":
          pendingOperationClear = true;
          pendingOperationChanges.clear();
          pendingComputerOperationsReset = true;
          scheduleRefresh("operations");
          scheduleRefresh("computer");
          break;
        case "computer-settings":
          pendingComputerStatus = true;
          pendingComputerPreview = true;
          scheduleRefresh("computer");
          break;
        case "computer-snapshot":
          pendingComputerPreview = true;
          pendingComputerPreviewSnapshotId = event.snapshotId;
          scheduleRefresh("computer");
          break;
        case "mcp-servers":
          scheduleRefresh("mcp");
          break;
      }
    });
  }

  function scheduleGlobalRefresh() {
    pendingGlobalProcessEvent = true;
    pendingGlobalOperationEvent = true;
    pendingGlobalComputerEvent = true;
    pendingGlobalAgentEvent = true;
    scheduleRefresh("processes");
    scheduleRefresh("operations");
    scheduleRefresh("computer");
    scheduleRefresh("agents");
    scheduleRefresh("mcp");
  }

  function mergeProcessChange(
    event: Extract<RuntimeEvent, { type: "process" }>,
  ) {
    const pending = pendingProcessChanges.get(event.processId) ?? {
      output: false,
      summary: false,
      deleted: false,
    };
    pending.output ||= Boolean(event.output);
    pending.summary ||= !event.output;
    pending.deleted ||= Boolean(event.deleted);
    pendingProcessChanges.set(event.processId, pending);
  }

  function mergeAgentChange(event: Extract<RuntimeEvent, { type: "agent" }>) {
    if (!event.sessionId) return;
    const pending = pendingAgentChanges.get(event.sessionId) ?? {
      turnIds: new Set<string>(),
      itemIds: new Set<string>(),
      deleted: false,
    };
    for (const id of event.turnIds ?? []) pending.turnIds.add(id);
    for (const id of event.itemIds ?? []) pending.itemIds.add(id);
    pending.deleted ||= Boolean(event.deleted);
    pendingAgentChanges.set(event.sessionId, pending);
  }

  function scheduleRefresh(scope: RefreshScope) {
    pendingScopes.add(scope);
    if (flushTimer) return;
    flushTimer = setTimeout(flushRefreshes, EVENT_COALESCE_MS);
  }

  function flushRefreshes() {
    flushTimer = null;
    if (pendingScopes.delete("mcp")) mcpRevision.value += 1;

    if (pendingScopes.delete("processes")) {
      processChanges.value = pendingGlobalProcessEvent
        ? null
        : [...pendingProcessChanges].map(([processId, change]) => ({
            processId,
            output: change.output,
            summary: change.summary,
            deleted: change.deleted,
          }));
      pendingProcessChanges.clear();
      pendingGlobalProcessEvent = false;
      processRevision.value += 1;
    }

    if (pendingScopes.delete("operations")) {
      operationChanges.value = pendingGlobalOperationEvent
        ? null
        : {
            changes: [...pendingOperationChanges].map(
              ([operationId, pluginId]) => ({
                operationId,
                pluginId,
              }),
            ),
            clearAll: pendingOperationClear,
          };
      pendingOperationChanges.clear();
      pendingOperationClear = false;
      pendingGlobalOperationEvent = false;
      operationRevision.value += 1;
    }

    if (pendingScopes.delete("computer")) {
      computerChanges.value = pendingGlobalComputerEvent
        ? null
        : {
            status: pendingComputerStatus,
            preview: pendingComputerPreview,
            previewSnapshotId: pendingComputerPreviewSnapshotId,
            operationsReset: pendingComputerOperationsReset,
            operationIds: [...pendingComputerOperationIds],
          };
      pendingComputerStatus = false;
      pendingComputerPreview = false;
      pendingComputerPreviewSnapshotId = null;
      pendingComputerOperationsReset = false;
      pendingComputerOperationIds.clear();
      pendingGlobalComputerEvent = false;
      computerRevision.value += 1;
    }

    if (pendingScopes.delete("agents")) {
      agentChanges.value = pendingGlobalAgentEvent
        ? null
        : [...pendingAgentChanges].map(([sessionId, change]) => ({
            sessionId,
            turnIds: [...change.turnIds],
            itemIds: [...change.itemIds],
            deleted: change.deleted,
          }));
      pendingAgentChanges.clear();
      pendingGlobalAgentEvent = false;
      agentRevision.value += 1;
    }
  }

  function clearPending() {
    pendingScopes.clear();
    pendingProcessChanges.clear();
    pendingOperationChanges.clear();
    pendingComputerOperationIds.clear();
    pendingAgentChanges.clear();
    pendingComputerStatus = false;
    pendingComputerPreview = false;
    pendingComputerPreviewSnapshotId = null;
    pendingComputerOperationsReset = false;
    pendingGlobalProcessEvent = false;
    pendingGlobalOperationEvent = false;
    pendingOperationClear = false;
    pendingGlobalComputerEvent = false;
    pendingGlobalAgentEvent = false;
  }

  async function loadRuntimeStatus() {
    const request = runtimeRequests.begin();
    const startedAt = performance.now();
    try {
      const next = await api<RuntimeStatus>("/runtime", {
        signal: request.signal,
      });
      if (!runtimeRequests.isCurrent(request)) return;
      runtime.value = next;
      latencyMs.value = Math.max(0, Math.round(performance.now() - startedAt));
    } catch {
      if (!runtimeRequests.isCurrent(request)) return;
      latencyMs.value = null;
      if (!navigator.onLine) connectionState.value = "offline";
    }
  }

  function handleOnline() {
    connectionState.value = "reconnecting";
    latencyMs.value = null;
    stream?.close();
    stream = null;
    connectEvents();
    void loadRuntimeStatus();
  }

  function handleOffline() {
    connectionState.value = "offline";
    latencyMs.value = null;
  }

  async function loadUpdateStatus() {
    const generation = lifecycle;
    if (!runtime.value?.version) await loadRuntimeStatus();
    if (generation !== lifecycle) return;
    const currentVersion = runtime.value?.version;
    if (!currentVersion) return;
    try {
      const response = await fetch(
        "https://api.github.com/repos/gymZJ/ChatRoom/releases/latest",
        {
          headers: { accept: "application/vnd.github+json" },
          signal: AbortSignal.timeout(5000),
        },
      );
      if (generation !== lifecycle || !response.ok) return;
      const release = (await response.json()) as {
        tag_name?: unknown;
        html_url?: unknown;
      };
      if (generation !== lifecycle || typeof release.tag_name !== "string")
        return;
      const latestVersion = normalizeVersion(release.tag_name);
      updateStatus.value = {
        latestVersion,
        updateAvailable: compareVersions(latestVersion, currentVersion) > 0,
        releaseUrl:
          typeof release.html_url === "string" ? release.html_url : null,
      };
    } catch {
      // Update checks are best-effort; preserve the last successful result.
    }
  }

  return {
    runtime,
    updateStatus,
    processRevision,
    processChanges,
    operationRevision,
    operationChanges,
    computerRevision,
    computerChanges,
    agentRevision,
    agentChanges,
    mcpRevision,
    connectionState,
    latencyMs,
    start,
    clear,
  };
}

function parseRuntimeEvent(message: Event): RuntimeEvent | null {
  if (!(message instanceof MessageEvent) || typeof message.data !== "string")
    return null;
  try {
    const value = JSON.parse(message.data) as { type?: unknown };
    return typeof value?.type === "string" ? (value as RuntimeEvent) : null;
  } catch {
    return null;
  }
}

function normalizeVersion(value: string): string {
  const trimmed = value.trim();
  return trimmed.startsWith("v") ? trimmed.slice(1) : trimmed;
}

function compareVersions(left: string, right: string): number {
  const a = parseVersion(left);
  const b = parseVersion(right);
  if (!a || !b) return 0;
  for (let index = 0; index < 3; index += 1) {
    const difference = a.core[index]! - b.core[index]!;
    if (difference) return Math.sign(difference);
  }
  if (!a.prerelease && !b.prerelease) return 0;
  if (!a.prerelease) return 1;
  if (!b.prerelease) return -1;
  return a.prerelease.localeCompare(b.prerelease, undefined, {
    numeric: true,
    sensitivity: "base",
  });
}

function parseVersion(
  value: string,
): { core: [number, number, number]; prerelease: string | null } | null {
  const match =
    /^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/.exec(
      value.trim(),
    );
  if (!match) return null;
  return {
    core: [Number(match[1]), Number(match[2]), Number(match[3])],
    prerelease: match[4] ?? null,
  };
}
