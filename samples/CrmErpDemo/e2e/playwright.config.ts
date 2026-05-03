import { defineConfig, devices } from "@playwright/test";
import * as dotenv from "dotenv";

dotenv.config({ path: ".env" });
dotenv.config({ path: ".env.local", override: true });

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
  // Failure-mode + resubmit specs do multiple long waits in series. Per-test
  // budget needs to accommodate the worst case: write fails → ServiceBus retries
  // → DLQ → resubmit → propagate.
  timeout: 360_000,
  expect: {
    timeout: 30_000,
  },
  use: {
    baseURL: NIMBUS_OPS_URL,
    trace: "on-first-retry",
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
