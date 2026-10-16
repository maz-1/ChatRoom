import * as undici from "undici";
import { createUndiciDispatcher, type Runtime } from "#js2ray";
import { dispatcherCleanup } from "./diagnostics.js";

export function createHttpDispatcher(
  runtime: Runtime,
  timeoutMs: number,
  outboundTag: string | null,
): undici.Dispatcher {
  const lifetime = new AbortController();
  const dispatcher = createUndiciDispatcher(runtime, {
    undici,
    timeoutMs,
    signal: lifetime.signal,
    ...(outboundTag ? { outboundTag } : {}),
  });
  const destroy = dispatcher.destroy.bind(dispatcher);
  dispatcher.destroy = function (
    error?: Error | null | (() => void),
    callback?: () => void,
  ): Promise<void> | void {
    lifetime.abort(error instanceof Error ? error : dispatcherCleanup);
    if (typeof error === "function") return destroy(error);
    if (callback) return destroy(error ?? null, callback);
    return destroy(error ?? null);
  } as undici.Dispatcher["destroy"];
  return dispatcher;
}
