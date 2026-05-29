# Feature Specification: Spec Review Tool

Feature Branch: `019-spec-review-tool`
Created: 2026-05-29
Updated: 2026-05-29
Status: Proposed
Input: User description: "I would like a tool to view the specs for reviewing and would like to be able to comment the specs that can then be used by the agent to update the specs based on review comments. The UI should be an HTML page rendering the spec, which is markdown. I would like this tool to be something that can be used across different projects / git repos and not just specific for this solution."

> NOTE: This specification describes a **standalone, repo-agnostic** developer tool (working name `spec-review`). The tool is intended to live in its own package/repository and be runnable against any repo's specs (`npx spec-review ./docs/specs`). This document lives under NimBus's `docs/specs/` only because that is where the author keeps specifications; nothing in the tool's design is NimBus-specific.

## Problem

Feature specifications in this and other repos are authored as markdown (`docs/specs/NNN-*/spec.md`) and reviewed by hand. The review loop today is ad hoc and lossy:

- **No rendered reading view.** A reviewer reads raw markdown in an editor or a generic previewer; there is no purpose-built page that renders a spec for review.
- **No anchored comments.** A reviewer's feedback (e.g. "FR-033 is ambiguous — does it also abandon?") lives in a chat message, a PR thread, or their head. It is not attached to the specific passage it concerns, so applying it later means re-finding the passage by hand.
- **No machine-readable hand-off to an agent.** We have just done a review loop by hand twice (a reviewer produced findings; an agent edited the specs to resolve them). That loop works but is entirely manual: the findings are prose, the agent locates passages by guesswork, and there is no record of which findings were resolved and how.

The result is that the highest-value part of the loop — *anchored review comments that an agent can mechanically apply and then mark resolved* — has no tooling. The author wants a small tool that closes this loop and, critically, works in **any git repo**, not just NimBus, so the same review workflow applies everywhere they write specs.

This spec defines that tool: a local CLI that serves an HTML page rendering a spec's markdown, lets a reviewer attach comments to highlighted passages, and persists those comments in a stable, agent-consumable JSON sidecar that an agent reads to update the spec and mark each comment resolved.

## Scope

In scope:
- A **Node CLI** (`npx spec-review [path]`) that starts a **localhost-only web server** and opens the reviewer's browser. It discovers `**/spec.md` under the target folder (configurable glob) and works against any folder in any repo with zero per-project config.
- An **HTML web UI** that lists discovered specs in a sidebar and renders the selected spec's markdown as sanitized HTML (GFM: headings, tables, code, lists, links).
- **Selection-anchored commenting**: the reviewer highlights a passage and adds a comment; the comment anchors to that passage by quoted snippet (plus nearest heading and surrounding context) so it survives later edits to the spec.
- **Comment threads** (replies) and a **status lifecycle** (`open` / `resolved` / `wontfix`).
- A **sidecar JSON store per spec** (`spec.review.json` next to each `spec.md`) — the durable, versioned, agent-facing interface.
- An **agent-apply contract**: a precisely specified schema and resolution protocol so a separate agent run can read the spec + the store, edit the spec, and write each comment back as `resolved` with a note. The tool then reflects the resolved state.
- A small **JSON API** (read specs; read/write the store) and **live reload** when files change on disk.

Out of scope:
- **Building or standardizing the agent itself.** This spec defines only the data contract the agent consumes/produces (the `spec.review.json` schema and resolution protocol). The companion `/apply-review`-style agent command is a separate follow-up (see Open Questions).
- **Editing the spec markdown in the browser.** The tool captures comments; it never mutates `spec.md`. The agent (or a human) edits the spec.
- Authentication, multi-user/real-time collaboration, or any non-localhost network exposure.
- Hosting / SaaS / a long-running shared server.
- PR / GitHub / GitLab integration (posting comments as PR review comments). Possible later; not v1.
- Rendering arbitrary documentation beyond the discovery glob (the tool targets spec files, not a general docs site).

## User Scenarios & Testing

