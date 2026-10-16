import { ChatRoomError } from "#core/errors/chatroom-error";
import type { LogWriter } from "#core/logging/types";

function failureCode(error: unknown): string {
  const seen = new Set<unknown>();
  let code = "ERR_REQUEST";
  while (error instanceof Error && !seen.has(error)) {
    seen.add(error);
    const value = (error as { code?: unknown }).code;
    if (
      error.name === "TimeoutError" ||
      value === "ETIMEDOUT" ||
      value === "ERR_TIMEOUT" ||
      (typeof value === "string" && /^UND_ERR_\w*TIMEOUT$/.test(value))
    )
      return "ERR_TIMEOUT";
    if (typeof value === "string" && /^[A-Za-z0-9_-]{1,64}$/.test(value))
      code = value;
    error = error.cause;
  }
  return code;
}

export function logRequestFailure(
  logs: LogWriter | undefined,
  error: unknown,
  timeoutMs: number,
  proxy: string | undefined,
): void {
  const code = failureCode(error);
  logs?.warn(
    "http",
    "http.request_failed",
    code === "ERR_TIMEOUT" ? "HTTP request timed out" : "HTTP request failed",
    {
      code,
      timeoutMs,
      proxy:
        proxy === "js2ray" || proxy === "v2ray"
          ? "js2ray"
          : proxy === undefined
            ? "direct"
            : "custom",
    },
  );
}

export function translateRequestError(
  error: unknown,
  timeoutMs: number,
): ChatRoomError {
  if (error instanceof ChatRoomError) return error;
  if (failureCode(error) === "ERR_TIMEOUT")
    return new ChatRoomError(
      "PROCESS_FAILED",
      `HTTP request timed out after ${timeoutMs}ms`,
      undefined,
      { cause: error },
    );
  if (error instanceof Error) {
    const cause = error.cause as { code?: string } | undefined;
    const detail = cause?.code
      ? `${error.message} (${cause.code})`
      : error.message;
    return new ChatRoomError(
      "PROCESS_FAILED",
      `HTTP request failed: ${detail}`,
      undefined,
      { cause: error },
    );
  }
  return new ChatRoomError("PROCESS_FAILED", "HTTP request failed");
}
