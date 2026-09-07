import { randomUUID } from "node:crypto";
import { test, expect } from "@playwright/test";
import { E2eControl } from "../helpers/e2e-control.js";
import { NimBusApiClient } from "../helpers/nimbus-api-client.js";
import { waitFor } from "../helpers/wait-for.js";

for (const side of ["crm", "erp"] as const) {
  for (const kind of ["missing-payload", "null-payload", "invalid-json", "deep-json", "mismatched-type", "missing-event-type", "missing-event-id", "unsupported"]) {
    test(`${side} publishes ${kind}: exact wire disposition without invoking a handler`, async ({}, testInfo) => {
      const source = await E2eControl.create(side);
      const destination = await E2eControl.create(side === "crm" ? "erp" : "crm");
      const nimbus = await NimBusApiClient.create();
      const session = randomUUID();
      try {
        const type = side === "crm" ? "CrmAccountUpdated" : "ErpCustomerUpdated";
        await destination.script(session, type, []);
        const sent = await source.wire(session, kind);
        if (kind === "unsupported") {
          await waitFor(async () => (await nimbus.searchEvents(sent.endpoint, { eventId: sent.eventId,
            resolutionStatus: ["Unsupported"] })).length === 1 ? true : null,
          { timeoutMs: 60_000, description: "Unknown handler is Unsupported" });
          expect(await destination.deadletters(session)).toHaveLength(0);
        } else {
          const deadletters = await waitFor(async () => {
            const messages = await destination.deadletters(session);
            return messages.some(message => message.messageId === sent.messageId) ? messages : null;
          }, { timeoutMs: 60_000, description: `${kind} reached the actual broker DLQ` });
          expect(deadletters.filter(message => message.messageId === sent.messageId)).toHaveLength(1);
          expect(deadletters[0].deadLetterReason).toBeTruthy();
          expect(deadletters[0].deadLetterReason).not.toBe("MaxDeliveryCountExceeded");
          if (kind !== "missing-event-id") {
            await waitFor(async () => (await nimbus.searchEvents(sent.endpoint, { eventId: sent.eventId,
              resolutionStatus: ["DeadLettered"] })).length === 1 ? true : null,
            { timeoutMs: 60_000, description: "Resolver recorded DeadLettered" });
          }
        }
        expect(await destination.attempts(session)).toHaveLength(0);
        await testInfo.attach("wire-evidence", { body: JSON.stringify({ sent,
          events: await nimbus.searchEvents(sent.endpoint, { sessionId: session }),
          deadletters: await destination.deadletters(session),
        }, null, 2), contentType: "application/json" });
      } finally { await destination.remove(session); await Promise.all([source.dispose(), destination.dispose(), nimbus.dispose()]); }
    });
  }
}
