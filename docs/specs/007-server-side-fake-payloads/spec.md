# Feature Specification: Server-Side Schema-Valid Fake Event Payloads

Feature Branch: `007-server-side-fake-payloads`
Created: 2026-05-28
Updated: 2026-05-28
Status: Proposed (back-port candidate from DIS; see DIS commit `776550e5`).
Input: User description: "The WebApp's Compose New Event and Resubmit-with-changes dialogs offer a 'Generate fake data' action that today runs entirely in the browser: it walks the EventType's example payload as JSON, replaces leaf values heuristically based on field names, and inserts the result into the textarea. That heuristic is type-blind — it has no way to know a nullable enum field exists, that `[StringLength(20)]` limits a string, or that the type defines its own validation rules — so the generated payload often fails the server-side `IEvent.TryValidate` gate on send (e.g., `Could not convert string 'Sample CustomerServiceArea 156' to enum CustomerServiceArea`). We want a server endpoint `GET /api/event-types/{eventtypeid}/fake` that reflects over the registered CLR type, produces a randomized payload that satisfies the type's own validation, and is consumed by the Compose dialog instead of the client-side heuristic."

## Problem

The WebApp's existing "Generate fake data" action is implemented in `components/dev/dev-tools.tsx` as a JavaScript function (`generatePayload`) that takes the example JSON for an event type and walks its keys substituting heuristic values (`Pick("Alice", "Bob", …)` for fields named `*name`, formatted digit strings for `*code`, etc.). The heuristic is convenient but fundamentally type-blind:

- An event property typed as a nullable `CustomerServiceArea` enum gets a heuristic string (e.g., `"Sample CustomerServiceArea 156"`) that fails `Newtonsoft.Json` enum binding on send.
- A property typed as `Guid` may get a digit-string that does not parse.
- A `[StringLength(40)]` field gets a value clamped to the heuristic's own length, not the attribute's, and rejects on the server.
- The type's own `TryValidate` rules — required combinations of fields, cross-field invariants, regex constraints — are entirely invisible to the browser.

The net effect is that "Generate fake data" produces a payload that *looks* plausible but rejects with a 400 on send. The operator then has to hand-edit until validation passes, which is the exact friction the button was supposed to remove.

The Compose / Resubmit-with-changes flow already gates submission on `IEvent.TryValidate` server-side (any invalid payload returns a 400 with the validation errors). The clean fix is to move the generator there too: reflect over the registered CLR type, walk the actual property graph type-aware, run the value through the same `TryValidate` gate the submit path uses, retry until valid (capped at a small number of attempts), and return the resulting JSON to the client. The browser then just inserts the response into the textarea — no client-side guesswork.

## Scope

In scope:
- A new endpoint `GET /api/event-types/{eventtypeid}/fake` on the WebApp that, given an event type id, returns a JSON payload string guaranteed to:
  (a) deserialize as the type, and
  (b) satisfy the type's own `IEvent.TryValidate` rules — the same gate the Compose / Resubmit-with-changes submit path applies.
- A new `FakeEventPayloadGenerator` class in the platform-side core library (or `NimBus.WebApp.Services/`) that performs the reflection-driven randomization and validation loop.
- The generator's strategy: seed from the type's authored `Example` (when present), deep-clone it, type-aware randomize every leaf, run `TryValidate`, retry up to a small fixed number of attempts; on exhaustion fall back to the unrandomized example (when seeded) or the latest randomized attempt (when not). Return `null` only when the type cannot be constructed.
- Updating the WebApp ClientApp's "Generate fake data" button to call the new endpoint, replacing the in-browser heuristic.

Out of scope:
- Removing the existing client-side heuristic codepath entirely in v1. After the server endpoint is wired up and verified, the obsolete heuristic can be deleted in a follow-up. v1 simply stops calling it.
- Server-side schema export (JSON Schema, OpenAPI component schemas) for event types. The generator works directly off CLR reflection, not a derived schema artefact.
- A "Generate fake data" button on event types defined outside the platform graph (e.g., types loaded only by an adapter). The endpoint serves types in `IPlatform.EventTypes`, which is the same surface the rest of the page uses.
- Heuristics for non-canonical types. Properties whose CLR type is not enum / Guid / string / numeric / bool / DateTime / DateTimeOffset / nested object / collection are skipped (their values stay at the example or default), same as DIS.
- Multilingual or locale-aware fake values. v1 uses the DIS heuristics (English-Danish bias) as a starting point; localization is a follow-up if requested.

