import { randomUUID } from "node:crypto";
import { test, expect } from "../helpers/lifecycle-fixture.js";
import { E2eControl } from "../helpers/e2e-control.js";
import { waitFor } from "../helpers/wait-for.js";
import { outboxFailureLog } from "../helpers/aspire-control.js";

test("ERP rollback commits neither a customer nor an outbox event", async ({ harness }) => {
  const control = await E2eControl.create("erp");
  try {
    const id = await control.rollback();
    expect(await harness.erp.getCustomer(id)).toBeNull();
    expect(await control.outbox(id)).toEqual({ pending: 0, total: 0 });
    expect(await harness.erp.getAuditLog("Customer", id)).toHaveLength(0);
    expect(await harness.nimbus.searchEvents("CrmEndpoint", { sessionId: id })).toHaveLength(0);
  } finally { await control.dispose(); }
});

test("ERP committed outbox survives broker send rejection and dispatches after recovery", async ({ harness }, testInfo) => {
  const control = await E2eControl.create("erp");
  const original = await harness.nimbus.channelState("ErpEndpoint", "send");
  try {
    await harness.nimbus.setChannelState("ErpEndpoint", "send", false);
    const customer = await harness.erp.createCustomer({ legalName: `E2E-outbox-${randomUUID()}`, countryCode: "DE" });
    expect(await harness.erp.getCustomer(customer.id)).not.toBeNull();
    await waitFor(async () => (await control.outbox(customer.id)).pending === 1 ? true : null,
      { timeoutMs: 30_000, description: "Committed event retained in ERP SQL outbox" });
    const failure = await waitFor(() => outboxFailureLog(customer.id),
      { timeoutMs: 90_000, intervalMs: 2_000, description: "Real outbox dispatcher failed to send this session" });
    await testInfo.attach("outbox-send-rejection", { body: failure, contentType: "application/json" });
    expect((await control.outbox(customer.id)).pending).toBe(1);
    expect((await harness.crm.listAccounts()).some(account => account.erpCustomerId === customer.id)).toBe(false);
    await harness.nimbus.setChannelState("ErpEndpoint", "send", true);
    await waitFor(async () => (await harness.crm.listAccounts()).find(account => account.erpCustomerId === customer.id) ?? null,
      { timeoutMs: 120_000, description: "Outbox dispatch reaches CRM after broker recovery" });
    await waitFor(async () => (await control.outbox(customer.id)).pending === 0 ? true : null,
      { timeoutMs: 60_000, description: "Outbox dispatched marker committed" });
    expect((await harness.crm.listAccounts()).filter(account => account.erpCustomerId === customer.id)).toHaveLength(1);
  } finally {
    await harness.nimbus.setChannelState("ErpEndpoint", "send", original === "active");
    await control.dispose();
  }
});
