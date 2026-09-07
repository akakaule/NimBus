import { test, expect, directions } from "../helpers/lifecycle-fixture.js";
import { waitFor } from "../helpers/wait-for.js";

for (const direction of directions) {
  test(`${direction} contact create, update and delete preserve parent and origin`, async ({ harness }) => {
    const flow = await harness.createFlow(direction);
    const source = direction === "crm-to-erp" ? harness.crm : harness.erp;
    const target = direction === "crm-to-erp" ? harness.erp : harness.crm;
    const data = { firstName: "E2E", lastName: flow.name, email: "e2e@example.invalid", phone: "123",
      ...(direction === "crm-to-erp" ? { accountId: flow.session } : { customerId: flow.session }) };
    const contact = await source.createContact(data);
    const mirrored = await waitFor(() => target.getContact(contact.id), { timeoutMs: 60_000, description: "Contact creation propagated" });
    expect(mirrored.firstName).toBe(data.firstName);
    expect(mirrored.email).toBe(data.email);
    expect(mirrored.origin).toBe(direction === "crm-to-erp" ? "Crm" : "Erp");
    expect("customerId" in mirrored ? mirrored.customerId : (mirrored as { accountId?: string }).accountId).toBe(flow.targetId);
    await source.updateContact(contact.id, { ...data, firstName: "Updated", phone: "456" });
    const updated = await waitFor(async () => {
      const result = await target.getContact(contact.id);
      return result?.firstName === "Updated" && result.phone === "456" ? result : null;
    }, { timeoutMs: 60_000, description: "Contact update propagated" });
    expect(updated.origin).toBe(mirrored.origin);
    await source.deleteContact(contact.id);
    await waitFor(async () => (await target.getContact(contact.id))?.isDeleted ? true : null,
      { timeoutMs: 60_000, description: "Contact deletion propagated" });
    const prefix = direction === "crm-to-erp" ? "Crm" : "Erp";
    await waitFor(async () => {
      const events = await harness.nimbus.searchEvents(flow.endpoint, { sessionId: contact.id,
        eventTypeId: ["Created", "Updated", "Deleted"].map(suffix => `${prefix}Contact${suffix}`), resolutionStatus: ["Completed"] });
      return events.length === 3 ? true : null;
    }, { timeoutMs: 60_000, description: "All three contact events completed" });
  });
}
