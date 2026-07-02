# Feature Specification: Apply Review (agent half of the review loop)

Feature Branch: `020-apply-review`
Created: 2026-05-30
Updated: 2026-05-30
Status: Proposed (review findings applied)
Input: User description: "Now if the agent (e.g. Claude) should monitor the spec reviews — either continuously or by the user triggering a skill — what should each solution then look like."

> NOTE: This specifies the **agent half** of the review loop defined by [spec 019](../019-spec-review-tool/spec.md). Spec 019 built a tool that *captures* anchored review comments in `*.review.json` sidecars and **never edits the spec itself**; it left the companion apply step as an explicit follow-up (019 Open Questions). This spec defines that step. Like 019, the deliverable is **repo-agnostic** — it operates on the file contract, not on anything NimBus-specific.

## Problem

Spec 019 closed everything *except* the payoff. A reviewer can now attach anchored comments to a spec and they persist in a stable, agent-consumable sidecar — but turning those comments into applied edits is still manual and undefined:

- **No defined apply step.** 019's FR-050..FR-053 specify the *data contract* an agent must honor (which fields it may/may not change, the `rev` bump). It does **not** define the agent that performs the loop: read open comments → locate each passage → edit `spec.md` → mark each resolved with a note.
- **No defined trigger.** Even with an apply step, there is no decision on *when* it runs — on demand when the author asks, or continuously as comments arrive.

The result is that the highest-value part of 019 — *mechanically applying anchored feedback and recording a resolution trail* — has no defined, repeatable procedure. This spec defines the procedure (the **apply engine**), packages it as an **on-demand skill**, and documents how the same engine can be driven **continuously** as a deferred option.

## Scope

In scope (built now):
- An **apply engine**: a precise, file-direct procedure that reads `open` + `anchored` comments from each `*.review.json` sidecar, edits the paired markdown to address each, and writes the comment back as `resolved` (or `wontfix`) with a `resolution` note, conforming to 019 FR-050..FR-053.
- An **on-demand skill** (`apply-review`) that runs the apply engine once over a target folder or a single spec, then reports a summary. It is **user-invoked** (e.g. `/apply-review [path]`).
- **Decoupled operation.** The engine works directly on the files (Read/Edit/Write); the 019 tool server need NOT be running. When it *is* running, the engine's `rev` bump + the tool's file-watch keep the live UI coherent.

Out of scope (deferred — documented in "Continuous monitoring" below, not built now):
- **Continuous / unattended triggers** (`/loop`, `/schedule`, tool `--watch-apply`). The shapes and trade-offs are recorded so they can be adopted later without redesign.
- **Auto-commit.** The engine leaves edits in the working tree; the human reviews `git diff` and commits.
- **Editing the contract.** This spec consumes 019's sidecar schema; it does not change it.
- **Authoring `anchorState`.** That field is tool-maintained (019 FR-034); the agent reads it, never writes it.

## User Scenarios & Testing

### User Story 1 - Apply all open comments on demand (Priority: P1)

As the spec author, I want to run one command and have an agent address every open, anchored review comment across my specs, so the review→edit loop is mechanical and auditable.

Why this priority: This is the payoff the whole loop exists for.

Independent Test: In a folder with a `spec.md` and a `spec.review.json` holding one `open`/`anchored` comment, run `/apply-review`. Confirm `spec.md` was edited to address the comment and the comment is now `resolved` with a `resolution { by, note, at }`; `id`/`anchor`/`body` are unchanged; `rev` incremented by 1.

Acceptance Scenarios:
1. Given sidecars with `open`+`anchored` comments, When the engine runs, Then for each it locates the passage via `anchor.quote` (disambiguated by `prefix`/`suffix`/`offsetHint`), edits the markdown, and sets `status:"resolved"` + `resolution` (019 FR-040/FR-050).
2. Given the engine finishes, When the author inspects results, Then it prints a per-spec summary: counts of resolved / wontfix / skipped, and the changed spec paths.
3. Given no `open`+`anchored` comments anywhere, When the engine runs, Then it reports "nothing to apply" and changes no files.

---

### User Story 2 - Target one spec (Priority: P2)

