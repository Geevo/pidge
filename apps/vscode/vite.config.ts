import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

import { fontLicenses } from "../../scripts/viteFontLicenses";

/**
 * The webview bundle. Names are fixed rather than hashed because the panel's
 * HTML references them directly, and nothing is loaded from a CDN: the webview
 * runs under a strict CSP that only allows this bundle.
 */
export default defineConfig({
  // vsce packages everything under media/, so emitting there is enough.
  plugins: [react(), fontLicenses()],
  build: {
    target: "es2022",
    outDir: "media",
    emptyOutDir: false,
    sourcemap: true,
    rollupOptions: {
      input: "src/webview/main.tsx",
      output: {
        entryFileNames: "webview.js",
        chunkFileNames: "webview-[name].js",
        // The panel HTML references webview.css by name; everything else
        // (fonts, images) gets a hashed filename so nothing collides.
        assetFileNames: (asset: { names?: string[]; name?: string }) => {
          const name = asset.names?.[0] ?? asset.name ?? "";
          return name.endsWith(".css") ? "webview.css" : "assets/[name]-[hash][extname]";
        },
      },
    },
  },
});