### User Story 1 - Render a spec for reading (Priority: P1)

As a reviewer, I want to run one command in any repo and get an HTML page that renders a spec's markdown, so I can read it comfortably without raw-markdown noise.

Why this priority: Rendering is the baseline; nothing else is reachable without it.

Independent Test: In a repo containing `docs/specs/008-*/spec.md`, run `npx spec-review docs/specs`. A browser opens to a localhost URL; the sidebar lists every discovered `spec.md`; clicking one renders its markdown as HTML (headings, tables, code blocks intact).

Acceptance Scenarios:

1. Given a folder containing one or more `spec.md` files, When the reviewer runs `npx spec-review <folder>`, Then a localhost server starts, the default browser opens to it, and the sidebar lists each discovered spec by its title/path.
2. Given a spec is selected, When it renders, Then GFM markdown (headings, fenced code, tables, lists, inline code, links) is shown as sanitized HTML, and each heading has a stable anchor for deep-linking.
3. Given the reviewer passes no path, When the tool starts, Then it defaults to `./docs/specs` if present, else the current working directory, and reports which folder and glob it is using.

---

### User Story 2 - Comment on a highlighted passage (Priority: P1)

As a reviewer, I want to highlight a sentence or block and attach a comment to it, so my feedback is anchored to the exact passage it concerns and persists for later.

Why this priority: Anchored commenting is the feature's entire reason to exist.

Independent Test: Render a spec, select a passage (e.g. the text of FR-033), type a comment, submit. Confirm a `spec.review.json` appears next to that `spec.md` containing the comment with the quoted text. Reload the page and confirm the comment re-renders against the same passage (highlight/margin marker).

Acceptance Scenarios:

1. Given a passage is selected, When the reviewer adds a comment, Then a comment is written to the spec's `spec.review.json` with an `anchor` capturing the quoted text, the nearest heading, and surrounding prefix/suffix context (FR-030).
2. Given a comment exists, When the spec is rendered, Then the anchored passage shows a visible marker (highlight and/or margin indicator) and the comment text is viewable beside it.
3. Given a comment, When the reviewer adds a reply, Then the reply is appended to that comment's `thread` (FR-031).
4. Given the tool is started with `--read-only`, When the reviewer views a spec, Then existing comments render but no new comments or replies can be written.

---

### User Story 3 - An agent applies review comments and resolves them (Priority: P1)

As the spec author, I want an agent to read the spec and its review comments, edit the spec to address each open comment, and mark each one resolved with a note, so the review→edit loop is mechanical and auditable.

Why this priority: This is the payoff — turning anchored comments into applied edits with a resolution trail. It is the explicit motivation in the user's request.

Independent Test: With a `spec.md` and a `spec.review.json` containing one `open` comment, run the agent step (out-of-band) against that pair. Confirm the agent edited `spec.md` to address the comment and set the comment's `status` to `resolved` with a `resolution.note`. Reload the tool and confirm the comment shows as resolved.

Acceptance Scenarios:

1. Given a `spec.review.json` with `open` comments, When the agent runs the apply step, Then for each open comment it locates the passage via `anchor.quote`, edits `spec.md` accordingly, and sets `status: "resolved"` plus `resolution { by, note, at }` (FR-040).
2. Given the agent finishes, When the reviewer reloads the tool, Then resolved comments render distinctly (e.g. struck-through / dimmed) and can be filtered out (FR-032).
3. Given a comment the agent decides not to act on, When it records `status: "wontfix"` with a `resolution.note`, Then the tool shows that state and reason without treating it as open.
4. Given the agent edits `spec.md`, When the tool's file watcher fires, Then the rendered view live-reloads to the updated spec (FR-013).

---

### User Story 4 - Comments survive edits to the spec (Priority: P2)

As a reviewer returning after the spec was edited, I want my comment to still point at the right passage — or clearly flag that it can no longer be located — rather than silently attach to the wrong place.

Why this priority: A comment that drifts onto unrelated text after an edit is worse than no anchor. Robust re-anchoring is what makes the sidecar durable across the very edits the review produces.

