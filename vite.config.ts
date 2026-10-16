/** Vite/Vue development and production build configuration for the embedded WebUI. */
import { realpathSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import vuetify from "vite-plugin-vuetify";

const projectRoot = realpathSync(dirname(fileURLToPath(import.meta.url)));
const webRoot = resolve(projectRoot, "src/plugins/web/ui");

export default defineConfig({
  plugins: [vue(), vuetify({ autoImport: true })],
  resolve: {
    conditions: [
      "chatroom-source",
      "module",
      "browser",
      "development|production",
    ],
  },
  root: webRoot,
  publicDir: false,
  build: {
    outDir: resolve(projectRoot, "dist/web"),
    emptyOutDir: false,
  },
  server: {
    proxy: {
      "^/api(?:/|$)": "http://127.0.0.1:8765",
      "^/mcp(?:/|$)": "http://127.0.0.1:8765",
    },
  },
});
