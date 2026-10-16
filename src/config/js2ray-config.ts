import { z } from "zod";

export function defaultJs2rayConfig() {
  return {
    enabled: false,
    configPath: null as string | null,
    outboundTag: null as string | null,
    reverse: {
      enabled: false,
      bridgeTag: "bridge",
      localOutboundTag: "mcp-local",
      targetHost: "127.0.0.1",
      targetPort: 8765,
    },
  };
}

export type Js2rayConfig = ReturnType<typeof defaultJs2rayConfig>;

export function js2rayConfigSchema() {
  const defaults = defaultJs2rayConfig();
  const tag = z.string().regex(/^[A-Za-z0-9_.-]{1,128}$/);
  return z
    .object({
      enabled: z.boolean().default(false),
      configPath: z.string().min(1).nullable().default(null),
      outboundTag: tag.nullable().default(null),
      reverse: z
        .object({
          enabled: z.boolean().default(false),
          bridgeTag: tag.default(defaults.reverse.bridgeTag),
          localOutboundTag: tag.default(defaults.reverse.localOutboundTag),
          targetHost: z.string().min(1).default(defaults.reverse.targetHost),
          targetPort: z
            .number()
            .int()
            .min(1)
            .max(65535)
            .default(defaults.reverse.targetPort),
        })
        .strict()
        .default(defaults.reverse),
    })
    .strict()
    .default(defaults);
}