Independent Test: Add a comment anchored to a passage, then insert several paragraphs *above* it in `spec.md`. Reload; confirm the comment still highlights the original passage (re-anchored by quote match). Then delete the quoted passage entirely; confirm the comment is shown as "needs re-anchor / orphaned", not attached elsewhere.

Acceptance Scenarios:

1. Given the spec was edited above an anchored passage, When the spec re-renders, Then the comment re-anchors by matching `anchor.quote` (disambiguated by `prefix`/`suffix`/`offsetHint`) and highlights the original passage.
2. Given the quoted passage no longer exists in the spec, When the spec re-renders, Then the comment is marked `orphaned` in the UI (and a flag is exposed in the store) and is listed separately so the reviewer can re-place or close it; it MUST NOT attach to an unrelated passage.
3. Given two identical quotes exist, When re-anchoring, Then `prefix`/`suffix`/`offsetHint` select the intended occurrence; if still ambiguous, the comment is flagged rather than guessed.

---

### User Story 5 - Same tool across different repos (Priority: P2)

As a developer who writes specs in several repos, I want the identical tool to work in each one with no per-repo setup, so the review workflow is the same everywhere.

Why this priority: Cross-project reuse is an explicit requirement; a tool that needs per-repo wiring would not be adopted across repos.

Independent Test: Run `npx spec-review docs/specs` in two unrelated repos with different folder layouts. Confirm both render and comment correctly with no config files added to either repo (beyond the `spec.review.json` sidecars the tool itself writes).

Acceptance Scenarios:

1. Given a different repo with specs under a non-default path, When the reviewer runs `npx spec-review <path>` (or `--glob`), Then specs are discovered and rendered without any project-specific configuration.
2. Given the tool runs against a folder, When it writes sidecars, Then it writes only `spec.review.json` files next to discovered specs and touches nothing else in the repo.

---

### User Story 6 - Triage comments by status (Priority: P3)

As a reviewer or author, I want to filter comments by `open` / `resolved` / `wontfix` and see threads, so I can focus on outstanding feedback.

Why this priority: Quality-of-life triage; the core loop works without it.

Independent Test: Create comments in several statuses; toggle a filter to show only `open`; confirm resolved/wontfix are hidden and the open count is shown.

Acceptance Scenarios:

1. Given comments in mixed statuses, When the reviewer filters by `open`, Then only open comments are listed and a count is shown.
2. Given a comment with replies, When it is expanded, Then the full `thread` is shown in order with authors and timestamps.

## Edge Cases

- **Quoted passage not found on reopen** (the spec was edited and the quote deleted/changed): the comment is `orphaned` — surfaced separately, never attached to the wrong text.
- **Duplicate quote matches**: more than one occurrence of `anchor.quote`; the tool disambiguates via `prefix`/`suffix` then `offsetHint`, and flags rather than guesses if still ambiguous.
- **Spec file renamed or deleted** while its `*.review.json` exists: via the sidecar-discovery rule (FR-061a) the orphaned sidecar is listed as "no matching spec" so the reviewer can relocate or remove it; the tool never deletes it automatically.
- **Concurrent edits to `spec.review.json`** (e.g. the agent writes a `resolved` status while the reviewer, from a UI loaded earlier, adds a new comment): writes are atomic (write-temp-then-rename) *and* lost-update-safe — the server merges discrete operations by comment `id` onto the current on-disk file and enforces optimistic concurrency on `rev` (FR-044). A stale write is rejected with 409; the client re-fetches and retries. Last-writer-wins on the *whole file* is explicitly disallowed.
- **Very large spec**: rendering and re-anchoring must stay responsive (SC-001); rendering is incremental where feasible.
- **Non-`spec.md` markdown / unrelated files** in the folder: only files matching the discovery glob (as specs) and `*.review.json` (as sidecars) are read; binaries, secrets, and any other file are never opened or served (FR-061/FR-061a).
- **Port already in use**: the CLI picks the next free port (or honors `--port`) and reports the chosen URL; it never silently fails.
- **Selection spanning multiple blocks** or only part of a code block: the quote captures the exact selected text; multi-block selections store the full span and re-anchor on the start quote (Open Questions covers sub-block robustness).
- **No author identity available**: author defaults from `git config user.name`, else `$USER`/`$USERNAME`, else an editable "unknown" the reviewer can set in the UI.
- **Read-only filesystem / `--read-only`**: commenting is disabled with a clear banner; rendering still works.

