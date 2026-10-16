import { normalizeConfig, type NativeConfig } from "#js2ray";
import type { Js2rayConfig } from "#config/js2ray-config";
import { ChatRoomError } from "#core/errors/chatroom-error";

/** Import only an outbound/Bridge runtime; ChatRoom owns every ingress. */
export function prepareRuntimeConfig(
  raw: unknown,
  settings: Js2rayConfig,
  baseDir: string,
): NativeConfig {
  const config = normalizeConfig(raw, { baseDir });
  if (config.inbounds?.length || config.reverse?.portals?.length)
    throw new ChatRoomError(
      "INVALID_INPUT",
      "ChatRoom js2ray configuration must have no inbounds or reverse portals",
    );
  const outbounds = config.outbounds ?? [];
  if (
    settings.outboundTag &&
    !outbounds.some((item) => item.tag === settings.outboundTag)
  )
    throw new ChatRoomError(
      "INVALID_INPUT",
      "js2ray.outboundTag does not exist in the runtime configuration",
    );
  const bridges = config.reverse?.bridges ?? [];
  if (!settings.reverse.enabled) {
    if (bridges.length)
      throw new ChatRoomError(
        "INVALID_INPUT",
        "Enable js2ray.reverse to use the configured Bridge",
      );
    return config;
  }
  const reverse = settings.reverse;
  const bridge = bridges[0];
  if (bridges.length !== 1 || bridge?.tag !== reverse.bridgeTag)
    throw new ChatRoomError(
      "INVALID_INPUT",
      "Configure exactly one Bridge matching js2ray.reverse.bridgeTag",
    );
  const carrier = settings.outboundTag ?? outbounds[0]?.tag;
  const local = outbounds.findIndex(
    (item) => item.tag === reverse.localOutboundTag,
  );
  if (!carrier || carrier === reverse.localOutboundTag || local < 0)
    throw new ChatRoomError(
      "INVALID_INPUT",
      "js2ray reverse requires separate carrier and local service outbounds",
    );
  if (typeof config.routing === "function")
    throw new ChatRoomError(
      "INVALID_INPUT",
      "js2ray reverse requires declarative routing",
    );
  if (bridge.domain === reverse.targetHost)
    throw new ChatRoomError(
      "INVALID_INPUT",
      "The reverse service target must differ from its control domain",
    );

  outbounds[local] = {
    tag: reverse.localOutboundTag,
    protocol: "service",
    service: "chatroom",
    allow: { host: reverse.targetHost, port: reverse.targetPort },
  };
  // Bridge traffic is pinned to this exact service; imported rules cannot
  // redirect remote sessions to arbitrary local/private network destinations.
  config.routing = {
    ...config.routing,
    rules: [
      {
        type: "field",
        inboundTag: [bridge.tag],
        domain: [`full:${bridge.domain}`],
        outboundTag: carrier,
      },
      {
        type: "field",
        inboundTag: [bridge.tag],
        outboundTag: reverse.localOutboundTag,
      },
      ...(config.routing?.rules ?? []),
    ],
  };
  return config;
}
