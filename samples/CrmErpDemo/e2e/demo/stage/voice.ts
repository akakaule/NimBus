// Narration playback — or rather, narration *timing*.
//
// Playwright's video recording has no audio track, so nothing here plays a sound.
// Instead each narration line does two things: it holds the shot for exactly as
// long as the line takes to say, and it records where in the act that line starts.
// `scripts/mux-narration.ts` then mixes the clips onto the footage at those
// offsets, which is what keeps voice and picture in sync across re-records even
// though act durations vary with real Service Bus latency.
//
// Without demo-audio/manifest.json (nobody has run `npm run demo:voice`) every
// call degrades to the old fixed Pace timing, so the harness still records silent
// footage exactly as it did before.

import fs from "node:fs";
import path from "node:path";
import type { Page } from "@playwright/test";
import { AUDIO_DIR_NAME, MANIFEST_NAME, type AudioClip, type AudioManifest } from "./audio-manifest.js";
import { NARRATION_BY_ID } from "./narration.js";

export const AUDIO_DIR = path.resolve(process.cwd(), AUDIO_DIR_NAME);

/**
 * Fine adjustment applied to every cue, in milliseconds — positive moves the voice
 * later. Timeline zero is anchored to page creation (see recordFootage), so this
 * should stay close to zero; it exists to trim residual drift by ear against a
 * finished cut without touching code.
 */
const START_SKEW_MS = Number(process.env.NARRATION_OFFSET_MS ?? 0);

/** Silence after a line before the next thing happens, so cues don't butt together. */
const TAIL_MS = 450;

/** Fallback hold when there is no audio for a line — the old Pace.read. */
const SILENT_HOLD_MS = 2500;

export interface NarrationCue {
  readonly id: string;
  readonly file: string;
  /** Milliseconds from the start of this act's video. */
  readonly atMs: number;
  readonly durationMs: number;
  readonly text: string;
}

let clips: ReadonlyMap<string, AudioClip> | null = null;
let manifestChecked = false;
let cues: NarrationCue[] = [];
let timelineStart = 0;

function loadClips(): ReadonlyMap<string, AudioClip> {
  if (clips) return clips;
  const manifestPath = path.join(AUDIO_DIR, MANIFEST_NAME);
  if (!fs.existsSync(manifestPath)) {
    clips = new Map();
    if (!manifestChecked) {
      manifestChecked = true;
      // eslint-disable-next-line no-console
      console.warn(
        `[voice] no ${AUDIO_DIR_NAME}/${MANIFEST_NAME} — recording silent footage with fixed pacing.\n` +
          "[voice] run `npm run demo:voice` first to pace the shots to the narration.",
      );
    }
    return clips;
  }
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8")) as AudioManifest;
  clips = new Map(manifest.clips.map((clip) => [clip.id, clip]));
  return clips;
}

/** Called per act by recordFootage(); resets the cue list and timeline zero. */
export function beginNarrationTimeline(): void {
  timelineStart = Date.now();
  cues = [];
}

export function narrationCues(): readonly NarrationCue[] {
  return cues;
}

/** Persist this act's cue sheet next to its .webm for the mux step. */
export function writeNarrationCues(filePath: string): void {
  if (!cues.length) return;
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, `${JSON.stringify({ cues }, null, 2)}\n`, "utf8");
}

/**
 * Start narrating a line and return a promise that resolves when it has finished
 * speaking. Await it immediately to hold a static shot for the whole line, or keep
 * the promise and await it later to let the narration run *over* the next few
 * interactions:
 *
 * ```ts
 * const said = startNarration(page, "2.7");   // talks over the clicking
 * await resubmitButton.click();
 * await said;                                 // don't start the next line early
 * ```
 */
export function startNarration(page: Page, id: string): Promise<void> {
  const line = NARRATION_BY_ID.get(id);
  if (!line) throw new Error(`[voice] no narration line with id "${id}" — check demo/stage/narration.ts`);

  const clip = loadClips().get(id);
  if (!clip) {
    return page.waitForTimeout(SILENT_HOLD_MS);
  }

  cues.push({
    id,
    file: clip.file,
    atMs: Math.max(0, Date.now() - timelineStart + START_SKEW_MS),
    durationMs: clip.durationMs,
    text: line.text,
  });

  return page.waitForTimeout(clip.durationMs + TAIL_MS);
}

/** Narrate a line and hold the shot until it is finished. */
export async function narrate(page: Page, id: string): Promise<void> {
  await startNarration(page, id);
}