## Requirements

### Functional Requirements

#### CLI & server

- FR-001: The tool MUST ship as a Node package runnable via `npx spec-review [path]` (and installable globally) with no build step required by the consumer.
- FR-002: The path argument MUST default to `./docs/specs` when it exists, otherwise the current working directory. The resolved target folder and glob MUST be printed on startup.
- FR-003: The CLI MUST accept flags: `--port <n>` (default: an open port, reported on startup), `--no-open` (do not auto-launch the browser), `--read-only` (render but disallow writes), and `--glob <pattern>` (default `**/spec.md`).
- FR-004: The server MUST bind to `127.0.0.1` only (never `0.0.0.0`). Binding to localhost is **necessary but not sufficient**: the tool writes repo files, and any web page open in the reviewer's browser can issue requests to `http://127.0.0.1:<port>` (CSRF) or attempt DNS-rebinding. The server MUST therefore enforce browser-origin protections (FR-007) even though no human-facing login is required. This MUST be stated explicitly: "localhost-only" is a network-reachability control, not the write-authorization control.
- FR-005: The server MUST expose a small JSON API: list discovered specs; fetch one spec's raw markdown; fetch/write a spec's review sidecar. The web UI consumes only this API.
- FR-006: The tool MUST discover specs by the glob relative to the target folder and present them in a sidebar, labeled by spec title (first `# ` heading) and path. It MUST additionally discover existing review sidecars (FR-061a) so a sidecar whose spec was renamed/deleted is listed as orphaned rather than vanishing.

#### Local-server security (browser-origin hardening)

- FR-007: Every **state-changing** request (creating/replying-to/resolving a comment — i.e. all writes) MUST be rejected unless ALL of the following hold, defeating CSRF and DNS-rebinding from a malicious page in the same browser:
  - **Per-run token.** On startup the tool MUST mint a high-entropy random token, embed it in the URL it opens / serves to the UI, and require it on every write (e.g. an `Authorization: Bearer <token>` or `X-Spec-Review-Token` header set by the UI). Requests without the correct token MUST be rejected with 403. The token is per-process and never persisted.
  - **Origin/Referer check.** The request's `Origin` (or `Referer`) MUST match the tool's own served origin; cross-origin write requests MUST be rejected.
  - **Host check.** The request's `Host` header MUST be `127.0.0.1:<port>` or `localhost:<port>`; any other Host (e.g. an attacker-controlled rebinding hostname) MUST be rejected, which closes the DNS-rebinding vector.
  - **JSON-only.** Writes MUST require `Content-Type: application/json` and reject `application/x-www-form-urlencoded`/`multipart/form-data`/`text/plain` (the content types a simple cross-site form can send without a CORS preflight).
- FR-008: Read endpoints (list specs, fetch markdown, fetch sidecar) MUST also validate `Host` (FR-007) to prevent DNS-rebinding exfiltration of repo contents, and SHOULD require the per-run token as well. The server MUST NOT set permissive CORS headers (no `Access-Control-Allow-Origin: *`).

#### Rendering

- FR-010: Markdown MUST be rendered to **sanitized** HTML (no raw HTML injection / XSS) using a maintained GFM-capable library (e.g. `markdown-it`), supporting headings, fenced code with language classes, tables, lists, blockquotes, inline code, and links.
- FR-011: Every heading MUST receive a stable slug/anchor so comments and deep links can reference sections.
- FR-012: Each rendered top-level block MUST carry its **source line range**, derived from the renderer's token source map (e.g. `markdown-it` token `.map`), exposed as a `data-source-*` attribute. This is the bridge that maps a browser text selection back to source text for the comment quote (FR-030).
- FR-013: When `spec.md` or its `spec.review.json` changes on disk, the UI MUST live-reload the affected view (file watch + push/poll).

