import { test, expect } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { NimBusApiClient, type NimBusEvent } from "../helpers/nimbus-api-client.js";
import { Timeouts } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

/**
 * PendingHandoff failure → operator Resubmit via NimBus.WebApp UI.
 *
 * Companion to spec 05 (which exercises Skip via REST). Here the operator
 * action is the same one used in 02-error-mode-recovery — driving the WebApp
 * UI's Resubmit button on the row in the endpoint detail page. The handoff
 * landing as Failed shares its operator-action surface with synchronous
 * failures, so the recovery flow should look identical from the UI side.
 */
test.describe("PendingHandoff failure: handoff fails → operator Resubmit via WebApp UI → succeeds", () => {
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
    await erp.resetHandoffMode();
  });

  test.afterAll(async () => {
    await erp.resetFailureModes();
    await erp.resetHandoffMode();
    await crm.dispose();
    await erp.dispose();
    await nimbus.dispose();
  });

  test("create + handoff fails → Failed row → Resubmit in WebApp → Completed + ERP customer materialises", async ({ page }) => {
    // ── 1. Force every handoff settlement to fail with a DMF-style error.
    await erp.setHandoffMode({ enabled: true, durationSeconds: 3, failureRate: 1.0 });

    // ── 2. Create one CRM account. Single-message scenario: no siblings.
    const legalName = `Handoff-Resubmit-${Date.now()}`;
    const account = await crm.createAccount({ legalName, countryCode: "FR" });

    // ── 3. Wait for the audit row to reach Failed with the DMF errorText.
    //       ~3s deadline + 1s BackgroundService tick + slack.
    const failedEvent: NimBusEvent = await waitFor(
      async () => {
        const events = await nimbus.searchEvents("ErpEndpoint", {
          sessionId: account.id,
          eventTypeId: ["CrmAccountCreated"],
          resolutionStatus: ["Failed"],
        });
        return events[0] ?? null;
      },
      {
        timeoutMs: Timeouts.failedMessageMs,
        description: `Failed CrmAccountCreated audit row for session ${account.id}`,
      },
    );
    expect(failedEvent.eventId, "eventId").toBeTruthy();
    expect(failedEvent.lastMessageId, "lastMessageId").toBeTruthy();
    const errorText = failedEvent.messageContent?.errorContent?.errorText ?? "";
    expect(errorText, "messageContent.errorContent.errorText").toMatch(/^DMF rejected:/);

    // Sanity: ERP customer was NOT created (FailHandoff path skips the upsert).
    expect(await erp.findCustomerByCrmAccountId(account.id)).toBeNull();

    // ── 4. Disable handoff mode BEFORE clicking Resubmit. Otherwise the
    //       redelivery would just hand off + fail again and the test would
    //       race. Mirrors the setErrorMode(false) step in spec 02.
    await erp.setHandoffMode({ enabled: false, durationSeconds: 3, failureRate: 0 });

    // ── 5. Operator opens the NimBus WebApp, navigates to ErpEndpoint detail,
    //       confirms the failed handoff row is visible, and clicks "Resubmit".
    await page.goto(`/Endpoints/Details/ErpEndpoint`);
    const failedRow = page.locator("table tbody tr").filter({ hasText: failedEvent.eventId.substring(0, 8) });
    await expect(failedRow).toBeVisible();
    await expect(failedRow).toContainText("Failed");
    await failedRow.locator("button", { hasText: "Resubmit" }).click();

    // ── 6. With handoff mode off, the resubmitted message hits the synchronous
    //       upsert path in the ERP adapter, so the customer should now exist.
    const erpCustomer = await waitFor(
      async () => await erp.findCustomerByCrmAccountId(account.id),
      { timeoutMs: Timeouts.propagationMs, description: `ERP customer created after handoff Resubmit (${account.id})` },
    );
    expect(erpCustomer.legalName).toBe(legalName);

    // ── 7. The original eventId should leave the Failed bucket (Resolver
    //       MERGE-overwrites Failed → Completed for the same id).
    await waitFor(
      async () => {
        const events = await nimbus.searchEvents("ErpEndpoint", { resolutionStatus: ["Failed"] });
        const stillFailed = events.find((e) => e.eventId === failedEvent.eventId);
        return stillFailed ? null : true;
      },
      { timeoutMs: Timeouts.failedMessageMs, description: `Failed event ${failedEvent.eventId} cleared after Resubmit` },
    );
  });
});
