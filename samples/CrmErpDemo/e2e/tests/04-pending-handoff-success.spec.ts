import { test, expect } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { NimBusApiClient, type NimBusEvent } from "../helpers/nimbus-api-client.js";
import { Timeouts } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

/**
 * PendingHandoff happy path.
 *
 * With handoff mode enabled, the ERP adapter's CrmAccountCreated handler
 * signals MarkPendingHandoff(...) and returns. NimBus records the audit row
 * as Pending+Handoff and blocks the session, so sibling messages on the same
 * session (further updates to the same account) defer FIFO behind it.
 *
 * After the configured deadline elapses, Erp.Api's HandoffJobBackgroundService
 * applies the ERP-side upsert and signals CompleteHandoff. The Resolver flips
 * the Pending row to Completed; the deferred siblings replay in order.
 *
 * Verification: all three audit rows end Completed, ERP customer reflects the
 * latest update, and the original handoff metadata fields were populated.
 */
test.describe("PendingHandoff success: create handed off, sibling updates defer, all complete", () => {
  let crm: CrmApiClient;
  let erp: ErpApiClient;
  let nimbus: NimBusApiClient;

  test.beforeAll(async () => {
    crm = await CrmApiClient.create();
    erp = await ErpApiClient.create();
    nimbus = await NimBusApiClient.create();
    await erp.resetFailureModes();
    await erp.resetHandoffMode();
  });

  test.afterEach(async () => {
    // Reset handoff mode so other specs see a clean state. Other failure modes
    // are owned by their own specs' afterEach; we only own handoff here.
    await erp.resetHandoffMode();
  });

  test.afterAll(async () => {
    await erp.resetFailureModes();
    await erp.resetHandoffMode();
    await crm.dispose();
    await erp.dispose();
    await nimbus.dispose();
  });

  test("create + 2 in-flight updates → create reaches Pending+Handoff, updates defer, all complete", async () => {
    // ── 1. Enable handoff mode with a short deadline so the test wraps up
    //       quickly. failureRate=0 means CompleteHandoff always wins.
    await erp.setHandoffMode({ enabled: true, durationSeconds: 3, failureRate: 0 });

    // ── 2. Create one CRM account. The ERP adapter's CrmAccountCreated handler
    //       reads handoff mode, registers a job in Erp.Api, and signals
    //       MarkPendingHandoff — so the audit row should land as Pending+Handoff.
    const baseName = `Handoff-Success-${Date.now()}`;
    const account = await crm.createAccount({ legalName: baseName, countryCode: "US" });

    // ── 3. Wait for the CrmAccountCreated audit row on ErpEndpoint to surface
    //       in Pending+Handoff with the handoff metadata fields populated.
    const pendingCreate: NimBusEvent = await waitFor(
      async () => {
        const events = await nimbus.searchEvents("ErpEndpoint", {
          sessionId: account.id,
          eventTypeId: ["CrmAccountCreated"],
          resolutionStatus: ["Pending"],
        });
        return events.find((e) => e.pendingSubStatus === "Handoff") ?? null;
      },
      { timeoutMs: Timeouts.propagationMs, description: `Pending+Handoff CrmAccountCreated for session ${account.id}` },
    );
    expect(pendingCreate.handoffReason, "handoffReason").toBeTruthy();
    expect(pendingCreate.externalJobId, "externalJobId").toBeTruthy();
    expect(pendingCreate.expectedBy, "expectedBy").toBeTruthy();

    // Sanity: the corresponding ERP customer should NOT exist yet — the upsert
    // is deferred to the BackgroundService settlement tick.
    expect(await erp.findCustomerByCrmAccountId(account.id)).toBeNull();

    // ── 4. Within the 3s window, fire two updates back-to-back. They share
    //       SessionKey=AccountId with the in-flight create, so they should
    //       defer behind it instead of running.
    const updates = [
      { revision: 1, legalName: `${baseName}-rev1` },
      { revision: 2, legalName: `${baseName}-rev2` },
    ];
    for (const u of updates) {
      await crm.updateAccount(account.id, { legalName: u.legalName, countryCode: "US" });
    }

    // ── 5. Both update audit rows on ErpEndpoint should reach Deferred. We
    //       poll because the outbox dispatcher + ServiceBus have ~1s latency
    //       before the messages even hit the subscription.
    await waitFor(
      async () => {
        const events = await nimbus.searchEvents("ErpEndpoint", {
          sessionId: account.id,
          eventTypeId: ["CrmAccountUpdated"],
          resolutionStatus: ["Deferred"],
        });
        return events.length >= 2 ? events : null;
      },
      { timeoutMs: Timeouts.propagationMs, description: `Both CrmAccountUpdated rows reach Deferred for session ${account.id}` },
    );

    // ── 6. Wait past the 3s deadline + one BackgroundService tick (~1s) plus
    //       slack for the deferred replay to drain. The polling helper covers
    //       the slack — we don't need a fixed sleep.
    //
    // ── 7. All three audit rows for our session on ErpEndpoint should be
    //       Completed (the create after CompleteHandoff fires; the two updates
    //       after the deferred replay drains the session).
    const lastRevName = updates[updates.length - 1].legalName;
    await waitFor(
      async () => {
        const events = await nimbus.searchEvents("ErpEndpoint", {
          sessionId: account.id,
          resolutionStatus: ["Completed"],
        });
        const create = events.find((e) => e.eventTypeId === "CrmAccountCreated");
        const updateCount = events.filter((e) => e.eventTypeId === "CrmAccountUpdated").length;
        return create && updateCount >= 2 ? events : null;
      },
      {
        timeoutMs: Timeouts.failedMessageMs,
        description: `Create + 2 updates all Completed for session ${account.id}`,
      },
    );

    // ── 8. Verify the ERP customer materialised and reflects the LATEST update.
    //       (BackgroundService applied the create-time payload on settlement,
    //       then the deferred update handlers ran the synchronous upsert path.)
    const finalCustomer = await waitFor(
      async () => {
        const c = await erp.findCustomerByCrmAccountId(account.id);
        return c && c.legalName === lastRevName ? c : null;
      },
      {
        timeoutMs: Timeouts.propagationMs,
        description: `ERP customer reflects final update ${lastRevName} for session ${account.id}`,
      },
    );
    expect(finalCustomer.legalName).toBe(lastRevName);

    // No Failed/DeadLettered/Pending/Deferred should linger on this session.
    const stuck = (await nimbus.searchEvents("ErpEndpoint", {
      sessionId: account.id,
      resolutionStatus: ["Failed", "Deferred", "DeadLettered", "Pending"],
    })).filter((e) => e.sessionId === account.id);
    expect.soft(stuck.length, "no lingering non-terminal audit rows").toBe(0);
  });
});
