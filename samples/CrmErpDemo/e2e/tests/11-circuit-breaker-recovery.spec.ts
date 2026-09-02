import { test, expect } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { NimBusApiClient } from "../helpers/nimbus-api-client.js";
import { Timeouts } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

// Circuit-breaker showcase (docs/circuit-breaker.md): CRM outage mode makes
// every crm-adapter handler fail with a synthetic 503, the CrmEndpoint circuit
// opens (demo thresholds: 5 outcomes / 50% / 60s window), the receiver pauses,
// and after the outage clears half-open probes close it again. State is
// asserted via crm-api's /api/admin/circuit-state — served state pushed by the
// adapter, not logs or metrics, so the spec stays deterministic.
//
// Traffic driver: each CRM account created here triggers CRM → ERP → outbox →
// ErpCustomerCreated → crm-adapter (link-erp callback). Each account is its own
// session, so every failed callback is an independent breaker outcome. The
// failed sessions this spec leaves behind stay blocked with their Failed
// events — matching the other failure specs, which filter by their own ids.

test.describe("Circuit breaker opens on CRM outage and recovers", () => {
  let crm: CrmApiClient;
  let erp: ErpApiClient;
  let nimbus: NimBusApiClient;

  test.beforeAll(async () => {
    crm = await CrmApiClient.create();
    erp = await ErpApiClient.create();
    nimbus = await NimBusApiClient.create();
    await crm.resetFailureModes();
    await erp.resetFailureModes();
  });

  test.afterAll(async () => {
    await crm.resetFailureModes();
    await crm.dispose();
    await erp.dispose();
    await nimbus.dispose();
  });

  test("outage opens the circuit → recovery probes close it → traffic resumes", async () => {
    // ── 1. Baseline: the circuit reports Closed before the outage. ────────────
    expect((await crm.getCircuitState()).state).toBe("Closed");

    // ── 2. Flip the CRM API outage on: every crm-adapter handler call now gets
    //       a synthetic 503 and fails retry-classified. ─────────────────────────
    await crm.setErrorMode(true);
    expect((await crm.getErrorMode()).enabled).toBe(true);

    // ── 3. Drive traffic: each account produces an ErpCustomerCreated the
    //       crm-adapter fails to handle. Six independent sessions comfortably
    //       exceed MinimumThroughput=5 inside the 60s sampling window. ─────────
    const stamp = Date.now();
    const accountIds: string[] = [];
    for (let i = 0; i < 6; i++) {
      const account = await crm.createAccount({
        legalName: `Breaker Trip Co ${stamp}-${i}`,
        countryCode: "SE",
      });
      accountIds.push(account.id);
    }

    // ── 4. The circuit opens: crm-adapter pushes the transition to crm-api. ───
    const open = await waitFor(
      async () => {
        const state = await crm.getCircuitState();
        return state.state === "Open" ? state : null;
      },
      { timeoutMs: Timeouts.failedMessageMs, description: "CrmEndpoint circuit Open after sustained failures" },
    );
    expect(open.endpoint).toBe("CrmEndpoint");

    // Sanity: NimBus recorded real failures on CrmEndpoint for this run.
    const failedEvents = await nimbus.searchEvents("CrmEndpoint", { resolutionStatus: ["Failed"] });
    expect(failedEvents.some((e) => accountIds.includes(e.sessionId))).toBe(true);

    // ── 5. Outage over. The paused receiver waits out BreakDuration (20s),
    //       probes at one session, and closes after 2 successful probes. Fresh
    //       accounts provide the probe traffic — without messages, half-open
    //       has nothing to prove itself with. ──────────────────────────────────
    await crm.setErrorMode(false);

    let probeIndex = 0;
    await waitFor(
      async () => {
        const state = await crm.getCircuitState();
        if (state.state === "Closed") return state;
        // Feed the probes: one fresh session per poll while recovering.
        await crm.createAccount({
          legalName: `Breaker Probe Co ${stamp}-${probeIndex++}`,
          countryCode: "SE",
        });
        return null;
      },
      { timeoutMs: 120_000, description: "CrmEndpoint circuit Closed after recovery probes" },
    );

    // ── 6. Normal flow proves the receiver resumed: a fresh account's
    //       ErpCustomerCreated completes on CrmEndpoint end to end. ────────────
    const proof = await crm.createAccount({
      legalName: `Breaker Recovered Co ${stamp}`,
      countryCode: "SE",
    });
    await waitFor(
      async () => {
        const events = await nimbus.searchEvents("CrmEndpoint", { resolutionStatus: ["Completed"] });
        return events.find((e) => e.sessionId === proof.id) ?? null;
      },
      { timeoutMs: Timeouts.propagationMs, description: `Completed CrmEndpoint event for post-recovery account ${proof.id}` },
    );
  });
});
