<script setup lang="ts">
import { shallowRef, watch } from "vue";
import { useLocale } from "vuetify";
import {
  api,
  type WorkspaceMcpSettings,
  type WorkspaceMcpPolicyUpdate,
} from "../api.js";
import { createRequestGate } from "../utils/requests.js";
import { errorMessage } from "../utils/errors.js";

const props = defineProps<{
  root: string;
  server: WorkspaceMcpSettings["servers"][number];
  busy: boolean;
  defaultTimeoutSec: number;
}>();
const emit = defineEmits<{ save: [update: WorkspaceMcpPolicyUpdate] }>();
const locale = useLocale();
const mode = shallowRef("all");
const allowed = shallowRef<string[]>([]);
const blocked = shallowRef<string[]>([]);
const suggestions = shallowRef<string[]>([]);
const loadingTools = shallowRef(false);
const error = shallowRef("");
const timeout = shallowRef<string | number | null>(null);
const requests = createRequestGate();

watch(
  () => [props.root, props.server] as const,
  () => {
    requests.invalidate();
    mode.value = props.server.enabledTools === null ? "all" : "selected";
    allowed.value = [...(props.server.enabledTools ?? [])];
    blocked.value = [...props.server.disabledTools];
    timeout.value = props.server.toolTimeoutSec;
    suggestions.value = [];
    loadingTools.value = false;
    error.value = "";
  },
  { immediate: true },
);

async function loadTools() {
  const request = requests.begin();
  loadingTools.value = true;
  error.value = "";
  try {
    const result = await api<{
      tools: { name: string }[];
      status: string;
      error: string | null;
    }>(
      `/workspace/mcp/servers/${encodeURIComponent(props.server.name)}/tools?root=${encodeURIComponent(props.root)}`,
      { signal: request.signal },
    );
    if (!requests.isCurrent(request)) return;
    suggestions.value = result.tools.map((tool) => tool.name);
    if (result.error) error.value = result.error;
  } catch (cause) {
    if (requests.isCurrent(request)) error.value = errorMessage(cause);
  } finally {
    if (requests.isCurrent(request)) loadingTools.value = false;
  }
}

function save() {
  const seconds =
    timeout.value === null || timeout.value === ""
      ? null
      : Number(timeout.value);
  if (
    seconds !== null &&
    (!Number.isFinite(seconds) || seconds < 0.001 || seconds > 2_147_483.647)
  ) {
    error.value = locale.t("$vuetify.chatroom.workspaces.mcpTimeoutInvalid");
    return;
  }
  const clean = (names: string[]) => [
    ...new Set(names.map((name) => name.trim()).filter(Boolean)),
  ];
  emit("save", {
    enabledTools: mode.value === "all" ? null : clean(allowed.value),
    disabledTools: clean(blocked.value),
    toolTimeoutSec: seconds,
  });
}
</script>

<template>
  <div class="workspace-mcp-editor pa-5">
    <div class="d-flex align-center ga-3 mb-3 flex-wrap">
      <strong>{{ server.name }}</strong>
      <v-btn
        size="small"
        variant="tonal"
        :loading="loadingTools"
        :disabled="busy || !server.globallyEnabled"
        @click="loadTools"
        >{{ locale.t("$vuetify.chatroom.workspaces.mcpLoadTools") }}</v-btn
      >
    </div>
    <p class="text-body-2 muted mb-4">
      {{ locale.t("$vuetify.chatroom.workspaces.mcpToolsHelp") }}
    </p>
    <v-alert
      v-if="error"
      type="error"
      variant="tonal"
      density="compact"
      class="mb-4"
      >{{ error }}</v-alert
    >
    <v-select
      v-model="mode"
      :items="[
        {
          title: locale.t('$vuetify.chatroom.workspaces.mcpAllTools'),
          value: 'all',
        },
        {
          title: locale.t('$vuetify.chatroom.workspaces.mcpSelectedTools'),
          value: 'selected',
        },
      ]"
      :label="locale.t('$vuetify.chatroom.workspaces.mcpToolMode')"
      :disabled="busy"
      density="compact"
      variant="outlined"
      class="mb-4"
    />
    <v-combobox
      v-if="mode === 'selected'"
      v-model="allowed"
      :items="suggestions"
      :label="locale.t('$vuetify.chatroom.workspaces.mcpAllowedTools')"
      :hint="locale.t('$vuetify.chatroom.workspaces.mcpEmptyAllowlist')"
      persistent-hint
      :disabled="busy"
      multiple
      chips
      closable-chips
      clearable
      density="compact"
      variant="outlined"
      class="mb-3"
    />
    <v-combobox
      v-model="blocked"
      :items="suggestions"
      :label="locale.t('$vuetify.chatroom.workspaces.mcpBlockedTools')"
      :disabled="busy"
      multiple
      chips
      closable-chips
      clearable
      density="compact"
      variant="outlined"
    />
    <v-text-field
      v-model="timeout"
      type="number"
      min="0.001"
      max="2147483.647"
      step="any"
      :label="locale.t('$vuetify.chatroom.workspaces.mcpTimeout')"
      :hint="
        locale.t(
          '$vuetify.chatroom.workspaces.mcpTimeoutHelp',
          defaultTimeoutSec,
        )
      "
      persistent-hint
      clearable
      :disabled="busy"
      density="compact"
      variant="outlined"
      class="mb-4"
    />
    <v-btn color="primary" :loading="busy" :disabled="busy" @click="save">{{
      locale.t("$vuetify.chatroom.workspaces.save")
    }}</v-btn>
  </div>
</template>
