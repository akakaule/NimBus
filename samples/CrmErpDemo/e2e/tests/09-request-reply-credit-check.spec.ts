import { test, expect } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { Timeouts } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

/**
 * Request/reply showcase: CRM synchronously asks ERP for a customer's credit
 * standing (PublisherClient.Request over the CrmEndpoint-reply subscription).
 * Also covers the timeout path via ERP maintenance mode.
 */
test.describe("Request/reply: ERP credit check", () => {
  let crm: CrmApiClient;
  let erp: ErpApiClient;

  test.beforeAll(async () => {
    crm = await CrmApiClient.create();
    erp = await ErpApiClient.create();
    await erp.resetFailureModes();
    await erp.setServiceMode(false);
  });

  test.afterAll(async () => {
    // Never leave maintenance mode on for later suites.
    await erp.setServiceMode(false);
    await crm.dispose();
    await erp.dispose();
  });

  test("credit check returns Approved for a synced account", async () => {
    const account = await crm.createAccount({
      legalName: `CreditCheck Co ${Date.now()}`,
      countryCode: "US",
    });

    // Wait until the ERP customer exists so the check can find it.
    await waitFor(
      async () => await erp.findCustomerByCrmAccountId(account.id),
      { timeoutMs: Timeouts.propagationMs, description: `ERP customer for account ${account.id}` },
    );

    const result = await crm.creditCheck(account.id);

    expect(result).not.toBeNull();
    expect(result!.approved).toBe(true);
    expect(result!.status).toBe("Active");
    expect(result!.customerNumber).toBeTruthy();
  });

  test("credit check returns NotFound before any ERP customer exists", async () => {
    // Deliberately no wait: check immediately after creation. The request still
    // round-trips; ERP answers NotFound (or the sync already won the race and it
    // answers Active) — either way a typed reply arrives, no timeout.
    const account = await crm.createAccount({
      legalName: `CreditCheck Unsynced ${Date.now()}`,
      countryCode: "US",
    });

    const result = await crm.creditCheck(account.id);

    expect(result).not.toBeNull();
    expect(["NotFound", "Active"]).toContain(result!.status);
  });

  test("credit check times out while ERP is in maintenance mode", async () => {
    const account = await crm.createAccount({
      legalName: `CreditCheck Timeout ${Date.now()}`,
      countryCode: "US",
    });
    await waitFor(
      async () => await erp.findCustomerByCrmAccountId(account.id),
      { timeoutMs: Timeouts.propagationMs, description: `ERP customer for account ${account.id}` },
    );

    await erp.setServiceMode(true);
    try {
      const result = await crm.creditCheck(account.id);
      // 504 → helper returns null.
      expect(result).toBeNull();
    } finally {
      await erp.setServiceMode(false);
    }
  });
});
