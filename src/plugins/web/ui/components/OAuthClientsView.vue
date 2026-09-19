<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { useLocale } from "vuetify";
import { api, type OAuthClientSummary } from "../api.js";

const locale = useLocale();
const clients = ref<OAuthClientSummary[]>([]);
const loading = ref(false);
const busy = ref(new Set<string>());
const error = ref<string | null>(null);
const revokeTarget = ref<OAuthClientSummary | null>(null);
const noteTarget = ref<OAuthClientSummary | null>(null);
const noteDraft = ref("");
const expandedRedirects = ref(new Set<string>());

const activeCount = computed(
  () => clients.value.filter((client) => !client.disabledAt).length,
);

onMounted(() => void load());

async function load(): Promise<void> {
  loading.value = true;
  error.value = null;
  try {
    const payload = await api<{ clients: OAuthClientSummary[] }>(
      "/oauth-clients",
    );
    clients.value = payload.clients;
  } catch (cause) {
    error.value = messageOf(cause);
  } finally {
    loading.value = false;
  }
}

async function setDisabled(
  client: OAuthClientSummary,
  disabled: boolean,
): Promise<void> {
  if (busy.value.has(client.clientId)) return;
  busy.value.add(client.clientId);
  busy.value = new Set(busy.value);
  error.value = null;
  try {
    const updated = await api<OAuthClientSummary>(
      `/oauth-clients/${encodeURIComponent(client.clientId)}`,
      {
        method: "PATCH",
        body: JSON.stringify({ disabled }),
      },
    );
    replaceClient(updated);
  } catch (cause) {
    error.value = messageOf(cause);
  } finally {
    busy.value.delete(client.clientId);
    busy.value = new Set(busy.value);
  }
}

function redirectsExpanded(clientId: string): boolean {
  return expandedRedirects.value.has(clientId);
}

function visibleRedirectUris(client: OAuthClientSummary): string[] {
  return redirectsExpanded(client.clientId)
    ? client.redirectUris
    : client.redirectUris.slice(0, 1);
}

function toggleRedirects(clientId: string): void {
  const next = new Set(expandedRedirects.value);
  if (next.has(clientId)) next.delete(clientId);
  else next.add(clientId);
  expandedRedirects.value = next;
}

function openNoteEditor(client: OAuthClientSummary): void {
  noteTarget.value = client;
  noteDraft.value = client.note;
}

async function saveNote(): Promise<void> {
  const client = noteTarget.value;
  if (!client || busy.value.has(client.clientId)) return;
  busy.value.add(client.clientId);
  busy.value = new Set(busy.value);
  error.value = null;
  try {
    const updated = await api<OAuthClientSummary>(
      `/oauth-clients/${encodeURIComponent(client.clientId)}`,
      {
        method: "PATCH",
        body: JSON.stringify({ note: noteDraft.value }),
      },
    );
    replaceClient(updated);
    noteTarget.value = null;
    noteDraft.value = "";
  } catch (cause) {
    error.value = messageOf(cause);
  } finally {
    busy.value.delete(client.clientId);
    busy.value = new Set(busy.value);
  }
}

async function revokeClient(): Promise<void> {
  const client = revokeTarget.value;
  if (!client || busy.value.has(client.clientId)) return;
  busy.value.add(client.clientId);
  busy.value = new Set(busy.value);
  error.value = null;
  try {
    const deleted = await api<{ clientId: string }>(
      `/oauth-clients/${encodeURIComponent(client.clientId)}/revoke`,
      { method: "POST" },
    );
    clients.value = clients.value.filter(
      (item) => item.clientId !== deleted.clientId,
    );
    revokeTarget.value = null;
  } catch (cause) {
    error.value = messageOf(cause);
  } finally {
    busy.value.delete(client.clientId);
    busy.value = new Set(busy.value);
  }
}

function replaceClient(updated: OAuthClientSummary): void {
  const index = clients.value.findIndex(
    (client) => client.clientId === updated.clientId,
  );
  if (index >= 0) clients.value[index] = updated;
}

function statusOf(client: OAuthClientSummary): "active" | "disabled" {
  return client.disabledAt ? "disabled" : "active";
}

function statusColor(client: OAuthClientSummary): string {
  return statusOf(client) === "active" ? "success" : "warning";
}

function formatTime(value: string | null): string {
  return value ? new Date(value).toLocaleString() : "—";
}

function messageOf(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}
</script>

