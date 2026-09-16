import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["packages/ui/src/test/setup.ts"],
    include: ["packages/**/*.test.{ts,tsx}", "apps/**/src/**/*.test.{ts,tsx}"],
    css: false,
  },
});
