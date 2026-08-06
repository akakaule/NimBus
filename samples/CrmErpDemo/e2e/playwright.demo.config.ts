import { defineConfig } from "@playwright/test";
import * as dotenv from "dotenv";

dotenv.config({ path: ".env" });
dotenv.config({ path: ".env.local", override: true });

// Video-recording harness for the demo film (see ../docs/demo-video-script.md).
// Separate from playwright.config.ts because the goals conflict: the regression
// suite wants to be fast and headless, this wants to be slow, pretty and always
// recorded. Assumes the AppHost is already running.
const NIMBUS_OPS_URL = process.env.NIMBUS_OPS_URL ?? "http://localhost:28376";

// 16:9 at a size where the SPAs' max-w-5xl content still has comfortable margins.
const FRAME = { width: 1600, height: 900 };

export default defineConfig({
  testDir: "./demo",
  fullyParallel: false,
  // The acts share one live AppHost and run as a single continuous story.
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  // Acts hold shots deliberately and wait on real Service Bus round-trips.
  timeout: 15 * 60 * 1000,
  expect: { timeout: 90_000 },
  outputDir: "./demo-results",
  use: {
    baseURL: NIMBUS_OPS_URL,
    browserName: "chromium",
    viewport: FRAME,
    video: { mode: "on", size: FRAME },
    screenshot: "off",
    trace: "off",
    ignoreHTTPSErrors: true,
    // Slow the synthetic input down a little; instant clicks read as glitches.
    launchOptions: { slowMo: 120 },
  },
  projects: [{ name: "demo" }],
});
