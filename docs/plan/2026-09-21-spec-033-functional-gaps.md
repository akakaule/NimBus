# Spec 033 functional gaps implementation plan

## Objective

Finish the remaining Spec 033 behavior on `master` while keeping Integration Intelligence disabled by default, advisory only, and test-only against mocked providers.

## Work slices and regression coverage

1. **Provider contract validation**
   - Define the pinned category set from `FailureClassificationQuestionSet`.
   - Require the response probability map to contain every pinned category exactly once, reject unknown keys, require the selected category to be present, and reject inconsistent confidence/distribution values.
   - Translate invalid provider responses to stable sanitized HTTP 503 responses.
   - Add mocked HTTP tests for missing, unknown, selected-category mismatch, confidence mismatch, and valid full-map responses.

2. **Pre-controller audit boundary**
   - Add a WebApp middleware around the intelligence POST endpoint that observes authentication, authorization, rate-limit, and model-binding/request-validation rejections.
   - Mark requests that entered the controller before the service call so service/controller outcomes are audited exactly once.
   - Use a bounded, best-effort audit attempt with route identifiers only in audit data; failures do not alter the response or replay work.
   - Add TestServer regressions for 401, 403, 429, invalid request, service rejection, and audit failure.

3. **Telemetry completion**
   - Centralize one request/activity/metric recording path for service outcomes and pre-controller rejections.
   - Record `rejected`, `busy`, `unknown`, `provider_error`, `store_error`, `ok`, and `cached` consistently, with provider/model/category plus `nimbus.endpoint` and `nimbus.event_type` tags.
   - Keep credentials, evidence, and event/message/session IDs out of activities and metric tags.
   - Add `MeterListener` and `ActivityListener` assertions for each outcome and tag allow-list.

4. **Classification card and occurrence targeting**
   - Move card rendering to the event error section and create one card per stored failure occurrence, including dead-letter rows with no `errorContent`.
   - Pass each row’s exact `messageId`, discard stale async state after navigation, and keep Reader visibility/action gating.
   - Add explicit “No analysis has been performed.” and “Analyzing failure…” states, historical labeling during re-analysis/unknown recovery, expandable next-two-probability and question-set details, and the advisory disclaimer.
   - Add helper/component tests for occurrence selection, stale navigation, Reader/Contributor visibility, and presentation states.

5. **Activation diagnostics and adapter safety**
   - Emit one sanitized warning for invalid enabled configuration.
   - Make readiness require the host authorization/audit boundary and selected durable storage; prune execution routes when unavailable so controller construction cannot fail.
   - Preserve disabled short-circuiting before nested provider binding and execution/storage registration.
   - Add startup tests for missing adapters, invalid enabled configuration, disabled invalid configuration, and zero provider/store construction.

## Verification

- `dotnet test tests/NimBus.Extensions.IntegrationIntelligence.Tests -c Release -p:SkipSpaBuild=true`
- focused `dotnet test tests/NimBus.WebApp.Tests ...` filters for activation, rate limiting, audit, and telemetry
- `npm test -- --run ...` for the affected WebApp component tests
- `npm run build` in `src/NimBus.WebApp/ClientApp`
- final `dotnet test src/NimBus.sln -c Release -p:SkipSpaBuild=true`

No deployment, publish, or live TypeSafe request is part of this work.

## Completed behavior

- Provider responses now require the exact version-1 category set, a selected category in the map, finite bounded values, and a distribution sum within tolerance. Invalid responses become sanitized `ProviderUnavailable` 503 results and cannot be replayed automatically.
- Classification POSTs are wrapped after routing and before authentication, authorization, rate limiting, and MVC. Pre-controller 400/401/403/429 outcomes get one bounded best-effort audit attempt; controller entry suppresses middleware auditing so service outcomes are not duplicated. Audit failures do not modify the response or trigger provider work.
- Service and pre-controller paths record one activity and metric set with rejected, busy, unknown, provider-error, store-error, ok, or cached outcomes. Tags are limited to provider/model/outcome/category, `nimbus.endpoint`, and `nimbus.event_type`; no credentials, evidence, or event/message/session IDs are emitted.
- The card remains Reader-visible for saved results, keeps Contributor-only actions and the advisory disclaimer, and now shows explicit empty and active-analysis states, historical labeling during re-analysis or unknown recovery, plus expandable next-two probabilities and question-set version.
- Cards are rendered from each stored failure occurrence, including dead-letter rows that have no error content, and use the exact stored event/message coordinates. Component keys, abort cleanup, and occurrence-specific URLs prevent stale navigation results.
- Enabled configuration diagnostics are emitted once with fixed, sanitized messages. Readiness requires storage and the host authorization/audit adapter; missing adapters prune both routes. Disabled configuration returns before provider binding, classification storage registration, hosted initialization, or outbound client setup.

## Verification completed

- `dotnet test tests/NimBus.Extensions.IntegrationIntelligence.Tests/NimBus.Extensions.IntegrationIntelligence.Tests.csproj -c Release --no-restore -p:SkipSpaBuild=true`: 36 passed, 4 skipped (provider integration cases requiring external infrastructure).
- Focused WebApp activation/rate-limit tests: 16 passed.
- `dotnet test src/NimBus.sln -c Release --no-restore -p:SkipSpaBuild=true`: passed; relevant totals included Integration Intelligence 36 passed/4 skipped and WebApp 462 passed.
- `npm test -- --run src/components/event-details/intelligence-card.test.tsx src/components/event-details/message-listing.test.tsx`: 51 passed.
- Full frontend suite with `npm test -- --run --no-file-parallelism`: 51 files and 369 tests passed.
- `npm run build` in `src/NimBus.WebApp/ClientApp`: production build passed.
- `git diff --check`: passed.

No deployment, publish, or live TypeSafe request was performed.
