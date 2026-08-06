// Synthesize the demo film's voice-over with ElevenLabs.
//
//   npm run demo:voice            # synthesize everything that changed
//   npm run demo:voice -- --force # re-synthesize even unchanged lines
//   npm run demo:voice -- --act 2 # only act 2
//
// Output: demo-audio/<hash>.mp3 plus demo-audio/manifest.json, which the recording
// harness reads to pace each shot and the mux step reads to place each clip.
//
// Clips are cached on a hash of (text + voice + model + settings), so re-running
// after editing one line re-synthesizes that line only. The whole script is about
// 4,500 characters — a full re-render is a rounding error on any paid plan, but the
// cache keeps iteration instant as well as cheap.
//
// Run with plain node (v22.6+ strips the types): `node scripts/synthesize-narration.ts`.

import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { AUDIO_DIR_NAME, MANIFEST_NAME, type AudioClip, type AudioManifest } from "../demo/stage/audio-manifest.ts";
import { NARRATION, actLines, type NarrationLine } from "../demo/stage/narration.ts";
import { loadEnv } from "./load-env.ts";

loadEnv();

const API_KEY = process.env.ELEVENLABS_API_KEY;
// "Daniel" from the ElevenLabs shared library — measured, documentary-ish delivery
// that suits a technical product film. Run `npm run demo:voices` to list what your
// account actually has and set ELEVENLABS_VOICE_ID to switch.
const VOICE_ID = process.env.ELEVENLABS_VOICE_ID ?? "onwK4e9ZLuTAKqWW03F9";
const MODEL_ID = process.env.ELEVENLABS_MODEL_ID ?? "eleven_multilingual_v2";

// mp3_44100_128 is available on every tier and is constant bitrate, which lets us
// derive clip duration from file size when ffprobe isn't installed.
const OUTPUT_FORMAT = "mp3_44100_128";
const CBR_BYTES_PER_MS = 128_000 / 8 / 1000;

const VOICE_SETTINGS = {
  // Higher stability than the default: narration should not drift in tone between
  // clips that get mixed onto one timeline.
  stability: 0.55,
  similarity_boost: 0.8,
  // A little style, not a performance. High style values make it emote over
  // technical terms.
  style: 0.15,
  use_speaker_boost: true,
};

const AUDIO_DIR = path.resolve(process.cwd(), AUDIO_DIR_NAME);
const MANIFEST_PATH = path.join(AUDIO_DIR, MANIFEST_NAME);

const args = process.argv.slice(2);
const force = args.includes("--force");
const actArg = args.indexOf("--act");
// Explicit null check, not truthiness: `--act 0` (the cold open) is a real act, and
// treating 0 as "no filter" silently re-renders the entire film.
const onlyAct = actArg >= 0 ? Number(args[actArg + 1]) : null;
if (onlyAct !== null && !Number.isInteger(onlyAct)) {
  console.error(`--act needs an act number (0-5), got "${args[actArg + 1] ?? ""}".`);
  process.exit(1);
}

if (!API_KEY) {
  console.error("ELEVENLABS_API_KEY is not set. Put it in e2e/.env.local (gitignored):");
  console.error("  ELEVENLABS_API_KEY=sk_...");
  process.exit(1);
}

function hashOf(line: NarrationLine): string {
  return crypto
    .createHash("sha256")
    .update(JSON.stringify({ text: line.text, VOICE_ID, MODEL_ID, OUTPUT_FORMAT, VOICE_SETTINGS }))
    .digest("hex")
    .slice(0, 16);
}

/** Exact duration when ffprobe is on PATH, bitrate estimate otherwise. */
function durationOf(file: string, bytes: number): { durationMs: number; source: "ffprobe" | "cbr" } {
  const probe = spawnSync(
    "ffprobe",
    ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file],
    { encoding: "utf8" },
  );
  const parsed = Number.parseFloat((probe.stdout ?? "").trim());
  if (probe.status === 0 && Number.isFinite(parsed) && parsed > 0) {
    return { durationMs: Math.round(parsed * 1000), source: "ffprobe" };
  }
  return { durationMs: Math.round(bytes / CBR_BYTES_PER_MS), source: "cbr" };
}