As the author mid-review, I want to apply comments for just the spec I'm working on.

Independent Test: Run `/apply-review docs/specs/019-spec-review-tool`. Confirm only that spec's sidecar is processed.

Acceptance Scenarios:
1. Given a path argument (a folder, a spec dir, or a `spec.md`), When the engine runs, Then it processes only sidecars under/for that target; absent an argument it defaults to `./docs/specs`.

---

### User Story 3 - Never guess; skip and report unhealthy anchors (Priority: P1)

As the author, I want the agent to refuse to edit when it cannot be sure where a comment applies, so a drifting comment never causes a wrong edit.

Why this priority: A wrong edit is worse than a deferred one; trust in the loop depends on this.

Independent Test: Add a comment whose `quote` no longer matches uniquely (duplicated or removed in the current source). Run the engine. Confirm it is left untouched (`status` still `open`, `anchorState` unchanged) and listed in the report as needing human re-placement.

"Unhealthy" is judged by **live recompute against the current markdown** (FR-004), not by the persisted `anchorState`, which the engine treats as advisory only (FR-003). A stored `orphaned`/`ambiguous` state is a hint that recompute will likely fail; a stored `anchored` state does not license an edit if recompute no longer finds a unique match.

Acceptance Scenarios:
1. Given a comment whose live recompute yields no match or more than one match (whatever its stored `anchorState`), When the engine runs, Then it does NOT edit any passage for it and does NOT change `anchorState`; it lists the comment for the human (019 FR-052).
2. Given a comment whose `quote` recomputes to a unique passage but whose intended edit the agent genuinely cannot determine, When the engine runs, Then it sets `wontfix` with a reason in `resolution.note` rather than guessing.

---

### User Story 4 - Idempotent re-runs reach a fixpoint (Priority: P2)

As the author, I want re-running apply to be safe — it should do nothing when there's no new feedback.

Why this priority: This is what makes any *continuous* trigger viable: an idle run must be a cheap no-op, not churn.

Independent Test: Run `/apply-review`, then run it again with no new comments. Confirm the second run changes no files and resolves nothing.

Acceptance Scenarios:
1. Given a prior apply resolved every comment whose anchor recomputes to a unique match, When the engine re-runs, Then the only remaining `open` comments are the skipped ones (anchor not uniquely locatable), so no edits and no resolutions occur.
2. Given a reviewer adds a new `open`+`anchored` comment after a prior run, When the engine re-runs, Then only that new comment is applied.

---

### User Story 5 - A running tool reflects applied resolutions (Priority: P3)

As a reviewer with the 019 tool open, I want resolutions the agent applied to show up live.

Independent Test: Start `spec-review` on a folder, run `/apply-review`, and confirm the UI live-reloads to show the comment resolved and the edited spec rendered.

Acceptance Scenarios:
1. Given the 019 server is watching the folder, When the engine writes a sidecar with a bumped `rev` and edits the spec, Then the tool's file-watch pushes an update and the UI reflects both (019 FR-013/FR-044).

## Edge Cases

- **Sidecar with no paired spec** (orphaned sidecar, 019 FR-061a): report it; do not edit anything.
- **Malformed sidecar JSON**: report a clear error for that file; continue with the others; never partially write it.
- **Concurrent live edit**: the engine bumps `rev`; a reviewer's stale-`rev` write is then rejected 409 by the running tool and they refetch (019 FR-044). To avoid the reverse race — the engine clobbering a sidecar a reviewer or another agent changed while it was editing markdown — the engine re-reads the sidecar immediately before writing and merges its changes by comment `id` rather than blind-overwriting (FR-007a).
- **Selection that matched at create time but the quote is now duplicated/changed**: the engine recomputes anchoring from `quote`/`prefix`/`suffix`/`offsetHint` against the *current* source before editing; if that recompute is not a unique match the comment is skipped (User Story 3). The persisted `anchorState` is advisory only — used for reporting, never as the authority on whether to edit.
- **Reply-only threads / already-resolved comments**: skipped (only `open` comments are work).
- **Unattended run with "leave uncommitted"**: working-tree changes accumulate until a human commits — acceptable on demand; a consideration if a continuous trigger is later enabled (see below).

