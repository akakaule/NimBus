import { test, expect } from "../helpers/lifecycle-fixture.js";
import { request } from "@playwright/test";
import { ServiceUrls } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

test("concurrent credit checks correlate replies to the correct CRM account", async ({ harness }) => {
  const approved = await harness.createFlow("crm-to-erp");
  const held = await harness.createFlow("crm-to-erp");
  await harness.crm.placeCreditHold(held.session, "E2E correlation");
  await waitFor(async () => (await harness.crm.creditCheck(held.session))?.status === "OnHold" ? true : null,
    { timeoutMs: 60_000, description: "Credit hold command took effect" });
  const replies = await Promise.all([approved, held, approved, held].map(flow => harness.crm.creditCheck(flow.session)));
  for (let i = 0; i < replies.length; i++) {
    expect(replies[i]?.accountId).toBe(i % 2 === 0 ? approved.session : held.session);
    expect(replies[i]?.approved).toBe(i % 2 === 0);
  }
});

test("handler failure webhook and Resolver history carry the failed event identity", async ({ harness }) => {
  const flow = await harness.createFlow("crm-to-erp");
  await flow.script(["fail"]); await flow.update("notification");
  const [failed] = await flow.status("Failed");
  const api = await request.newContext({ baseURL: ServiceUrls.erpApi, ignoreHTTPSErrors: true });
  try {
    const alert = await waitFor(async () => {
      const response = await api.get("/api/admin/alerts");
      if (!response.ok()) throw new Error(`Read alerts: ${response.status()}`);
      const alerts = await response.json() as Array<{ eventId: string; messageId: string; errorDetails: string }>;
      return alerts.find(alert => alert.eventId === failed.eventId && alert.errorDetails.includes("E2E fail")) ?? null;
    }, { timeoutMs: 60_000, description: "Correlated ERP failure notification" });
    const [attempt] = await flow.control.attempts(flow.session);
    expect(alert.messageId).toBe(attempt.messageId);
    const history = await harness.nimbus.history(flow.endpoint, failed.eventId);
    expect(history.some(message => /errorresponse/i.test(message.messageType))).toBe(true);
  } finally { await api.dispose(); }
});
