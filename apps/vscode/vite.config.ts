import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

/**
 * The webview bundle. Names are fixed rather than hashed because the panel's
 * HTML references them directly, and nothing is loaded from a CDN: the webview
 * runs under a strict CSP that only allows this bundle.
 */
export default defineConfig({
  plugins: [react()],
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
        assetFileNames: "webview.[ext]",
      },
    },
  },
});