## User Scenarios & Testing

### User Story 1 - Compose dialog produces a payload that submits cleanly on first try (Priority: P1)

As an integration developer using the WebApp's Compose New Event dialog, I want the "Generate fake data" button to fill the textarea with a payload that submits without a 400, so that I can spend my time on the integration scenario instead of guessing field shapes.

Why this priority: This is the central user complaint the spec exists to fix.

Independent Test: Open the Compose dialog for an event type that has at least one nullable enum, one `[StringLength]`-constrained string, and one `Guid`. Click "Generate fake data". Click Submit. The submit succeeds (200 / 202) on the first attempt, with no manual edits.

Acceptance Scenarios:

1. Given an event type whose CLR shape includes enum, `Guid`, `[StringLength]` string, integer, bool, and `DateTime` properties, When the user clicks "Generate fake data" and immediately submits, Then the request returns 2xx and the resulting event has populated values that match each property's CLR type.
2. Given an event type whose CLR shape includes nested objects, When the user clicks "Generate fake data", Then the nested objects are recursively populated with type-aware values.
3. Given an event type whose CLR shape includes collections (lists / arrays of complex items), When the user clicks "Generate fake data", Then collection *elements* present in the example are recursively populated; the generator does not fabricate new elements from nothing (it cannot infer how many).
4. Given the same event type, When the user clicks "Generate fake data" twice in a row, Then the two payloads differ in at least one leaf value (randomization is per-call).

---

### User Story 2 - Endpoint refuses unknown event types (Priority: P1)

As a security-conscious operator, I want the endpoint to return a 404 when called with an event type id that is not in the platform graph, so that the surface cannot be abused as a generic CLR-reflection oracle.

Why this priority: The endpoint reflects over loaded types. Its scope must match the rest of the EventType API — platform-known types only.

Independent Test: Issue `GET /api/event-types/00000000-0000-0000-0000-000000000000/fake`. Expect 404 with a clear body. Issue `GET /api/event-types/{validEventtypeid}/fake`. Expect 200 with a payload field.

Acceptance Scenarios:

1. Given an unknown event type id, When the endpoint is called, Then the response is 404 `Not Found` and the body is `"EventType not found"` (matching the pattern of `GetEventtypesEventtypeidAsync`).
2. Given a valid event type id but the type was not present in `IPlatform.EventTypes` at startup, When the endpoint is called, Then the response is 404.
3. Given a malformed id, When the endpoint is called, Then the response is 400 or 404 — whichever the existing controller routing convention enforces — but never 500.

---

### User Story 3 - Type with no authored example still gets a usable payload (Priority: P2)

As an integration developer composing an event whose type defines no `static T Example = …` field, I want the generator to construct a fresh instance from the type and populate it, so that "Generate fake data" is useful for newly-authored types where no example has been written yet.

Why this priority: Without it, the button degrades into "no example available, sorry" on exactly the events most likely to need composition help.

Independent Test: Register an event type that defines no `Example`. Hit `GET /api/event-types/{eventtypeid}/fake`. Receive a 200 with a non-null payload string whose JSON shape includes every public writable property of the type.

Acceptance Scenarios:

1. Given an event type with no authored example, When the endpoint is called, Then the generator constructs a fresh instance from the parameterless constructor, populates every public writable leaf with a type-aware value, recurses into nested objects, and returns the serialized result.
2. Given an event type whose CLR type is `abstract` or has no accessible parameterless constructor, When the endpoint is called, Then the endpoint returns 200 with a payload of `null`. The client renders a "Could not generate fake data" toast — no 500.

---

### User Story 4 - Existing example payloads continue to work as the fallback baseline (Priority: P2)

As a NimBus maintainer, I want the generator to never *worsen* a type's authored example: if no randomized variant validates within the retry budget, the unrandomized example MUST be returned as the safe fallback, because by contract the authored example validates.

Why this priority: Authored examples are written by integration developers as known-good payloads. The generator must respect them as a floor, not a ceiling.

