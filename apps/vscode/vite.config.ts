import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

import { fontLicenses } from "../../scripts/viteFontLicenses";

/**
 * The webview bundles: a request tab and the side bar. Names are fixed rather
 * than hashed because the pages' HTML references them directly, and nothing is loaded from a CDN: the webview
 * runs under a strict CSP that only allows this bundle.
 */
export default defineConfig({
  // vsce packages everything under media/, so emitting there is enough.
  plugins: [react(), fontLicenses()],
  // Relative, so fonts and images resolve next to the bundle: a webview
  // serves it from a resource URI, not from the root of an origin.
  base: "./",
  build: {
    target: "es2022",
    outDir: "media",
    emptyOutDir: false,
    sourcemap: true,
    // One stylesheet for both pages: they share the UI's styles.
    cssCodeSplit: false,
    rollupOptions: {
      input: { webview: "src/webview/main.tsx", sidebar: "src/webview/sidebar.tsx" },
      output: {
        entryFileNames: "[name].js",
        chunkFileNames: "webview-[name].js",
        // The pages reference webview.css by name; everything else
        // (fonts, images) gets a hashed filename so nothing collides.
        assetFileNames: (asset: { names?: string[]; name?: string }) => {
          const name = asset.names?.[0] ?? asset.name ?? "";
          return name.endsWith(".css") ? "webview.css" : "assets/[name]-[hash][extname]";
        },
      },
    },
  },
});
