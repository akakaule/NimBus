import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { NimBusApiClient } from "./nimbus-api-client.js";

/** Failure counters an endpoint already carried before the suite published anything. */
export interface EndpointFailureBaseline {
  failed: number;
  deadletter: number;
}

export type FailureBaseline = Record<string, EndpointFailureBaseline>;

const BASELINE_FILE = resolve("artifacts/failure-baseline.json");

/**
 * Captures the pre-suite failure counters once per run and writes them to disk.
 *
 * This has to survive a worker restart. Playwright re-runs `beforeAll` for a retry, so a
 * baseline captured there was re-read *after* the leak it was supposed to catch: a stray
 * failure raised the counter, attempt 1 failed, then the retry captured the raised counter
 * as its own baseline and passed. The test could therefore only ever be reported flaky,
 * never failed, and the run that produced the leak was the only one able to see it.
 *
 * Called from global setup, which runs exactly once regardless of retries.
 */
export async function captureFailureBaseline(endpointIds: string[]): Promise<FailureBaseline> {
  const nimbus = await NimBusApiClient.create();
  try {
    const counts = await nimbus.getStatusCounts(endpointIds);
    const baseline: FailureBaseline = Object.fromEntries(
      counts.map((c) => [c.endpointId, { failed: c.failedCount, deadletter: c.deadletterCount }]),
    );
    mkdirSync(dirname(BASELINE_FILE), { recursive: true });
    writeFileSync(BASELINE_FILE, JSON.stringify(baseline, null, 2));
    return baseline;
  } finally {
    await nimbus.dispose();
  }
}

/** Reads the baseline captured by global setup. Returns zeroes if it was never written. */
export function readFailureBaseline(): FailureBaseline {
  try {
    return JSON.parse(readFileSync(BASELINE_FILE, "utf8")) as FailureBaseline;
  } catch {
    return {};
  }
}

/**
 * Prints the failed and dead-lettered messages an endpoint is holding.
 *
 * A bare counter says a message failed but not which one, and the application logs that
 * would answer it are collected only after the whole suite finishes. Dumping the audit rows
 * at the moment the assertion trips keeps the evidence attached to the failing test.
 */
export async function reportEndpointFailures(nimbus: NimBusApiClient, endpointId: string): Promise<void> {
  const events = await nimbus.searchEvents(endpointId, { resolutionStatus: ["Failed", "DeadLettered"] }, 25);
  if (events.length === 0) {
    console.log(`[failure-baseline] ${endpointId}: counters moved but no Failed/DeadLettered rows were returned.`);
    return;
  }
  for (const event of events) {
    const error = event.messageContent?.errorContent;
    console.log(
      `[failure-baseline] ${endpointId} ${event.resolutionStatus} eventType=${event.eventTypeId} ` +
        `eventId=${event.eventId} sessionId=${event.sessionId} ` +
        `error=${error?.errorType ?? "?"}: ${error?.errorText ?? "(none)"}`,
    );
  }
}
