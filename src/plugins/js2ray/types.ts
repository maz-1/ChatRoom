export interface Js2rayStatus {
  enabled: boolean;
  state: string;
  listeners: number;
  listenerEndpoints: Array<{ address: string; port: number }>;
  reverse: Array<{
    tag: string;
    role: string;
    transports: number;
    active: number;
    sessions: number;
    connecting?: boolean;
  }>;
  lastError: { code: string; phase: string } | null;
}
