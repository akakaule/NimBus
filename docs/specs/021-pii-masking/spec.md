# Feature Specification: PII Masking for Logs, Audit, and WebApp Reveal

Feature Branch: `021-pii-masking`
Created: 2026-06-02
Updated: 2026-06-02
Status: Proposed (back-port candidate from DIS; see `BH.DIS.Core/Messages/PII/`, `BH.DIS.SDK/Logging/`, and `BH.DIS.WebApp/Services/PII/`). Bundles DIS commits `51315a54` (platform-wide masking), `d8c4e598` (adapter ILogger masking), `9ce7528e` (review fixes), `5c754bd4` (WebApp audit masking), `9aad33f5` (role-gated reveal), and `2723c614` (sample showcase).
Input: User description: "Port the PII masking initiative from DIS. NimBus has zero PII masking today: operator-supplied event payloads (EventContent / EventJson) flow verbatim into structured logs, the Application Insights sink, the durable audit trail, and the WebApp Event Details view. An adapter or operator handling real personal data (CPR numbers, SSNs, emails, addresses) leaks it everywhere. We want a declarative `[Sensitive]` attribute on event properties, a masker that rewrites event JSON before it reaches any log/audit sink, a logging decorator that covers both the WebApp and adapter hosts, and a role-gated reveal so only authorized operators see raw payloads in the WebApp — masked by default in every environment, including local dev."

## Problem

NimBus surfaces and persists raw event payloads in at least four places, none of which mask anything:

1. **Structured logs / Application Insights.** Any `ILogger` call that carries an event's `EventJson` (or `EventContent`) writes the raw payload to the configured sinks. The WebApp uses `Microsoft.Extensions.Logging` with `AddApplicationInsightsTelemetry()`; adapter hosts use whatever logging the host configures. There is no interception point.
2. **The durable audit trail.** `AuditLogService.LogAuditAsync(..., data: ...)` (spec 008) persists a serialized action context to the message store and mirrors it into App Insights. On the resubmit-with-changes path, `EventImplementation` serializes the entire operator-supplied `body` — including `EventContent.EventJson` — into `data` on every branch (success, access-denied, fail-closed). That payload is PII-bearing and is written verbatim.
3. **The WebApp Event Details view.** The event-detail read path returns the stored `EventJson` to the SPA, which renders it. Any operator who can open the page sees raw personal data regardless of need-to-know.
4. **Adapter logs.** Handlers that log "processing event X with payload Y" leak the same data at the edge, before anything reaches NimBus's own infrastructure.

DIS solved this with a single, declarative subsystem:

- A `[Sensitive]` attribute (with `Redact` / `PartialReveal` / `Hash` modes) declared on event-contract properties.
- An `IEventJsonMasker` that resolves an `eventTypeId` to its CLR type via `IPlatform`, walks the JSON tree, and rewrites every `[Sensitive]` field, stamping a `$piiMasked` sidecar marker.
- A logging decorator that masks any structured log following the `EventTypeId` + `EventJson`/`EventContent` convention before it reaches a sink — fail-closed when no platform is available.
- WebApp audit-write masking and identifier-only logs on the resubmit path.
- A `PiiReader` role that gates reveal of raw payloads in the WebApp, with no dev short-circuit.

NimBus has none of this. This spec ports the subsystem, adapting it to NimBus's `Microsoft.Extensions.Logging` stack (DIS's WebApp was Serilog), its spec-008 audit service, and its endpoint-manager auth model (which has no PII role today).

## Scope

In scope:

