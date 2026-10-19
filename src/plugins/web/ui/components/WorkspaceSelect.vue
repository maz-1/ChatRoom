<script setup lang="ts">
import { computed, nextTick, shallowRef, watch } from "vue";
import { useLocale } from "vuetify";
import type { WorkspaceEntry } from "../api.js";

defineOptions({ inheritAttrs: false });
const props = defineProps<{
  items: WorkspaceEntry[];
  label?: string;
  loading?: boolean;
  disabled?: boolean;
}>();
const selected = defineModel<string | null>({ required: true });
const locale = useLocale();
const open = shallowRef(false);
const openedRoots = shallowRef<string[]>([]);
const field = shallowRef<{ focus: () => void } | null>(null);
const selectedName = computed(
  () => props.items.find((item) => item.root === selected.value)?.name ?? "",
);
const groups = computed(() => {
  const grouped = new Map<string, WorkspaceEntry[]>();
  for (const item of props.items) {
    const path = item.root.replace(/[\\/]+$/, "");
    const separator = Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\"));
    const root =
      separator === 0 || (separator === 2 && path[1] === ":")
        ? path.slice(0, separator + 1)
        : path.slice(0, separator);
    const children = grouped.get(root) ?? [];
    children.push(item);
    grouped.set(root, children);
  }
  return [...grouped].map(([root, children]) => ({
    root,
    value: `root:${root}`,
    children,
  }));
});

watch(open, (value) => {
  if (!value) return;
  const group =
    groups.value.find((item) =>
      item.children.some((child) => child.root === selected.value),
    ) ?? groups.value[0];
  openedRoots.value = group ? [group.value] : [];
});
watch(
  () => props.disabled,
  (disabled) => {
    if (disabled) open.value = false;
  },
);

function toggle() {
  if (!props.disabled) open.value = !open.value;
}

async function select(root: string) {
  if (props.disabled) return;
  selected.value = root;
  open.value = false;
  await nextTick();
  field.value?.focus();
}
</script>

<template>
  <v-menu
    v-model="open"
    :disabled="disabled"
    :close-on-content-click="false"
    :activator-props="{ 'aria-haspopup': 'listbox' }"
    max-height="400"
    max-width="min(44rem, calc(100vw - 24px))"
  >
    <template #activator="{ props: activatorProps }">
      <v-text-field
        ref="field"
        v-bind="{ ...$attrs, ...activatorProps }"
        :model-value="selectedName"
        :label="label"
        :aria-label="
          label || locale.t('$vuetify.chatroom.workspaces.workspace')
        "
        :loading="loading"
        :disabled="disabled"
        :active="open"
        :append-inner-icon="open ? '$collapse' : '$expand'"
        prepend-inner-icon="$mdiFolderOutline"
        class="workspace-select"
        density="compact"
        variant="outlined"
        role="combobox"
        readonly
        hide-details
        @keydown.enter.prevent="toggle"
        @keydown.space.prevent="toggle"
      />
    </template>

    <v-list
      v-model:opened="openedRoots"
      :selected="selected ? [selected] : []"
      :aria-label="label || locale.t('$vuetify.chatroom.workspaces.workspace')"
      open-strategy="multiple"
      density="compact"
      class="workspace-select-list"
    >
      <v-list-group
        v-for="group in groups"
        :key="group.value"
        :value="group.value"
        prepend-icon="$mdiFolderOutline"
      >
        <template #activator="{ props: groupProps, isOpen }">
          <v-list-item
            v-bind="groupProps"
            :title="group.root"
            :aria-expanded="isOpen"
            class="workspace-root-item"
          />
        </template>
        <v-list-item
          v-for="item in group.children"
          :key="item.root"
          :value="item.root"
          :title="item.name"
          :active="item.root === selected"
          :append-icon="item.root === selected ? '$complete' : undefined"
          color="primary"
          @click="select(item.root)"
        />
      </v-list-group>
      <v-list-item
        v-if="!groups.length"
        :title="locale.t('$vuetify.noDataText')"
        disabled
      />
    </v-list>
  </v-menu>
</template>

<style scoped>
.workspace-select :deep(input) {
  cursor: pointer;
}

.workspace-select-list :deep(.workspace-root-item .v-list-item-title) {
  font-family: "SFMono-Regular", Consolas, "Liberation Mono", monospace;
  font-size: 12px;
  font-weight: 600;
  overflow-wrap: anywhere;
  white-space: normal;
}
</style>
