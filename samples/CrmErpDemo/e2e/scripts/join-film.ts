// Join the five narrated acts into one film.
//
//   npm run demo:film
//
// Stream-copies demo-film/act*.mp4 into demo-film/nimbus-demo.mp4 (no re-encode —
// mux-narration.ts gives every act identical codec settings) and merges the per-act
// WebVTT into one subtitle track with the offsets shifted to the joined timeline.

import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";

const FILM_DIR = path.resolve(process.cwd(), "demo-film");
const OUTPUT = path.join(FILM_DIR, "nimbus-demo.mp4");
const SUBTITLES = path.join(FILM_DIR, "nimbus-demo.vtt");

if (spawnSync("ffmpeg", ["-version"], { encoding: "utf8" }).status !== 0) {
  console.error("ffmpeg is not on PATH. Install it, then re-run:\n  winget install Gyan.FFmpeg");
  process.exit(1);
}

const acts = fs.existsSync(FILM_DIR)
  ? fs.readdirSync(FILM_DIR).filter((f) => /^act\d.*\.mp4$/.test(f)).sort()
  : [];

if (acts.length < 2) {
  console.error(`Need at least two acts in ${FILM_DIR}. Run \`npm run demo:mux\` first.`);
  process.exit(1);
}

function durationMs(file: string): number {
  const probe = spawnSync(
    "ffprobe",
    ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file],
    { encoding: "utf8" },
  );
  const seconds = Number.parseFloat((probe.stdout ?? "").trim());
  if (!Number.isFinite(seconds)) throw new Error(`ffprobe could not read the duration of ${file}`);
  return Math.round(seconds * 1000);
}

// ── video ────────────────────────────────────────────────────────────
const listFile = path.join(FILM_DIR, ".concat.txt");
fs.writeFileSync(listFile, acts.map((act) => `file '${act.replace(/'/g, "'\\''")}'`).join("\n") + "\n", "utf8");

const result = spawnSync(
  "ffmpeg",
  ["-y", "-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy", "-movflags", "+faststart", OUTPUT],
  { encoding: "utf8", cwd: FILM_DIR },
);
fs.rmSync(listFile, { force: true });

if (result.status !== 0) {
  console.error(`ffmpeg concat failed:\n${result.stderr?.slice(-2000)}`);
  process.exit(1);
}

// ── subtitles, shifted onto the joined timeline ──────────────────────
function shiftVtt(body: string, offsetMs: number): string {
  const toMs = (stamp: string): number => {
    const [hours, minutes, rest] = stamp.split(":");
    const [seconds, millis = "0"] = (rest ?? "0").split(".");
    return (
      Number(hours) * 3_600_000 + Number(minutes) * 60_000 + Number(seconds) * 1000 + Number(millis.padEnd(3, "0"))
    );
  };
  const toStamp = (ms: number): string => {
    const pad = (value: number, width = 2) => String(value).padStart(width, "0");
    return `${pad(Math.floor(ms / 3_600_000))}:${pad(Math.floor(ms / 60_000) % 60)}:${pad(
      Math.floor(ms / 1000) % 60,
    )}.${pad(Math.floor(ms % 1000), 3)}`;
  };
  return body.replace(
    /(\d{2}:\d{2}:\d{2}\.\d{3}) --> (\d{2}:\d{2}:\d{2}\.\d{3})/g,
    (_match, from: string, to: string) => `${toStamp(toMs(from) + offsetMs)} --> ${toStamp(toMs(to) + offsetMs)}`,
  );
}

let offset = 0;
const blocks: string[] = [];
for (const act of acts) {
  const vtt = path.join(FILM_DIR, act.replace(/\.mp4$/, ".vtt"));
  if (fs.existsSync(vtt)) {
    const body = fs.readFileSync(vtt, "utf8").replace(/^WEBVTT\s*/, "").trim();
    if (body) blocks.push(shiftVtt(body, offset));
  }
  offset += durationMs(path.join(FILM_DIR, act));
}
if (blocks.length) fs.writeFileSync(SUBTITLES, `WEBVTT\n\n${blocks.join("\n\n")}\n`, "utf8");

console.log(
  `${acts.length} acts joined -> ${path.relative(process.cwd(), OUTPUT)} ` +
    `(${(offset / 1000 / 60).toFixed(1)} minutes)`,
);
if (blocks.length) console.log(`Subtitles -> ${path.relative(process.cwd(), SUBTITLES)}`);
