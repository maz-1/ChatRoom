import assert from "node:assert/strict";
import test from "node:test";
import { Js2rayError } from "#js2ray";
import {
  dispatcherCleanup,
  logJs2rayDiagnostic,
} from "../../src/plugins/js2ray/diagnostics.js";
import type { LogWriter } from "../../src/core/logging/types.js";

test("js2ray only silences identifiable disposal and clean shutdown cancellation", () => {
  const records: unknown[] = [];
  const capture: LogWriter["warn"] = (...args) => {
    records.push(args);
  };
  const logs: LogWriter = {
    debug: capture,
    info: capture,
    warn: capture,
    error: capture,
  };
  const cancelled = (cause?: unknown) =>
    new Js2rayError("ERR_CANCELLED", "private-node-message", {
      phase: "transport",
      cause,
    });
  logJs2rayDiagnostic(logs, cancelled(dispatcherCleanup), false);
  logJs2rayDiagnostic(
    logs,
    cancelled(
      new Js2rayError("ERR_CANCELLED", "cleanup", { cause: dispatcherCleanup }),
    ),
    false,
  );
  logJs2rayDiagnostic(logs, cancelled(), true);
  logJs2rayDiagnostic(
    logs,
    cancelled(new DOMException("shutdown", "AbortError")),
    true,
  );
  assert.equal(records.length, 0);

  for (const closing of [false, true]) {
    const causes = [
      new DOMException("private-timeout", "TimeoutError"),
      new Js2rayError("ERR_TIMEOUT", "private-timeout"),
      new Js2rayError("ERR_TRANSPORT", "private-connection-error"),
      new Error("private-unknown-cause"),
    ];
    for (const cause of causes)
      logJs2rayDiagnostic(logs, cancelled(cause), closing);
    logJs2rayDiagnostic(
      logs,
      new Js2rayError("ERR_TLS", "private-cert-error"),
      closing,
    );
  }
  assert.equal(records.length, 10);
  logJs2rayDiagnostic(logs, cancelled(), false);
  assert.equal(
    records.length,
    11,
    "An unexplained runtime cancellation must remain visible",
  );
  assert.doesNotMatch(JSON.stringify(records), /private-/);
});
