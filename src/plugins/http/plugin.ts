import type { InternalPlugin } from "../types.js";
import { createServiceToken } from "../types.js";
import { HttpFetcher } from "./http-fetcher.js";
import { registerHttpTools } from "./mcp.js";

export const HttpFetcherToken = createServiceToken<HttpFetcher>("http");

export function createHttpPlugin(): InternalPlugin {
  let fetcher: HttpFetcher | null = null;

  return {
    id: "http",
    activate(context) {
      fetcher = new HttpFetcher(context.config.http);
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
