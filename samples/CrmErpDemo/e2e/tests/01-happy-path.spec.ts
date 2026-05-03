import { test, expect } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { NimBusApiClient } from "../helpers/nimbus-api-client.js";
import { Timeouts } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

test.describe("Bidirectional happy-path propagation", () => {
  let crm: CrmApiClient;
  let erp: ErpApiClient;
  let nimbus: NimBusApiClient;
  // Captured at suite start so we can assert THIS test didn't leak a new failure
  // even if prior runs left dead-lettered messages behind on the live broker.
  let baseline: Record<string, { failed: number; deadletter: number }> = {};

  test.beforeAll(async () => {
    crm = await CrmApiClient.create();
    erp = await ErpApiClient.create();
    nimbus = await NimBusApiClient.create();
    // Make sure we start from a clean failure-mode state.
    await erp.resetFailureModes();
    const counts = await nimbus.getStatusCounts(["CrmEndpoint", "ErpEndpoint"]);
    baseline = Object.fromEntries(
      counts.map((c) => [c.endpointId, { failed: c.failedCount, deadletter: c.deadletterCount }]),
    );
  });

  test.afterAll(async () => {
    await erp.resetFailureModes();
    await crm.dispose();
    await erp.dispose();
    await nimbus.dispose();
  });

  test("CRM-originated account propagates to ERP and links back", async () => {
    const legalName = `Acme Robotics ${Date.now()}`;

    // 1. Operator creates an account in CRM.
    const crmAccount = await crm.createAccount({
      legalName,
      countryCode: "US",
      taxId: `TAX-${Date.now()}`,
    });
    expect(crmAccount.id).toBeTruthy();

    // 2. ERP receives CrmAccountCreated, creates a Customer with crmAccountId set.
    const erpCustomer = await waitFor(
      async () => await erp.findCustomerByCrmAccountId(crmAccount.id),
      { timeoutMs: Timeouts.propagationMs, description: `ERP customer linked to CRM account ${crmAccount.id}` },
    );
    expect(erpCustomer.legalName).toBe(legalName);
    expect(erpCustomer.crmAccountId).toBe(crmAccount.id);

    // 3. ERP publishes ErpCustomerCreated (origin=Crm) which loops back to CRM and
    //    populates the erpCustomerId / erpCustomerNumber fields on the CRM account.
    const linkedCrmAccount = await waitFor(
      async () => {
        const fresh = await crm.getAccount(crmAccount.id);
        return fresh && fresh.erpCustomerId ? fresh : null;
      },
      { timeoutMs: Timeouts.propagationMs, description: `CRM account ${crmAccount.id} linked back to ERP customer` },
    );
    expect(linkedCrmAccount.erpCustomerId).toBe(erpCustomer.id);
    expect(linkedCrmAccount.erpCustomerNumber).toBeTruthy();

    // 4. NimBus admin: this happy-path flow added no new failures or dead-letters
    //    beyond whatever was already on the broker when the suite started.
    const counts = await nimbus.getStatusCounts(["CrmEndpoint", "ErpEndpoint"]);
    for (const c of counts) {
      expect.soft(c.failedCount, `${c.endpointId} failedCount`).toBeLessThanOrEqual(baseline[c.endpointId]?.failed ?? 0);
      expect.soft(c.deadletterCount, `${c.endpointId} deadletterCount`).toBeLessThanOrEqual(baseline[c.endpointId]?.deadletter ?? 0);
    }
  });

  test("ERP-originated customer propagates to CRM", async () => {
    const legalName = `Globex Ltd ${Date.now()}`;

    // 1. Operator creates a customer in ERP directly.
    const erpCustomer = await erp.createCustomer({
      legalName,
      countryCode: "DK",
    });
    expect(erpCustomer.id).toBeTruthy();
    expect(erpCustomer.customerNumber).toBeTruthy();

    // 2. CRM receives ErpCustomerCreated (origin=Erp) and upserts an Account
    //    with origin="Erp", linked back to the ERP customer.
    const crmAccount = await waitFor(
      async () => {
        const accounts = await crm.listAccounts();
        return accounts.find((a) => a.erpCustomerId === erpCustomer.id && !a.isDeleted) ?? null;
      },
      { timeoutMs: Timeouts.propagationMs, description: `CRM account upserted from ERP customer ${erpCustomer.id}` },
    );
    expect(crmAccount.legalName).toBe(legalName);
    expect(crmAccount.origin).toBe("Erp");
    expect(crmAccount.erpCustomerNumber).toBe(erpCustomer.customerNumber);

    // 3. NimBus admin: this happy-path flow added no new failures or dead-letters.
    const counts = await nimbus.getStatusCounts(["CrmEndpoint", "ErpEndpoint"]);
    for (const c of counts) {
      expect.soft(c.failedCount, `${c.endpointId} failedCount`).toBeLessThanOrEqual(baseline[c.endpointId]?.failed ?? 0);
      expect.soft(c.deadletterCount, `${c.endpointId} deadletterCount`).toBeLessThanOrEqual(baseline[c.endpointId]?.deadletter ?? 0);
    }
  });
});