Independent Test: Register an event type whose example validates and whose randomization is unlikely to (e.g., a regex-constrained field). Hit the endpoint repeatedly. Every response either validates (a successful random variant) or equals the example (the safe fallback). Never returns invalid JSON.

Acceptance Scenarios:

1. Given an event type whose example validates and whose randomization frequently fails validation, When the endpoint is called, Then within the configured retry budget (5 attempts by default) the response is either a randomized payload that validates, or the authored example serialized verbatim.
2. Given an event type with no example and whose randomization fails to validate every attempt, When the endpoint is called, Then the response is the latest randomized attempt (fully populated, type-correct, but not guaranteed to satisfy every validation attribute), accompanied by 200. The client treats this as best-effort; the operator can still hand-edit.

---

### User Story 5 - Client wiring replaces the heuristic without breaking the dialog (Priority: P1)

As an operator, I want the Compose dialog to keep working exactly as today — same button, same textarea, same submit affordance — but with the new payload source, so that the change is invisible until I notice that the payloads now submit cleanly.

Why this priority: The user experience is unchanged; only the payload quality improves. Any visible behaviour regression here defeats the spec.

Independent Test: Click "Generate fake data" with the new wiring in place. The textarea fills, the modal does not flicker beyond a brief loading state, and the submit succeeds.

Acceptance Scenarios:

1. Given the user clicks "Generate fake data" with a successful endpoint response, When the call returns, Then the textarea is populated with the returned `payload` string and the existing JSON-pretty-print layout renders unchanged.
2. Given the endpoint returns 200 with `payload: null`, When the call returns, Then a non-blocking toast informs the user that fake data could not be generated for this event type and the textarea is left as-is.
3. Given the endpoint returns 404, When the call returns, Then a non-blocking toast informs the user that the event type was not found (this should be impossible in normal flow but is handled defensively).
4. Given the endpoint returns 5xx or the network fails, When the call returns, Then a toast informs the user that the action failed; the textarea is left as-is. No client-side fallback to the old heuristic.
5. Given the endpoint is slow, When the call is in flight, Then the button shows a loading state and is disabled to prevent double-click duplicate calls.

---

## Edge Cases

- Event type has a property whose CLR type is an unsupported leaf (e.g., custom `record struct`). The generator skips the property (leaves the example or default value). Documented in the generator's class comment.
- Event type has a `MessageMetadata` property (internal envelope). The generator MUST skip it — it is set by the platform on send, not authored by users.
- Property is marked `[JsonProperty("…")]` with a custom name. The generator works off CLR property names for the randomizer, but `JsonConvert.SerializeObject` honors the attribute on serialization — so the response JSON contains the externally-visible property name, identical to what the submit path expects.
- Property is marked `[JsonConverter(typeof(MyConverter))]`. The generator runs serialization through the same converters Newtonsoft does on send, so a custom converter is honored both ways (round-trip safety).
- Property has `[StringLength(min, max)]` or `[MinLength]` / `[MaxLength]` attributes. The generator MUST clamp / pad to those bounds; otherwise validation fails.
- Property has `[Required]`, `[Range]`, `[RegularExpression]`, etc. These run as part of `TryValidate`; if no randomized variant satisfies them within the retry budget, the example (or null fallback) is returned.
- Two concurrent requests for the same type. The generator MUST use a per-call `Random` instance (seeded from `Guid.NewGuid().GetHashCode()` or equivalent); shared `Random` state is a correctness bug under load.
- `IEvent.TryValidate` throws (poorly-written validator). The generator catches and treats the attempt as invalid, retries up to the budget. A consistently throwing validator returns the example or null.
- The type's example mutates (e.g., the authored example is a singleton instance). The generator MUST deep-clone the example before randomizing — never mutate the static field.
- Recursion depth: a type with a self-referential structure could recurse indefinitely. The generator caps depth at a fixed bound (12) and ignores deeper levels.
- Collection elements: the generator only recurses into elements *present* on the example; it does not fabricate new elements. A type whose example collection is empty and whose validator requires at least one element will not be randomly satisfied; the fallback path returns the example anyway.
- Endpoint hit by an unauthenticated user. The endpoint inherits the same global authorize policy as the rest of the WebApp API; no anonymous access.

## Requirements

### Functional Requirements

#### REST endpoint