#### Commenting

- FR-020: The reviewer MUST be able to select rendered text and attach a comment to that selection.
- FR-021: Each comment MUST be assigned a stable, unique `id` (e.g. `c_` + short random/hash) that never changes across edits or resolution.
- FR-030: On creation, a comment's `anchor` MUST capture: `quote` (the exact selected source text), `heading` (nearest preceding heading slug/text), `prefix` and `suffix` (bounded context, ~32 chars each, for disambiguation), and `offsetHint` (advisory character offset into the raw markdown). The selection is mapped to source via FR-012's block source ranges plus intra-block text matching.
- FR-031: A comment MUST support a `thread` of replies (`author`, `body`, `createdAt`), appended in order.
- FR-032: A comment MUST have a `status` of `open` | `resolved` | `wontfix`. The UI MUST visually distinguish statuses and allow filtering by status.
- FR-033: `author` MUST be resolved from `git config user.name`, falling back to `$USER`/`$USERNAME`, then an editable UI value; it MUST be recorded on each comment and reply.
- FR-034: On reopen, the tool MUST **re-anchor** each comment by locating `anchor.quote` in the current source, disambiguating with `prefix`/`suffix`/`offsetHint`, and MUST record the outcome in the comment's `anchorState` field: `anchored` (located unambiguously), `orphaned` (quote not found), or `ambiguous` (multiple matches that prefix/suffix/offset could not resolve). The tool MUST persist `anchorState` to the sidecar so the agent sees the same state (FR-040), surface non-`anchored` comments separately in the UI, and MUST NOT attach a non-`anchored` comment to any passage (User Story 4). `anchorState` is **tool-maintained**: it is recomputed on each re-anchor; the agent does not author it but reads it (FR-052).

#### Store contract (`spec.review.json`)

- FR-040: Each spec's comments MUST persist to a sidecar in the **same directory** as the reviewed markdown file, named by **deriving from the markdown filename**: the file's name with its `.md` extension replaced by `.review.json`. So `spec.md` → `spec.review.json`, `architecture.md` → `architecture.review.json`. This lets a directory reviewed under a broader glob (FR-003 `--glob`) hold several reviewed markdown files without their sidecars colliding (a fixed `spec.review.json` name would collide). The `spec` field inside the file records the markdown filename it belongs to. The file is the durable, agent-facing interface. Its shape:

  ```json
  {
    "version": 1,
    "rev": 7,
    "spec": "spec.md",
    "comments": [
      {
        "id": "c_ab12cd",
        "anchor": {
          "heading": "FR-033",
          "quote": "MUST dead-letter via IMessageContext.DeadLetter",
          "prefix": "…up to 32 chars of source before the quote…",
          "suffix": "…up to 32 chars of source after the quote…",
          "offsetHint": 1240
        },
        "body": "This is ambiguous — does it also abandon the message?",
        "author": "alvin",
        "status": "open",
        "anchorState": "anchored",
        "createdAt": "2026-05-29T10:00:00Z",
        "updatedAt": "2026-05-29T10:00:00Z",
        "thread": [
          { "author": "alvin", "body": "Follow-up clarification.", "createdAt": "2026-05-29T10:05:00Z" }
        ],
        "resolution": null
      }
    ]
  }
  ```

