<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { useDisplay, useLocale } from "vuetify";
import LoginView from "./components/LoginView.vue";
import WorkspaceDownloadPage from "./components/WorkspaceDownloadPage.vue";
import PasskeyManagerDialog from "./components/PasskeyManagerDialog.vue";
import { useAppNavigation, type View } from "./composables/useAppNavigation.js";
import { useAppPreferences } from "./composables/useAppPreferences.js";
import { useAuthSession } from "./composables/useAuthSession.js";
import { useRuntimeEvents } from "./composables/useRuntimeEvents.js";

const locale = useLocale();
const isDownloadPage = window.location.pathname === "/download";
const display = useDisplay();
const drawer = shallowRef(!display.smAndDown.value);
const navigation = useAppNavigation(!isDownloadPage);
const runtimeState = useRuntimeEvents();
const preferences = useAppPreferences();
const auth = useAuthSession({
  onAuthenticated: () => {
    if (!isDownloadPage) runtimeState.start();
  },
  onSignedOut: () => {
    runtimeState.clear();
    if (!isDownloadPage) navigation.reset();
    drawer.value = !display.smAndDown.value;
  },
});

const { view, current, definitions: nav, prefetch } = navigation;
const {
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
} = runtimeState;
const { themeMode, themeIcon, languageName, setThemeMode, setLocale } =
  preferences;
const {
  authenticated,
  canUsePasskeys,
  passkeyRegistered,
  passkeyBusy,
  loginBusy,
  passkeyDialog,
  passkeys,
  passkeyName,
  token,
  remember,
  loginError,
  passkeyError,
  login,
  loginWithPasskey,
  logout,
  openPasskeyManager,
  registerPasskey,
  removePasskey,
} = auth;

const currentTitle = computed(() =>
  locale.t(`$vuetify.chatroom.${current.value.titleKey}`),
);
const currentComponent = computed(() => current.value.component);
const currentProps = computed<Record<string, unknown>>(() => {
  switch (view.value) {
    case "processes":
      return {
        revision: processRevision.value,
        changes: processChanges.value,
      };
    case "operations":
      return {
        revision: operationRevision.value,
        changes: operationChanges.value,
      };
    case "computer":
      return {
        revision: computerRevision.value,
        changes: computerChanges.value,
      };
    case "agents":
      return {
        revision: agentRevision.value,
        changes: agentChanges.value,
      };
    case "mcp-servers":
      return { revision: mcpRevision.value };
    default:
      return {};
  }
});

watch(
  () => display.smAndDown.value,
  (compact) => {
    drawer.value = !compact;
  },
);

watch(
  currentTitle,
  (title) => {
    document.title = `${title} · ChatRoom`;
  },
  { immediate: true },
);

function navigate(next: View) {
  prefetch(next);
  navigation.navigate(next);
  if (display.smAndDown.value) drawer.value = false;
}

function formatUptime(totalMinutes: number): string {
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  return locale.t(
    "$vuetify.chatroom.runtime.uptimeHoursMinutes",
    hours,
    minutes,
  );
}
</script>

