<script setup lang="ts">
import { shallowRef, watch } from "vue";
import { useLocale } from "vuetify";
import {
  api,
  type WorkspaceSkillSettings,
  type WorkspaceSkill,
} from "../api.js";
import { errorMessage } from "../utils/errors.js";
import { createRequestGate } from "../utils/requests.js";

const props = defineProps<{ root: string }>();
const info = shallowRef<WorkspaceSkillSettings | null>(null);
const loading = shallowRef(false);
const error = shallowRef("");
const saving = shallowRef<string | null>(null);
const locale = useLocale();
const loadRequests = createRequestGate();

watch(
  () => props.root,
  () => {
    info.value = null;
    saving.value = null;
    void load();
  },
  { immediate: true },
);

async function load() {
  const request = loadRequests.begin();
  const root = props.root;
  loading.value = true;
  error.value = "";
  try {
    const next = await api<WorkspaceSkillSettings>(
      `/workspace/skills?root=${encodeURIComponent(root)}`,
      { signal: request.signal },
    );
    if (!loadRequests.isCurrent(request) || props.root !== root) return;
    info.value = next;
  } catch (cause) {
    if (loadRequests.isCurrent(request)) error.value = errorMessage(cause);
  } finally {
    if (loadRequests.isCurrent(request)) loading.value = false;
  }
}

async function setEnabled(skillPath: string, enabled: boolean) {
  if (loading.value || saving.value !== null || !info.value) return;
  const request = loadRequests.begin();
  const root = props.root;
  saving.value = skillPath;
  error.value = "";
  try {
    const next = await api<WorkspaceSkillSettings>("/workspace/skills", {
      method: "PATCH",
      body: JSON.stringify({ root, path: skillPath, enabled }),
      signal: request.signal,
    });
    if (loadRequests.isCurrent(request) && props.root === root)
      info.value = next;
  } catch (cause) {
    if (loadRequests.isCurrent(request)) error.value = errorMessage(cause);
  } finally {
    if (loadRequests.isCurrent(request)) saving.value = null;
  }
}

function sourceLabel(skill: WorkspaceSkill): string {
  if (skill.path.startsWith(".claude/skills/"))
    return locale.t("$vuetify.chatroom.skills.sources.claude");
  if (skill.path.startsWith(".chatroom/skills/"))
    return locale.t("$vuetify.chatroom.skills.sources.chatRoom");
  return locale.t("$vuetify.chatroom.skills.sources.agent");
}
</script>

<template>
  <div class="workspace-skills-pane">
    <div class="panel-header">
      <div class="min-w-0">
        <div class="panel-title">
          {{ locale.t("$vuetify.chatroom.workspaces.skills") }}
        </div>
        <div class="panel-subtitle">
          {{ locale.t("$vuetify.chatroom.skills.settingsHelp") }}
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
    <v-alert v-if="error" type="error" variant="tonal" density="compact">
      {{ error }}
    </v-alert>
    <div v-if="info" class="px-5 pb-3 text-caption muted text-break">
      {{ info.configPath }}
    </div>
    <div v-if="info?.skills.length" class="workspace-skill-grid">
      <div
        v-for="skill in info.skills"
        :key="skill.path"
        class="workspace-skill-card"
      >
        <div class="workspace-skill-header">
          <div class="workspace-skill-title">
            <v-icon icon="$mdiPuzzleOutline" size="18" />
            <strong>{{ skill.name }}</strong>
          </div>
          <v-chip size="x-small" variant="tonal">
            {{ sourceLabel(skill) }}
          </v-chip>
        </div>
        <div class="workspace-skill-description">
          {{
            skill.description ||
            locale.t("$vuetify.chatroom.skills.noDescription")
          }}
        </div>
        <div class="workspace-skill-path">{{ skill.path }}</div>
        <div class="d-flex align-center justify-space-between ga-3 mt-3">
          <v-chip
            size="small"
            variant="tonal"
            :color="skill.enabled ? 'success' : 'secondary'"
            >{{
              locale.t(
                skill.enabled
                  ? "$vuetify.chatroom.workspaces.mcpAvailable"
                  : "$vuetify.chatroom.workspaces.mcpWorkspaceDisabled",
              )
            }}</v-chip
          >
          <v-switch
            :model-value="skill.enabled"
            :loading="saving === skill.path"
            :disabled="loading || saving !== null"
            :aria-label="
              locale.t('$vuetify.chatroom.skills.toggle', skill.name)
            "
            color="primary"
            hide-details
            inset
            density="compact"
            @update:model-value="setEnabled(skill.path, Boolean($event))"
          />
        </div>
      </div>
    </div>
    <v-empty-state
      v-else-if="!loading && !error"
      icon="$mdiPuzzleOutline"
      :title="locale.t('$vuetify.chatroom.skills.empty')"
    />
  </div>
</template>
