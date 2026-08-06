import { test } from "@playwright/test";
import { agenda, fadeOut, introBackdrop, statement, wordmark } from "./stage/intro.js";
import { narrate, recordFootage, startNarration } from "./stage/stage.js";

// ACT 0 — Cold open.
// Says what NimBus is and what the next seven minutes contain, so the viewer has a
// frame before Act 1 drops them into a CRM. Script: ../docs/demo-video-script.md#act-0.
//
// The only act with no product on screen and no dependency on the AppHost: it draws
// its own typography into about:blank. That means the opening — the part most likely
// to be re-cut when the pitch changes — can be re-recorded on its own in a minute,
// then `npm run demo:mux -- act0 && npm run demo:film` rebuilds the film around it.

recordFootage("act0-intro.webm");

test("Act 0 — what NimBus is, and what follows", async ({ page }) => {
  await introBackdrop(page);

  // ── What it is ─────────────────────────────────────────────────
  await wordmark(page, "Azure-native integration you can actually operate");
  await narrate(page, "0.1");

  // ── Why it exists ──────────────────────────────────────────────
  await statement(
    page,
    "Moving the message is the easy part",
    "Every integration platform can get an event from A to B. The difference shows up on the night it breaks.",
  );
  await narrate(page, "0.2");

  // ── What's coming ──────────────────────────────────────────────
  // Paced to run under the line rather than ahead of it: 0.3 is the longest line in
  // the film at 23s, so five items at 3.8s fill about 19s and the finished list
  // holds for the last few seconds instead of sitting static for ten.
  const runningOrder = startNarration(page, "0.3");
  await agenda(
    page,
    "One account · five situations",
    [
      "A round trip: CRM to ERP",
      "Something breaks — and is repaired",
      "A slow external system",
      "Questions and commands",
      "A partner outside the platform",
    ],
    3800,
  );
  await runningOrder;

  // ── It's real ──────────────────────────────────────────────────
  await statement(
    page,
    "A live system, not a mock-up",
    "Two applications, a real Azure Service Bus namespace, and the NimBus operator console.",
  );
  await narrate(page, "0.4");

  await fadeOut(page);
});
