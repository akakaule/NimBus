import { test as base, expect } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { CrmApiClient } from "./crm-api-client.js";
import { ErpApiClient } from "./erp-api-client.js";
import { E2eControl, type Fault } from "./e2e-control.js";
import { NimBusApiClient, type NimBusEvent } from "./nimbus-api-client.js";
import { Timeouts } from "./service-urls.js";
import { waitFor } from "./wait-for.js";

export type Direction = "crm-to-erp" | "erp-to-crm";
export const directions: Direction[] = ["crm-to-erp", "erp-to-crm"];

export class Flow {
  constructor(readonly direction: Direction, readonly session: string, readonly targetId: string,
    readonly name: string, readonly crm: CrmApiClient, readonly erp: ErpApiClient,
    readonly nimbus: NimBusApiClient, readonly control: E2eControl) {}
  get endpoint() { return this.direction === "crm-to-erp" ? "ErpEndpoint" : "CrmEndpoint"; }
  get eventType() { return this.direction === "crm-to-erp" ? "CrmAccountUpdated" : "ErpCustomerUpdated"; }
  async script(actions: Fault[], stage = "handler") { await this.control.script(this.session, this.eventType, actions, stage); }
  async update(revision: string) {
    const data = { legalName: `${this.name}-${revision}`, taxId: revision, countryCode: "DE" };
    if (this.direction === "crm-to-erp") await this.crm.updateAccount(this.session, data);
    else await this.erp.updateCustomer(this.session, data);
  }
  async target() { return this.direction === "crm-to-erp" ? this.erp.getCustomer(this.targetId) : this.crm.getAccount(this.targetId); }
  async targetRevision(revision: string) {
    return await waitFor(async () => {
      const target = await this.target();
      return target?.legalName === `${this.name}-${revision}` && target.taxId === revision ? target : null;
    }, { timeoutMs: Timeouts.propagationMs, description: `${this.direction} target revision ${revision}` });
  }
  async events(status?: string): Promise<NimBusEvent[]> {
    return this.nimbus.searchEvents(this.endpoint, { sessionId: this.session, eventTypeId: [this.eventType],
      ...(status ? { resolutionStatus: [status] } : {}) });
  }
  async status(status: string, count = 1): Promise<NimBusEvent[]> {
    return waitFor(async () => {
      const events = (await this.events(status)).filter(event => status !== "Pending" || event.pendingSubStatus === "Handoff");
      return events.length === count ? events : null;
    }, { timeoutMs: Timeouts.propagationMs, description: `${count} ${status} ${this.eventType} in ${this.session}` });
  }
  async audit(): Promise<Array<{ action: string; fieldName: string; newValue: string; timestamp: string }>> {
    return (this.direction === "crm-to-erp" ? await this.erp.getAuditLog("Customer", this.targetId)
      : await this.crm.getAuditLog("Account", this.targetId)) as Array<{ action: string; fieldName: string; newValue: string; timestamp: string }>;
  }
  async clean() {
    await waitFor(async () => {
      const outstanding = await this.nimbus.searchEvents(this.endpoint, { sessionId: this.session,
        resolutionStatus: ["Failed", "Pending", "Deferred", "DeadLettered", "Unsupported"] });
      return outstanding.length === 0 ? true : null;
    }, { timeoutMs: Timeouts.propagationMs, description: `No unfinished messages in ${this.session}` });
  }
}

interface Harness {
  crm: CrmApiClient; erp: ErpApiClient; nimbus: NimBusApiClient;
  createFlow(direction: Direction): Promise<Flow>;
}

export const test = base.extend<{ harness: Harness }>({
  harness: async ({}, use, testInfo) => {
    const crm = await CrmApiClient.create();
    const erp = await ErpApiClient.create();
    const nimbus = await NimBusApiClient.create();
    const flows: Flow[] = [];
    const controls: E2eControl[] = [];
    const crmMode = await crm.getErrorMode();
    const erpError = await erp.getErrorMode();
    const service = await erp.getServiceMode();
    const handoff = await erp.getHandoffMode();
    try {
      await crm.resetFailureModes(); await erp.resetFailureModes(); await erp.resetHandoffMode();
      await use({ crm, erp, nimbus, async createFlow(direction) {
        const name = `E2E-${randomUUID()}`;
        const control = await E2eControl.create(direction === "crm-to-erp" ? "erp" : "crm");
        controls.push(control);
        let session: string, targetId: string;
        if (direction === "crm-to-erp") {
          const source = await crm.createAccount({ legalName: name, countryCode: "DE" });
          session = source.id;
          const target = await waitFor(() => erp.findCustomerByCrmAccountId(source.id),
            { timeoutMs: Timeouts.propagationMs, description: "CRM account reaches ERP" });
          targetId = target.id;
        } else {
          const source = await erp.createCustomer({ legalName: name, countryCode: "DE" });
          session = source.id;
          const target = await waitFor(async () => (await crm.listAccounts()).find(account => account.erpCustomerId === source.id) ?? null,
            { timeoutMs: Timeouts.propagationMs, description: "ERP customer reaches CRM" });
          targetId = target.id;
        }
        const flow = new Flow(direction, session, targetId, name, crm, erp, nimbus, control);
        flows.push(flow);
        await flow.clean();
        return flow;
      } });
    } finally {
      const cleanupErrors: unknown[] = [];
      // One failed diagnostic or cleanup must not prevent restoring the remaining controls.
      for (const flow of flows) {
        try {
          await testInfo.attach(`evidence-${flow.session}`, { body: JSON.stringify({
            events: await flow.events(), attempts: await flow.control.attempts(flow.session), audit: await flow.audit(),
          }, null, 2), contentType: "application/json" });
          await flow.script([]);
          for (const event of await flow.events("Pending")) {
            if (event.pendingSubStatus !== "Handoff") continue;
            await nimbus.settleHandoff(flow.endpoint, event, "fail");
            await waitFor(async () => (await flow.events("Failed")).find(failed => failed.eventId === event.eventId) ?? null,
              { timeoutMs: 30_000, description: "Cleanup handoff failure reached Resolver" });
          }
          for (const event of await flow.events("Failed")) await nimbus.skipMessage(event.eventId, event.lastMessageId);
        } catch (error) { cleanupErrors.push(error); }
        finally {
          try { await flow.control.remove(flow.session); } catch (error) { cleanupErrors.push(error); }
        }
      }
      const restored = await Promise.allSettled([crm.setErrorMode(crmMode.enabled), erp.setErrorMode(erpError.enabled),
        erp.setServiceMode(service.enabled), erp.setHandoffMode(handoff)]);
      cleanupErrors.push(...restored.filter(result => result.status === "rejected").map(result => result.reason));
      await Promise.all([...controls.map(control => control.dispose()), crm.dispose(), erp.dispose(), nimbus.dispose()]);
      if (cleanupErrors.length > 0) throw new AggregateError(cleanupErrors, "E2E cleanup or evidence collection failed");
    }
  },
});
export { expect };
