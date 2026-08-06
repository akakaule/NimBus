// Voice-over script for the demo film — the single source of truth for what the
// narrator says.
//
// These lines mirror the Narration column of ../../docs/demo-video-script.md, but
// they are written to be *spoken*, not read: code identifiers are spelled the way
// a person says them (`CrmAccountCreated` -> "CRM Account Created"), "2am" becomes
// "two a.m.", and "FIFO" is spelled out. Keep the markdown script as the editorial
// document and this file as the recording script; when they disagree, this one is
// what ships in the film.
//
// Ids match the shot numbers in the markdown script, so a line can always be traced
// back to the shot it belongs to. Shot 3.10 is deliberately absent — the script
// marks it "not recorded". Act 0 is the cold open: typography only, no product
// footage, so it can be re-cut without a running AppHost.
//
// Imported by:
//   - stage/voice.ts        (Playwright, via the ./narration.js specifier)
//   - ../../scripts/*.ts    (plain Node, via the ./narration.ts specifier)
// so it must stay a leaf module with no imports of its own.

export interface NarrationLine {
  /** Shot id from docs/demo-video-script.md, e.g. "2.4". */
  readonly id: string;
  /** 0 is the cold open, which has no product footage of its own. */
  readonly act: 0 | 1 | 2 | 3 | 4 | 5;
  /** What the narrator says. Spoken form — see the note above. */
  readonly text: string;
}

