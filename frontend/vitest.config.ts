import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

export default defineConfig({
  resolve: { alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) } },
  esbuild: { jsx: "automatic" },
  test: {
    environment: "jsdom",
    include: ["tests/**/*.test.{ts,tsx}"],
    setupFiles: ["./tests/setup.ts"],
    clearMocks: true,
    // Bound concurrent DOM environments so full runs do not starve query notifications.
    maxWorkers: 4,
    // DOM-heavy interaction tests can exceed the 5s default under four workers.
    testTimeout: 10_000,
  },
});
