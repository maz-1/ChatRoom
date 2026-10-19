<script setup lang="ts">
import { shallowRef, watch } from "vue";
import { useLocale } from "vuetify";
import {
  api,
  type WorkspaceMcpSettings,
  type WorkspaceMcpPolicyUpdate,
} from "../api.js";
import WorkspaceMcpServerEditor from "./WorkspaceMcpServerEditor.vue";
import { errorMessage } from "../utils/errors.js";
import { createRequestGate } from "../utils/requests.js";

const props = defineProps<{ root: string }>();
const payload = shallowRef<WorkspaceMcpSettings | null>(null);
const loading = shallowRef(false);
const saving = shallowRef<string | null>(null);
const error = shallowRef("");
const saved = shallowRef(false);
const selectedName = shallowRef<string | null>(null);
const locale = useLocale();
const requests = createRequestGate();

watch(
  () => props.root,
  () => {
    payload.value = null;
    saving.value = null;
    selectedName.value = null;
    void load();
  },
  { immediate: true },
);

async function load() {
  const request = requests.begin();
  const root = props.root;
  loading.value = true;
  error.value = "";
  saved.value = false;
  try {
    const next = await api<WorkspaceMcpSettings>(
      `/workspace/mcp/servers?root=${encodeURIComponent(root)}`,
      { signal: request.signal },
    );
    if (requests.isCurrent(request) && props.root === root)
      payload.value = next;
  } catch (cause) {
    if (requests.isCurrent(request)) error.value = errorMessage(cause);
  } finally {
    if (requests.isCurrent(request)) loading.value = false;
  }
}

async function setEnabled(server: string, enabled: boolean) {
  await saveSettings(server, { enabled });
}

async function saveSettings(server: string, update: WorkspaceMcpPolicyUpdate) {
  if (loading.value || saving.value !== null || !payload.value) return;
  const request = requests.begin();
  const root = props.root;
  saving.value = server;
  error.value = "";
  saved.value = false;
  try {
    const next = await api<WorkspaceMcpSettings>(
      `/workspace/mcp/servers/${encodeURIComponent(server)}`,
      {
        method: "PATCH",
        body: JSON.stringify({ root, ...update }),
        signal: request.signal,
      },
    );
    if (requests.isCurrent(request) && props.root === root) {
      payload.value = next;
      saved.value = true;
    }
  } catch (cause) {
    if (requests.isCurrent(request)) error.value = errorMessage(cause);
  } finally {
    if (requests.isCurrent(request)) saving.value = null;
  }
}
</script>

<template>
  <div class="workspace-settings-pane">
    <div class="panel-header">
      <div class="min-w-0">
        <div class="panel-title">
          {{ locale.t("$vuetify.chatroom.workspaces.mcpTitle") }}
        </div>
        <div class="panel-subtitle">
          {{ locale.t("$vuetify.chatroom.workspaces.mcpHelp") }}
        </div>
      </div>
      <v-btn
        icon="$mdiRefresh"
        size="small"
        variant="text"
        :loading="loading"
        :disabled="saving !== null"
        :aria-label="locale.t('$vuetify.chatroom.workspaces.refreshSettings')"
        @click="load"
      />
    </div>
    <v-progress-linear v-if="loading" indeterminate />
    <v-alert v-if="error" type="error" variant="tonal" density="compact">{{
      error
    }}</v-alert>
    <div v-if="payload" class="px-5 pb-3 text-caption muted text-break">
      {{ payload.configPath }}
    </div>
    <v-alert v-if="saved" type="success" variant="tonal" density="compact">{{
      locale.t("$vuetify.chatroom.workspaces.settingsSaved")
    }}</v-alert>
    <v-table v-if="payload?.servers.length">
      <thead>
        <tr>
          <th>{{ locale.t("$vuetify.chatroom.mcp.server") }}</th>
          <th>
            {{ locale.t("$vuetify.chatroom.workspaces.mcpWorkspaceEnabled") }}
          </th>
          <th>{{ locale.t("$vuetify.chatroom.mcp.status") }}</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="server in payload.servers" :key="server.name">
          <td>
            <div class="font-weight-medium">{{ server.name }}</div>
            <div class="text-caption muted">{{ server.type }}</div>
          </td>
          <td>
            <v-switch
              :model-value="server.enabled"
              color="primary"
              density="compact"
              hide-details
              inset
              :loading="saving === server.name"
              :disabled="loading || saving !== null"
              :aria-label="
                locale.t('$vuetify.chatroom.mcp.toggleServer', server.name)
              "
              @update:model-value="setEnabled(server.name, Boolean($event))"
            />
          </td>
          <td>
            <v-chip
              size="small"
              variant="tonal"
              :color="server.effectiveEnabled ? 'success' : 'secondary'"
              >{{
                locale.t(
                  server.globallyEnabled
                    ? server.enabled
                      ? "$vuetify.chatroom.workspaces.mcpAvailable"
                      : "$vuetify.chatroom.workspaces.mcpWorkspaceDisabled"
                    : "$vuetify.chatroom.workspaces.mcpGloballyDisabled",
                )
              }}</v-chip
            >
          </td>
          <td class="text-right">
            <v-btn
              size="small"
              variant="text"
              :disabled="loading || saving !== null"
              @click="
                selectedName = selectedName === server.name ? null : server.name
              "
              >{{
                locale.t("$vuetify.chatroom.workspaces.mcpConfigure")
              }}</v-btn
            >
          </td>
        </tr>
      </tbody>
    </v-table>
    <v-empty-state
      v-else-if="!loading && !error"
      :title="locale.t('$vuetify.chatroom.mcp.empty')"
    />
    <template v-for="server in payload?.servers ?? []" :key="server.name">
      <WorkspaceMcpServerEditor
        v-if="server.name === selectedName"
        :root="root"
        :server="server"
        :default-timeout-sec="payload!.defaultToolTimeoutSec"
        :busy="loading || saving !== null"
        @save="saveSettings(server.name, $event)"
      />
    </template>
  </div>
</template>
