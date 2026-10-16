import { z } from "zod";

export const HTTP_METHODS = [
  "GET",
  "POST",
  "PUT",
  "PATCH",
  "DELETE",
  "HEAD",
  "OPTIONS",
] as const;

export const httpRequestSchema = z.object({
  url: z.string().min(1),
  proxy: z
    .string()
    .min(1)
    .optional()
    .describe(
      "Set js2ray (or v2ray) for the configured in-process runtime, or use an http://, https://, or socks5:// proxy URL without authentication. Omit for a direct connection. js2ray follows its configured routing rules.",
    ),
  method: z.enum(HTTP_METHODS).default("GET"),
  headers: z.record(z.string(), z.string()).optional(),
  body: z.string().optional(),
  bodyEncoding: z.enum(["text", "base64"]).default("text"),
  timeoutMs: z.number().int().positive().optional(),
  responseFormat: z.enum(["auto", "text", "base64", "omit"]).default("auto"),
});

export const httpResponseSchema = z.object({
  status: z.number().int(),
  statusText: z.string(),
  url: z.string(),
  headers: z.record(z.string(), z.string()),
  body: z.string(),
  bodyEncoding: z.enum(["text", "base64"]),
  bodyBytes: z.number().int().nonnegative(),
  truncated: z.boolean(),
});

export const httpRequestResultSchema = httpResponseSchema.extend({
  operationId: z.string(),
});

export type HttpRequestInput = z.infer<typeof httpRequestSchema>;
export type HttpResponse = z.infer<typeof httpResponseSchema>;
