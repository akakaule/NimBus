import { test, expect, directions } from "../helpers/lifecycle-fixture.js";

for (const direction of directions) {
  test.describe(`${direction} handoff contract on real handlers`, () => {
    test("duplicate and stale settlements never release a newer blocked event", async ({ harness }) => {
      const flow = await harness.createFlow(direction);
      await flow.script(["pending", "pending"]);
      await flow.update("first");
      const [first] = await flow.status("Pending");
      const [coordinates] = await flow.control.attempts(flow.session);
      await flow.control.settle(flow.session, flow.eventType, coordinates, "complete");
      await flow.status("Completed");
      await flow.update("second");
      const [second] = await flow.status("Pending");
      expect(second.eventId).not.toBe(first.eventId);
      await flow.control.settle(flow.session, flow.eventType, coordinates, "complete");
      await flow.control.settle(flow.session, flow.eventType, coordinates, "fail");
      await flow.update("tail"); await flow.status("Deferred");
      expect((await flow.status("Pending"))[0].eventId).toBe(second.eventId);
      expect((await flow.target())?.legalName).toBe(flow.name);
      await harness.nimbus.settleHandoff(flow.endpoint, second, "complete");
      await flow.status("Completed", 3); await flow.targetRevision("tail"); await flow.clean();
      expect(await flow.control.attempts(flow.session)).toHaveLength(3);
    });

    test("pending is observable, becomes overdue, and completion invokes no handler", async ({ harness }) => {
      const flow = await harness.createFlow(direction);
      await flow.script(["pending"]); await flow.update("external");
      const [pending] = await flow.status("Pending");
      expect(pending.pendingSubStatus).toBe("Handoff");
      expect(pending.handoffReason).toBe("E2E external work");
      expect(pending.externalJobId).toBe(`e2e-${pending.eventId}`);
      expect(pending.expectedBy).toBeTruthy();
      await flow.update("after"); await flow.status("Deferred");
      expect((await flow.target())?.legalName).toBe(flow.name);
      await new Promise(resolve => setTimeout(resolve, Math.max(0, Date.parse(pending.expectedBy!) - Date.now()) + 1_000));
      expect((await flow.status("Pending"))[0].pendingSubStatus).toBe("Handoff");
      await harness.nimbus.settleHandoff(flow.endpoint, pending, "complete");
      const completed = await flow.status("Completed", 2); await flow.targetRevision("after");
      expect(completed.find(event => event.eventId === pending.eventId)?.pendingSubStatus).toBeFalsy();
      expect((await flow.control.attempts(flow.session)).map(attempt => attempt.action)).toEqual(["pending", "continue"]);
      expect((await flow.audit()).some(row => row.newValue === `${flow.name}-external`)).toBe(false);
      await flow.clean();
    });

    for (const recovery of ["skip", "resubmit", "handoff-again"] as const) {
      test(`failed handoff with siblings recovers through ${recovery}`, async ({ harness }) => {
        const flow = await harness.createFlow(direction);
        await flow.script(["pending"]); await flow.update("external");
        const [pending] = await flow.status("Pending");
        await flow.update("tail"); await flow.status("Deferred");
        await harness.nimbus.settleHandoff(flow.endpoint, pending, "fail", "E2E external rejection");
        const [failed] = await flow.status("Failed");
        expect(JSON.stringify(failed)).toContain("E2E external rejection");
        expect(failed.pendingSubStatus).toBeFalsy();
        expect((await flow.target())?.legalName).toBe(flow.name);
        expect(await flow.control.attempts(flow.session)).toHaveLength(1);
        if (recovery === "skip") {
          await harness.nimbus.skipMessage(failed.eventId, failed.lastMessageId);
          await flow.status("Skipped"); await flow.status("Completed");
        } else {
          if (recovery === "handoff-again") await flow.script(["pending"]);
          await harness.nimbus.resubmit(failed.eventId, failed.lastMessageId);
          if (recovery === "handoff-again") {
            const [again] = await flow.status("Pending");
            expect(again.eventId).toBe(pending.eventId);
            await flow.status("Deferred");
            await harness.nimbus.settleHandoff(flow.endpoint, again, "complete");
          }
          await flow.status("Completed", 2);
        }
        await flow.targetRevision("tail"); await flow.clean();
        const attempts = await flow.control.attempts(flow.session);
        expect(new Set(attempts.filter(attempt => attempt.eventId === pending.eventId)
          .map(attempt => attempt.originatingMessageId)).size).toBe(1);
      });
    }

    test("last handoff declaration wins", async ({ harness }) => {
      const flow = await harness.createFlow(direction);
      await flow.script(["pending-twice"]); await flow.update("external");
      const [pending] = await flow.status("Pending");
      expect(pending.handoffReason).toBe("E2E final declaration");
      expect(pending.externalJobId).toBe(`final-${pending.eventId}`);
      expect(pending.expectedBy).toBeFalsy();
      await harness.nimbus.settleHandoff(flow.endpoint, pending, "complete");
      await flow.status("Completed");
      expect(await flow.control.attempts(flow.session)).toHaveLength(1);
    });

    test("an exception after declaring handoff takes the failure path", async ({ harness }) => {
      const flow = await harness.createFlow(direction);
      await flow.script(["pending-throw"]); await flow.update("bad");
      const [failed] = await flow.status("Failed");
      expect(failed.pendingSubStatus).toBeFalsy();
      expect(await flow.events("Pending")).toHaveLength(0);
      expect((await flow.target())?.legalName).toBe(flow.name);
    });
  });
}
