import { resolve } from "node:path";

import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

import { fontLicenses } from "../../scripts/viteFontLicenses";

// Tauri serves this over a fixed port in development and from disk in a build.
export default defineConfig({
  plugins: [
    react(),
    // The fonts are embedded in the frontend bundle, so their licences go with
    // them. `copyTo` also drops them where tauri.conf.json's bundle.resources
    // can pick them up, so an installed app has them as plain files on disk.
    fontLicenses({ copyTo: resolve(import.meta.dirname, "src-tauri", "licenses") }),
  ],
  clearScreen: false,
  server: {
    port: 5173,
    strictPort: true,
  },
  build: {
    target: "es2022",
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: true,
  },
});
