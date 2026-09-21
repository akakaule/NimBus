# Implementation review: Spec 033 Integration Intelligence (2026-09-20, in-progress tree)

Reviewed the uncommitted working tree on `master` (base 460e6b0) against spec 033 and the implementation plan. Scope: the new extension project, its tests, the WebApp wiring and adapters, the Core redactor, the SPA card, Bicep and docs. Build and test results are at the end.

Verdict: **not mergeable yet.** The skeleton is right (project boundary, controller pruning, host adapters, rate-limit convention branch, deterministic guidance, one-request provider call), but there are three defects that break the feature or its guarantees in production, several spec deviations in the coordination protocol, and most of the test plan is still missing. Findings are ordered by severity, each with the file and line to go to.

## Blocking defects

### 1. Singleton host adapter captures scoped authorization and audit services (authorization bypass)

`Startup.cs:84` registers `WebAppIntegrationIntelligenceHostAdapter` as a singleton. Its constructor takes `IEndpointAuthorizationService` and `IAuditLogService`, both registered **scoped** (`Startup.cs:638`, `:642`). `EndpointAuthorizationService` caches the resolved principal per instance (`_resolved` at `EndpointAuthorizationService.cs:42`, `:86`).

- In Development, scope validation is on: the first request to any intelligence route throws "Cannot consume scoped service from singleton" and returns 500.
- In Production, scope validation is off: the singleton captures the **first** request's authorization service, and its cached `CurrentUserAccess` answers every later user's Reader and Contributor checks. That is a cross-user authorization bypass, and audit rows would carry the wrong actor.

`IntegrationIntelligenceStatusService`, `FailureClassificationService` and `FailureEvidenceBuilder` are also singletons that hold the adapter. Fix: make the adapter and everything that depends on it per-request (scoped), or make the adapter resolve the scoped services through `IHttpContextAccessor.HttpContext.RequestServices` on each call. Add a host test that runs two requests as two different users and asserts the second is authorized on its own roles.

### 2. Reservation protocol is not atomic on Cosmos and has no owner fencing anywhere

Spec §13 and the plan require one owner per active analysis across WebApp instances and that late owners cannot persist.

- `CosmosClassificationStore.ReserveAsync` (`:55-84`) does two queries and then `CreateItemAsync` with a **fresh GUID id**. Two instances racing the first request both create an operation document with the same revision, both become owners, and both call the provider. The `IfNoneMatchEtag = "*"` on a create with a unique id cannot conflict, and the `Conflict` catch is unreachable. The second `CompleteAsync` batch then fails with 409 on `result:{revision}`, and line 103 treats a 409 batch as success, so the second result is silently dropped after being billed.
- Neither store carries an ownership token. `SqlClassificationStore.CompleteAsync` (`:151-157`) inserts the result and updates `WHERE OperationId=@id AND Status='Active'` without checking rows affected. A forced re-analysis creates a new operation row, so the superseded owner's own row is still `Active` and its late completion is persisted. Cosmos `CompleteAsync` upserts the head and replaces the operation without an ETag, so a stale owner can overwrite a newer head.
- The SQL insert into `IntelligenceResults` runs even when the operation update affects zero rows (already failed or superseded).

Fix: per-failure head document or row that is created conditionally on first use and updated with ETag / rowversion on every reservation; reservation and completion carry the allocated revision and an unguessable token and are rejected when the head's token differs. This is what the plan's Task 6 conformance suite was meant to pin down before either store was written.

### 3. Expired reservations are silently re-reservable (automatic replay after unknown outcome)

All three stores treat an `Active` operation whose `ExpiresAtUtc` has passed as free: `SqlClassificationStore.cs:110`, `CosmosClassificationStore.cs:71`, `InMemoryClassificationStore.cs:66`. A non-force POST after a crash, timeout past 60 s, or client disconnect therefore starts a new provider call with no warning. Spec §13 and acceptance criterion 9 say expiry is an ambiguity boundary: it reads as `OutcomeUnknown`, a non-force request surfaces that state without a provider call, and only a force request with a new key may supersede it.

