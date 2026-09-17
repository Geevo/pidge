import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

import { fontLicenses } from "../../scripts/viteFontLicenses";

// Tauri serves this over a fixed port in development and from disk in a build.
export default defineConfig({
  plugins: [
    react(),
    // The fonts are embedded in the frontend bundle, so their licences go with
    // them. An installed app gets them as plain files too: tauri.conf.json
    // lists the same sources under bundle.resources.
    fontLicenses(),
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