- FR-001: A new route `GET /api/event-types/{eventtypeid}/fake` MUST be added to the WebApp. The path and parameter name MUST match the existing event-type routes verified in `src/NimBus.WebApp/api-spec.yaml` (line 128 — `/api/event-types`, line 143 — `/api/event-types/{eventtypeid}`). NSwag's generated TypeScript method name on `api-client` will follow the existing `get-eventtypes-eventtypeid` operation-id pattern: suggested operationId `get-eventtypes-eventtypeid-fake`, generated client method `getEventtypesEventtypeidFake({eventtypeid})`.
- FR-002: The endpoint MUST be added to `api-spec.yaml` and surfaced through the existing NSwag-generated TypeScript client (`ApiContract.g.cs` on the server side; `api-client` on the SPA side). The generated method name MUST follow the existing convention (e.g., `getEventtypesEventtypeidFake`).
- FR-003: The endpoint MUST return a response body shaped as:
  ```json
  { "payload": "<json string or null>" }
  ```
  The `payload` field is a fully-formed JSON string (indented), not a structured object — the client inserts it verbatim into the textarea.
- FR-004: The endpoint MUST return 404 with body `"EventType not found"` when `eventTypeId` does not match any type in `IPlatform.EventTypes`.
- FR-005: The endpoint MUST honor the global authorize policy. Anonymous calls return 401 (or redirect to login, per the existing scheme).
- FR-006: The endpoint MUST NOT mutate any platform state. It is read-only / pure.

#### Generator

- FR-010: A new class `FakeEventPayloadGenerator` MUST be added to a project consumed by the WebApp. Suggested location: `NimBus.WebApp.Services/FakeEventPayloadGenerator.cs` (keeps the reflection logic adjacent to the controller). Alternative: `NimBus.Core.Events` if reusable from other hosts.
- FR-011: The generator MUST expose `string? Generate(IEventType eventType)` — returns an indented JSON payload string, or `null` when the type cannot be constructed.
- FR-012: The strategy MUST be:
  1. Resolve the CLR type via `eventType.GetEventClassType()`. Return `null` if missing.
  2. Obtain the authored example via `eventType.GetEventExample()`. If present, deep-clone it (round-trip through `JsonConvert.SerializeObject` / `DeserializeObject`) so the static field is not mutated.
  3. Construct a fresh instance via `Activator.CreateInstance(type, nonPublic: true)` if no example is available. Return `null` if construction throws or the type is abstract / interface.
  4. Per-call: instantiate a new `Random` seeded from `Guid.NewGuid().GetHashCode()`.
  5. Up to `MaxAttempts` (default 5): randomize every leaf in the cloned instance, run `instance.TryValidate()`. If valid, serialize and return. Remember the latest invalid attempt as `lastCandidate`.
  6. If no randomized variant validates: if an example was used, return the example serialized verbatim; otherwise return `lastCandidate`.
- FR-013: Randomization MUST be type-aware on these leaf kinds:
  - **Enum** (nullable or not): pick a random member from `Enum.GetValues(type)`.
  - **`Guid`**: `Guid.NewGuid()`.
  - **`string`**: heuristic value keyed on the property name (email-like, phone-like, postcode-like, name-like, code-like, etc.; falls back to `"Sample {PropName} {digits}"`). Clamped / padded to `[StringLength]` / `[MinLength]` / `[MaxLength]` bounds.
  - **`bool`**: random.
  - **`DateTime`** / **`DateTimeOffset`**: now minus 0-365 days.
  - **Integer** kinds (`int`, `long`, `short`, `byte`, `sbyte`, `uint`, `ulong`, `ushort`): 1-999.
  - **`decimal`** / **`double`** / **`float`**: random 0-1000, rounded to 2 decimals.
- FR-014: Nested complex objects MUST be recursed into, capped at a fixed `MaxDepth = 12`. Properties whose value is null on the example AND whose type has a public parameterless constructor MUST be instantiated and populated when building-from-bare-type (`buildMissing = true`).
- FR-015: Collections MUST have their existing elements recursed into; the generator MUST NOT fabricate new elements from nothing.
- FR-016: The property named `MessageMetadata` (the internal envelope) MUST be skipped at every depth.
- FR-017: Index properties and read-only properties MUST be skipped.
- FR-018: Serialization MUST use `JsonConvert.SerializeObject(instance, Formatting.Indented)` so `[JsonProperty]` / `[JsonConverter]` attributes on the type are honored, matching the submit path's deserializer.

