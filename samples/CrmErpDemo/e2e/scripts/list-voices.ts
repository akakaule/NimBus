// List the voices and models the ElevenLabs account can actually use, so the voice
// for the demo film is chosen from what exists rather than from a blog post.
//
//   npm run demo:voices
//
// Copy an id into e2e/.env.local as ELEVENLABS_VOICE_ID (and optionally
// ELEVENLABS_MODEL_ID), then run `npm run demo:voice`.

import { loadEnv } from "./load-env.ts";

loadEnv();

const API_KEY = process.env.ELEVENLABS_API_KEY;
if (!API_KEY) {
  console.error("ELEVENLABS_API_KEY is not set. Put it in e2e/.env.local (gitignored).");
  process.exit(1);
}

async function get(path: string): Promise<unknown> {
  const response = await fetch(`https://api.elevenlabs.io/v1/${path}`, {
    headers: { "xi-api-key": API_KEY! },
  });
  if (!response.ok) {
    throw new Error(`GET /v1/${path} -> ${response.status} ${await response.text().catch(() => "")}`);
  }
  return response.json();
}

const voices = (await get("voices")) as {
  voices?: { voice_id: string; name: string; category?: string; labels?: Record<string, string> }[];
};

console.log("VOICES\n");
for (const voice of voices.voices ?? []) {
  const labels = Object.values(voice.labels ?? {}).filter(Boolean).join(", ");
  console.log(`  ${voice.voice_id}  ${voice.name.padEnd(22)} ${voice.category ?? ""}${labels ? ` — ${labels}` : ""}`);
}

const models = (await get("models")) as {
  model_id: string;
  name?: string;
  can_do_text_to_speech?: boolean;
  description?: string;
}[];

console.log("\nMODELS (text-to-speech capable)\n");
for (const model of models) {
  if (model.can_do_text_to_speech === false) continue;
  console.log(`  ${model.model_id.padEnd(28)} ${model.name ?? ""}`);
}

console.log(
  "\nSet these in e2e/.env.local:\n" +
    "  ELEVENLABS_VOICE_ID=<voice_id from above>\n" +
    "  ELEVENLABS_MODEL_ID=<model_id from above>",
);
