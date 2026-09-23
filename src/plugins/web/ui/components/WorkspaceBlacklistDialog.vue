<script setup lang="ts">
import { useLocale } from "vuetify";

defineProps<{
  roots: string[];
  busy: boolean;
  loading: boolean;
  error: string;
}>();
defineEmits<{ restore: [root: string] }>();
const open = defineModel<boolean>({ required: true });
const locale = useLocale();
</script>

<template>
  <v-dialog v-model="open" max-width="640" scrollable>
    <v-card>
      <v-card-title class="d-flex align-center justify-space-between">
        <span>{{ locale.t("$vuetify.chatroom.workspaces.blacklist") }}</span>
        <v-btn
          icon="$mdiClose"
          variant="text"
          size="small"
          :aria-label="locale.t('$vuetify.chatroom.common.close')"
          @click="open = false"
        />
      </v-card-title>
      <v-divider />
      <v-card-text>
        <p class="mb-4">
          {{ locale.t("$vuetify.chatroom.workspaces.blacklistHelp") }}
        </p>
        <v-alert
          v-if="error"
          type="error"
          variant="tonal"
          density="compact"
          class="mb-4"
        >
          {{ error }}
        </v-alert>
        <v-progress-linear
          v-if="loading || busy"
          indeterminate
          color="primary"
        />
        <v-list v-if="roots.length" lines="two">
          <v-list-item v-for="root in roots" :key="root">
            <v-list-item-title>{{
              root.split(/[\\/]/).pop()
            }}</v-list-item-title>
            <div class="text-caption blacklist-path">{{ root }}</div>
            <template #append>
              <v-btn
                size="small"
                variant="text"
                color="primary"
                :disabled="busy || loading"
                @click="$emit('restore', root)"
              >
                {{ locale.t("$vuetify.chatroom.workspaces.restore") }}
              </v-btn>
            </template>
          </v-list-item>
        </v-list>
        <p v-else-if="!loading && !busy" class="text-medium-emphasis">
          {{ locale.t("$vuetify.chatroom.workspaces.blacklistEmpty") }}
        </p>
      </v-card-text>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.blacklist-path {
  overflow-wrap: anywhere;
}
</style>
