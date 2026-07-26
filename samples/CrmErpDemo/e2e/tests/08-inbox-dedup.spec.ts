import { test, expect } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { NimBusApiClient } from "../helpers/nimbus-api-client.js";
import { Timeouts } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

/**
 * Inbox deduplication showcase (CrmEndpoint): resubmitting an already-Completed
 * event from nimbus-ops redelivers the same broker MessageId; the CRM adapter's
 * SQL inbox skips it with reason DuplicateDetected and the handler never runs a
 * second time (no new CRM audit rows).
 */
test.describe("Inbox deduplication on CrmEndpoint", () => {
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
    await crm.dispose();
    await erp.dispose();
    await nimbus.dispose();
  });

  test("resubmitted Completed ErpCustomerCreated is skipped as DuplicateDetected", async () => {
    // 1. ERP-originated customer → ErpCustomerCreated → CRM adapter creates the account.
    const legalName = `Dedup GmbH ${Date.now()}`;
    const erpCustomer = await erp.createCustomer({ legalName, countryCode: "DE" });

    const crmAccount = await waitFor(
      async () => {
        const accounts = await crm.listAccounts();
        return accounts.find((a) => a.legalName === legalName) ?? null;
      },
      { timeoutMs: Timeouts.propagationMs, description: `CRM account for ERP customer ${erpCustomer.id}` },
    );

    // 2. Find the Completed ErpCustomerCreated event on CrmEndpoint.
    const completed = await waitFor(
      async () => {
        const events = await nimbus.searchEvents("CrmEndpoint", {
          eventTypeId: ["ErpCustomerCreated"],
          resolutionStatus: ["Completed"],
          payload: erpCustomer.id,
        });
        return events[0] ?? null;
      },
      { timeoutMs: Timeouts.propagationMs, description: "Completed ErpCustomerCreated on CrmEndpoint" },
    );

    const auditRowsBefore = (await crm.getAuditLog("Account", crmAccount.id)).length;

    // 3. Operator resubmits the Completed event — same MessageId is redelivered.
    await nimbus.resubmit(completed.eventId, completed.lastMessageId);

    // 4. The duplicate is skipped with reason DuplicateDetected.
    await waitFor(
      async () => {
        const events = await nimbus.searchEvents("CrmEndpoint", {
          eventId: completed.eventId,
          resolutionStatus: ["Skipped"],
        });
        return events.find(
          (e) => e.messageContent?.errorContent?.errorText === "DuplicateDetected",
        ) ?? null;
      },
      { timeoutMs: Timeouts.propagationMs, description: "DuplicateDetected skip for the resubmitted event" },
    );

    // 5. The handler never ran again: no new CRM audit rows for the account.
    const auditRowsAfter = (await crm.getAuditLog("Account", crmAccount.id)).length;
    expect(auditRowsAfter).toBe(auditRowsBefore);
  });
});
