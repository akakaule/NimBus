// On-camera presentation helpers for the demo recording harness.
//
// These exist purely to make Playwright's output watchable as a video: burned-in
// captions, act title cards, element spotlights, and deliberate pacing. Nothing
// here asserts anything — the acts assert on real cross-system state using the
// same helpers as the regression suite in ../../tests.

import { test, type Locator, type Page } from "@playwright/test";
import fs from "node:fs";
import path from "node:path";
import { beginNarrationTimeline, writeNarrationCues } from "./voice.js";

export const FOOTAGE_DIR = path.resolve(process.cwd(), "demo-footage");

// Re-exported so acts import their whole on-camera vocabulary from one module.
export { narrate, startNarration } from "./voice.js";

/**
 * Deliberate pacing. A viewer needs roughly 2.5s to read a caption headline plus
 * detail line, so shots are held far longer than a functional test would need.
 */
export const Pace = {
  beat: 800,
  read: 2500,
  hold: 3800,
} as const;

const CAPTION_ID = "__nimbus_demo_caption";
const CHIP_ID = "__nimbus_demo_chip";
const CARD_ID = "__nimbus_demo_card";

/**
 * Bottom-left caption card. Persists across SPA route changes (it is appended to
 * <body>, outside the React root) but is lost on a full page load, so `visit()`
 * re-applies the act chip and callers re-caption after navigating.
 */
export async function caption(page: Page, headline: string, detail = ""): Promise<void> {
  await page.evaluate(
    ({ id, headline, detail }) => {
      let el = document.getElementById(id);
      if (!el) {
        el = document.createElement("div");
        el.id = id;
        el.style.cssText = [
          "position:fixed", "left:34px", "bottom:34px", "z-index:2147483645",
          // Kept under ~44% so the caption never covers the Status column of the
          // operator tables, which is the whole point of the Act 2 and 3 shots.
          "max-width:44%", "padding:18px 24px", "border-radius:14px",
          "background:rgba(9,16,32,0.94)", "color:#f8fafc",
          "border-left:5px solid #38bdf8",
          "box-shadow:0 18px 45px rgba(0,0,0,0.45)",
          "font-family:'Segoe UI',system-ui,-apple-system,sans-serif",
          "pointer-events:none", "opacity:0", "transform:translateY(10px)",
          "transition:opacity 300ms ease,transform 300ms ease",
        ].join(";");
        document.body.appendChild(el);
      }
      el.innerHTML =
        `<div style="font-size:23px;font-weight:650;letter-spacing:-0.01em;line-height:1.25">${headline}</div>` +
        (detail
          ? `<div style="font-size:15.5px;color:#a9bad2;margin-top:7px;line-height:1.45">${detail}</div>`
          : "");
      const node = el;
      requestAnimationFrame(() => {
        node.style.opacity = "1";
        node.style.transform = "translateY(0)";
      });
    },
    { id: CAPTION_ID, headline, detail },
  );
  await page.waitForTimeout(Pace.beat);
}

/** Top-right act marker, so a viewer scrubbing the footage knows where they are. */
export async function actChip(page: Page, label: string): Promise<void> {
  await page.evaluate(
    ({ id, label }) => {
      let el = document.getElementById(id);
      if (!el) {
        el = document.createElement("div");
        el.id = id;
        el.style.cssText = [
          "position:fixed", "right:28px", "top:22px", "z-index:2147483645",
          "padding:7px 15px", "border-radius:999px",
          "background:rgba(9,16,32,0.88)", "color:#7dd3fc",
          "font-family:'Segoe UI',system-ui,sans-serif", "font-size:12.5px",
          "font-weight:600", "letter-spacing:0.06em", "text-transform:uppercase",
          "pointer-events:none", "box-shadow:0 6px 18px rgba(0,0,0,0.3)",
        ].join(";");
        document.body.appendChild(el);
      }
      el.textContent = label;
    },
    { id: CHIP_ID, label },
  );
}

