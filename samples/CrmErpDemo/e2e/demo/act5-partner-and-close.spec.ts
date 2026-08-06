import { expect, test } from "@playwright/test";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { resolveWebUrls, type WebUrls } from "./stage/demo-urls.js";
import { Pace, actChip, caption, narrate, recordFootage, spotlight, startNarration, titleCard, visit } from "./stage/stage.js";

// ACT 5 — The ecosystem close.
// Widens out: NimBus is not a closed world. PartnerPortal references only
// Azure.Messaging.ServiceBus and speaks plain CloudEvents 1.0 in both directions.
// Script: ../docs/demo-video-script.md#act-5.

const CHIP = "Act 5 · Interop";

let webs: WebUrls;
let erp: ErpApiClient;

recordFootage("act5-partner-and-close.webm");

test.beforeAll(async () => {
  webs = await resolveWebUrls();
  erp = await ErpApiClient.create();
  await erp.resetFailureModes();
  await erp.resetHandoffMode();
});

test.afterAll(async () => {
  await erp.dispose();
});

test("Act 5 — an external partner with zero NimBus code, and the operator surface", async ({ page }) => {
  await visit(page, `${webs.crmWeb}/contacts`, CHIP);
  await titleCard(
    page,
    "Act 5",
    "Not a closed world",
    "The third system in this demo has never heard of NimBus.",
  );

  // partner-portal publishes a lead every 45s; the contacts list does not poll,
  // so reload until one shows up.
  const partnerRow = page.locator("tr").filter({ hasText: "Partner" }).first();
  await caption(page, "CRM contacts", "Waiting for the next partner lead to arrive over the bus…");
  await expect(async () => {
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForTimeout(1200);
    await actChip(page, CHIP);
    await expect(partnerRow).toBeVisible({ timeout: 5_000 });
  }).toPass({ timeout: 180_000 });

  await caption(
    page,
    "A partner with zero NimBus code",
    "This lead came from a simulated third party that references only the raw Azure Service Bus SDK — no NimBus packages at all.",
  );
  const zeroNimBusCode = startNarration(page, "5.1");
  await spotlight(partnerRow, 2600);
  await zeroNimBusCode;

  await caption(
    page,
    "Plain CloudEvents 1.0, in and out",
    "The partner stamps none of NimBus's routing properties. NimBus synthesises routing from the CloudEvent envelope at consume time — and publishes its own events back out in the same format.",
  );
  await narrate(page, "5.2");

  // ── One operator surface over all of it ────────────────────────
  await visit(page, "/Endpoints", CHIP);
  await caption(
    page,
    "One operator surface over all of it",
    "Two internal systems, an external partner, synchronous and asynchronous traffic — one audit trail, one place to fix things.",
  );
  await narrate(page, "5.3");

  await caption(page, "NimBus", "Azure-native integration you can actually operate.");
  await narrate(page, "5.4");
  // Hold past the last word so the film doesn't cut on the closing syllable.
  await page.waitForTimeout(Pace.hold + 1200);
});
