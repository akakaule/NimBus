import { test, expect } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { Timeouts } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

/**
 * Command showcase: PlaceCustomerOnCreditHold is an imperative message with
 * exactly one consumer (ErpEndpoint, enforced by platform validation). The hold
 * becomes observable through the ERP customer record and through the next
 * credit check returning OnHold — the command and request/reply showcases
 * prove each other.
 */
test.describe("Command: PlaceCustomerOnCreditHold", () => {
  let crm: CrmApiClient;
  let erp: ErpApiClient;

  test.beforeAll(async () => {
    crm = await CrmApiClient.create();
    erp = await ErpApiClient.create();
    await erp.resetFailureModes();
  });

  test.afterAll(async () => {
    await crm.dispose();
    await erp.dispose();
  });

  test("credit hold command flips the ERP customer and the next credit check", async () => {
    // 1. Synced account, initially approved.
    const account = await crm.createAccount({
      legalName: `HoldMe Ltd ${Date.now()}`,
      countryCode: "GB",
    });
    await waitFor(
      async () => await erp.findCustomerByCrmAccountId(account.id),
      { timeoutMs: Timeouts.propagationMs, description: `ERP customer for account ${account.id}` },
    );
    const before = await crm.creditCheck(account.id);
    expect(before?.approved).toBe(true);

    // 2. Fire the command.
    await crm.placeCreditHold(account.id, "e2e credit hold");

    // 3. ERP customer flips to creditHold=true.
    await waitFor(
      async () => {
        const customer = await erp.findCustomerByCrmAccountId(account.id);
        return customer?.creditHold ? customer : null;
      },
      { timeoutMs: Timeouts.propagationMs, description: `credit hold applied for account ${account.id}` },
    );

    // 4. The next credit check reports OnHold — request/reply reading the
    //    state the command wrote.
    const after = await crm.creditCheck(account.id);
    expect(after).not.toBeNull();
    expect(after!.approved).toBe(false);
    expect(after!.status).toBe("OnHold");
  });
});
