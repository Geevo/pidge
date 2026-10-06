import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

import { fontLicenses } from "../../scripts/viteFontLicenses";

// The desktop host serves this from its own `app://` scheme in a build, and
// loads the dev server over a fixed port in development.
export default defineConfig({
  plugins: [
    react(),
    // The fonts are embedded in the frontend bundle, so their licences go with
    // them. The desktop executable embeds the whole dist folder, licences too.
    fontLicenses(),
  ],
  // Relative, so the bundle loads the same from any origin the host picks.
  base: "./",
  clearScreen: false,
  server: {
    port: 5173,
    strictPort: true,
  },
  build: {
    target: "es2022",
    outDir: "dist",
    emptyOutDir: true,
    // Embedded in the executable, so they would only make it bigger.
    sourcemap: false,
  },
});
