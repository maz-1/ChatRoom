import { createServiceToken, type InternalPlugin } from "#plugins/types";
import { Js2rayController } from "./controller.js";

export const Js2rayService = createServiceToken<Js2rayController>("js2ray");

export function createJs2rayPlugin(): InternalPlugin {
  let controller: Js2rayController | null = null;
  return {
    id: "js2ray",
    activate(context) {
      controller = new Js2rayController(context.config.js2ray, context.logs);
      context.services.provide(Js2rayService, controller);
    },
    async deactivate() {
      await controller?.stop();
      controller = null;
    },
  };
}
