// Mix the synthesized narration onto the recorded footage.
//
//   npm run demo:mux              # every act that has footage + cues
//   npm run demo:mux -- act3      # one act
//
// Reads demo-footage/<act>.webm plus the <act>.cues.json the harness wrote beside
// it, and produces demo-film/<act>.mp4 with the voice-over placed at the offsets
// the recording actually happened at — which is why this survives a re-record even
// though act lengths vary with real Service Bus latency.
//
// Also writes demo-film/<act>.vtt. Subtitles are not optional for this film:
// LinkedIn and X autoplay muted, so a silent viewer gets nothing without them.
//
// Requires ffmpeg on PATH (winget install Gyan.FFmpeg).

import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { AUDIO_DIR_NAME } from "../demo/stage/audio-manifest.ts";

interface Cue {
  id: string;
  file: string;
  atMs: number;
  durationMs: number;
  text: string;
}

const FOOTAGE_DIR = path.resolve(process.cwd(), "demo-footage");
const AUDIO_DIR = path.resolve(process.cwd(), AUDIO_DIR_NAME);
const FILM_DIR = path.resolve(process.cwd(), "demo-film");

const filter = process.argv.slice(2).find((arg) => !arg.startsWith("-"));

if (spawnSync("ffmpeg", ["-version"], { encoding: "utf8" }).status !== 0) {
  console.error("ffmpeg is not on PATH. Install it, then re-run:\n  winget install Gyan.FFmpeg");
  process.exit(1);
}

if (!fs.existsSync(FOOTAGE_DIR)) {
  console.error(`No ${FOOTAGE_DIR}. Record first: npm run demo`);
  process.exit(1);
}

function timestamp(ms: number): string {
  const hours = Math.floor(ms / 3_600_000);
  const minutes = Math.floor(ms / 60_000) % 60;
  const seconds = Math.floor(ms / 1000) % 60;
  const millis = Math.floor(ms % 1000);
  const pad = (value: number, width = 2) => String(value).padStart(width, "0");
  return `${pad(hours)}:${pad(minutes)}:${pad(seconds)}.${pad(millis, 3)}`;
}

function writeVtt(cues: Cue[], target: string): void {
  const blocks = cues.map(
    (cue) => `${timestamp(cue.atMs)} --> ${timestamp(cue.atMs + cue.durationMs)}\n${cue.text}`,
  );
  fs.writeFileSync(target, `WEBVTT\n\n${blocks.join("\n\n")}\n`, "utf8");
}

const videos = fs
  .readdirSync(FOOTAGE_DIR)
  .filter((file) => file.endsWith(".webm"))
  .filter((file) => !filter || file.includes(filter))
  .sort();

if (!videos.length) {
  console.error(filter ? `No footage matching "${filter}".` : "No .webm files in demo-footage/.");
  process.exit(1);
}

fs.mkdirSync(FILM_DIR, { recursive: true });

let muxed = 0;
for (const video of videos) {
  const base = video.replace(/\.webm$/, "");
  const cuesPath = path.join(FOOTAGE_DIR, `${base}.cues.json`);
  const output = path.join(FILM_DIR, `${base}.mp4`);

  const cues: Cue[] = fs.existsSync(cuesPath)
    ? (JSON.parse(fs.readFileSync(cuesPath, "utf8")).cues as Cue[])
    : [];

  const present = cues.filter((cue) => fs.existsSync(path.join(AUDIO_DIR, cue.file)));
  const missing = cues.length - present.length;
  if (missing) {
    console.warn(`  ${base}: ${missing} cue(s) reference missing audio — re-run \`npm run demo:voice\`.`);
  }

  const args = ["-y", "-i", path.join(FOOTAGE_DIR, video)];
  for (const cue of present) args.push("-i", path.join(AUDIO_DIR, cue.file));

  if (present.length) {
    // One adelay per clip, then a single amix. normalize=0 keeps each clip at its
    // recorded level — amix's default normalisation would duck the whole track by
    // the number of inputs, which for 9 cues is inaudible.
    // `all=1` applies the delay to every channel whatever the clip's layout is —
    // the `adelay=ms|ms` form is per-channel and breaks on mono input.
    const delays = present
      .map((cue, index) => `[${index + 1}:a]adelay=${cue.atMs}:all=1[a${index}]`)
      .join(";");
    const mix = `${present.map((_, index) => `[a${index}]`).join("")}amix=inputs=${present.length}:normalize=0[out]`;
    args.push("-filter_complex", `${delays};${mix}`, "-map", "0:v", "-map", "[out]", "-c:a", "aac", "-b:a", "192k");
  } else {
    args.push("-map", "0:v", "-an");
  }

  // Playwright's VP8 output is variable frame rate; pin it so the mp4 scrubs
  // predictably in an editor. yuv420p for players that reject VP8-native 4:4:4.
  args.push(
    "-c:v", "libx264", "-preset", "slow", "-crf", "20",
    "-pix_fmt", "yuv420p", "-r", "30", "-movflags", "+faststart",
    output,
  );

  process.stdout.write(`  ${base} … `);
  const result = spawnSync("ffmpeg", args, { encoding: "utf8" });
  if (result.status !== 0) {
    console.error(`\nffmpeg failed for ${base}:\n${result.stderr?.slice(-2000)}`);
    process.exit(1);
  }

  if (present.length) writeVtt(present, path.join(FILM_DIR, `${base}.vtt`));
  const spoken = present.reduce((sum, cue) => sum + cue.durationMs, 0);
  console.log(`${present.length} cue(s), ${(spoken / 1000).toFixed(0)}s narration -> ${path.relative(process.cwd(), output)}`);
  muxed++;
}

console.log(`\n${muxed} act(s) written to ${path.relative(process.cwd(), FILM_DIR)}/.`);
console.log("Concatenate to one film with:  npm run demo:film");