- FR-041: The store MUST be **versioned** (`version`) and evolve **additively** — new optional fields only, so older tools and agents tolerate newer files.
- FR-042: Writes MUST be **atomic** (write to a temp file, then rename) and use stable key ordering / formatting so the file is diff-friendly and git-mergeable. Atomic rename prevents *partial* files; it does NOT prevent *lost updates* — see FR-044.
- FR-044: Writes MUST NOT lose concurrent updates (e.g. a reviewer adding a comment from a UI copy loaded before the agent wrote a `resolved` status). The server MUST guarantee this by BOTH:
  - **Merge by `id`, never blind full-file overwrite.** Write operations are expressed as discrete intents — add comment, add reply to `<id>`, set status/resolution of `<id>` — that the server applies onto the **current** on-disk `comments[]`, keyed by immutable `id`. The server (not a stale client) is the writer of record; a client MUST NOT PUT a whole `comments[]` it loaded earlier.
  - **Optimistic concurrency on `rev`.** The store carries a monotonically increasing `rev`. A write MUST include the `rev` the client last read; if it differs from the on-disk `rev`, the server MUST reject with 409 Conflict, and the client MUST re-fetch and retry. The server increments `rev` on every successful write.
  Together these ensure a UI append and an agent resolution interleave without either clobbering the other.
- FR-043: `resolution`, when present, MUST be `{ "by": "<agent or user>", "note": "<what changed / why>", "at": "<ISO timestamp>" }`. It is `null` while `status` is `open`.

#### Agent-apply contract

- FR-050: The store MUST be specified precisely enough that an agent can, given `spec.md` + `spec.review.json`: (1) read all comments with `status: "open"`; (2) locate each comment's passage in `spec.md` via `anchor.quote` (disambiguated by `prefix`/`suffix`/`offsetHint`); (3) edit `spec.md` to address the comment; (4) set the comment's `status` to `resolved` (or `wontfix`) and populate `resolution { by, note, at }`; (5) leave `id`, `anchor`, `anchorState`, `body`, `author`, `createdAt`, and `thread` unchanged. If the agent writes the sidecar directly on disk (out-of-band), it MUST preserve all fields it does not own and increment the file's `rev` (FR-044) so a running tool's optimistic-concurrency check stays coherent.
- FR-051: The agent MUST NOT delete comments or change their `id`/`anchor`; resolution is a status+resolution write only. This guarantees the tool can always correlate a resolved comment back to the original feedback.
- FR-052: An agent that cannot locate a comment's `quote` — or whose `anchorState` is already `orphaned`/`ambiguous` (FR-034) — MUST set a non-`resolved` state (e.g. leave `open` and append a `thread` note, or mark `wontfix` with a reason) rather than edit the wrong passage. The agent reads `anchorState` but does not author it; it MUST NOT write `anchorState` (tool-maintained).
- FR-053: The contract is transport-agnostic: it defines the files, not how the agent is invoked. The companion command/prompt that performs the apply step is out of scope for this spec (Open Questions).

#### Configuration & discovery

- FR-060: The tool MUST work with **zero per-repo configuration**. Any behavior overridable by flags MAY also be set via an optional config file, but the absence of one MUST be fully functional.
- FR-061: For spec content, the tool MUST only read files matching the discovery glob, and it MUST only ever write `*.review.json` sidecars (FR-040) next to discovered specs; it MUST NOT modify any markdown file or any other repo file.
- FR-061a: Independently of the spec glob, the tool MUST also discover existing **review sidecars** in the target tree (the corresponding `*.review.json` pattern). A sidecar whose paired markdown file no longer matches the glob (renamed/deleted) MUST be listed as an **orphaned sidecar** ("no matching spec") so its comments are not lost from view; the tool MUST NOT auto-delete it. Without this rule, sidecars would be invisible because they do not themselves match `**/spec.md` (the edge case at "Spec file renamed or deleted" depends on it). The tool MUST NOT open or serve any file that is neither a glob-matched spec nor a `*.review.json` sidecar.

### Non-Functional Requirements

