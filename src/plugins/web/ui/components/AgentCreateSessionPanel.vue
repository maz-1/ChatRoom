<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { useLocale } from "vuetify";
import {
  api,
  type AgentModel,
  type AgentProviderId,
  type AgentProviderStatus,
  type AgentSession,
  type WorkspaceEntry,
} from "../api.js";
import {
  approvalPolicyMode,
  approvalsReviewerValue,
  defaultGranularApproval,
  modelMetadata,
  nativeConfig,
  nativeSetting,
  permissionModeValue,
  type ApprovalPolicyMode,
  type ApprovalsReviewer,
  type GranularApproval,
  type PermissionMode,
  type ReasoningSummary,
} from "../utils/agent-config.js";
import {
  agentProviderName,
  defaultAgentSettingLabel,
} from "../utils/agent-display.js";
import { errorMessage } from "../utils/errors.js";
import AgentSessionConfigFields from "./AgentSessionConfigFields.vue";
import WorkspaceSelect from "./WorkspaceSelect.vue";

const props = defineProps<{
  providers: AgentProviderStatus[];
  providerModels: Readonly<Record<AgentProviderId, AgentModel[]>>;
  workspaces: WorkspaceEntry[];
}>();

const emit = defineEmits<{
  created: [session: AgentSession];
}>();

const locale = useLocale();
const workspace = shallowRef<string | null>(null);
const provider = shallowRef<AgentProviderId>("");
const model = shallowRef<string | null>(null);
const reasoningEffort = shallowRef<string | null>(null);
const reasoningSummary = shallowRef<ReasoningSummary | null>(null);
const serviceTier = shallowRef<string | null>(null);
const approvalMode = shallowRef<ApprovalPolicyMode | null>(null);
const granular = shallowRef<GranularApproval>(defaultGranularApproval());
const reviewer = shallowRef<ApprovalsReviewer>(null);
const permissionMode = shallowRef<PermissionMode>(null);
const busy = shallowRef(false);
const error = shallowRef("");

const providerItems = computed(() =>
  props.providers.map((item) => ({
    title: agentProviderName(props.providers, item.id),
    value: item.id,
    disabled: !item.installed || !item.authenticated,
  })),
);
const modelItems = computed(() => [
  {
    title: locale.t("$vuetify.chatroom.agents.defaultModel"),
    value: null,
    subtitle: "",
  },
  ...(props.providerModels[provider.value] ?? []).map((item) => ({
    title: item.displayName,
    value: item.id,
    subtitle: item.description,
  })),
]);
const selectedModel = computed(() =>
  modelMetadata(props.providerModels, provider.value, model.value),
);
const approvalSetting = computed(() =>
  nativeSetting(props.providers, provider.value, "approvalPolicy"),
);
const reviewerSetting = computed(() =>
  nativeSetting(props.providers, provider.value, "approvalsReviewer"),
);
const permissionSetting = computed(() =>
  nativeSetting(props.providers, provider.value, "permissionMode"),
);
const canCreate = computed(() => {
  const status = props.providers.find((item) => item.id === provider.value);
  return Boolean(
    workspace.value && status?.installed && status.authenticated && !busy.value,
  );
});

watch(
  () => props.workspaces,
  (next) => {
    if (!workspace.value || !next.some((item) => item.root === workspace.value))
      workspace.value = next[0]?.root ?? null;
  },
  { immediate: true },
);

watch(
  () => props.providers,
  (next) => {
    const current = next.find((item) => item.id === provider.value);
    if (current?.installed && current.authenticated) return;
    provider.value =
      next.find((item) => item.installed && item.authenticated)?.id ??
      next[0]?.id ??
      "";
  },
  { immediate: true },
);

watch(provider, () => {
  model.value = null;
  reasoningEffort.value = null;
  reasoningSummary.value = null;
  serviceTier.value = null;
  resetNativeSettings();
});