Related: `FailureClassificationService.GetLatestAsync` (`:40`) reports `AnalysisInProgress` for an expired `Active` row forever, and a client-cancelled request (`OperationCanceledException` where `cancellationToken.IsCancellationRequested`) is not caught at `:109`, so the reservation is left `Active` until expiry and then replayed by the next click.

## Defects that break behaviour without breaking guarantees

### 3b. An existing WebApp test is red on this tree

`RateLimitEndpointMetadataTests.No_other_endpoint_carries_any_policy` fails: expected 46 policy-carrying endpoints, found 47. The test builds a minimal host (`RateLimitEndpointMetadataTests.cs:204-231`) that adds the WebApp and Identity assemblies as application parts and never calls `AddNimBusIntegrationIntelligence`. Because the test project is a Web SDK project that references the WebApp, its build emits an application-part attribute for the extension assembly too, so `IntegrationIntelligenceController` is discovered, nothing prunes it, and `RateLimitPoliciesConvention` attaches the new policy. Fix the test host to register the extension (disabled by default, so the controller is pruned) and add the enabled case with explicit expectations for the `Intelligence` policy in both directions, which is what the plan's Task 9 asked for. Any other Web SDK test host that builds its own MVC pipeline has the same exposure.

### 4. Exception-dump filter drops most error texts

`IntelligenceDataRedactor.ExceptionDumpPattern` (`:26-28`) matches `(?:Exception|AggregateException):\s*[^\r\n]+`. That matches any text of the form `SomeException: message`. `ResponseService.cs:67` writes exactly that shape for every failure classified as Discard, and many handler messages embed inner-exception text the same way. `ScrubText` returns null for the whole field, so the provider receives no error message for those failures and classifies on the type name alone. Keep the stack-frame alternative (`^\s*at\s+…`), drop the second alternative, and add a test with `"CustomerNotFoundException: Customer 4711 does not exist"` expecting the text to survive.

### 5. Unknown-outcome recovery in the card is a dead end

`intelligence-card.tsx:125` calls `analyze(Boolean(classification))`. After a 409 `AnalysisOutcomeUnknown` there is no classification, so "Re-analyze" sends `force: false`, the server answers 409 again, and the operator is stuck. Send `force: true` when `unknownOutcome` is set, and show the spec's warning that the previous request may have incurred cost.

### 6. Card hidden from Readers, no existing result loaded, no progress refresh

`intelligence-card.tsx:87` returns null unless `status.canAnalyze`. Spec §16 and criterion 4: the card renders for any Reader when status is `Ready`, shows an existing result on open, and only the Analyze button is gated on `canAnalyze`. The card also never issues the latest-classification GET on load or while an analysis is in progress, so cached results are invisible until someone clicks, and `AnalysisInProgress` is shown as a raw error string. `contractVersion` is returned by the server but never checked. The disclaimer, the "Analyzed with … revision … by …" line and the details disclosure from §16 are absent.

### 7. Any store exception becomes a 500

`IntegrationIntelligenceController.ExecuteAsync` (`:60-71`) catches only `ClassificationServiceException`. A missing Cosmos container or SQL table, a `SqlException` deadlock victim in the serializable reservation, or a Cosmos throttle surfaces as an unhandled 500. Spec §14 and the plan: store unavailability is a 503 with a stable code and no provider call.

### 8. Silent fallback to the in-memory store in the WebApp

`IntegrationIntelligenceRegistration.cs:81-96` returns `InMemoryClassificationStore` whenever the storage settings do not resolve (Cosmos client or database name missing). In a multi-instance WebApp that silently makes idempotency and caching per-process and loses results on restart. The WebApp should end in `ProviderNotConfigured` instead; the in-memory fallback belongs only to hosts that opt in explicitly.

### 9. Retention failures fail the admin operation after the core delete succeeded

`AdminService.Purge.cs:255`, `:266-268`, `:596-597` and the loops in `DeleteMessagesByToAsync`, `DeleteByStatusAsync` and `DeleteDeadLetteredAsync` await the retention hook with no try/catch. If the classification store is unavailable, the admin call fails or aborts the bulk loop even though the message rows are already gone. Wrap each call, log, and continue. Coverage of the deletion paths is otherwise complete: six paths call the hook, and `PurgeSubscriptionAsync` only touches Service Bus, so it needs none.

