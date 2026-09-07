import { test, expect, directions } from "../helpers/lifecycle-fixture.js";
import { adapterCommand } from "../helpers/aspire-control.js";

for (const direction of directions) {
  test(`${direction} receiver restart preserves a blocked session and queued work`, async ({ harness }) => {
    const flow = await harness.createFlow(direction);
    const resource = direction === "crm-to-erp" ? "erp-adapter" : "crm-adapter";
    await flow.script(["fail"]); await flow.update("head");
    const [failed] = await flow.status("Failed");
    await flow.update("deferred"); await flow.status("Deferred");
    try {
      await adapterCommand(resource, "stop");
      await flow.update("queued");
      expect((await flow.target())?.legalName).toBe(flow.name);
    } finally { await adapterCommand(resource, "start"); }
    await flow.status("Deferred", 2);
    await harness.nimbus.resubmit(failed.eventId, failed.lastMessageId);
    await flow.status("Completed", 3); await flow.targetRevision("queued"); await flow.clean();
    expect((await flow.control.attempts(flow.session)).map(attempt => attempt.action)).toEqual(["fail", "continue", "continue", "continue"]);
  });
}
