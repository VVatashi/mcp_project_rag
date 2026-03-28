import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  testMatch: ["**/*.spec.ts"],
  fullyParallel: false,
  workers: 1,
  timeout: 30_000,
  use: {
    headless: true,
    viewport: { width: 1440, height: 960 },
    channel: "msedge",
  },
});
