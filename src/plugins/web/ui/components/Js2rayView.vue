<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import { useLocale } from "vuetify";
import { api, type Js2rayStatus } from "../api.js";
import { errorMessage } from "../utils/errors.js";

const locale = useLocale();
const status = ref<Js2rayStatus | null>(null);
const loading = ref(false);
const error = ref("");
const lifetime = new AbortController();
let timer: ReturnType<typeof setInterval> | undefined;
const text = (key: string) => locale.t(`$vuetify.chatroom.js2ray.${key}`);
async function refresh() {
  if (loading.value) return;
  loading.value = true;
  try {
    status.value = await api<Js2rayStatus>("/js2ray", {
      signal: lifetime.signal,
    });
    error.value = "";
  } catch (cause) {
    if (!lifetime.signal.aborted) {
      status.value = null;
      error.value = errorMessage(cause);
    }
  } finally {
    loading.value = false;
  }
}
onMounted(() => {
  void refresh();
  timer = setInterval(() => void refresh(), 15000);
});
onBeforeUnmount(() => {
  lifetime.abort();
  clearInterval(timer);
});
</script>

<template>
  <section>
    <div class="d-flex align-center justify-space-between mb-4 ga-3">
      <h2>{{ text("title") }}</h2>
      <v-btn variant="tonal" :loading="loading" @click="refresh">{{
        text("refresh")
      }}</v-btn>
    </div>
    <p class="text-medium-emphasis mb-4">{{ text("description") }}</p>
    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{
      error
    }}</v-alert>
    <v-card v-if="status" variant="outlined" class="mb-4">
      <v-list>
        <v-list-item :title="text('state')">
          <template #append
            ><v-chip
              :color="status.state === 'running' ? 'success' : undefined"
              >{{ text(`states.${status.state}`) }}</v-chip
            ></template
          >
        </v-list-item>
        <v-list-item
          :title="text('listeners')"
          :subtitle="String(status.listeners)"
        />
        <v-list-item
          v-if="status.lastError"
          :title="text('error')"
          :subtitle="`${status.lastError.code} · ${status.lastError.phase}`"
        />
      </v-list>
    </v-card>
    <h3 v-if="status" class="mb-3">{{ text("reverse") }}</h3>
    <v-table v-if="status?.reverse.length" class="mb-4">
      <thead>
        <tr>
          <th>{{ text("tag") }}</th>
          <th>{{ text("role") }}</th>
          <th>{{ text("active") }}</th>
          <th>{{ text("sessions") }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="bridge in status.reverse" :key="bridge.tag">
          <td>{{ bridge.tag }}</td>
          <td>{{ bridge.role }}</td>
          <td>{{ bridge.active }} / {{ bridge.transports }}</td>
          <td>{{ bridge.sessions }}</td>
        </tr>
      </tbody>
    </v-table>
    <p v-else-if="status" class="text-medium-emphasis mb-4">
      {{ text("noReverse") }}
    </p>
    <p class="text-medium-emphasis">{{ text("hint") }}</p>
  </section>
</template>