watch(selectedModel, (next) => {
  if (
    !next?.reasoningEfforts?.some((item) => item.id === reasoningEffort.value)
  )
    reasoningEffort.value = null;
  if (!next?.reasoningSummaries?.includes(reasoningSummary.value ?? "none"))
    reasoningSummary.value = null;
  if (!next?.serviceTiers?.some((item) => item.id === serviceTier.value))
    serviceTier.value = null;
});

async function createSession() {
  if (!workspace.value || !canCreate.value) return;
  busy.value = true;
  error.value = "";
  try {
    const session = await api<AgentSession>("/agents/sessions", {
      method: "POST",
      body: JSON.stringify({
        workspaceRoot: workspace.value,
        provider: provider.value,
        model: model.value,
        reasoningEffort: reasoningEffort.value,
        reasoningSummary: reasoningSummary.value,
        serviceTier: serviceTier.value,
        ...nativeConfig(
          props.providers,
          provider.value,
          approvalMode.value,
          granular.value,
          reviewer.value,
          permissionMode.value,
        ),
      }),
      timeoutMs: 30_000,
    });
    emit("created", session);
  } catch (cause) {
    error.value = errorMessage(cause);
  } finally {
    busy.value = false;
  }
}

function resetNativeSettings() {
  approvalMode.value = approvalPolicyMode(
    null,
    approvalSetting.value?.defaultValue ?? null,
  );
  granular.value = defaultGranularApproval();
  reviewer.value = approvalsReviewerValue(
    null,
    reviewerSetting.value?.defaultValue ?? null,
  );
  permissionMode.value = permissionModeValue(
    null,
    permissionSetting.value?.defaultValue ?? null,
  );
}

function effortItems(value: AgentModel | null) {
  const defaultEffort = value?.reasoningEfforts.find(
    (item) => item.id === value.defaultReasoningEffort,
  );
  return [
    {
      title: defaultAgentSettingLabel(
        locale.t("$vuetify.chatroom.agents.defaultModel"),
        value?.defaultReasoningEffort,
      ),
      value: null,
      subtitle: defaultEffort?.description ?? "",
    },
    ...(value?.reasoningEfforts ?? []).map((item) => ({
      title: item.id,
      value: item.id,
      subtitle: item.description,
    })),
  ];
}
</script>

<template>
  <div class="agent-create">
    <WorkspaceSelect
      v-model="workspace"
      :items="workspaces"
      :label="locale.t('$vuetify.chatroom.agents.workspace')"
      density="compact"
      hide-details
    />
    <v-select
      v-model="provider"
      :items="providerItems"
      :label="locale.t('$vuetify.chatroom.agents.provider')"
      density="compact"
      hide-details
    />
    <v-select
      v-model="model"
      :items="modelItems"
      :label="locale.t('$vuetify.chatroom.agents.model')"
      density="compact"
      hide-details
    />
    <v-select
      v-if="selectedModel?.reasoningEfforts?.length"
      v-model="reasoningEffort"
      :items="effortItems(selectedModel)"
      :label="locale.t('$vuetify.chatroom.agents.reasoningEffort')"
      density="compact"
      hide-details
    >
      <template #item="{ props: itemProps, item }">
        <v-list-item v-bind="itemProps" :subtitle="item.subtitle" />
      </template>
    </v-select>
    <AgentSessionConfigFields
      v-model:reasoning-summary="reasoningSummary"
      v-model:service-tier="serviceTier"
      v-model:approval-mode="approvalMode"
      v-model:granular="granular"
      v-model:reviewer="reviewer"
      v-model:permission-mode="permissionMode"
      :model="selectedModel"
      :approval-setting="approvalSetting"
      :reviewer-setting="reviewerSetting"
      :permission-setting="permissionSetting"
      :disabled="busy"
    />
    <v-alert v-if="error" type="error" variant="tonal" density="compact">
      {{ error }}
    </v-alert>
    <v-btn
      color="primary"
      variant="tonal"
      :loading="busy"
      :disabled="!canCreate"
      @click="createSession"
    >
      {{ locale.t("$vuetify.chatroom.agents.create") }}
    </v-btn>
  </div>
</template>

<style scoped>
.agent-create {
  display: grid;
  gap: 10px;
  padding: 14px;
}
</style>
