// Visuals for the cold open (Act 0).
//
// Unlike the other acts, the intro shows no product — it is typography over a
// backdrop, drawn into an about:blank page. That is deliberate: the opening is the
// piece most likely to be re-cut when the pitch changes, and keeping it free of the
// AppHost means it can be re-recorded on its own in about a minute, with no live
// Service Bus namespace and no warm-up.

import type { Page } from "@playwright/test";

const STAGE_ID = "__nimbus_intro_stage";

const FONT = "'Segoe UI',system-ui,-apple-system,sans-serif";

/** Dark backdrop that every intro beat is drawn onto. Call once, first. */
export async function introBackdrop(page: Page): Promise<void> {
  await page.goto("about:blank");
  await page.evaluate(
    ({ id, font }) => {
      document.body.style.cssText = "margin:0;padding:0;overflow:hidden";
      const stage = document.createElement("div");
      stage.id = id;
      stage.style.cssText = [
        "position:fixed", "inset:0",
        "background:radial-gradient(1200px 700px at 50% 35%,#12294d 0%,#050b18 62%)",
        `font-family:${font}`,
        "display:flex", "flex-direction:column",
        "align-items:center", "justify-content:center",
        "text-align:center",
      ].join(";");
      document.body.appendChild(stage);
    },
    { id: STAGE_ID, font: FONT },
  );
}

/** Cross-fade the centre of the stage to new content. */
async function swap(page: Page, html: string, fadeMs = 420): Promise<void> {
  await page.evaluate(
    ({ id, html, fadeMs }) => {
      const stage = document.getElementById(id);
      if (!stage) return;
      const previous = stage.firstElementChild as HTMLElement | null;
      if (previous) {
        previous.style.transition = `opacity ${fadeMs}ms ease`;
        previous.style.opacity = "0";
        setTimeout(() => previous.remove(), fadeMs);
      }
      const next = document.createElement("div");
      next.innerHTML = html;
      next.style.cssText = `opacity:0;transition:opacity ${fadeMs}ms ease,transform ${fadeMs}ms ease;transform:translateY(12px)`;
      setTimeout(
        () => {
          stage.appendChild(next);
          requestAnimationFrame(() => {
            next.style.opacity = "1";
            next.style.transform = "translateY(0)";
          });
        },
        previous ? fadeMs : 0,
      );
    },
    { id: STAGE_ID, html, fadeMs },
  );
  await page.waitForTimeout(fadeMs * 2);
}

/** Opening wordmark. */
export async function wordmark(page: Page, tagline: string): Promise<void> {
  await swap(
    page,
    `<div style="color:#f8fafc;font-size:104px;font-weight:700;letter-spacing:-0.035em;line-height:1">
       Nim<span style="color:#38bdf8">Bus</span>
     </div>
     <div style="color:#93a7c4;font-size:23px;margin-top:22px;letter-spacing:0.01em">${tagline}</div>`,
  );
}

/** A single full-screen statement. */
export async function statement(page: Page, lead: string, detail: string): Promise<void> {
  await swap(
    page,
    `<div style="color:#f8fafc;font-size:46px;font-weight:640;letter-spacing:-0.02em;max-width:20ch;line-height:1.2;margin:0 auto">${lead}</div>
     <div style="color:#93a7c4;font-size:20px;margin-top:22px;max-width:52ch;line-height:1.55;margin-left:auto;margin-right:auto">${detail}</div>`,
  );
}

/**
 * The running order, revealed one line at a time. Returns immediately after the
 * last reveal — the caller decides how long to hold on the finished list, usually
 * by awaiting the narration line that runs underneath it.
 */
export async function agenda(page: Page, heading: string, items: string[], perItemMs = 1500): Promise<void> {
  await swap(
    page,
    `<div style="color:#38bdf8;font-size:13.5px;font-weight:700;letter-spacing:0.24em;text-transform:uppercase">${heading}</div>
     <ol id="__nimbus_intro_agenda" style="list-style:none;margin:30px 0 0;padding:0;text-align:left"></ol>`,
  );

  for (const [index, item] of items.entries()) {
    await page.evaluate(
      ({ item, index }) => {
        const list = document.getElementById("__nimbus_intro_agenda");
        if (!list) return;
        const li = document.createElement("li");
        li.style.cssText = [
          "display:flex", "align-items:baseline", "gap:18px",
          "margin:0 0 15px", "opacity:0", "transform:translateX(-10px)",
          "transition:opacity 380ms ease,transform 380ms ease",
        ].join(";");
        li.innerHTML =
          `<span style="color:#38bdf8;font-size:15px;font-weight:700;min-width:2.2ch">${index + 1}</span>` +
          `<span style="color:#e6edf7;font-size:27px;font-weight:520;letter-spacing:-0.01em">${item}</span>`;
        list.appendChild(li);
        requestAnimationFrame(() => {
          li.style.opacity = "1";
          li.style.transform = "translateX(0)";
        });
      },
      { item, index },
    );
    await page.waitForTimeout(perItemMs);
  }
}

/** Fade the whole stage to black — the cut into Act 1. */
export async function fadeOut(page: Page, ms = 900): Promise<void> {
  await page.evaluate(
    ({ id, ms }) => {
      const stage = document.getElementById(id);
      if (!stage) return;
      stage.style.transition = `opacity ${ms}ms ease`;
      stage.style.opacity = "0";
      document.body.style.background = "#050b18";
    },
    { id: STAGE_ID, ms },
  );
  await page.waitForTimeout(ms + 200);
}