## Requirements

### Functional Requirements

#### Apply engine

- FR-001: The engine MUST resolve its target as follows (default target `./docs/specs`):
  - a **folder** (incl. a spec directory) → glob `**/*.review.json` under it;
  - a **`.md` file** (e.g. `docs/specs/foo/spec.md`) → resolve **directly** to its sibling sidecar via the `.md`→`.review.json` rule in the same directory (`sidecarPathFor`, `tools/spec-review/src/store.js`), processing only that one sidecar;
  - a **`.review.json` file** → process that sidecar directly.

  It MUST operate **file-direct** and MUST NOT require the 019 server to be running.
- FR-002: For each sidecar, the engine MUST read it and locate the paired markdown (the sidecar's name with `.review.json` replaced by `.md`, in the same directory). It MUST cross-check that filename against the sidecar's `spec` field: **if they disagree, the engine MUST NOT edit any markdown for that sidecar** — it reports the mismatch and skips it, continuing with the others (safe-by-default; the field may name a different or moved file). Only on agreement does it read and edit the paired markdown.
- FR-003: The engine MUST select as work every comment with `status == "open"`; it MUST skip `resolved`/`wontfix` (done). The persisted `anchorState` MUST be treated as **advisory only** — a stored `orphaned`/`ambiguous` is a reporting hint, and a stored `anchored` does not by itself authorize an edit. The authority on editability is the live recompute in FR-004, which is what skips genuinely unhealthy anchors (User Story 3).
- FR-004: For each work comment, the engine MUST **recompute** the passage location against the *current* markdown via `anchor.quote`, disambiguated by `anchor.prefix`/`anchor.suffix`/`anchor.offsetHint`. It MUST edit **only when the recompute yields a single unambiguous match**, and then edit the markdown to address the comment's `body` (019 FR-050). If the recompute yields no match or more than one candidate (the quote is missing, moved, or now duplicated), the engine MUST NOT edit and MUST skip + report it (User Story 3), leaving `anchorState` unchanged.
- FR-005: After editing, the engine MUST set the comment's `status` to `"resolved"` (or `"wontfix"` when it deliberately declines), populate `resolution { by, note, at }` (`by` = the agent identity, e.g. `"claude"`; `at` = ISO timestamp), and set `updatedAt`. It MUST set `resolution` to a non-null object whenever `status != "open"` (019 FR-043).
- FR-006: The engine MUST leave `id`, `anchor`, `anchorState`, `body`, `author`, `createdAt`, and `thread` unchanged (019 FR-051). It MUST NOT write `anchorState` (019 FR-052).
- FR-007: The engine MUST increment the sidecar's `rev` by 1 per write (one bump per sidecar, regardless of how many comments changed) so a running tool's optimistic-concurrency check stays coherent (019 FR-044).
- FR-007a: Immediately before writing a sidecar, the engine MUST **re-read it from disk** and compare its `rev` to the value read at the start of processing.
  - If `rev` is unchanged, it writes the merged store with `rev + 1`.
  - If `rev` changed (a reviewer or another agent wrote while it was editing the markdown), the engine MUST re-apply only its own per-comment changes **by immutable comment `id`** — setting `status`/`resolution`/`updatedAt` on the matching comments and preserving every other comment and field exactly as found on disk — then write with the re-read `rev + 1`.

  The engine MUST NOT blind-overwrite a sidecar with a stale in-memory copy. This mirrors the tool's own read→apply(`set-status` by id)→write orchestration (`applyOperation`, `tools/spec-review/src/store.js`; 019 FR-044 merge-by-id).
- FR-008: The engine MUST write the sidecar back byte-compatible with the tool's own serializer, so diffs stay clean and a running tool can read it (019 FR-042/NFR-005). The exact rule (the contract; kept in parity with `serializeStore`/`orderComment` in `tools/spec-review/src/store.js`):
  - UTF-8, 2-space indentation, and a single trailing newline.
  - Top-level key order: `version`, `rev`, `spec`, `comments`.
  - Each comment's key order: `id`, `anchor`, `body`, `author`, `status`, `anchorState`, `createdAt`, `updatedAt`, `thread`, `resolution`.
  - The `anchor` object's key order: `heading`, `quote`, `prefix`, `suffix`, `offsetHint` (`heading`/`offsetHint` default to `null`; `quote`/`prefix`/`suffix` default to `""`).
  - The `resolution` object's shape and key order: `{ by, note, at }`, or `null` when `status == "open"`.
- FR-009: The engine MUST NOT commit or push. Edits remain in the working tree for human review.
- FR-010: The engine MUST print a per-spec summary: number resolved, number wontfix, and the list of skipped comments (anchor not uniquely locatable, plus any `spec`-field-mismatch sidecars) with their `id` and `quote`, plus the changed spec paths.
- FR-011: The agent MUST NOT guess. There are two distinct decline paths, and on neither does it edit an unrelated passage or modify `thread` (which is reviewer-owned and stays byte-immutable on every path, FR-006):
  - **Anchor not uniquely locatable** (recompute yields no/duplicate match, FR-004): leave `status:"open"` and `anchorState` unchanged; surface it in the run summary for the human to re-place (User Story 3).
  - **Anchor located but the correct edit is undeterminable**: set `status:"wontfix"` with the reason in `resolution.note`.

#### Packaging (on-demand)

- FR-020: The apply engine MUST be packaged as a **user-invocable skill** named `apply-review`, defined at `.claude/skills/apply-review/SKILL.md`, accepting an optional path argument.
- FR-021: The skill MUST be self-contained guidance — runnable with the repo's existing tools (Glob/Read/Edit/Write) and no new runtime dependency.

### Non-Functional Requirements

- NFR-001: **Contract-faithful.** The engine consumes and produces exactly the 019 sidecar schema; an older or newer 019 tool reading the result MUST tolerate it (019 FR-041 additive evolution).
- NFR-002: **Decoupled.** No coupling to a specific server port/token; the engine reads/writes files. (An API-driven variant via `POST /api/review` is possible but not required.)
- NFR-003: **Idempotent.** Re-running with no new feedback makes no changes (User Story 4).
- NFR-004: **Safe-by-default autonomy.** Edits are left uncommitted; the human decides what to commit.

## Continuous monitoring (deferred — design recorded, not built now)

The on-demand skill IS the shared apply engine. "Continuous" means *re-invoking that same engine automatically*; it does not change the engine. The 019 tool has **no external-process hook** today (its watcher only notifies browsers via SSE), so continuous monitoring is **polling-based** unless the tool is extended. Three shapes, in increasing autonomy:

1. **In-session `/loop`.** `/loop <interval> /apply-review` (or self-paced `/loop /apply-review`) re-runs the engine while a session is open. Make idle cheap: a fast "any `open`+`anchored` work?" scan first; do real work only when new feedback exists. Best for *actively reviewing right now*; stops when the session closes.
2. **Cross-session `/schedule` routine.** A cron routine running `/apply-review` between/after sessions (e.g. hourly or overnight). Survives session close — the robust "continuous" option. Caveat: unattended + "leave uncommitted" lets working-tree changes accumulate, so enabling this should revisit the commit policy (e.g. branch + PR per batch) — a decision deferred with this option.
3. **Tool-driven `--watch-apply` (event-driven).** Extend `tools/spec-review/src/server.js`'s existing file-watcher to spawn `claude -p "/apply-review <spec>"` when a sidecar changes. Truly event-driven (no polling), but it is the only option that requires a **code change to the tool** and is the biggest autonomy step (an LLM run triggered by a file save).

Recommended adoption order if/when continuous is wanted: `/loop` (zero new code) → `/schedule` (zero new code, add commit policy) → `--watch-apply` (tool change). All three reuse the unchanged `apply-review` engine.

## Key Entities

- **Apply engine** — the procedure (FR-001..FR-011) that transforms `open`+`anchored` comments into spec edits + resolutions. Reused by every trigger.
- **`apply-review` skill** — the on-demand packaging of the engine (FR-020).
- Consumes 019's **`ReviewFile`**, **`Comment`**, **`Anchor`**, **`Resolution`** entities unchanged.

## Success Criteria

- SC-001: Given only `spec.md` + `spec.review.json` with one `open`/`anchored` comment, `/apply-review` edits the spec and sets the comment `resolved` + `resolution`, **without schema ambiguity** — extends 019 SC-003 to the running engine.
- SC-002: After apply, `id`/`anchor`/`anchorState`/`body`/`author`/`createdAt`/`thread` are byte-unchanged for every comment on every outcome (resolved, wontfix, and skipped); only `status`/`resolution`/`updatedAt` (and the file's `rev`) ever change. In particular the agent never writes `thread`.
- SC-003: `rev` is incremented by exactly 1 per written sidecar; a running 019 tool live-reloads to show the resolution and the edited spec (019 FR-013/FR-044).
- SC-004: A comment whose live recompute is not a unique match (missing, moved, or duplicated `quote`) — whatever its stored `anchorState` — is skipped and reported, never applied; it stays `open` and its `anchorState` is unchanged.
- SC-005: A second `/apply-review` with no new comments changes no files and resolves nothing (idempotent fixpoint).
- SC-006: The written sidecar is byte-compatible with the tool's own `serializeStore` output (formatting/ordering), so a round-trip through the tool produces a clean diff.
- SC-007: No commit/push is performed by the engine (changes only in the working tree).

## Assumptions

- The 019 sidecars are present and conform to the 019 schema (`version`, `rev`, `spec`, `comments[]`).
- The agent runtime (Claude Code) provides Glob/Read/Edit/Write and a git working tree.
- The agent identity recorded in `resolution.by` is the running agent (e.g. `"claude"`).
- Continuous triggers, if later adopted, reuse this engine unchanged (see deferred section).

## Out of Scope

- Building the continuous triggers (`/loop`, `/schedule`, `--watch-apply`) — designed above, not implemented here.
- Auto-commit / PR creation.
- Changing the 019 sidecar schema or anchoring algorithm.
- An API-driven apply path (the file-direct path is the deliverable; API is a noted alternative).

## Open Questions

- **Commit policy for unattended runs.** "Leave uncommitted" is right for on-demand; a `/schedule` routine likely wants branch + PR per batch. Decide when/if continuous is enabled.
- **Agent identity string.** `resolution.by` default `"claude"`; could carry a model/version for a richer audit trail.
- **API vs file-direct for live sessions.** When the tool is running, routing writes through `POST /api/review` (server bumps `rev`, no formatting drift) is an alternative to file-direct; left as a possible enhancement.

## Resolved Questions

- **Trigger for v1: on-demand skill**, not continuous. Resolved — the engine is the hard part and is shared; on-demand is the safe, auditable first step. Continuous is documented and deferred.
- **Operation mode: file-direct**, not server-API. Resolved — keeps the agent decoupled from a running tool/port/token and matches 019's transport-agnostic contract (019 FR-053).
- **Autonomy: edit + resolve, leave uncommitted.** Resolved — the human stays in control of commits; matches 019's principle that only the agent/human edits the spec while the tooling stays out of the commit decision.
- **Never guess on unhealthy anchors.** Resolved — `orphaned`/`ambiguous` comments are skipped and reported, never applied (019 FR-052); trust in the loop depends on it.
- **Anchoring authority: live recompute, stored `anchorState` advisory.** Resolved — the engine is file-direct, so a persisted `anchorState` can be stale when the tool isn't running or hasn't reloaded after edits. The engine recomputes anchoring from `quote`/`prefix`/`suffix`/`offsetHint` against the current source and edits only on a unique match; stored state is a reporting hint, never the editability gate (FR-003/FR-004).
- **Decline path: `wontfix` + `resolution.note`, never `thread`.** Resolved — earlier wording let the engine append a `thread` note, which collided with `thread` byte-immutability (FR-006/SC-002). The agent writes only `status`/`resolution`/`updatedAt`; an undeterminable edit becomes `wontfix` with a note, an unplaceable anchor stays `open` and is reported (FR-011).
- **Sidecar `spec`-field mismatch: skip + report.** Resolved — if the sidecar's `spec` field disagrees with its paired markdown filename, the engine does not edit; it reports and skips that sidecar (safe-by-default), so a stale/moved field never causes an edit to the wrong file (FR-002).
