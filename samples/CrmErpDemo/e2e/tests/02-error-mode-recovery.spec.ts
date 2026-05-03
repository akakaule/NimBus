import { test, expect } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { NimBusApiClient } from "../helpers/nimbus-api-client.js";
import { Timeouts } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

test.describe("Error mode + resubmit-from-WebApp recovery", () => {
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

  test("publish during error mode → fails → resubmit from NimBus admin → succeeds", async ({ page }) => {
    // ── 1. Force the ERP adapter into error mode so every handler throws. ──────
    await erp.setErrorMode(true);
    expect((await erp.getErrorMode()).enabled).toBe(true);

    // ── 2. Publish from CRM. The ErpEndpoint handler will throw; NimBus retries
    //       per the configured policy and eventually marks the message Failed.
    const legalName = `BadFlow Inc ${Date.now()}`;
    const crmAccount = await crm.createAccount({ legalName, countryCode: "GB" });

    // ── 3. Wait for NimBus to register a failed event matching this account on
    //       the ErpEndpoint subscription.
    const failedEvent = await waitFor(
      async () => {
        const events = await nimbus.searchEvents("ErpEndpoint", {
          resolutionStatus: ["Failed"],
        });
        return events.find((e) => e.sessionId === crmAccount.id) ?? null;
      },
      { timeoutMs: Timeouts.failedMessageMs, description: `Failed event for account ${crmAccount.id} on ErpEndpoint` },
    );
    expect(failedEvent.eventId).toBeTruthy();
    expect(failedEvent.lastMessageId).toBeTruthy();

    // Sanity: ERP has not yet created the customer (handler kept throwing).
    expect(await erp.findCustomerByCrmAccountId(crmAccount.id)).toBeNull();

    // ── 4. Operator opens the NimBus WebApp, navigates to ErpEndpoint detail,
    //       confirms the failed message is visible, and clicks "Resubmit".
    await page.goto(`/Endpoints/Details/ErpEndpoint`);
    // Default page filter shows Failed/DeadLettered/Unsupported, so the row
    // should appear without changing filters.
    const failedRow = page.locator("table tbody tr").filter({ hasText: failedEvent.eventId.substring(0, 8) });
    await expect(failedRow).toBeVisible();
    await expect(failedRow).toContainText("Failed");

    // ── 5. Turn error mode OFF so the next delivery attempt succeeds, THEN click
    //       Resubmit. (If we resubmitted while error mode was still on, the
    //       message would just fail again and the test would race.)
    await erp.setErrorMode(false);
    expect((await erp.getErrorMode()).enabled).toBe(false);

    await failedRow.locator("button", { hasText: "Resubmit" }).click();

    // ── 6. After resubmit, the ERP customer should now exist (the new delivery
    //       attempt succeeds because error mode is off). The specific failed
    //       event should also disappear from the Failed bucket — the resolver
    //       MERGE-overwrites the Failed status with Completed for the same id.
    const erpCustomer = await waitFor(
      async () => await erp.findCustomerByCrmAccountId(crmAccount.id),
      { timeoutMs: Timeouts.propagationMs, description: `ERP customer created after resubmit for account ${crmAccount.id}` },
    );
    expect(erpCustomer.legalName).toBe(legalName);

    await waitFor(
      async () => {
        const events = await nimbus.searchEvents("ErpEndpoint", { resolutionStatus: ["Failed"] });
        const stillFailed = events.find((e) => e.eventId === failedEvent.eventId);
        return stillFailed ? null : true;
      },
      { timeoutMs: Timeouts.failedMessageMs, description: `Failed event ${failedEvent.eventId} cleared after resubmit` },
    );
  });

  test("publish during service mode → message dead-lettered → resubmit succeeds after toggle off", async ({ page }) => {
    // Service mode rejects messages by throwing — same shape as a real downstream
    // outage. Service Bus retries until the delivery count limit, then the message
    // sits as Failed/DeadLettered in NimBus until resubmitted.

    await erp.setServiceMode(true);
    expect((await erp.getServiceMode()).enabled).toBe(true);

    const legalName = `Quiet Drop Co ${Date.now()}`;
    const crmAccount = await crm.createAccount({ legalName, countryCode: "DE" });

    // Wait for NimBus to surface the message as Failed (after Service Bus
    // exhausted its retries against the throwing handler).
    const failedEvent = await waitFor(
      async () => {
        const events = await nimbus.searchEvents("ErpEndpoint", { resolutionStatus: ["Failed", "DeadLettered"] });
        return events.find((e) => e.sessionId === crmAccount.id) ?? null;
      },
      { timeoutMs: Timeouts.failedMessageMs, description: `Failed event for service-mode account ${crmAccount.id}` },
    );

    // Sanity: ERP didn't create the customer.
    expect(await erp.findCustomerByCrmAccountId(crmAccount.id)).toBeNull();

    // Recovery: turn service mode off, then resubmit from the WebApp.
    await erp.setServiceMode(false);
    await page.goto(`/Endpoints/Details/ErpEndpoint`);
    const failedRow = page.locator("table tbody tr").filter({ hasText: failedEvent.eventId.substring(0, 8) });
    await expect(failedRow).toBeVisible();
    await failedRow.locator("button", { hasText: "Resubmit" }).click();

    const erpCustomer = await waitFor(
      async () => await erp.findCustomerByCrmAccountId(crmAccount.id),
      { timeoutMs: Timeouts.propagationMs, description: `ERP customer created after service-mode resubmit (${crmAccount.id})` },
    );
    expect(erpCustomer.legalName).toBe(legalName);
  });
});
