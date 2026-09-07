import { defineConfig, devices } from "@playwright/test";
import * as dotenv from "dotenv";

// Explicit runner/Aspire-discovered URLs take precedence over saved local ports.
dotenv.config({ path: ".env.local" });
dotenv.config({ path: ".env" });

// Tests assume the AppHost (samples/CrmErpDemo/CrmErpDemo.AppHost) is already running.
// Service URLs come from .env.local (created by the operator) or fall back to common
// Aspire defaults. The base URL points at the NimBus management WebApp because that
// is what page.goto(...) targets in the failure-recovery specs.
const NIMBUS_OPS_URL = process.env.NIMBUS_OPS_URL ?? "http://localhost:28376";

export default defineConfig({
  testDir: "./tests",
  fullyParallel: false,
  // Force serial execution: tests share Service Bus + storage state and need
  // deterministic ordering against a single live AppHost.
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? "github" : [["list"], ["html", { open: "never" }]],
  // Recovery specs include several bounded waits, broker redeliveries and
  // adapter restarts within a single test.
  timeout: 360_000,
  expect: {
    timeout: 30_000,
  },
  use: {
    baseURL: NIMBUS_OPS_URL,
    actionTimeout: 30_000,
    navigationTimeout: 30_000,
    // Enable explicitly with --trace=retain-on-failure when diagnosing a case.
    // Screenshots, video and session evidence remain available by default.
    trace: "off",
    screenshot: "only-on-failure",
    video: "retain-on-failure",
    ignoreHTTPSErrors: true,
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
