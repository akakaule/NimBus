import { test, expect, directions } from "../helpers/lifecycle-fixture.js";
import { waitFor } from "../helpers/wait-for.js";

for (const direction of directions) {
  test(`${direction} repeated transient failures exhaust broker delivery without a NimBus retry`, async ({ harness }) => {
    const flow = await harness.createFlow(direction);
    await flow.script(Array(10).fill("transient"));
    await flow.update("poison");
    const letters = await waitFor(async () => {
      const messages = await flow.control.deadletters(flow.session);
      return messages.length === 1 ? messages : null;
    }, { timeoutMs: 180_000, description: "Broker MaxDeliveryCountExceeded after three deliveries" });
    expect(letters[0].deadLetterReason).toBe("MaxDeliveryCountExceeded");
    const attempts = await flow.control.attempts(flow.session);
    expect(attempts).toHaveLength(3);
    expect(new Set(attempts.map(attempt => attempt.messageId)).size).toBe(1);
    expect((await flow.target())?.legalName).toBe(flow.name);
    const history = await harness.nimbus.history(flow.endpoint, attempts[0].eventId);
    expect(history.some(message => /retryrequest/i.test(message.messageType))).toBe(false);
    await flow.script([]); await flow.update("healthy"); await flow.targetRevision("healthy");
  });
}
