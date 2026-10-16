import type { Js2rayError } from "#js2ray";
import type { LogWriter } from "#core/logging/types";

// Identity, rather than an error code or message, marks our own disposal.
export const dispatcherCleanup = Symbol("ChatRoom HTTP dispatcher cleanup");

export function logJs2rayDiagnostic(
  logs: LogWriter,
  error: Js2rayError,
  closing: boolean,
): void {
  if (error.code === "ERR_CANCELLED" && expectedCancellation(error, closing))
    return;
  logs.warn(
    "js2ray",
    "js2ray.diagnostic",
    "js2ray reported a connection error",
    {
      code: safeIdentifier(error.code, "ERR_INTERNAL"),
      phase: safeIdentifier(error.phase, "runtime"),
    },
  );
}

function expectedCancellation(error: Error, closing: boolean): boolean {
  const seen = new Set<unknown>();
  let cause: unknown = error;
  while (cause !== undefined && !seen.has(cause)) {
    if (cause === dispatcherCleanup) return true;
    seen.add(cause);
    if (!(cause instanceof Error)) return false;
    const code = (cause as { code?: unknown }).code;
    if (
      code !== "ERR_CANCELLED" &&
      code !== "ABORT_ERR" &&
      !(cause instanceof DOMException && cause.name === "AbortError")
    )
      return false;
    cause = cause.cause;
  }
  return closing && cause === undefined;
}

export function safeIdentifier(value: unknown, fallback: string): string {
  return typeof value === "string" && /^[A-Za-z0-9_-]{1,64}$/.test(value)
    ? value
    : fallback;
}