- NFR-001: **No network exposure + browser-origin hardening.** The server binds `127.0.0.1` only and MUST refuse to bind a public interface; it works fully offline. Because localhost is still reachable from any page in the reviewer's browser, write authorization MUST rest on the per-run token + Origin/Host/Content-Type checks (FR-007/FR-008), not on the bind address alone.
- NFR-002: **Cross-platform.** Windows, macOS, and Linux; path handling and file watching MUST work on all three.
- NFR-003: **Minimal, current dependencies.** A small dependency set (markdown rendering + sanitizer + a tiny server; UI bundle prebuilt). No native build steps for the consumer.
- NFR-004: **Never mutates the spec.** A pure-comment session MUST leave `spec.md` byte-identical (the tool only writes sidecars). Verified by SC-005.
- NFR-005: **Git-friendly sidecars.** Stable formatting/ordering so `spec.review.json` diffs cleanly; the file MAY be committed (shared review) or gitignored (local-only) per the team's choice (Open Questions notes the default).
- NFR-006: **Responsive.** Rendering a spec and re-anchoring its comments completes within ~1s for typical specs (a few hundred lines) on a developer laptop (SC-001).
- NFR-007: **Resilient persistence.** Atomic writes; a malformed or partially-written `spec.review.json` MUST surface a clear error and never crash the server or lose unrelated comments.

## Key Entities

- **`ReviewFile`** — the contents of one sidecar (`<basename>.review.json`, FR-040): `{ version, rev, spec, comments[] }`. One per reviewed markdown file, co-located. `version` is the schema version; `rev` is the monotonic data revision used for optimistic concurrency (FR-044); `spec` names the markdown file it pairs with.
- **`Comment`** — `{ id, anchor, body, author, status, anchorState, createdAt, updatedAt, thread[], resolution }`. The unit of review feedback; `id` is immutable. `status` (`open`/`resolved`/`wontfix`) is review-lifecycle; `anchorState` (`anchored`/`orphaned`/`ambiguous`) is tool-maintained anchoring health (FR-034).
- **`Anchor`** — `{ heading, quote, prefix, suffix, offsetHint }`. Locates a comment's passage in the source markdown, resilient to edits above it; the agent and the UI both resolve passages through it.
- **`ThreadEntry`** — `{ author, body, createdAt }`. A reply on a comment.
- **`Resolution`** — `{ by, note, at }` or `null`. Records who resolved a comment, why/what changed, and when.

## Success Criteria

### Measurable Outcomes

- SC-001: A typical spec (a few hundred lines) renders and re-anchors its comments in under ~1 second on a developer laptop.
- SC-002: A comment **round-trips**: created via the UI, it persists to `spec.review.json`, and after a reload it re-anchors to the same passage (or is flagged `orphaned` if the passage was removed) — never onto an unrelated passage.
- SC-003: An agent, given only `spec.md` + `spec.review.json`, can resolve an open comment (edit the spec, set `resolved` + `resolution`) **without schema ambiguity** — verified by a worked example following the FR-050 contract.
- SC-004: The **same** published tool runs unchanged against at least two different repos with different spec layouts, with no per-repo config files added.
- SC-005: After a pure-comment session (no agent edits), every `spec.md` is **byte-identical** to before; only `spec.review.json` sidecars changed.
- SC-006: Rendered HTML is sanitized — a spec containing embedded raw HTML / script does not execute it (XSS test passes).
- SC-007: The server is reachable only on `127.0.0.1` (a connection attempt from another interface fails).
- SC-008: A write request that omits the per-run token, carries a foreign `Origin`, a non-`127.0.0.1`/`localhost` `Host`, or a non-JSON content type is **rejected** (403/400) — verified by simulating each as a cross-site request; a correctly-tokened same-origin JSON write succeeds (FR-007/FR-008).
- SC-009: Interleaving a UI comment-add and an agent resolution against the same sidecar loses neither: both survive, and a stale-`rev` write is rejected with 409 and succeeds after re-fetch (FR-044).
- SC-010: A sidecar whose paired markdown was renamed/deleted appears in the UI as an orphaned sidecar rather than disappearing (FR-061a); and two reviewed markdown files in one directory produce two distinct, non-colliding sidecars (FR-040).

## Assumptions

- Node.js is available on the reviewer's machine (the tool is distributed via npm/`npx`).
- Specs are UTF-8 markdown files matched by the discovery glob (default `**/spec.md`).
- One reviewer at a time per running instance (local, single-user); concurrent multi-user editing is out of scope.
- The agent step that applies comments is performed **out-of-band** (e.g. a `/apply-review`-style command or a pasted prompt), reading and writing the files this tool produces.
- `git` may or may not be present; author identity degrades gracefully when it is not (FR-033).