## Spec deviations to fix or to record as spec amendments

- **Outbound state shape and data minimisation.** `TypeSafeFailureIntelligenceProvider.cs:57-71` sends `MessageId`, `EventId`, `SessionId` and each history row's `EnqueuedTimeUtc`, with PascalCase keys and no `failure` / `recentHistory` / `eventPayload` grouping. Spec §8 defines the state without identifiers and with descriptive top-level keys. The plan's Task 4 exit criterion is an assertion on the exact outbound JSON; there is none.
- **Status endpoint skips the Reader check.** `IntegrationIntelligenceStatusService.GetAsync` verifies endpoint existence and Contributor for `canAnalyze` but never requires Reader. Spec §14: 403 without Reader. Any authenticated user can currently probe endpoint ids.
- **Ineligible status returns 404, not 409.** `FailureEvidenceBuilder.BuildAsync` returns null for a non-error row or a non-Failed/DeadLettered event, and the service maps null to `FailureNotFound` 404 without an audit row. Spec §6 and §14: 409 with a stable code, and rejected requests are audited.
- **Evidence is built before authorization.** `LoadAuthorizedInputAsync` builds history and the redacted payload first and authorizes second. Nothing leaks, but the plan's sequence is load coordinates, authorize, then build; move the eligibility and evidence steps after the role check.
- **Provider 5xx and exhausted 529 map to 409 `AnalysisOutcomeUnknown`.** Spec §18 maps timeout, 429/529, 5xx and 401/403 to 503 and the unavailable message; only true ambiguity (request sent, no answer) should require a forced re-analysis.
- **`IncludeRecentFailureHistory` is bound and ignored.** `FailureClassificationDataOptions.IncludeRecentFailureHistory` never reaches `FailureEvidenceBuilder`; history is always sent. Spec §8 step 3: send an empty list when disabled.
- **History `Attempt` renumbers after the limit.** `FailureEvidenceBuilder.cs:82-83` applies `TakeLast` before numbering. Spec: ordinal among all filtered earlier failures.
- **Cosmos container gets a 90-day TTL.** `cosmosDB.bicep` sets `defaultTtl: 7776000`. Criterion 9 says successful history is immutable and retained, the retention decision was purge-with-event, SQL has no equivalent expiry, and `docs/integration-intelligence.md` does not mention it. Remove the TTL or record the decision in the spec and doc.
- **SQL schema differs from §13.** Tables are `IntelligenceOperations` and `IntelligenceResults` in the message-store schema, created by an inline `CREATE TABLE` on startup, not `dbo.FailureClassifications` with a `dbo.IntelligenceSchemaVersions` DbUp journal. Using the store's schema is reasonable; amend §13 and keep a versioned journal so a second script can ever ship.
- **Undocumented second configuration shape.** `IntegrationIntelligenceRegistration.cs:37` falls back to a root-level `FailureClassification` section when `NimBus:IntegrationIntelligence` is absent. Nothing documents it and `Validate()` then rejects it because `IntelligenceEnabled` stays false. Remove the fallback.
- **Card placement and target.** The card is rendered at page level from `cosmosEvent.lastMessageId` (`event-details.tsx:324-331`), not beside the error row in `message-listing.tsx`. For a Failed or DeadLettered event the last message is normally the error row, so this works for MVP; note it as a deliberate simplification.

## Gaps against the plan's test requirements

What exists: 10 extension tests (guidance order, in-memory cache and duplicate key, secret scrubbing, question-set ids, provider parse and 429 retry), 2 Core redactor tests, 2 SPA tests. What the plan requires and is absent, in the order it matters:

