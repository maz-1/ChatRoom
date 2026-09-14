<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useLocale } from "vuetify";
import { api, type McpServersView, type McpToolSummary } from "../api.js";
import CodeViewer from "./CodeViewer.vue";

const props = defineProps<{ revision: number }>();

const locale = useLocale();
const payload = ref<McpServersView | null>(null);
const selectedName = ref<string | null>(null);
const error = ref("");
const busy = ref(false);

const servers = computed(() => payload.value?.servers ?? []);
const selected = computed(
  () =>
    servers.value.find((item) => item.name === selectedName.value) ??
    servers.value[0] ??
    null,
);
const connectedCount = computed(
  () => servers.value.filter((item) => item.status === "connected").length,
);

watch(
  () => props.revision,
  () => void load(),
  { immediate: true },
);

async function load(): Promise<void> {
  try {
    error.value = "";
    payload.value = await api<McpServersView>("/mcp/servers");
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  }
}

async function refresh(server?: string): Promise<void> {
  busy.value = true;
  try {
    error.value = "";
    payload.value = await api<McpServersView>("/mcp/servers/refresh", {
      method: "POST",
      body: JSON.stringify(server ? { server } : {}),
    });
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

function toolSchema(tool: McpToolSummary): string | null {
  if (tool.inputSchema === null) return null;
  const properties = tool.inputSchema.properties;
  if (!properties || typeof properties !== "object") return null;
  const required = Array.isArray(tool.inputSchema.required)
    ? (tool.inputSchema.required as string[])
    : [];
  return Object.entries(properties as Record<string, unknown>)
    .map(([name, schema]) => {
      const type =
        schema && typeof schema === "object" && "type" in schema
          ? String((schema as { type?: unknown }).type ?? "any")
          : "any";
      return `${name}: ${type}${required.includes(name) ? " (required)" : ""}`;
    })
    .join("\n");
}
</script>

<template>
  <div class="master-detail-layout mcp-layout">
    <div class="master-pane">
      <v-card class="panel-card">
        <div class="panel-header">
          <div>
            <div class="panel-title">
              {{ locale.t("$vuetify.chatroom.mcp.title") }}
            </div>
            <div class="panel-subtitle">
              {{ locale.t("$vuetify.chatroom.mcp.subtitle") }}
            </div>
          </div>
          <div class="mcp-header-actions">
            <span v-if="servers.length" class="text-caption muted">
              {{
                locale.t(
                  "$vuetify.chatroom.mcp.summary",
                  connectedCount,
                  servers.length,
                )
              }}
            </span>
            <v-btn
              variant="text"
              size="small"
              prepend-icon="mdi-refresh"
              :loading="busy"
              @click="refresh()"
            >
              {{ locale.t("$vuetify.chatroom.mcp.reconnectAll") }}
            </v-btn>
          </div>
        </div>
        <v-divider />

        <v-alert
          v-if="error"
          type="error"
          variant="tonal"
          density="compact"
          class="ma-3"
        >
          {{ error }}
        </v-alert>

        <div v-if="servers.length" class="table-shell">
          <v-table density="comfortable" hover class="mcp-table">
            <thead>
              <tr>
                <th>{{ locale.t("$vuetify.chatroom.mcp.server") }}</th>
                <th>{{ locale.t("$vuetify.chatroom.mcp.transport") }}</th>
                <th>{{ locale.t("$vuetify.chatroom.mcp.status") }}</th>
                <th class="text-right">
                  {{ locale.t("$vuetify.chatroom.mcp.tools") }}
                </th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="item in servers"
                :key="item.name"
                class="clickable"
                :class="{ 'selected-row': selected?.name === item.name }"
                @click="selectedName = item.name"
              >
                <td>
                  <div class="font-weight-medium">{{ item.name }}</div>
                  <div
                    class="text-caption muted mcp-target"
                    :title="item.target"
                  >
                    {{ item.target }}
                  </div>
                </td>
                <td>
                  <v-chip size="small" variant="tonal">{{ item.type }}</v-chip>
                </td>
                <td>
                  <v-chip
                    size="small"
                    variant="tonal"
                    :color="item.status === 'connected' ? 'success' : 'error'"
                    :prepend-icon="
                      item.status === 'connected'
                        ? 'mdi-check-circle-outline'
                        : 'mdi-alert-circle-outline'
                    "
                  >
                    {{
                      locale.t(`$vuetify.chatroom.mcp.statuses.${item.status}`)
                    }}
                  </v-chip>
                </td>
                <td class="text-right text-body-2">{{ item.toolCount }}</td>
              </tr>
            </tbody>
          </v-table>
        </div>

        <div v-else class="empty-inline">
          {{ locale.t("$vuetify.chatroom.mcp.empty") }}
        </div>
      </v-card>

      <v-card class="panel-card mcp-hint">
        <div class="panel-header">
          <div>
            <div class="panel-title">
              {{ locale.t("$vuetify.chatroom.mcp.addTitle") }}
            </div>
            <div class="panel-subtitle">
              {{ locale.t("$vuetify.chatroom.mcp.addSubtitle") }}
            </div>
          </div>
        </div>
        <v-divider />
        <div class="mcp-hint-body">
          <p class="text-body-2">
            {{ locale.t("$vuetify.chatroom.mcp.addBody") }}
          </p>
          <CodeViewer
            v-if="payload"
            :text="payload.configPath"
            filename="config.json"
            language="json"
            :toolbar="false"
          />
          <p class="text-caption muted">
            {{ locale.t("$vuetify.chatroom.mcp.addNotice") }}
          </p>
        </div>
      </v-card>
    </div>

    <div class="detail-pane">
      <v-card v-if="selected" class="panel-card">
        <div class="panel-header">
          <div>
            <div class="panel-title">{{ selected.name }}</div>
            <div class="panel-subtitle mcp-target">{{ selected.target }}</div>
          </div>
          <v-btn
            variant="text"
            size="small"
            prepend-icon="mdi-refresh"
            :loading="busy"
            @click="refresh(selected.name)"
          >
            {{ locale.t("$vuetify.chatroom.mcp.reconnect") }}
          </v-btn>
        </div>
        <v-divider />

        <v-alert
          v-if="selected.error"
          type="error"
          variant="tonal"
          density="compact"
          class="ma-3"
        >
          {{ selected.error }}
        </v-alert>

        <div class="mcp-section">
          <div class="mcp-section-title">
            {{ locale.t("$vuetify.chatroom.mcp.tools") }} ({{
              selected.toolCount
            }})
          </div>
          <div v-if="selected.tools.length" class="mcp-tool-list">
            <div
              v-for="tool in selected.tools"
              :key="tool.name"
              class="mcp-tool"
            >
              <div class="font-weight-medium">{{ tool.name }}</div>
              <div v-if="tool.title" class="text-caption muted">
                {{ tool.title }}
              </div>
              <div v-if="tool.description" class="text-body-2 mcp-tool-desc">
                {{ tool.description }}
              </div>
              <CodeViewer
                v-if="toolSchema(tool)"
                :text="toolSchema(tool) ?? ''"
                filename="schema.txt"
                :toolbar="false"
              />
            </div>
          </div>
          <div v-else class="empty-inline">
            {{ locale.t("$vuetify.chatroom.mcp.noTools") }}
          </div>
        </div>

        <template v-if="selected.stderrTail">
          <v-divider />
          <div class="mcp-section">
            <div class="mcp-section-title">
              {{ locale.t("$vuetify.chatroom.mcp.stderr") }}
            </div>
            <CodeViewer
              :text="selected.stderrTail"
              filename="stderr.log"
              language="text"
              :toolbar="false"
            />
          </div>
        </template>
      </v-card>

      <v-card v-else class="panel-card">
        <div class="empty-panel">
          {{ locale.t("$vuetify.chatroom.mcp.select") }}
        </div>
      </v-card>
    </div>
  </div>
</template>

<style scoped>
.mcp-layout {
  align-items: start;
}

.mcp-table {
  width: 100%;
}

.mcp-target {
  max-width: 320px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mcp-hint {
  margin-top: 16px;
}

.mcp-header-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

.mcp-hint-body {
  display: grid;
  gap: 8px;
  padding: 12px 16px 16px;
}

.mcp-section {
  display: grid;
  gap: 8px;
  padding: 12px 16px 16px;
}

.mcp-section-title {
  font-weight: 600;
  font-size: 0.875rem;
}

.mcp-tool-list {
  display: grid;
  gap: 12px;
}

.mcp-tool {
  display: grid;
  gap: 4px;
  padding: 10px 12px;
  border: 1px solid rgb(var(--v-theme-on-surface), 0.08);
  border-radius: 12px;
}

.mcp-tool-desc {
  color: rgb(var(--v-theme-on-surface), 0.7);
}
</style>
