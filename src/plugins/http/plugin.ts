import type { InternalPlugin } from "../types.js";
import { createServiceToken } from "#app/service-registry";
import { HttpFetcher } from "./http-fetcher.js";
import { registerHttpTools } from "./mcp.js";
import type { ProxyDispatcherFactory } from "#core/runtime/proxy-dispatcher";

export const HttpFetcherToken = createServiceToken<HttpFetcher>("http");

export function createHttpPlugin(
  namedProxy?: ProxyDispatcherFactory,
): InternalPlugin {
  let fetcher: HttpFetcher | null = null;

  return {
    id: "http",
    activate(context) {
      fetcher = new HttpFetcher(context.config.http, namedProxy, context.logs);
      context.services.provide(HttpFetcherToken, fetcher);
    },
    registerMcp(mcp) {
      if (!fetcher) throw new Error("HTTP plugin is not active");
      registerHttpTools(mcp, fetcher);
    },
    deactivate() {
      fetcher = null;
    },
  };
}