#### Client wiring

- FR-020: `components/event-types/compose-event-modal.tsx` (or whichever component owns the Compose dialog) MUST call the generated `getEventtypesEventtypeidFake({id})` method on click of "Generate fake data". The result's `payload` field MUST be inserted verbatim into the textarea.
- FR-021: The button MUST show a loading state during the request and MUST be disabled to prevent duplicate calls.
- FR-022: Error handling:
  - 200 with `payload === null` → non-blocking toast: "Fake data could not be generated for this event type."
  - 404 → non-blocking toast: "Event type not found."
  - 5xx / network error → non-blocking toast: "Could not generate fake data." (Generic; the user can retry or hand-edit.)
- FR-023: The existing client-side `generatePayload` heuristic in `components/dev/dev-tools.tsx` MUST NOT be deleted in v1. The Compose flow stops calling it; the function stays in place for any other current callers (Admin → Dev Tools may still consume it locally). A follow-up issue tracks final removal.

#### Tests

- FR-030: Unit tests on the server MUST cover, at minimum:
  1. Generator returns null for an abstract type.
  2. Generator returns a valid JSON string for a type with an authored example and no validation attributes.
  3. Generator respects `[StringLength(max)]` — clamps long heuristic values.
  4. Generator respects nullable enum — produces a member value, not a heuristic string.
  5. Generator falls back to the example when validation never passes randomly (verified by giving the type a [RegularExpression] that excludes every random value).
  6. Generator returns latest random attempt (not null) when no example exists and validation never passes.
  7. Generator is concurrent-safe (no shared `Random`): two parallel calls produce two payloads.
- FR-031: A controller-level integration test MUST verify the endpoint:
  1. Returns 404 for an unknown id.
  2. Returns 200 with `payload` populated for a known id.
  3. Requires authentication (401 anonymous).
- FR-032: A Vitest test MUST verify the client wiring:
  1. Click "Generate fake data" → API call → textarea populated.
  2. API returns `payload: null` → toast shown, textarea unchanged.
  3. API returns 5xx → toast shown, textarea unchanged.

#### Documentation

- FR-040: `docs/webapp-rest-api.md` MUST document the new endpoint, request shape, response shape, and error codes.
- FR-041: A short note in `docs/extensions.md` (or the event type authoring guide) MUST explain that authored `static T Example` payloads now feed both the existing "Example payload" tab *and* the "Generate fake data" baseline — so authoring a good example pays off twice.

### Non-Functional Requirements

- NFR-001: The generator MUST be deterministic given identical inputs and seed. (The per-call seed is fresh — but two calls with the same seed MUST produce the same payload, for test reproducibility.)
- NFR-002: The endpoint MUST respond within 100 ms for typical event types (a handful of properties, shallow nesting). Worst case is bounded by `MaxAttempts × per-attempt cost`, which is `5 × O(properties)`.
- NFR-003: The endpoint MUST NOT allocate unbounded memory. The `MaxDepth = 12` cap is the recursion bound.
- NFR-004: Concurrent calls MUST be safe. The generator instance is registered as a singleton (matching DIS); per-call `Random` instances guarantee no shared mutable state.
- NFR-005: The generator MUST NOT log the produced payload as a structured property or include it in exception text. PII concerns flow through the existing masker for any audit / log surfaces.
- NFR-006: No new NuGet dependency. Reflection (`System.Reflection`), `Newtonsoft.Json`, and `System.ComponentModel.DataAnnotations` are already referenced.

## Key Entities

- **`FakeEventPayloadGenerator`** — new class. Singleton-registered. Method: `string? Generate(IEventType eventType)`. Internal helpers: `DeepClone`, `CreateEvent`, `Randomize`, `TryFakeLeaf`, `FakeString`, `HeuristicString`, `ClampToLength`, `IsComplex`, `IsInstantiableComplex`. Constants: `MaxAttempts = 5`, `MaxDepth = 12`.
- **`FakeEventPayload` DTO** — new shape: `{ payload: string? }`. Added to `api-spec.yaml`.
- **`GET /api/event-types/{eventtypeid}/fake`** — new endpoint. 200 with `FakeEventPayload`, 404 for unknown id.
- **`getEventtypesEventtypeidFake`** — generated TypeScript client method on `api-client`.
- **Compose dialog** — existing `components/event-types/compose-event-modal.tsx` (suggested file). Replaces its client-side `generatePayload(...)` call with the new server call.

