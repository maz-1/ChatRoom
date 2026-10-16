import { EventEmitter } from "node:events";

export type RuntimeEvent =
  | {
      type: "operation";
      operationId: string;
      pluginId: string;
    }
  | { type: "operations-cleared" }
  | {
      type: "process";
      processId: string;
      output?: boolean;
      deleted?: boolean;
    }
  | { type: "computer-settings" }
  | { type: "computer-snapshot"; snapshotId: string }
  | {
      type: "agent";
      sessionId: string;
      turnIds?: string[];
      itemIds?: string[];
      deleted?: boolean;
    }
  | { type: "mcp-servers" };

export class RuntimeEventBus {
  private readonly emitter = new EventEmitter();

  emit(event: RuntimeEvent): void {
    this.emitter.emit("event", event);
  }

  subscribe(listener: (event: RuntimeEvent) => void): () => void {
    this.emitter.on("event", listener);
    return () => this.emitter.off("event", listener);
  }
}
