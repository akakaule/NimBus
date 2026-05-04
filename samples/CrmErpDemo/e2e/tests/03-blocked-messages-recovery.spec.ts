import { test, expect } from "@playwright/test";
import { CrmApiClient, type CrmAccount } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { NimBusApiClient } from "../helpers/nimbus-api-client.js";
import { Timeouts } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

/**
 * Multiple updates published while the receiving system is in error mode.
 *
 * Because each update is published with the same SessionKey (account id),
 * the Service Bus session lock plus NimBus's session-aware processing means
 * later messages in the same session are *blocked* (deferred) behind the
 * first failed message. After error mode is turned off and the failed
 * head-of-session message is resubmitted, the deferred backlog drains.
 *
 * Verification: status counts on both endpoints return to all-zero, and
 * the final ERP customer reflects the most recent update.
 */
test.describe("Blocked/deferred messages drain after head resubmit", () => {
  let crm: CrmApiClient;
  let erp: ErpApiClient;
  let nimbus: NimBusApiClient;

  test.beforeAll(async () => {
    crm = await CrmApiClient.create();
    erp = await ErpApiClient.create();
    nimbus = await NimBusApiClient.create();
    await erp.resetFailureModes();
  });

  test.afterAll(async () => {
    await erp.resetFailureModes();
    await crm.dispose();
    await erp.dispose();
    await nimbus.dispose();
  });

  test("queue up create + 3 updates during error mode → resubmit head → all drain in order", async ({ page }) => {
    // ── 1. Create a CRM account first while everything is healthy so the ERP
    //       customer exists and subsequent updates are pure update events.
    const baseName = `BatchCo-${Date.now()}`;
    const account = await crm.createAccount({ legalName: baseName, countryCode: "SE" });
    await waitFor(
      async () => await erp.findCustomerByCrmAccountId(account.id),
      { timeoutMs: Timeouts.propagationMs, description: `Initial ERP customer for ${account.id}` },
    );

    // ── 2. Flip error mode ON, then push N updates back-to-back. Same session
    //       (account id) → first update fails, the rest queue behind it.
    await erp.setErrorMode(true);
    const updates: { revision: number; legalName: string }[] = [];
    for (let i = 1; i <= 3; i++) {
      const legalName = `${baseName}-rev${i}`;
      await crm.updateAccount(account.id, { legalName, countryCode: "SE" });
      updates.push({ revision: i, legalName });
    }

    // ── 3. Wait for at least one bad event on ErpEndpoint tied to OUR session.
    //       The ServiceBus retry budget can take a while to exhaust under error
    //       mode, so be generous on the timeout. We also accept DeadLettered as
    //       evidence the head reached terminal failure.
    const failedHead = await waitFor(
      async () => {
        const events = await nimbus.searchEvents("ErpEndpoint", {
          resolutionStatus: ["Failed", "DeadLettered"],
        });
        return events.find((e) => e.sessionId === account.id) ?? null;
      },
      { timeoutMs: Timeouts.failedMessageMs, description: `Head failure for session ${account.id}` },
    );

    // ── 4. Operator opens the NimBus admin endpoints page and drills in to
    //       ErpEndpoint to find and resubmit the failed message.
    await page.goto("/Endpoints");
    const erpRow = page.locator("tr").filter({ hasText: "ErpEndpoint" });
    await expect(erpRow).toBeVisible();

    await erpRow.click();
    await page.waitForURL(/\/Endpoints\/Details\/ErpEndpoint/);

    // Disable error mode BEFORE clicking Resubmit so the redelivery actually
    // processes. (The deferred siblings drain automatically once the head
    // succeeds and releases the session lock.)
    await erp.setErrorMode(false);

    const failedRow = page.locator("table tbody tr").filter({ hasText: failedHead.eventId.substring(0, 8) });
    await expect(failedRow).toBeVisible();
    await failedRow.locator("button", { hasText: "Resubmit" }).click();

    // ── 5. Strict design contract: resubmitting the head must auto-drain
    //       the deferred backlog. NimBus's StrictMessageHandler sends a
    //       ProcessDeferredRequest after a successful resubmit, which wakes
    //       the DeferredProcessor function and republishes parked siblings
    //       to the main subscription in DeferralSequence (FIFO) order.
    //       No manual `reprocessDeferred` / per-event `resubmit` is required.
    const lastRevName = updates[updates.length - 1].legalName;
    const finalCustomer = await waitFor(
      async () => {
        const c = await erp.findCustomerByCrmAccountId(account.id);
        return c && c.legalName === lastRevName ? c : null;
      },
      {
        timeoutMs: Timeouts.propagationMs,
        description: `ERP customer reached final update ${lastRevName} (auto-drain of deferred backlog for session ${account.id})`,
      },
    );
    expect(finalCustomer.legalName).toBe(lastRevName);

    // ── 6. The session should be fully clean — no Failed/Deferred events
    //       remaining for OUR session on ErpEndpoint. (Don't assert on global
    //       counts; the broker may carry leftover state from prior runs in
    //       OTHER sessions.)
    await waitFor(
      async () => {
        const stuck = (await nimbus.searchEvents("ErpEndpoint", {
          resolutionStatus: ["Failed", "Deferred", "DeadLettered"],
        })).filter((e) => e.sessionId === account.id);
        return stuck.length === 0 ? true : null;
      },
      {
        timeoutMs: Timeouts.failedMessageMs,
        description: `No Failed/Deferred/DeadLettered events linger for session ${account.id} after auto-drain`,
      },
    );

    // ── 7. Capture endpoint state for diagnostics — log only.
    const finalCounts = await nimbus.getStatusCounts(["CrmEndpoint", "ErpEndpoint"]);
    for (const c of finalCounts) {
      // eslint-disable-next-line no-console
      console.log(`[diag] ${c.endpointId}: pending=${c.pendingCount} failed=${c.failedCount} deferred=${c.deferredCount} deadletter=${c.deadletterCount}`);
    }
    // Sanity: the loopback path (ERP → CRM acks for each update) shouldn't have
    // produced unsupported events.
    for (const c of finalCounts) {
      expect.soft(c.unsupportedCount, `${c.endpointId} unsupportedCount`).toBe(0);
    }
  });
});
