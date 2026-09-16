import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// Tauri serves this over a fixed port in development and from disk in a build.
export default defineConfig({
  plugins: [react()],
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