## Out of Scope

- Building or standardizing the apply agent / slash command (only the file contract is specified here).
- In-browser editing of the spec markdown.
- Authentication, multi-user, or real-time collaborative review.
- Posting comments to GitHub/GitLab PR review threads, or any VCS-host integration.
- A hosted/shared long-running server or SaaS.
- Rendering general documentation sites beyond the spec discovery glob.

## Open Questions

- **Selection → source mapping for sub-block selections.** Block-level source maps (FR-012) make whole-paragraph/heading anchoring straightforward; mapping an arbitrary *inline* selection (part of a sentence inside a rendered block) back to an exact source offset is the trickiest part. Candidate approach: locate the containing block via its source range, then find the selected rendered text within that block's source by string match. Robustness for repeated inline phrases needs validation during implementation.
- **Commit vs gitignore the sidecar by default.** `spec.review.json` can be committed (shared, reviewable-in-PR feedback) or gitignored (ephemeral local review). Proposed default: committable, with documentation on both modes; confirm before implementation.
- **Package name / scope.** Working name `spec-review`; the published npm name/scope is TBD.
- **Companion apply command.** Whether to ship a `/apply-review`-style agent command (and where) as a follow-up spec, so the agent half of the loop is also standardized rather than left to an ad-hoc prompt.
- **Tech for the server/UI.** Proposed: Node + TypeScript CLI serving a prebuilt Vite/React/Tailwind static bundle with `markdown-it` for rendering — consistent with the author's existing stack. A lighter zero-framework UI is an alternative if dependency count must be minimized (NFR-003).

## Resolved Questions

- **Runtime model: a local Node CLI + localhost web server**, not a static file, VS Code extension, or .NET tool. Resolved — it gives a real browser UI with reliable disk read/write while staying point-at-any-folder portable across repos (User Story 5).
- **Comment storage: a sidecar `spec.review.json` per spec**, not one central store or a markdown review file. Resolved — locality (the comments live next to the spec), clean diffs/merges, and a structured shape the agent can parse and resolve precisely (FR-040, FR-050).
- **Anchoring: text selection + quoted snippet** (with heading + prefix/suffix + advisory offset), not line numbers or heading-only. Resolved — quotes survive edits above them and hand the agent the exact text to revise; line numbers drift, heading-only is too coarse (User Story 4).
- **Apply flow: the tool captures, a separate agent run applies and resolves.** Resolved — keeps the tool simple and the agent swappable; the tool only needs to define and reflect the contract, not invoke any specific agent (FR-050..FR-053).
- **The tool never edits `spec.md`.** Resolved — only the agent (or a human) edits the spec; the tool writes only sidecars (NFR-004, FR-061).
- **Localhost binding is not the write-authorization control.** Resolved — because the tool writes repo files and any browser page can reach `127.0.0.1`, writes are gated by a per-run token + Origin/Host/Content-Type checks (FR-007/FR-008), defeating CSRF and DNS-rebinding. Localhost-only remains as the reachability control, not the authorization control.
- **Sidecar name is derived from the markdown filename** (`<basename>.review.json`), not a fixed `spec.review.json`. Resolved — a fixed name collides when a directory holds multiple reviewed markdown files under a broad `--glob`; deriving from the filename keeps sidecars unique and co-located (FR-040).
- **Concurrent writes merge by `id` with optimistic concurrency on `rev`.** Resolved — atomic rename only prevents partial files; merge-by-id + `rev` conflict detection prevents a stale UI write from clobbering an agent's resolution and vice-versa (FR-044). Whole-file last-writer-wins is rejected.
- **Orphaned anchoring is a first-class, persisted state (`anchorState`),** not UI-only. Resolved — the agent must see the same anchoring health the UI does, so it is stored and tool-maintained (FR-034, FR-052).
