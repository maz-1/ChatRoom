import type { PluginMcpRegistrar } from "../../mcp/server/plugin-mcp-registrar.js";
import { openWorldMutation } from "../../mcp/server/tool-support.js";
import type { HttpFetcher } from "./http-fetcher.js";
import { httpRequestSchema, httpRequestResultSchema } from "./types.js";

export function registerHttpTools(
  mcp: PluginMcpRegistrar,
  fetcher: HttpFetcher,
): void {
  mcp.registerTool(
    "http_request",
    {
      title: "Send HTTP request",
      description:
        'Send a direct HTTP/HTTPS request from the user\'s machine and return the status, headers, and a size-bounded body. Use it to call web APIs, download pages, or reach local services. Optionally set proxy to an http://, https://, or socks5:// proxy URL without authentication; omit proxy for a direct connection. The selected proxy is also used for redirects. Provide custom headers (e.g. authorization, content-type) as needed; send JSON as a text body with a content-type header, and binary payloads as standard base64 with bodyEncoding "base64". Redirects are followed automatically and the final URL is reported. The response body limit defaults to 1 MiB (1,048,576 bytes), measured before base64 encoding, and is configurable via http.maxResponseBytes up to 16 MiB. Responses larger than this limit are truncated with truncated set to true and are incomplete downloads; binary responses are returned as base64 unless responseFormat selects text, base64, or omit (headers/status only).',
      inputSchema: httpRequestSchema,
      outputSchema: httpRequestResultSchema,
      annotations: openWorldMutation,
      action: (input) => input.method.toLowerCase(),
    },
    (input, execution) =>
      fetcher.request(input).then((response) => ({
        ...response,
        operationId: execution.operationId,
      })),
  );
}
