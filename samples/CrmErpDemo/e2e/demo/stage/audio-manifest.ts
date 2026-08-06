// Shared shape of demo-audio/manifest.json — the contract between the three
// stages of the voice pipeline:
//
//   scripts/synthesize-narration.ts  writes it  (one entry per narration line)
//   demo/stage/voice.ts              reads it   (to pace shots by clip length)
//   scripts/mux-narration.ts         reads it   (to resolve cue -> audio file)
//
// Leaf module, no imports — see the note in narration.ts about the dual
// .ts / .js specifier loading.

/** One synthesized narration clip. */
export interface AudioClip {
  /** Shot id, e.g. "2.4". */
  readonly id: string;
  /** File name inside demo-audio/, content-hashed so edits invalidate it. */
  readonly file: string;
  readonly durationMs: number;
  /** How durationMs was obtained — ffprobe is exact, cbr is a bitrate estimate. */
  readonly durationSource: "ffprobe" | "cbr";
  /** Hash of text + voice + model + settings. Identical hash means a cache hit. */
  readonly hash: string;
  /** The spoken text, kept here so the mux step can emit subtitles. */
  readonly text: string;
}

export interface AudioManifest {
  readonly voiceId: string;
  readonly modelId: string;
  /** ISO timestamp of the last synthesis run. */
  readonly generatedAt: string;
  readonly clips: readonly AudioClip[];
}

export const AUDIO_DIR_NAME = "demo-audio";
export const MANIFEST_NAME = "manifest.json";