1. Architecture test for §19 (no reference to Manager, SDK, ServiceBus, Resolver; no pipeline or classifier implementation; only WebApp references the extension). Nothing pins the invariant.
2. Host activation tests: disabled routes 404 under the four auth branches, `ProviderNotConfigured` exposes status only, invalid options never stop the host. There are no changes under `tests/NimBus.WebApp.Tests` at all.
3. Store conformance suite and real SQL and Cosmos runs. Defects 2 and 3 would have failed it.
4. Two-host race and lost-acknowledgment tests.
5. API and endpoint-capability tests (401, 403 Reader on POST, forged endpoint, cached second call, force revision 2, byte-identical message row, audit `Data`).
6. Evidence-builder tests asserting the exact outbound JSON, endpoint A/B isolation, session filtering, equal timestamps, and a dead-letter description built through `SendDeadLetterResponse` with a sentinel secret.
7. Rate-limit metadata coverage. `RateLimitEndpointMetadataTests` builds the live host with the feature disabled, so the pruned controller carries no policy and the new `Intelligence` policy is never asserted in either direction.
8. SPA tests for Reader visibility, 403, status not `Ready`, `contractVersion` mismatch, endpoint navigation in flight, stable request id on retry, GET-only progress refresh, unknown-outcome recovery.

## Smaller items

- `Microsoft.Extensions.Http.Resilience` is referenced by both new projects and unused; the provider implements its own bounded retry, which is the right call. Drop the reference.
- `Startup.cs` `AddObservability`: the `metrics.AddAspNetCoreInstrumentation()` line lost its indentation in the diff.
- `WebAppIntegrationIntelligenceStorageSettings.CosmosDatabaseName` hardcodes `"MessageDatabase"`. It matches `CosmosDbClient.DatabaseId` today, which is an internal constant, so this is correct but will drift silently; expose the constant or an option.
- `FailAsync` in the Cosmos store runs a cross-partition query to find the partition; the service already holds `reservation.FailureMessageId`. Pass it.
- No ADR-016 and no spec amendments yet (§14 degraded-state routes, §5 in-memory location, §13 SQL schema, §24 allow-list and retention decisions), which the plan's Task 1 gates on.

## Verified as correct

- Project boundary: the extension references only `NimBus.Core` and `NimBus.MessageStore.Abstractions`; only `NimBus.WebApp` references it among `src/` projects.
- Controller pruning by feature provider for disabled and `ProviderNotConfigured`, attached via `AddControllers().ConfigureApplicationPartManager` after `AddStorage`, matching the plan.
- Rate limiting attached through `RateLimitPoliciesConvention.PolicyFor` on the POST only, policy registered in `AddNimBusRateLimiting`, no attribute in the extension. Kill switch preserved.
- Storage connection taken from the registered `CosmosClient` singleton and `SqlServerMessageStoreOptions`, no configuration-key duplication.
- Deterministic guidance order and boundary values match §12; tests cover 0.59/0.60 and 0.74/0.75.
- One provider request with all four questions; only 429 and 529 retried, at most two retries, inside the total `TimeoutSeconds` budget; response `model` persisted; distribution validated.
- `IEventJsonRedactor.Redact` redacts all three annotation modes to `***`, keeps fail-closed markers, and leaves `Mask` and the `IEventJsonMasker` interface untouched. `NullEventJsonRedactor` yields the unknown-type marker, which the builder reads as "omit payload".
- Secret scrubbing runs before truncation. Stack traces and the raw dead-letter description are never copied into the input.
- `FailureClassified` appended last to `MessageAuditType` and to all three `api-spec.yaml` enum lists; generated contract and client regenerated.
- Telemetry names and tags match §18; no ids, payload or error text in tags.
- MVC serializes the extension's responses camelCase with string enums, so the SPA's `result`, `cached`, `canAnalyze` and `guidance` reads are correct.
- Committed configuration leaves the feature off; Bicep container is behind a parameter that defaults to false.

## Build and test evidence

| Check | Result |
|---|---|
| `dotnet build tests/NimBus.Extensions.IntegrationIntelligence.Tests -c Release -p:SkipSpaBuild=true` | Succeeded, 0 warnings |
| `dotnet test` extension project, Release | 10 passed |
| `dotnet test` Core `EventJsonMasker` filter, Release | 35 passed |
| `dotnet test` WebApp `RateLimit`, `AuditTypeContract`, `AnonymousEndpoints` filters, Release | 42 passed, **1 failed**: `No_other_endpoint_carries_any_policy` (finding 3b) |
| `npx vitest run intelligence-card.test.tsx` | 2 passed |
