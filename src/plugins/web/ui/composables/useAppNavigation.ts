import {
  computed,
  defineAsyncComponent,
  onBeforeUnmount,
  onMounted,
  shallowRef,
  type Component,
} from "vue";

import AsyncViewStatus from "../components/AsyncViewStatus.vue";

export type View =
  | "workspaces"
  | "agents"
  | "processes"
  | "computer"
  | "mcp-servers"
  | "tools"
  | "oauth-clients"
  | "cloud"
  | "js2ray"
  | "operations"
  | "systemLogs";

interface ViewDefinition {
  id: View;
  path: string;
  titleKey: string;
  icon: string;
  component: Component;
  preload: () => Promise<unknown>;
}

function viewDefinition(
  definition: Omit<ViewDefinition, "component" | "preload"> & {
    load: () => Promise<{ default: Component }>;
  },
): ViewDefinition {
  let pending: Promise<{ default: Component }> | null = null;
  const load = () => {
    if (!pending)
      pending = definition.load().catch((error) => {
        pending = null;
        throw error;
      });
    return pending;
  };
  return {
    id: definition.id,
    path: definition.path,
    titleKey: definition.titleKey,
    icon: definition.icon,
    component: defineAsyncComponent({
      loader: load,
      loadingComponent: AsyncViewStatus,
      errorComponent: AsyncViewStatus,
      delay: 120,
    }),
    preload: load,
  };
}

const definitions: readonly ViewDefinition[] = [
  viewDefinition({
    id: "workspaces",
    path: "/",
    titleKey: "nav.workspaces",
    icon: "$mdiFolderOutline",
    load: () => import("../components/WorkspacesView.vue"),
  }),
  viewDefinition({
    id: "agents",
    path: "/agents",
    titleKey: "nav.agents",
    icon: "$mdiMessageTextOutline",
    load: () => import("../components/AgentView.vue"),
  }),
  viewDefinition({
    id: "processes",
    path: "/processes",
    titleKey: "nav.processes",
    icon: "$mdiConsoleLine",
    load: () => import("../components/ProcessesView.vue"),
  }),
  viewDefinition({
    id: "computer",
    path: "/computer",
    titleKey: "nav.computer",
    icon: "$mdiMonitor",
    load: () => import("../components/ComputerView.vue"),
  }),
  viewDefinition({
    id: "mcp-servers",
    path: "/mcp-servers",
    titleKey: "nav.mcp",
    icon: "$mdiServerNetwork",
    load: () => import("../components/McpServersView.vue"),
  }),
  viewDefinition({
    id: "tools",
    path: "/tools",
    titleKey: "nav.mcpTools",
    icon: "$mdiTuneVariant",
    load: () => import("../components/McpToolsView.vue"),
  }),
  viewDefinition({
    id: "oauth-clients",
    path: "/oauth-clients",
    titleKey: "nav.oauthClients",
    icon: "$mdiKeyOutline",
    load: () => import("../components/OAuthClientsView.vue"),
  }),
  viewDefinition({
    id: "cloud",
    path: "/cloud",
    titleKey: "nav.cloud",
    icon: "$mdiCloudOutline",
    load: () => import("../components/CloudView.vue"),
  }),
  viewDefinition({
    id: "js2ray",
    path: "/js2ray",
    titleKey: "nav.js2ray",
    icon: "$v2ray",
    load: () => import("../components/Js2rayView.vue"),
  }),
  viewDefinition({
    id: "operations",
    path: "/operations",
    titleKey: "nav.operations",
    icon: "$mdiTextBoxOutline",
    load: () => import("../components/OperationsView.vue"),
  }),
  viewDefinition({
    id: "systemLogs",
    path: "/system-logs",
    titleKey: "nav.systemLogs",
    icon: "$mdiTextSearch",
    load: () => import("../components/SystemLogsView.vue"),
  }),
] as const;

const byId = new Map(definitions.map((item) => [item.id, item]));
const byPath = new Map(definitions.map((item) => [item.path, item.id]));

export function useAppNavigation(enabled = true) {
  const view = shallowRef<View>(viewFromPath());
  const current = computed(() => byId.get(view.value)!);

  onMounted(() => {
    if (!enabled) return;
    syncFromPath();
    window.addEventListener("popstate", syncFromPath);
  });
  onBeforeUnmount(() => window.removeEventListener("popstate", syncFromPath));

  function navigate(next: View) {
    view.value = next;
    const path = byId.get(next)!.path;
    if (window.location.pathname !== path)
      window.history.pushState(null, "", path);
  }

  function reset() {
    view.value = "workspaces";
    window.history.replaceState(null, "", "/");
  }

  function prefetch(next: View) {
    void byId
      .get(next)
      ?.preload()
      .catch(() => undefined);
  }

  function syncFromPath() {
    const resolved = byPath.get(window.location.pathname);
    if (!resolved) {
      view.value = "workspaces";
      window.history.replaceState(null, "", "/");
      return;
    }
    view.value = resolved;
  }

  return {
    view,
    current,
    definitions,
    navigate,
    reset,
    prefetch,
  };
}

function viewFromPath(): View {
  return byPath.get(window.location.pathname) ?? "workspaces";
}
