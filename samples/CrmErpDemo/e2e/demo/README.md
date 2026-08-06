# Demo recording harness

Drives the live CrmErpDemo AppHost through the five acts of
[`../../docs/demo-video-script.md`](../../docs/demo-video-script.md) and records
each one as a `.webm`.

This is *not* the regression suite. It shares the API clients in `../helpers/`
with the specs in `../tests/`, but its goal is watchable footage: burned-in
captions, act title cards, element spotlights, and shots held long enough to
read. It still asserts on real cross-system state — see "Captions never outrun
the footage" below.

## Run it

```bash
# 1. AppHost up, against a REAL Azure Service Bus namespace (not the emulator —
#    emulator 2.0.0 drops AMQP connections during warm-up and that lands on camera).
dotnet run --project samples/CrmErpDemo/CrmErpDemo.AppHost

# 2. Warm up once so Functions cold starts and DbUp migrations don't show up in Act 1.
#    Create an account in crm-web and wait for the ERP customer number to appear.

# 3. Synthesize the voice-over (see "Voice-over" below). Do this BEFORE recording —
#    the shots are paced to the narration.
cd samples/CrmErpDemo/e2e
npm run demo:voice

# 4. Record.
npm run demo            # all five acts, in order
npm run demo:act2       # just one act

# 5. Mix the voice onto the footage and join the acts into one film.
npm run demo:mux
npm run demo:film
```

Output lands in `../demo-footage/` (raw silent `.webm` per act plus its `.cues.json`)
and `../demo-film/` (narrated `.mp4` + `.vtt` per act, and the joined
`nimbus-demo.mp4`). All of `demo-footage/`, `demo-film/`, `demo-audio/` and
`demo-results/` are gitignored.

## Voice-over

Narration is synthesized with ElevenLabs and mixed in afterwards. It has to be
afterwards: **Playwright's video recording has no audio track**, so nothing can be
captured live.

What keeps it in sync is that the *pacing* happens at record time. `narrate()` holds
the shot for exactly as long as its line takes to say and records where in the act
that line started; `demo:mux` then places each clip at that offset. Act durations
vary with real Service Bus latency, so a fixed timeline would drift — this one is
regenerated on every take and needs no manual alignment.

```bash
npm run demo:voices          # list the voices + models your account can use
npm run demo:voice           # synthesize everything that changed
npm run demo:voice -- --act 2   # just act 2
npm run demo:voice -- --force   # ignore the cache
```

Put the key in `.env.local` (gitignored):

```
ELEVENLABS_API_KEY=sk_...
ELEVENLABS_VOICE_ID=onwK4e9ZLuTAKqWW03F9
ELEVENLABS_MODEL_ID=eleven_multilingual_v2
```

- **Lines live in `stage/narration.ts`**, keyed by the shot ids in the script
  (`"2.4"`). That file is the recording script: it holds the *spoken* form, so
  `CrmAccountCreated` is written "CRM Account Created" and "FIFO" is spelled out.
  The markdown script stays the editorial document.
- **Clips are cached** on a hash of text + voice + model + settings, so editing one
  line re-synthesizes one line. The whole script is ~4,500 characters.
- **Neighbouring lines are sent as `previous_text` / `next_text`** context so the
  voice carries prosody across an act instead of restarting cold each sentence.
  Context resets at act boundaries, because those are hard cuts in the film.
- **Without a manifest the harness still records**, silently, on the old fixed
  pacing — so you can iterate on shots without spending credits.
- `npm run demo:mux` needs **ffmpeg** on PATH (`winget install Gyan.FFmpeg`).

### Talking over the action

`narrate()` holds the shot until the line finishes. For narration that should run
*over* an interaction, keep the promise and await it later:

```ts
const oneClick = startNarration(page, "2.7");   // talks over the clicking
await resubmitButton.click();
await oneClick;                                 // don't start the next line early
```

Act 3 depends on this: its handoff window is capped at 60 seconds, and awaiting
every line serially between the create and the deferral assertion would blow the
budget and settle the handoff before anything defers.

### Subtitles

`demo:mux` writes a `.vtt` beside each act and `demo:film` merges them onto the
joined timeline. Keep them — LinkedIn and X autoplay muted, so a silent viewer gets
nothing from a film whose whole argument is in the voice-over.

## Service URLs

`crm-api` (5080), `erp-api` (5090) and `nimbus-ops` (28376) are pinned by the
AppHost. The two Vite SPAs are registered with `AddViteApp` and get
Aspire-assigned ports, so `stage/demo-urls.ts` resolves them in this order:

1. `CRM_WEB_URL` / `ERP_WEB_URL` from `.env.local`
2. a cached `.demo-web-urls.json` from a previous run, re-validated
3. probing every listening TCP port and matching each SPA's `<title>`

Pinning them in `.env.local` is fastest; delete those two lines to force a fresh
probe after an AppHost restart.

## Captions never outrun the footage

A caption that says "the siblings are deferred" is a claim about what is on
screen. Every such claim is gated on the store actually saying so before the
shot is framed — for example Act 2 and Act 3 both block on a `Deferred` event
existing for the session, then assert the `Deferred` row is visible, and only
then narrate it.

This has already paid for itself: the first take of Act 3 used a 30-second
handoff window, the operator shots ran long, the handoff settled before the
sibling edits were published, and nothing deferred. Without the assertion the
act would have recorded a confident caption over a table that disproved it.

If you add a shot, add the assertion with it.

## Files

| File | Role |
|---|---|
| `../playwright.demo.config.ts` | 1600×900, `video: on`, one worker, long timeouts |
| `stage/stage.ts` | captions, act chips, title cards, spotlights, pacing, footage naming |
| `stage/demo-urls.ts` | resolves the Aspire-assigned crm-web / erp-web ports |
| `stage/narration.ts` | the spoken script, keyed by shot id |
| `stage/voice.ts` | `narrate()` / `startNarration()` — shot pacing and cue capture |
| `stage/audio-manifest.ts` | shape of `demo-audio/manifest.json`, shared by all three stages |
| `../scripts/list-voices.ts` | `npm run demo:voices` |
| `../scripts/synthesize-narration.ts` | `npm run demo:voice` — ElevenLabs, cached |
| `../scripts/mux-narration.ts` | `npm run demo:mux` — audio onto footage, + `.vtt` |
| `../scripts/join-film.ts` | `npm run demo:film` — one mp4, merged subtitles |
| `act1..act5-*.spec.ts` | one act each, in narrative order |

The `scripts/` files run under plain `node` (which strips TypeScript types), so they
import with real `.ts` specifiers; everything under `demo/` runs through Playwright's
loader and imports with `.js`. Both resolve to the same files.

## Known rough edges

- The act chip (top right) overlaps the nimbus-ops "Jump to endpoint" search box.
  Cosmetic; move it in `stage.ts` if it bothers you.
- Raw footage without narration is roughly six minutes total; the script targets
  ~11 minutes. Synthesizing the voice-over first closes most of that gap on its own,
  because every narrated shot is held for the length of its line.
- Playwright renders no mouse cursor. Spotlight rings stand in for it.