/** Full-screen act title card. Fades in, holds, fades out, removes itself. */
export async function titleCard(
  page: Page,
  act: string,
  title: string,
  subtitle: string,
  holdMs = 3200,
): Promise<void> {
  await page.evaluate(
    ({ id, act, title, subtitle }) => {
      const el = document.createElement("div");
      el.id = id;
      el.style.cssText = [
        // Above the caption and chip so an act boundary is always a clean cut.
        "position:fixed", "inset:0", "z-index:2147483647",
        "background:linear-gradient(135deg,#050b18 0%,#0b1a33 100%)",
        "display:flex", "flex-direction:column",
        "align-items:center", "justify-content:center",
        "font-family:'Segoe UI',system-ui,-apple-system,sans-serif",
        "opacity:0", "transition:opacity 500ms ease", "pointer-events:none",
      ].join(";");
      el.innerHTML =
        `<div style="color:#38bdf8;font-size:14px;font-weight:700;letter-spacing:0.22em;text-transform:uppercase">${act}</div>` +
        `<div style="color:#f8fafc;font-size:52px;font-weight:680;margin-top:16px;letter-spacing:-0.02em;text-align:center;max-width:76%">${title}</div>` +
        `<div style="color:#93a7c4;font-size:19px;margin-top:16px;text-align:center;max-width:60%;line-height:1.5">${subtitle}</div>`;
      document.body.appendChild(el);
      requestAnimationFrame(() => { el.style.opacity = "1"; });
    },
    { id: CARD_ID, act, title, subtitle },
  );
  await page.waitForTimeout(holdMs);
  await page.evaluate((id) => {
    const el = document.getElementById(id);
    if (!el) return;
    el.style.opacity = "0";
    setTimeout(() => el.remove(), 600);
  }, CARD_ID);
  await page.waitForTimeout(700);
}

/** Navigate and re-apply the act chip (a full page load drops the overlays). */
export async function visit(page: Page, url: string, chipLabel: string): Promise<void> {
  await page.goto(url, { waitUntil: "domcontentloaded" });
  await page.waitForTimeout(900);
  await actChip(page, chipLabel);
}

/** Amber ring pulse around an element, so the viewer's eye lands where it should. */
export async function spotlight(target: Locator, ms = 1600): Promise<void> {
  await target.scrollIntoViewIfNeeded();
  await target.evaluate((el, ms) => {
    const node = el as HTMLElement;
    const prevShadow = node.style.boxShadow;
    const prevRadius = node.style.borderRadius;
    const prevTransition = node.style.transition;
    node.style.transition = "box-shadow 220ms ease";
    node.style.borderRadius = prevRadius || "6px";
    node.style.boxShadow = "0 0 0 3px #f59e0b, 0 0 0 10px rgba(245,158,11,0.22)";
    setTimeout(() => {
      node.style.boxShadow = prevShadow;
      node.style.borderRadius = prevRadius;
      node.style.transition = prevTransition;
    }, ms);
  }, ms);
  await target.page().waitForTimeout(ms);
}

/**
 * Spotlight one of the endpoint-details status tiles (FAILED / DEFERRED /
 * PENDING). These sit above the fold and are never covered by the caption, so
 * they carry the operator shots better than the table rows do. Best-effort — a
 * missing tile is not worth failing an act over.
 */
export async function spotlightStatTile(page: Page, label: string, ms = 2200): Promise<void> {
  const tile = page.getByText(label, { exact: true }).first().locator("xpath=..");
  if (await tile.count()) await spotlight(tile, ms);
}

/** Human-speed typing, so form fills read as deliberate rather than instant. */
export async function typeInto(field: Locator, text: string): Promise<void> {
  await field.scrollIntoViewIfNeeded();
  await field.click();
  await field.fill("");
  await field.pressSequentially(text, { delay: 55 });
  await field.page().waitForTimeout(Pace.beat);
}

/**
 * Register the hooks that persist this act's video under a stable name, plus the
 * narration cue sheet that pairs with it. Call once at the top of each act spec.
 * `page.video().saveAs()` needs the page closed first, which is why the close is
 * explicit here.
 *
 * The cue sheet is written beside the video as `<act>.cues.json` and consumed by
 * `npm run demo:mux`. Timeline zero is set in beforeEach, as close to the start of
 * the recording as a test hook can get.
 */
export function recordFootage(fileName: string): void {
  // Requesting `page` here matters: it forces the page fixture (and with it the
  // browser launch and the start of the video) to be created BEFORE timeline zero.
  // Without it the hook runs first and every cue is offset by the launch time —
  // measured at ~4.7s, which is a wildly out-of-sync film.
  test.beforeEach(async ({ page }) => {
    void page;
    beginNarrationTimeline();
  });

  test.afterEach(async ({ page }) => {
    const video = page.video();
    if (!video) return;
    await page.close();
    fs.mkdirSync(FOOTAGE_DIR, { recursive: true });
    await video.saveAs(path.join(FOOTAGE_DIR, fileName));
    writeNarrationCues(path.join(FOOTAGE_DIR, fileName.replace(/\.webm$/, ".cues.json")));
  });
}