<template>
  <v-app
    class="app-shell"
    :class="{
      'app-shell--contained':
        authenticated &&
        !isDownloadPage &&
        (view === 'agents' ||
          (view === 'processes' && display.width.value > 1100) ||
          (view === 'workspaces' && display.width.value > 820) ||
          ((view === 'operations' || view === 'systemLogs') &&
            display.width.value > 1100)),
    }"
  >
    <template v-if="authenticated && !isDownloadPage">
      <v-navigation-drawer
        v-model="drawer"
        :temporary="display.smAndDown.value"
        class="app-drawer"
        border="e"
      >
        <v-list nav density="comfortable" class="nav-list">
          <v-list-item
            v-for="item in nav"
            :key="item.id"
            :active="view === item.id"
            :aria-current="view === item.id ? 'page' : undefined"
            :prepend-icon="item.icon"
            :title="locale.t(`$vuetify.chatroom.${item.titleKey}`)"
            rounded="lg"
            @mouseenter="prefetch(item.id)"
            @focus="prefetch(item.id)"
            @click="navigate(item.id)"
          />
        </v-list>

        <template #append>
          <div class="drawer-runtime">
            <div class="drawer-runtime-row">
              <span>{{ locale.t("$vuetify.chatroom.runtime.version") }}</span>
              <div class="drawer-version-value">
                <strong>{{ runtime?.version ?? "—" }}</strong>
                <v-chip
                  v-if="updateStatus?.updateAvailable"
                  size="x-small"
                  color="primary"
                  variant="tonal"
                  :href="updateStatus.releaseUrl ?? undefined"
                  target="_blank"
                  rel="noopener noreferrer"
                  :title="
                    locale.t(
                      '$vuetify.chatroom.runtime.updateTitle',
                      updateStatus.latestVersion ?? '',
                    )
                  "
                >
                  {{ locale.t("$vuetify.chatroom.runtime.updateAvailable") }}
                </v-chip>
              </div>
            </div>
            <div class="drawer-runtime-row">
              <span>{{
                locale.t("$vuetify.chatroom.runtime.mcpRequests")
              }}</span>
              <strong>{{ runtime?.mcpRequests ?? "—" }}</strong>
            </div>
            <div class="drawer-runtime-row">
              <span>{{ locale.t("$vuetify.chatroom.runtime.uptime") }}</span>
              <strong>{{
                runtime ? formatUptime(runtime.uptimeMinutes) : "—"
              }}</strong>
            </div>
          </div>
          <div class="drawer-actions">
            <v-btn
              v-if="canUsePasskeys"
              icon="$mdiFingerprint"
              variant="text"
              size="small"
              :aria-label="locale.t('$vuetify.chatroom.auth.passkeys')"
              @click="openPasskeyManager"
            />

            <v-menu location="top start">
              <template #activator="{ props }">
                <v-btn
                  v-bind="props"
                  icon="$mdiTranslate"
                  variant="text"
                  size="small"
                  :aria-label="locale.t('$vuetify.chatroom.common.language')"
                />
              </template>
              <v-list density="compact">
                <v-list-item
                  :title="locale.t('$vuetify.chatroom.common.languages.zhHans')"
                  :active="locale.current.value === 'zhHans'"
                  @click="setLocale('zhHans')"
                />
                <v-list-item
                  :title="locale.t('$vuetify.chatroom.common.languages.zhHant')"
                  :active="locale.current.value === 'zhHant'"
                  @click="setLocale('zhHant')"
                />
                <v-list-item
                  :title="locale.t('$vuetify.chatroom.common.languages.en')"
                  :active="locale.current.value === 'en'"
                  @click="setLocale('en')"
                />
              </v-list>
            </v-menu>

            <v-menu location="top start">
              <template #activator="{ props }">
                <v-btn
                  v-bind="props"
                  :icon="themeIcon"
                  variant="text"
                  size="small"
                  :aria-label="locale.t('$vuetify.chatroom.common.theme')"
                />
              </template>
              <v-list density="compact">
                <v-list-item
                  prepend-icon="$mdiThemeLightDark"
                  :title="locale.t('$vuetify.chatroom.theme.system')"
                  :active="themeMode === 'system'"
                  @click="setThemeMode('system')"
                />
                <v-list-item
                  prepend-icon="$mdiWeatherSunny"
                  :title="locale.t('$vuetify.chatroom.theme.light')"
                  :active="themeMode === 'light'"
                  @click="setThemeMode('light')"
                />
                <v-list-item
                  prepend-icon="$mdiWeatherNight"
                  :title="locale.t('$vuetify.chatroom.theme.dark')"
                  :active="themeMode === 'dark'"
                  @click="setThemeMode('dark')"
                />
              </v-list>
            </v-menu>

            <v-btn
              icon="$mdiLogout"
              variant="text"
              size="small"
              :aria-label="locale.t('$vuetify.chatroom.common.signOut')"
              @click="logout"
            />
          </div>
        </template>
      </v-navigation-drawer>

      <v-app-bar flat class="app-bar">
        <v-app-bar-nav-icon @click="drawer = !drawer" />
        <v-app-bar-title class="page-title">
          {{ currentTitle }}
        </v-app-bar-title>
        <template #append>
          <div
            class="connection-state"
            :data-state="connectionState"
            role="status"
            aria-live="polite"
          >
            <span class="connection-dot" aria-hidden="true" />
            <span class="connection-state-label">
              {{
                locale.t(
                  `$vuetify.chatroom.common.connection.${connectionState}`,
                )
              }}
              <template
                v-if="connectionState === 'connected' && latencyMs !== null"
              >
                · {{ latencyMs }} ms
              </template>
            </span>
          </div>
        </template>
      </v-app-bar>

      <v-main>
        <v-container fluid class="app-content">
          <component :is="currentComponent" v-bind="currentProps" />
        </v-container>
      </v-main>
    </template>

    <WorkspaceDownloadPage v-else-if="authenticated && isDownloadPage" />

    <LoginView
      v-else-if="authenticated === false"
      v-model:token="token"
      v-model:remember="remember"
      :can-use-passkeys="canUsePasskeys"
      :passkey-registered="passkeyRegistered"
      :passkey-busy="passkeyBusy"
      :login-busy="loginBusy"
      :login-error="loginError"
      :language-name="languageName"
      :theme-icon="themeIcon"
      @login="login"
      @passkey-login="loginWithPasskey"
      @locale="setLocale"
      @theme="setThemeMode"
    />

    <v-main v-else class="login-page">
      <v-progress-circular indeterminate color="primary" />
    </v-main>

    <PasskeyManagerDialog
      v-model:open="passkeyDialog"
      v-model:name="passkeyName"
      :passkeys="passkeys"
      :busy="passkeyBusy"
      :error="passkeyError"
      @add="registerPasskey"
      @remove="removePasskey"
    />
  </v-app>
</template>