<template>
  <v-card class="panel-card">
    <div class="panel-header">
      <div>
        <div class="panel-title">
          {{ locale.t("$vuetify.chatroom.oauthClients.title") }}
        </div>
        <div class="panel-subtitle">
          {{ locale.t("$vuetify.chatroom.oauthClients.subtitle") }}
        </div>
      </div>
      <div class="d-flex align-center ga-3">
        <span v-if="clients.length" class="text-caption muted">
          {{
            locale.t(
              "$vuetify.chatroom.oauthClients.summary",
              activeCount,
              clients.length,
            )
          }}
        </span>
        <v-btn
          variant="text"
          size="small"
          prepend-icon="mdi-refresh"
          :loading="loading"
          @click="load"
        >
          {{ locale.t("$vuetify.chatroom.oauthClients.refresh") }}
        </v-btn>
      </div>
    </div>

    <v-alert
      v-if="error"
      type="error"
      variant="tonal"
      density="compact"
      class="ma-4 mb-0"
      closable
      @click:close="error = null"
    >
      {{ error }}
    </v-alert>

    <v-divider />

    <div v-if="clients.length" class="table-shell">
      <v-table class="oauth-clients-table" density="comfortable" hover>
        <thead>
          <tr>
            <th>{{ locale.t("$vuetify.chatroom.oauthClients.client") }}</th>
            <th>{{ locale.t("$vuetify.chatroom.oauthClients.note") }}</th>
            <th>
              {{ locale.t("$vuetify.chatroom.oauthClients.redirectUris") }}
            </th>
            <th>{{ locale.t("$vuetify.chatroom.oauthClients.createdAt") }}</th>
            <th>
              {{ locale.t("$vuetify.chatroom.oauthClients.lastAccessAt") }}
            </th>
            <th>{{ locale.t("$vuetify.chatroom.oauthClients.status") }}</th>
            <th class="text-center">
              {{ locale.t("$vuetify.chatroom.oauthClients.disabled") }}
            </th>
            <th class="text-right">
              {{ locale.t("$vuetify.chatroom.oauthClients.actions") }}
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="client in clients" :key="client.clientId">
            <td>
              <div class="font-weight-medium">{{ client.name }}</div>
              <div class="text-caption muted">{{ client.clientId }}</div>
            </td>
            <td>
              <div
                class="note-preview text-caption"
                :class="{ muted: !client.note }"
                :title="client.note || undefined"
              >
                {{
                  client.note ||
                  locale.t("$vuetify.chatroom.oauthClients.noNote")
                }}
              </div>
              <v-btn
                size="small"
                variant="outlined"
                color="primary"
                prepend-icon="mdi-pencil-outline"
                class="mt-2 note-edit-button"
                @click="openNoteEditor(client)"
              >
                {{ locale.t("$vuetify.chatroom.oauthClients.editNote") }}
              </v-btn>
            </td>
            <td class="redirect-cell">
              <div
                v-for="uri in visibleRedirectUris(client)"
                :key="uri"
                class="redirect-uri text-caption"
                :title="uri"
              >
                {{ uri }}
              </div>
              <v-btn
                v-if="client.redirectUris.length > 1"
                size="x-small"
                variant="tonal"
                color="primary"
                class="mt-2"
                :prepend-icon="
                  redirectsExpanded(client.clientId)
                    ? 'mdi-chevron-up'
                    : 'mdi-chevron-down'
                "
                @click="toggleRedirects(client.clientId)"
              >
                {{
                  redirectsExpanded(client.clientId)
                    ? locale.t(
                        "$vuetify.chatroom.oauthClients.collapseRedirects",
                      )
                    : locale.t(
                        "$vuetify.chatroom.oauthClients.showAllRedirects",
                        client.redirectUris.length,
                      )
                }}
              </v-btn>
            </td>
            <td>{{ formatTime(client.createdAt) }}</td>
            <td>
              <div>{{ formatTime(client.lastAccessAt) }}</div>
              <div class="text-caption muted">
                {{ locale.t("$vuetify.chatroom.oauthClients.runtimeOnly") }}
              </div>
            </td>
            <td>
              <v-chip size="small" variant="tonal" :color="statusColor(client)">
                {{
                  locale.t(
                    `$vuetify.chatroom.oauthClients.statuses.${statusOf(client)}`,
                  )
                }}
              </v-chip>
            </td>
            <td class="text-center">
              <v-switch
                :model-value="Boolean(client.disabledAt)"
                color="warning"
                density="compact"
                hide-details
                inset
                :loading="busy.has(client.clientId)"
                :aria-label="
                  locale.t(
                    '$vuetify.chatroom.oauthClients.toggleDisabled',
                    client.name,
                  )
                "
                @update:model-value="
                  (value) => setDisabled(client, Boolean(value))
                "
              />
            </td>
            <td class="text-right">
              <v-btn
                size="small"
                variant="text"
                color="error"
                :loading="busy.has(client.clientId)"
                @click="revokeTarget = client"
              >
                {{ locale.t("$vuetify.chatroom.oauthClients.revoke") }}
              </v-btn>
            </td>
          </tr>
        </tbody>
      </v-table>
    </div>

    <div v-else-if="!loading" class="empty-state">
      {{ locale.t("$vuetify.chatroom.oauthClients.empty") }}
    </div>
  </v-card>

  <v-dialog
    :model-value="Boolean(noteTarget)"
    max-width="560"
    @update:model-value="
      (value) => {
        if (!value) {
          noteTarget = null;
          noteDraft = '';
        }
      }
    "
  >
    <v-card>
      <v-card-title>
        {{ locale.t("$vuetify.chatroom.oauthClients.noteTitle") }}
      </v-card-title>
      <v-card-text>
        <div class="text-caption muted mb-3">
          {{ noteTarget?.name }}
        </div>
        <v-textarea
          v-model="noteDraft"
          :label="locale.t('$vuetify.chatroom.oauthClients.note')"
          :hint="locale.t('$vuetify.chatroom.oauthClients.noteHint')"
          persistent-hint
          auto-grow
          rows="3"
          maxlength="1000"
          counter="1000"
        />
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn
          variant="text"
          @click="
            noteTarget = null;
            noteDraft = '';
          "
        >
          {{ locale.t("$vuetify.chatroom.common.cancel") }}
        </v-btn>
        <v-btn
          color="primary"
          variant="flat"
          :loading="noteTarget ? busy.has(noteTarget.clientId) : false"
          @click="saveNote"
        >
          {{ locale.t("$vuetify.chatroom.oauthClients.saveNote") }}
        </v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog
    :model-value="Boolean(revokeTarget)"
    max-width="520"
    @update:model-value="(value) => !value && (revokeTarget = null)"
  >
    <v-card>
      <v-card-title>
        {{ locale.t("$vuetify.chatroom.oauthClients.revokeTitle") }}
      </v-card-title>
      <v-card-text>
        {{
          locale.t(
            "$vuetify.chatroom.oauthClients.revokeConfirm",
            revokeTarget?.name ?? "",
          )
        }}
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn variant="text" @click="revokeTarget = null">
          {{ locale.t("$vuetify.chatroom.common.cancel") }}
        </v-btn>
        <v-btn color="error" variant="flat" @click="revokeClient">
          {{ locale.t("$vuetify.chatroom.oauthClients.revoke") }}
        </v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.table-shell {
  overflow-x: auto;
}

