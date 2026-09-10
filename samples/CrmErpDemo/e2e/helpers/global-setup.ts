import { captureFailureBaseline } from "./failure-baseline.js";

/** Endpoints whose failure counters the happy-path spec guards. */
const GUARDED_ENDPOINTS = ["CrmEndpoint", "ErpEndpoint"];

/**
 * Runs once per Playwright run, before any worker starts, so the recorded baseline is
 * unaffected by a retry restarting a worker and re-running `beforeAll`.
 */
export default async function globalSetup(): Promise<void> {
  const baseline = await captureFailureBaseline(GUARDED_ENDPOINTS);
  console.log(`[failure-baseline] captured ${JSON.stringify(baseline)}`);
}