async function synthesize(line: NarrationLine, previousText: string, nextText: string): Promise<Buffer> {
  const response = await fetch(
    `https://api.elevenlabs.io/v1/text-to-speech/${VOICE_ID}?output_format=${OUTPUT_FORMAT}`,
    {
      method: "POST",
      headers: { "xi-api-key": API_KEY!, "content-type": "application/json" },
      body: JSON.stringify({
        text: line.text,
        model_id: MODEL_ID,
        voice_settings: VOICE_SETTINGS,
        // Prosody stitching: the neighbouring lines are context only — they are not
        // spoken. Without them every clip starts cold and the act sounds like a list
        // of sentences rather than a paragraph.
        previous_text: previousText || undefined,
        next_text: nextText || undefined,
      }),
    },
  );

  if (!response.ok) {
    const body = await response.text().catch(() => "");
    if (response.status === 401) {
      throw new Error(`ElevenLabs rejected the API key (401). ${body}`);
    }
    if (response.status === 404) {
      throw new Error(
        `Voice "${VOICE_ID}" not found (404). Run \`npm run demo:voices\` and set ELEVENLABS_VOICE_ID. ${body}`,
      );
    }
    if (response.status === 422) {
      throw new Error(
        `ElevenLabs rejected the request (422) — usually an unavailable model_id for your plan. ` +
          `Current model: "${MODEL_ID}". Run \`npm run demo:voices\` to see what is available. ${body}`,
      );
    }
    if (response.status === 429) {
      throw new Error(`Rate limited or out of credits (429). ${body}`);
    }
    throw new Error(`ElevenLabs returned ${response.status}. ${body}`);
  }

  return Buffer.from(await response.arrayBuffer());
}

const lines = onlyAct === null ? NARRATION : actLines(onlyAct);
if (!lines.length) {
  console.error(`No narration lines for act ${onlyAct}.`);
  process.exit(1);
}

fs.mkdirSync(AUDIO_DIR, { recursive: true });

// Keep clips for lines we're not regenerating this run (e.g. `--act 2`), so a
// single-act re-render doesn't strand the other four acts without audio.
const existing = new Map<string, AudioClip>();
if (fs.existsSync(MANIFEST_PATH)) {
  const previous = JSON.parse(fs.readFileSync(MANIFEST_PATH, "utf8")) as AudioManifest;
  if (previous.voiceId === VOICE_ID && previous.modelId === MODEL_ID) {
    for (const clip of previous.clips) existing.set(clip.id, clip);
  } else {
    console.log(`Voice or model changed (${previous.voiceId}/${previous.modelId} -> ${VOICE_ID}/${MODEL_ID}); rebuilding all clips.`);
  }
}

console.log(`Voice ${VOICE_ID}, model ${MODEL_ID} — ${lines.length} line(s).`);

let synthesized = 0;
let cached = 0;
let characters = 0;

for (const line of lines) {
  const hash = hashOf(line);
  const file = `${line.id.replace(".", "-")}.${hash}.mp3`;
  const absolute = path.join(AUDIO_DIR, file);

  if (!force && fs.existsSync(absolute) && existing.get(line.id)?.hash === hash) {
    cached++;
    continue;
  }

  // Context for prosody: the neighbours within the same act only — an act boundary
  // is a hard cut in the film, so the voice should reset there too.
  const siblings = actLines(line.act);
  const index = siblings.findIndex((candidate) => candidate.id === line.id);
  const audio = await synthesize(
    line,
    index > 0 ? siblings[index - 1]!.text : "",
    index < siblings.length - 1 ? siblings[index + 1]!.text : "",
  );

  fs.writeFileSync(absolute, audio);
  const { durationMs, source } = durationOf(absolute, audio.byteLength);
  existing.set(line.id, { id: line.id, file, durationMs, durationSource: source, hash, text: line.text });

  synthesized++;
  characters += line.text.length;
  console.log(`  ${line.id.padEnd(4)} ${(durationMs / 1000).toFixed(1)}s  ${line.text.slice(0, 58)}…`);
}

// Manifest order follows the script, not the order things were regenerated in.
const clips = NARRATION.map((line) => existing.get(line.id)).filter((clip): clip is AudioClip => Boolean(clip));

const manifest: AudioManifest = {
  voiceId: VOICE_ID,
  modelId: MODEL_ID,
  generatedAt: new Date().toISOString(),
  clips,
};
fs.writeFileSync(MANIFEST_PATH, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");

const totalMs = clips.reduce((sum, clip) => sum + clip.durationMs, 0);
const probed = clips.filter((clip) => clip.durationSource === "ffprobe").length;

console.log(
  `\n${synthesized} synthesized, ${cached} cached, ${characters} characters billed this run.\n` +
    `${clips.length} clips, ${(totalMs / 1000 / 60).toFixed(1)} minutes of narration total.`,
);
if (probed < clips.length) {
  console.log(
    `Note: ${clips.length - probed} clip duration(s) estimated from bitrate — install ffmpeg for exact values.`,
  );
}