export const NARRATION: readonly NarrationLine[] = [
  // ── Act 0 — Cold open ────────────────────────────────────────────
  {
    id: "0.1",
    act: 0,
    text: "NimBus is an integration platform for Azure. It connects business systems that were never designed to talk to each other, over Azure Service Bus — and it keeps every message ordered, audited, and recoverable.",
  },
  {
    id: "0.2",
    act: 0,
    text: "Moving a message between two systems is the easy part. The hard part is two a.m. on a Tuesday, when something downstream is broken: knowing which message failed, what was in it, what is now stuck behind it, and who can put it right without a redeploy.",
  },
  {
    id: "0.3",
    act: 0,
    text: "So rather than tour the features, we'll follow one customer account through five situations. A normal round trip between a CRM and an ERP. A system failing, and an operator fixing it from a browser. A slow external import that finishes minutes later. Questions and instructions, not just notifications. And an outside partner running no NimBus code at all.",
  },
  {
    id: "0.4",
    act: 0,
    text: "None of this is a mock-up. Everything you're about to see is a live system: two applications, a real Azure Service Bus namespace, and the NimBus operator console.",
  },

  // ── Act 1 — The happy path ───────────────────────────────────────
  {
    id: "1.1",
    act: 1,
    text: "This is a CRM and an ERP. Separate databases, separate APIs, separate deployment models. The CRM adapter is a worker container; the ERP adapter is an Azure Functions app. Same handler code in both.",
  },
  {
    id: "1.2",
    act: 1,
    text: "Everything starts the way it does in real life: somebody creates a customer in the CRM.",
  },
  {
    id: "1.3",
    act: 1,
    text: "Legal name, country, tax I.D. Nothing about messaging in this form — the CRM API just saves the row and publishes CRM Account Created.",
  },
  {
    id: "1.4",
    act: 1,
    text: "The account is saved. The ERP sync column says pending — the event is on its way across the bus.",
  },
  {
    id: "1.5",
    act: 1,
    text: "And there it is. The ERP created a customer, published its own event back, and the CRM wrote the ERP customer number onto its own row. That round trip crossed two topics and two hosting models.",
  },
  {
    id: "1.6",
    act: 1,
    text: "Same customer on the ERP side, tagged with the origin it came from.",
  },
  {
    id: "1.7",
    act: 1,
    text: "And this is what makes NimBus different from raw Service Bus: a full audit trail. Both events, same session key, in order — without anybody writing logging code.",
  },

  // ── Act 2 — Something breaks ─────────────────────────────────────
  {
    id: "2.1",
    act: 2,
    text: "Let's break the ERP. Every inbound handler will now throw — this is the integration equivalent of the downstream system falling over at two a.m.",
  },
  {
    id: "2.2",
    act: 2,
    text: "The CRM doesn't know or care. It saves its row and publishes, exactly as before. That's the point of decoupling.",
  },
  {
    id: "2.3",
    act: 2,
    text: "Now two more edits to the same account, hard on the heels of the first event.",
  },
  {
    id: "2.4",
    act: 2,
    text: "Here's the behaviour you don't get for free. The first message failed. The two updates behind it did not overtake it — they're deferred, parked behind the failure, because they share a session key. Order is preserved through the outage.",
  },
  {
    id: "2.5",
    act: 2,
    text: "The operator can see the real exception, the payload, and the delivery history. No log diving.",
  },
  { id: "2.6", act: 2, text: "We fix the ERP." },
  {
    id: "2.7",
    act: 2,
    text: "And the operator resubmits — one click, from a browser, no redeploy and no message surgery.",
  },
  {
    id: "2.8",
    act: 2,
    text: "The head succeeds, the session unblocks, and the parked updates replay in first-in, first-out order automatically. Nobody had to touch the deferred messages.",
  },
  {
    id: "2.9",
    act: 2,
    text: "The ERP ends up with the most recent version — not whichever message happened to win a race.",
  },

  // ── Act 3 — The slow external system ─────────────────────────────
  {
    id: "3.1",
    act: 3,
    text: "Real ERPs often don't do the work while you wait. They accept a job and finish it later. NimBus has a first-class state for that.",
  },
  { id: "3.2", act: 3, text: "New account, nothing special about it." },
  {
    id: "3.3",
    act: 3,
    text: "And immediately, two edits to the same account — published while the external import is still open.",
  },
  {
    id: "3.4",
    act: 3,
    text: "The message isn't failed and it isn't complete. It's pending — the handler said an external job has this, and returned. The broker message is settled, but the audit trail knows it isn't finished.",
  },
  {
    id: "3.5",
    act: 3,
    text: "They defer. The session stays ordered across an asynchronous, out-of-process wait — not just across a retry.",
  },
  {
    id: "3.6",
    act: 3,
    text: "It carries the external job I.D. and when we expect it back — so an operator chases the actual import, not just the message.",
  },
  {
    id: "3.7",
    act: 3,
    text: "Meanwhile the simulated ERP import counts down to settlement.",
  },
  {
    id: "3.8",
    act: 3,
    text: "The import finishes, calls back into NimBus, the original message completes, and the deferred edits replay in order. No operator involvement at all.",
  },
  {
    id: "3.9",
    act: 3,
    text: "Through a sixty second external wait and two concurrent edits.",
  },

  // ── Act 4 — Beyond fire-and-forget ───────────────────────────────
  {
    id: "4.1",
    act: 4,
    text: "Some things aren't notifications. Can this customer buy on credit right now — that's a question, and you need the answer before you respond to the user.",
  },
  {
    id: "4.2",
    act: 4,
    text: "Request reply over Service Bus sessions. Typed request, typed answer, back in well under a second — and the request itself is a normal audited NimBus event.",
  },
  {
    id: "4.3",
    act: 4,
    text: "This one is different again. It's not a notification and it's not a question — it's an instruction. NimBus models that as a Command, and the platform refuses to start if a command has anything other than exactly one consumer.",
  },
  {
    id: "4.4",
    act: 4,
    text: "The ERP applied the hold. Notice nothing was published back — a command doesn't owe you an event.",
  },
  {
    id: "4.5",
    act: 4,
    text: "And running the credit check again returns on hold. The command and the query just validated each other.",
  },

  // ── Act 5 — The ecosystem close ──────────────────────────────────
  {
    id: "5.1",
    act: 5,
    text: "This contact didn't come from the CRM or the ERP. It came from a simulated third-party system that references only the raw Azure Service Bus SDK — no NimBus packages at all.",
  },
  {
    id: "5.2",
    act: 5,
    text: "It publishes plain Cloud Events. NimBus consumes them natively, synthesising its own routing from the envelope, and publishes its own events back out as Cloud Events that the partner reads without knowing what NimBus is.",
  },
  {
    id: "5.3",
    act: 5,
    text: "And all of it — two internal systems, an external partner, sync and async — lands in one operator surface with one audit trail.",
  },
  { id: "5.4", act: 5, text: "That's NimBus." },
];

export const NARRATION_BY_ID: ReadonlyMap<string, NarrationLine> = new Map(
  NARRATION.map((line) => [line.id, line]),
);

/** Lines of one act, in script order. Used for prosody stitching at synthesis time. */
export function actLines(act: number): readonly NarrationLine[] {
  return NARRATION.filter((line) => line.act === act);
}