## Success Criteria

### Measurable Outcomes

- SC-001: On a representative event type with at least one enum, one Guid, one `[StringLength]`-constrained string, and one numeric leaf, "Generate fake data" → Submit succeeds on the first attempt in ≥ 95 % of trials (allowing for the 5-attempt fallback path on validator-heavy types).
- SC-002: The generated payload always either passes `IEvent.TryValidate` or equals the authored example (verified by repeat-testing the endpoint across the registered platform types).
- SC-003: The endpoint returns 404 with the expected body for an unknown id; returns 200 with a non-null `payload` for every type in `IPlatform.EventTypes` that has either an authored example OR an instantiable parameterless constructor.
- SC-004: Concurrent stress (50 parallel calls for the same type) returns 50 valid payloads with at least 40 distinct leaf-set fingerprints, confirming per-call `Random` state.
- SC-005: The Vitest client-wiring test passes for the populated, null-payload, and error-response cases.
- SC-006: No existing WebApp test fails as a result of the change.

## Assumptions

- The registered `IEventType.GetEventClassType()` and `IEventType.GetEventExample()` (or equivalents on the NimBus event type abstraction) exist and behave as DIS's do. (Verified — NimBus's `IEventType` exposes the same two methods.)
- Authored `static T Example` payloads always validate (the type authors guarantee this, and adding `TryValidate` checking to the example tab is a separate concern).
- Newtonsoft.Json is the serializer on both the submit path and the generator output, matching `JsonConvert` round-trip semantics.
- `IEvent.TryValidate` is idempotent and side-effect-free.
- `IPlatform.EventTypes` is the canonical list of platform-known types. The endpoint serves only this list, matching the scope of the existing event-type APIs.

## Out of Scope

- A schema-export endpoint (`GET /api/event-types/{eventtypeid}/schema`) returning a derived JSON Schema. The generator works off CLR directly; deriving and persisting a separate schema is a different feature.
- Operator-supplied seeds or "regenerate with this preset" affordances. v1 is purely random.
- Generating valid payloads for `IMessage` types beyond `IEvent`. The endpoint is event-type-scoped.
- Localizing the heuristic strings beyond DIS's English-Danish bias. The heuristics produce plausible-looking — not localized — values.
- Removing the existing client-side `generatePayload` heuristic (`components/dev/dev-tools.tsx`). v1 stops calling it from Compose; final removal is a follow-up.
- A `POST /fake` variant that accepts a partial input to "fill in the gaps." All-or-nothing in v1.

## Open Questions

- **Should the heuristic catalogue (`HeuristicString`) be configurable per host?** DIS's heuristics are Danish-bias (cities = Copenhagen / Aarhus, country codes = DNK / SWE). For NimBus's broader audience the heuristics are still plausible enough to validate, but operators may eventually want overrides. *Not blocking v1 — the existing strings validate, which is the only criterion that matters here.*

## Resolved Questions

- The generator lives on the server, not the client. Resolved — the client is by construction CLR-blind, and the type-aware retry-loop is the only design that produces consistently submit-clean payloads.
- The strategy is "seed-from-example, randomize, validate, retry, fall back." Resolved — matches DIS, and the example-as-floor invariant is the operator-friendliest behaviour.
- The endpoint returns a string field, not a structured JSON object. Resolved — the textarea takes raw text; round-tripping through a structured shape would force a re-serialize on the client that risks losing formatting and key order.
- `MaxAttempts = 5` is the retry budget. Resolved — empirically enough for validation rules that are not pathologically narrow; falls back gracefully when not.
- The existing client-side heuristic stays for Admin → Dev Tools. Resolved for v1 — the dev-tools surface is a separate consumer; cleaning it up is a follow-up.
- The endpoint inherits the global authorize policy; no separate role check. Resolved — the surface is read-only and the data it produces is not sensitive (no PII; the heuristics are made-up values).