- **Core masking contracts** in `NimBus.Core` (new `NimBus.Core.Pii` namespace): `IEventJsonMasker`, `SensitiveAttribute`, `MaskMode`, the `EventJsonMasker` implementation (reflection over `IPlatform.EventTypes` + `IEventType.GetEventClassType()`), and `NullEventJsonMasker`.
- **Logging masking pipeline** in `NimBus.SDK` (new `NimBus.SDK.Logging` namespace): `LogMaskingConventions` constants, an `ILoggerFactory`/`ILogger` decorator (`MaskingLoggerFactory` / `MaskingLogger`) operating at the `Microsoft.Extensions.Logging` abstraction level, `FailClosedEventJsonMasker`, and an idempotent `AddNimBusLogMasking()` DI opt-in. This single decorator covers **both** the WebApp and adapter hosts (NimBus has no Serilog, so DIS's separate Serilog enricher is not needed — see Resolved Questions).
- **WebApp audit-write masking**: a `BuildAuditDataForResubmit` helper that masks `EventContent` through `IEventJsonMasker` before the value is passed as `data` to `IAuditLogService.LogAuditAsync(...)`; and replacing the raw-`body` `LogInformation` calls on the resubmit-with-changes path with identifier-only logs (EventTypeId + ids, never payload).
- **Role-gated PII reveal**: a new `PiiReader` role concept and a `CurrentUserCanReadPii()` method on `IEndpointAuthorizationService`; server-side masking of `EventJson` on the event-detail read path when the caller lacks the role; and a frontend affordance reflecting masked-vs-revealed state. Masked by default in **every** environment (no dev short-circuit); `LocalDevAuthHandler` does **not** grant `PiiReader` by default.
- **Sample showcase** (item #14): `[Sensitive]` applied to sample event contracts (demonstrating `Redact` / `PartialReveal` / `Hash`) and publisher-form fields, so the masking is visible end-to-end in the local Aspire sample.
- **Tests**: masker unit tests (mode matrix, nesting, collections, `JsonProperty` renames, unknown-type / invalid-JSON markers, idempotency, `Reveal` validation); log-decorator tests (state-shape branches, fail-closed, idempotency, factory swap); audit-masking tests; and an authz test for `CurrentUserCanReadPii`.
- **Docs**: a new `docs/pii-masking.md` and an "Event payload masking" section in `docs/building-adapters.md` / `docs/architecture.md`.

Out of scope:

- Masking of non-event data (free-text operator notes, endpoint metadata, search filter criteria). Search-audit `data` carries filter criteria, not payload, and is intentionally left unmasked.
- Field-level encryption at rest or a key-management system. Masking is a one-way display/log transform, not encryption.
- Masking the message body on the wire (Service Bus) or in the message store's primary event record. The store remains the system of record for the real payload; masking is applied at the log/audit/reveal boundaries. (A future spec may add at-rest masking; this one does not.)
- Retrofitting every historical audit row. Masking applies to new writes going forward.
- A general "redact any string anywhere" framework. The contract is specifically event-payload JSON keyed by `eventTypeId`.

## User Scenarios & Testing

### User Story 1 - Sensitive fields are masked in logs and App Insights (Priority: P1)

As a data-protection-conscious operator, I want any structured log line carrying an event payload to have its `[Sensitive]` fields masked before it reaches the console, the file sink, or Application Insights — in both the WebApp and adapter hosts — so personal data never lands in a log store.

Why this priority: Logs and App Insights are the widest, least-controlled leak surface and are retained/queryable long-term. This is the core guarantee.

Independent Test: Configure a sample event with `[Sensitive]` on a `Cpr` property. Emit a structured log following the convention (`EventTypeId` + `EventJson`) from a host that called `AddNimBusLogMasking()`. Capture the sink output and assert `Cpr` reads `***` and a `$piiMasked` marker is present, while non-sensitive fields are verbatim.

Acceptance Scenarios:

1. Given a host registered `IPlatform` and called `AddNimBusLogMasking()`, When it logs `EventTypeId` + `EventJson` for an event whose contract marks `Cpr` `[Sensitive]`, Then the sink receives the JSON with `Cpr` = `***`.
2. Given `[Sensitive(Mode = PartialReveal, Reveal = 4)]` on a `Phone` field, When `"12345678"` is logged, Then the sink receives `"****5678"`.
3. Given `[Sensitive(Mode = Hash)]` on an `Email` field, When the same email is logged twice, Then both sink lines carry the same 64-char hex hash (deterministic correlation, value not recoverable).
4. Given a host that did NOT register a platform but called `AddNimBusLogMasking()`, When it logs an event payload, Then the payload is replaced with `[REDACTED:masker-unavailable]` (fail-closed), not the raw value.
5. Given a log call that carries `EventJson` but omits `EventTypeId`, Then it passes through unmasked (documented convention: payload-carrying calls MUST pass `EventTypeId`), and this is called out in the docs as a known sharp edge.

---

### User Story 2 - Resubmit-with-changes does not leak payload into logs or audit (Priority: P1)

As a compliance officer, I want the resubmit-with-changes action to record *what was done by whom to which event* in the audit trail, but never the raw operator-supplied payload, so the audit log itself is not a PII store.

Why this priority: This is the single most direct leak in the current WebApp — the entire `body` is serialized into both `LogInformation` and the durable audit `data` on every branch.

Independent Test: Call `POST /api/events/{id}/resubmit-with-changes` with a body whose `EventContent.EventJson` contains a `[Sensitive]` field. Inspect (a) the captured log output and (b) the persisted audit row's `Data`. Assert neither contains the raw sensitive value; the audit `Data` contains the masked JSON; the log contains only identifiers.

Acceptance Scenarios:

1. Given a resubmit-with-changes succeeds, When the audit row is read, Then its `Data` is the masked event JSON (sensitive fields `***`/`[REDACTED]`/hash) and `EventTypeId` is verbatim (not PII).
2. Given the same call, When the application log is inspected, Then it contains an identifier-only line (EventTypeId, EventId, EndpointId, auditor) and no `EventJson`.
3. Given the call is rejected (access-denied or fail-closed PII gate), When the audit row is read, Then the access-denied/rejection row is still written and its `Data` is still masked (no raw payload on the rejection path either).

---

### User Story 3 - WebApp reveal is gated by role, masked by default everywhere (Priority: P1)

As a security owner, I want raw event payloads in the WebApp Event Details view to be masked for every user by default — including in local development — and revealed only to users holding the `PiiReader` role, so that need-to-know is enforced and the local dev convenience does not become a leak.

Why this priority: The Event Details view is the interactive reveal surface; an un-gated reveal defeats the masking everywhere else.

Independent Test: Open Event Details for an event with a `[Sensitive]` field (a) as a user without `PiiReader` and (b) as a user with `PiiReader`. Assert (a) renders `***` and (b) renders the raw value. Confirm the masking is applied server-side (the masked response never contains the raw value over the wire).

Acceptance Scenarios:

1. Given a user without `PiiReader` opens Event Details, When the event-detail payload is fetched, Then the server returns `EventJson` with sensitive fields masked; the SPA renders the masked value with an indication that PII is hidden.
2. Given a user with `PiiReader` opens the same event, When the payload is fetched, Then the server returns the raw `EventJson`.
3. Given the app runs under `LocalDevAuthHandler` (local dev), When any user opens Event Details, Then the payload is masked by default (the dev handler issues `EIP_Management` but NOT `PiiReader`); a developer who needs raw values adds a `PiiReader` claim to the dev handler explicitly.
4. Given masking is applied server-side, When the network response is inspected, Then the raw sensitive value is absent from the wire for non-`PiiReader` users (not merely hidden by CSS).

---

### User Story 4 - Adapter authors declare sensitivity once, in the contract (Priority: P2)

As an adapter author, I want to annotate event-contract properties with `[Sensitive]` once and have masking apply automatically across logs, audit, and reveal, so I do not hand-roll redaction at every call site.

Why this priority: Declarative, single-source-of-truth sensitivity is what makes the subsystem maintainable; without it, masking decays.

Independent Test: Add `[Sensitive]` to a property on a sample event, register the platform, and confirm the field is masked in a handler log, in a resubmit audit row, and in the WebApp reveal — without any per-call-site code.

Acceptance Scenarios:

1. Given a `[Sensitive]` property on an event contract, When the masker resolves the event type via `IPlatform`, Then that field is masked everywhere the masker runs.
2. Given a class-level `[Sensitive]` on a nested complex type (e.g. `Address`), Then all members of that nested type are masked.
3. Given a `[JsonProperty(PropertyName = "ssn")]` renamed `[Sensitive]` property, Then the JSON key `ssn` is masked (the masker mirrors the contract resolver).

---

### User Story 5 - Masking failures fail closed, never leak (Priority: P1)

As a security owner, I want any masker error or missing-platform condition to produce a redaction placeholder rather than the raw value, so a bug in the masking path can never cause a worse outcome than over-redaction.

Why this priority: The whole point is to never leak; the failure mode must be safe.

Independent Test: Inject a masker that throws. Drive a log call and a resubmit audit write. Assert both emit a `[REDACTED:masker-error]`/`[REDACTED:masker-unavailable]` placeholder, not the raw payload, and the user-facing action still succeeds.

Acceptance Scenarios:

1. Given the masker throws on `Mask(...)`, When a payload-bearing log line is emitted, Then the sink receives `[REDACTED:masker-error]` and the log call does not throw.
2. Given no `IPlatform` is registered, When `AddNimBusLogMasking()` is active, Then the `FailClosedEventJsonMasker` returns `[REDACTED:masker-unavailable]`.
3. Given an unknown `eventTypeId` or unparseable JSON, Then the masker returns `[REDACTED:unknown-type]` / `[REDACTED:invalid-json]` respectively, never the input.

---

## Edge Cases

- **Already-masked payload** (carries `$piiMasked`): masking is idempotent — re-masking is a no-op; the operator resubmitting a masked payload is detectable via `ContainsRedactPlaceholder` (used to reject a resubmit that would persist a masked value as if it were real data).
- **`PartialReveal` with `Reveal` ≥ value length**: falls back to full `***` rather than revealing the whole value.
- **`PartialReveal` misconfiguration** (`Reveal <= 0`): the masker validates at construction and throws, surfacing the contract bug at startup rather than silently revealing.
- **Collections / arrays of complex items**: each item's `[Sensitive]` fields are masked.
- **Null / empty `EventJson`**: returned unchanged.
- **Log line carrying `EventContent` but not `EventJson`** (or vice-versa): both property names are recognized per `LogMaskingConventions`.
- **Hash mode salt**: deterministic within a deployment for correlation; salt is configuration, not a secret (documented as pseudonymization, not a MAC).

## Requirements

### Functional Requirements

Core masking (NimBus.Core.Pii):

- **FR-001**: A `SensitiveAttribute` MUST be applicable to properties and classes, with `Mode` (`Redact` default, `PartialReveal`, `Hash`) and `Reveal` (int, default 0) members; `Inherited = true`.
- **FR-002**: A `MaskMode` enum MUST define `Redact = 0`, `PartialReveal = 1`, `Hash = 2`.
- **FR-003**: `IEventJsonMasker` MUST expose `string Mask(string eventTypeId, string eventJson)`, `bool ContainsRedactPlaceholder(string eventTypeId, string eventJson)`, and `string StripMaskedMarker(string eventJson)`.
- **FR-004**: `EventJsonMasker` MUST resolve `eventTypeId` to a CLR type via `IPlatform.EventTypes` / `IEventType.GetEventClassType()` and build a type cache at construction.
- **FR-005**: `Mask` MUST return the input unchanged for null/empty JSON; `[REDACTED:unknown-type]` for an unresolvable type; `[REDACTED:invalid-json]` for unparseable JSON.
- **FR-006**: `Mask` MUST recursively walk objects, arrays, and nested complex types, masking each `[Sensitive]` field per its `Mode`, mirroring `DefaultContractResolver` so `[JsonProperty]` renames are honored.
- **FR-007**: `Redact` MUST emit `***`; `PartialReveal` MUST reveal the last `Reveal` chars (falling back to `***` when too short); `Hash` MUST emit a salted SHA-256 hex string (deterministic per deployment).
- **FR-008**: When any field is masked, `Mask` MUST stamp a `$piiMasked` sidecar at the JSON root; output MUST be minified.
- **FR-009**: `ContainsRedactPlaceholder` MUST detect the `$piiMasked` marker (primary) and fall back to scanning `[Sensitive]` fields for the redact token.
- **FR-010**: A `NullEventJsonMasker` (pass-through, no-op) MUST be provided for sample/bootstrap use; production hosts MUST register `EventJsonMasker`.

Log masking (NimBus.SDK.Logging):

- **FR-100**: `LogMaskingConventions` MUST define the recognized property names (`EventTypeId`, `EventJson`, `EventContent`) and placeholders (`[REDACTED:masker-error]`, `[REDACTED:masker-unavailable]`) as public constants, reused by every masking layer.
- **FR-101**: A `MaskingLogger` decorator MUST intercept `ILogger.Log<TState>`, extract `EventTypeId` + `EventJson`/`EventContent` from structured state, mask the payload, and forward the rewritten state to the inner logger.
- **FR-102**: A `MaskingLoggerFactory` MUST wrap the host's `ILoggerFactory` so every `ILogger<T>` created after registration is masked.
- **FR-103**: `AddNimBusLogMasking()` MUST be a single-line, idempotent DI opt-in that registers the masker singleton and swaps the `ILoggerFactory` descriptor; it MUST throw if no `ILoggerFactory` is registered.
- **FR-104**: A `FailClosedEventJsonMasker` MUST back the pipeline when no `IPlatform` is resolvable, returning `[REDACTED:masker-unavailable]` for all payloads.
- **FR-105**: A masker error during a log call MUST yield `[REDACTED:masker-error]` and MUST NOT throw out of the log call.
- **FR-106**: The same `MaskingLoggerFactory` MUST be usable by the WebApp host (NimBus uses `Microsoft.Extensions.Logging`, so no Serilog-specific enricher is required).

WebApp audit masking:

- **FR-200**: A `BuildAuditDataForResubmit(body)` helper MUST mask `EventContent` through `IEventJsonMasker` before the value is passed as `data` to `IAuditLogService.LogAuditAsync(...)`, keeping `EventTypeId` verbatim.
- **FR-201**: The resubmit-with-changes path MUST replace raw-`body` `LogInformation` calls with identifier-only logs (EventTypeId + EventId + EndpointId + auditor; never payload), on success, access-denied, and fail-closed branches.
- **FR-202**: A resubmit whose supplied payload already carries the `$piiMasked` marker MUST be rejected (operator must re-enter real PII), surfaced as a validation error, not silently persisted.
- **FR-203**: Search-audit and other non-payload audit `data` MUST be left unmasked (they carry filter criteria, not payload).

Role-gated reveal:

- **FR-300**: `IEndpointAuthorizationService` MUST expose `bool CurrentUserCanReadPii()` returning true only when the current principal holds the `PiiReader` role.
- **FR-301**: The event-detail read path MUST mask `EventJson` server-side through `IEventJsonMasker` when `CurrentUserCanReadPii()` is false; the raw value MUST NOT cross the wire for non-`PiiReader` users.
- **FR-302**: `CurrentUserCanReadPii()` MUST NOT short-circuit to true in any environment, including Development.
- **FR-303**: `LocalDevAuthHandler` MUST NOT issue the `PiiReader` role by default; the file MUST document how a developer opts in locally (add a `PiiReader` role claim).
- **FR-304**: The SPA Event Details view MUST visually indicate when PII is masked vs. revealed, driven by the server response (no client-side reveal of masked data).

Sample showcase (item #14):

- **FR-400**: At least one sample event contract MUST demonstrate each `MaskMode` (`Redact`, `PartialReveal`, `Hash`) via `[Sensitive]`.
- **FR-401**: The sample publisher UI MUST expose fields that populate the sensitive properties so the end-to-end masking is demonstrable locally.

### Non-Functional Requirements

- **NFR-001**: Masking MUST add no measurable latency to the hot message path (it runs only at log/audit/reveal boundaries, never on every message dispatch).
- **NFR-002**: The type cache MUST be built once per masker instance; reflection MUST NOT run per-call.
- **NFR-003**: The subsystem MUST add no new third-party dependency (Newtonsoft.Json and `Microsoft.Extensions.Logging` are already present).
- **NFR-004**: Release builds MUST stay warning-clean (`TreatWarningsAsErrors`).

## Key Entities

- **`SensitiveAttribute`** (`NimBus.Core.Pii`): declarative marker; `Mode`, `Reveal`.
- **`MaskMode`** (`NimBus.Core.Pii`): `Redact` / `PartialReveal` / `Hash`.
- **`IEventJsonMasker` / `EventJsonMasker` / `NullEventJsonMasker`** (`NimBus.Core.Pii`): the masking contract + implementations.
- **`LogMaskingConventions`** (`NimBus.SDK.Logging`): shared property-name + placeholder constants.
- **`MaskingLogger` / `MaskingLoggerFactory` / `FailClosedEventJsonMasker`** (`NimBus.SDK.Logging`): the `Microsoft.Extensions.Logging` decorator pipeline.
- **`AddNimBusLogMasking`** (`NimBus.SDK`): DI opt-in.
- **`PiiReader` role + `CurrentUserCanReadPii`** (`NimBus.WebApp.Services`): reveal gate.

## Success Criteria

### Measurable Outcomes

- **SC-001**: A captured sink shows `***`/masked output for every `[Sensitive]` field on every payload-bearing log line from a host that opted in; zero raw sensitive values in the sink.
- **SC-002**: The resubmit-with-changes audit row's `Data` and the corresponding log line contain zero raw sensitive values across success, access-denied, and fail-closed branches.
- **SC-003**: A non-`PiiReader` user's Event Details network response contains zero raw sensitive values; a `PiiReader` user's response contains them.
- **SC-004**: Local dev (`LocalDevAuthHandler`) shows masked payloads by default.
- **SC-005**: Masker/platform failure produces a `[REDACTED:*]` placeholder, never the raw value; the user-facing action still succeeds.
- **SC-006**: Full solution builds warning-clean; new unit tests (masker matrix, log decorator, audit masking, authz) all pass.

## Assumptions

- Event contracts are CLR types discoverable via the registered `IPlatform`; events without a resolvable type are masked to `[REDACTED:unknown-type]` (fail-closed).
- The message store remains the system of record for the true payload; masking is a boundary transform, not at-rest redaction.
- Hosts opt in explicitly via `AddNimBusLogMasking()`; the library does not force-decorate logging.
- The hash salt is supplied via configuration and is for correlation/pseudonymization, not cryptographic authentication.

## Out of Scope

- At-rest masking/encryption of the stored event record or the on-the-wire Service Bus body.
- Masking non-event data (operator notes, metadata, search filters).
- Retroactive masking of historical audit rows.
- A general-purpose redaction framework beyond event-payload JSON keyed by `eventTypeId`.

## Open Questions

- **OQ-001**: Hash-mode salt source. Recommendation: a `NimBus:Pii:HashSalt` config value, defaulting to empty (documented as correlation-only). Confirm the config key / whether per-environment salts are required.
- **OQ-002**: Is `PiiReader` a standalone role or implied by an existing group (e.g. should `EIP_Management` admins automatically get reveal)? Recommendation (matching DIS): a **standalone** `PiiReader` role, NOT implied by `EIP_Management`, so reveal is an explicit, separately-grantable capability. Confirm.
- **OQ-003**: Should opening Event Details as a `PiiReader` itself produce a distinct "PII revealed" audit row (beyond the existing spec-008 `GetEventDetails` row)? Recommendation: reuse the spec-008 view-audit and add a `PiiRevealed` flag rather than a new audit type.

## Resolved Questions

- **RQ-001**: *Where do the masking contracts live?* `NimBus.Core.Pii` for the masker/attribute (it is a message/event concern and `NimBus.Core` already houses message types and references `IPlatform` via `NimBus.Abstractions`); `NimBus.SDK.Logging` for the logging decorator and DI opt-in (adapter-facing). This mirrors DIS's `BH.DIS.Core/Messages/PII` + `BH.DIS.SDK/Logging` split.
- **RQ-002**: *Serilog enricher vs. M.E.Logging decorator?* DIS needed both because its WebApp was Serilog and its adapters were `Microsoft.Extensions.Logging`. **NimBus's WebApp is `Microsoft.Extensions.Logging`** (`AddApplicationInsightsTelemetry()`, no Serilog), so a **single `MaskingLoggerFactory`** covers both the WebApp and adapter hosts. We do NOT port `PiiMaskingLogEnricher`. This is a net simplification over DIS.
- **RQ-003**: *Net-new role vs. existing.* NimBus has no PII role or `CurrentUserCanReadPii` today, so #6 is net-new (DIS's commit merely removed a dev short-circuit from an existing method). We add the method and role from scratch, masked-by-default in all environments.

## Implementation Phases

Phased so each lands independently with its own tests and review:

1. **Phase 1 — Core masking infrastructure** (`NimBus.Core.Pii`): `SensitiveAttribute`, `MaskMode`, `IEventJsonMasker`, `EventJsonMasker`, `NullEventJsonMasker` + full masker unit-test matrix. No behavior change yet; pure foundation. (Unblocks all others.)
2. **Phase 2 — Log masking pipeline** (`NimBus.SDK.Logging`): `LogMaskingConventions`, `MaskingLogger`, `MaskingLoggerFactory`, `FailClosedEventJsonMasker`, `AddNimBusLogMasking()`; wire into the WebApp host and the sample adapter hosts + decorator tests.
3. **Phase 3 — WebApp audit masking**: `BuildAuditDataForResubmit`, identifier-only resubmit logs, masked audit `data`, `$piiMasked` resubmit rejection + tests. (Closes the most direct current leak.)
4. **Phase 4 — Role-gated reveal**: `PiiReader` role + `CurrentUserCanReadPii`, server-side mask on the event-detail read path, `LocalDevAuthHandler` default, SPA masked/revealed affordance + authz test.
5. **Phase 5 — Sample showcase** (item #14): `[Sensitive]` on sample events (all three modes) + publisher-form fields; `docs/pii-masking.md` + `building-adapters.md` section.
