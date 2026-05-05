import { test, expect } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { NimBusApiClient, type NimBusEvent } from "../helpers/nimbus-api-client.js";
import { Timeouts } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

/**
 * PendingHandoff failure path.
 *
 * With handoff mode enabled at failureRate=1.0, every settlement tick
 * picks a canned DMF-style error and calls IManagerClient.FailHandoff(...).
 * The Resolver flips Pending → Failed; the session stays blocked until an
 * operator clicks Skip (or Resubmit) in NimBus.WebApp.
 *
 * Verification:
 *  1. Audit row reaches resolutionStatus=Failed with errorContent.errorText
 *     starting with "DMF rejected:" (one of the canned strings produced by
 *     HandoffJobBackgroundService).
 *  2. Skip via NimBus REST flips the row to Skipped.
 */
test.describe("PendingHandoff failure: handoff fails → operator Skip → row reaches Skipped", () => {
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

  test("create + handoff fails → audit Failed with DMF errorText → operator Skip → Skipped", async () => {
    // ── 1. Force every handoff settlement to fail.
    await erp.setHandoffMode({ enabled: true, durationSeconds: 3, failureRate: 1.0 });

    // ── 2. Create one CRM account. Single-message scenario: no siblings.
    const account = await crm.createAccount({
      legalName: `Handoff-Fail-${Date.now()}`,
      countryCode: "GB",
    });

    // ── 3. Wait for the audit row to reach Failed with the DMF errorText.
    //       The window is ~3s (deadline) + 1s (BackgroundService tick) + slack
    //       for the resolver write to land — covered by the polling timeout.
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

    // ── 4. Operator triggers Skip via the NimBus REST API. The eventTypeId is
    //       inferred server-side from the message; we just need eventId +
    //       lastMessageId.
    await nimbus.skipMessage(failedEvent.eventId, failedEvent.lastMessageId);

    // ── 5. The audit row should flip to Skipped. The session unblocks; if any
    //       sibling messages had been queued they would now drain — but we
    //       intentionally created no siblings, so this just verifies the
    //       Skipped terminal state.
    await waitFor(
      async () => {
        const events = await nimbus.searchEvents("ErpEndpoint", {
          sessionId: account.id,
          eventTypeId: ["CrmAccountCreated"],
          resolutionStatus: ["Skipped"],
        });
        return events.find((e) => e.eventId === failedEvent.eventId) ?? null;
      },
      {
        timeoutMs: Timeouts.failedMessageMs,
        description: `Audit row ${failedEvent.eventId} flips to Skipped after operator skip`,
      },
    );

    // The Failed bucket for this event should be cleared.
    const stillFailed = (await nimbus.searchEvents("ErpEndpoint", {
      sessionId: account.id,
      resolutionStatus: ["Failed"],
    })).find((e) => e.eventId === failedEvent.eventId);
    expect.soft(stillFailed, "event no longer in Failed bucket").toBeFalsy();
  });
});