.oauth-clients-table {
  min-width: 1240px;
}

.oauth-clients-table :deep(thead th) {
  white-space: nowrap;
}

.oauth-clients-table :deep(tbody td) {
  vertical-align: top;
}

.oauth-clients-table :deep(th:nth-child(1)) {
  min-width: 210px;
}

.oauth-clients-table :deep(th:nth-child(2)) {
  min-width: 180px;
}

.oauth-clients-table :deep(th:nth-child(3)) {
  min-width: 340px;
}

.oauth-clients-table :deep(th:nth-child(4)),
.oauth-clients-table :deep(th:nth-child(5)) {
  min-width: 140px;
}

.oauth-clients-table :deep(th:nth-child(6)) {
  min-width: 90px;
}

.oauth-clients-table :deep(th:nth-child(7)),
.oauth-clients-table :deep(th:nth-child(8)) {
  min-width: 76px;
}

.oauth-clients-table :deep(td:nth-child(4)),
.oauth-clients-table :deep(td:nth-child(5)),
.oauth-clients-table :deep(td:nth-child(6)),
.oauth-clients-table :deep(td:nth-child(7)),
.oauth-clients-table :deep(td:nth-child(8)) {
  white-space: nowrap;
}

.note-preview {
  display: -webkit-box;
  max-width: 220px;
  overflow: hidden;
  overflow-wrap: anywhere;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.note-edit-button {
  text-transform: none;
}

.redirect-cell {
  min-width: 340px;
  max-width: 440px;
}

.redirect-uri {
  display: block;
  max-width: 420px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
