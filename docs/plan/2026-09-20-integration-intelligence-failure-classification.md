# Spec 033 implementation plan: Integration Intelligence failure classification

Date: 2026-09-20. Status: in progress.

Implementation-review remediation: [remediation plan](2026-09-20-integration-intelligence-remediation.md).
The corrected storage protocol uses one atomic per-failure aggregate (SQL rowversion / Cosmos
ETag), superseding the multi-table/document layout below; completed logical revisions remain
immutable. Retention uses durable-coordinate reconciliation and tombstone fencing every 60 seconds,
not synchronous purge hooks. Spec §13 and §24.6 document the capacity and retention decisions.

Review disposition: the six main findings in [the plan review](2026-09-20-integration-intelligence-failure-classification-review.md) are incorporated below. Retention also covers single-event, endpoint-wide and bulk deletion; the in-memory store remains test-only. Authentication coverage names LocalDev, Entra-only, Identity-only and dual explicitly.

Source of truth: [spec 033](../spec/033-integration-intelligence-failure-classification/spec.md), including the 2026-09-20 review corrections and packaging pivot in §23.1.

## Outcome and scope

Ship an optional, MIT-licensed `Akaule.NimBus.Extensions.IntegrationIntelligence` package in this repository. The stock WebApp references it, but the feature remains disabled by default. A Contributor can request advisory classification of a specific Failed/DeadLettered occurrence; Readers can view results. The operator supplies a TypeSafe key. No provider response changes message processing or recovery.

Implement both SQL Server and Cosmos stores, full-redaction support, endpoint authorization, durable request coordination, API, SPA, telemetry and deployment documentation. Do not implement licensing, a private repository/feed, reflection-based extension loading, `IWebAppExtension`, deploy CLI flags, automatic classification, or new recovery actions. These were removed or excluded by the current spec.

Implementation is proceeding locally. Deployment, provider calls, package publication, pushes and PR creation remain separate actions.

## Repository facts and implementation boundaries

| Existing location | Consequence for this work |
|---|---|
| `src/NimBus.WebApp/Startup.cs` | Insert activation after `AddStorage`, using its resolved provider; preserve existing authentication, authorization and controller registration order. |
| `src/NimBus.WebApp/IdentityControllersDisabledFeatureProvider.cs` | Disabled-controller precedent. Its registration is conditional on Identity being absent; register the new feature's suppression independently of that condition. |
| `src/NimBus.WebApp/Services/IEndpointAuthorizationService.cs` and `IAuditLogService.cs` | These contracts belong to the host. Extension controllers cannot reference them directly without a WebApp/extension cycle. Use host adapters described below. |
| `src/NimBus.Core/Messages/PII/EventJsonMasker.cs` | Existing `Mask` respects annotation modes. Add the separate full-redaction capability without changing this behavior. |
| `src/NimBus.WebApp/api-gen.nswag` | Generates `Controllers/ApiContract.g.cs` and `ClientApp/src/api-client/index.ts`. Regenerate audit enums; keep classification routes outside this OpenAPI document. |
| `src/NimBus.WebApp/RateLimiting/` | Policies attach only through `RateLimitPoliciesConvention.PolicyFor`, which returns early when `RateLimitOptions.Enabled` is false (the operator kill switch). An `[EnableRateLimiting]` attribute on an extension controller would bypass that switch, so the extension carries no rate-limit attribute; the WebApp adds a `PolicyFor` branch for the extension controller type and a policy name constant. |
| `src/NimBus.MessageStore.CosmosDb/CosmosDbMessageStoreBuilderExtensions.cs`, `src/NimBus.MessageStore.SqlServer/SqlServerMessageStoreBuilderExtensions.cs` | The Cosmos store registers `CosmosClient` as a DI singleton; `CosmosDbMessageStoreOptions` (database name) and `SqlServerMessageStoreOptions` (connection string, schema) hold the resolved connection settings. The extension never reads connection keys from `IConfiguration`; the WebApp adapter hands these over (see host contracts below). |
| `src/NimBus.WebApp/Services/AdminService.Purge.cs` | Seven deletion paths exist: `PurgeSessionAsync`, `PurgeSubscriptionAsync`, `DeleteEventAsync`, `DeleteAllEventsAsync`, `DeleteMessagesByToAsync`, `DeleteByStatusAsync` and `DeleteDeadLetteredAsync`. Retention integration is costed against all seven. |
| `deploy/bicep/templates/cosmosDB.bicep` | Already documents that Entra data-plane identities cannot create containers. Provision the new container conditionally through deployment. |
| `.github/workflows/dotnet.yml` | Already provides SQL and Cosmos emulator services. Extend these checks rather than inventing a second CI pipeline. |
| `.github/workflows/nuget-publish.yml` | Packs the solution and publishes the WebApp payload already. A packable solution project should join that flow without a new deployment command. |
| `src/NimBus.WebApp/ClientApp/package.json` | Follow actual installed React/router/Vite versions and scripts; older overview documents are not a dependency-upgrade requirement. |

Only WebApp may reference the new extension among production `src/` projects. The extension's only NimBus project references are Core and MessageStore.Abstractions. Tests may reference the extension and host; architecture tests must distinguish production references from test references.

## Decisions to record before their dependent implementation

Task 1 resolves these in the spec or an accompanying ADR before dependent code is written. The proposals below are implementation recommendations, not assertions that the currently open spec decisions are already approved.

| Decision | Proposed resolution and gate |
|---|---|
| Host authorization/audit dependency | Define narrow interfaces in the extension, implemented by WebApp adapters over its existing authorization/audit services: endpoint existence, Reader/Contributor checks, current actor (`HttpContext`-free; the adapter reads `IHttpContextAccessor` because `IAuditLogService.LogAuditAsync` resolves the actor from `HttpContext`) and best-effort audit. No duplicate ACL implementation, new shared host package, or reference back to WebApp. Resolve before controllers/activation. |
| In-memory classification store location | Spec §5 lists it as extension content; this plan builds it in the test project (Task 6). Keep it in the test project for MVP and amend §5; move it into the extension only if a custom host needs to run the conformance suite. |
| Host storage configuration | Reuse the DI `CosmosClient`; WebApp supplies resolved SQL connection settings from store options and the Cosmos store's fixed `MessageDatabase` name through extension-owned contracts. No duplicated connection-key or credential precedence. Retain adapter wiring tests. |
| Degraded-state routes | Resolved: `ProviderNotConfigured` exposes status only; the feature provider removes classification and history controllers, so their routes return 404 and the SPA rule stays "404 means hide". Amend the spec §14 table to say so. |
| Package/provider scope | One extension package with both providers, matching §5; no provider satellite projects for MVP. Both real-provider conformance runs are release requirements. |
| Cosmos provisioning | Add a deployment parameter defaulting to false and conditionally create `failureclassifications` with `/failureMessageId`. Runtime uses data-plane access to the existing container. Missing schema fails classification requests safely, not host startup. |
| Module disabled while parent enabled | Define `FailureClassification.Enabled = false` as absent classification/status routes for this single-module MVP; bind provider options only when both switches enable the feature. Record this missing activation branch. |
| Optional endpoint allow-list (§24.4) | Recommended: include `AllowedEndpoints`, empty meaning all, using exact case-insensitive endpoint IDs. It narrows status capability and POST authorization, including forced requests. Explicitly decide whether historical GET remains available; proposed behavior preserves authorized history reads. Record config/status/API tests before implementation. |
| Retention (§24.6) | Decide before schema/API completion. Inventory `AdminService.Purge.cs`: `PurgeSessionAsync`, `PurgeSubscriptionAsync`, `DeleteEventAsync`, `DeleteAllEventsAsync`, `DeleteMessagesByToAsync`, `DeleteByStatusAsync` and `DeleteDeadLetteredAsync`. Determine which stored evidence each removes. Recommended purge-with-event requires cleanup for every applicable path, durable retry after partial failure and fencing concurrent completion. A TTL alone is insufficient; do not add core-store dependencies on the extension. If retained instead, state retention and deletion procedures explicitly. |
| Durable protocol details | Pin SQL/Cosmos identity casing, conflict codes, operation read-back, idempotency-key retention, duplicate requests with different `force`, and allocation after failures. Define durable treatment of requests returning busy/cached so retrying the same key never unexpectedly starts new work. |
| Invariant ADR | Add ADR-016 if the number is still free, cross-link ADR-015. Record default-off, advisory-only, bring-your-own key, narrow host adapters and separate storage ownership. |

No uncertainty in this table requires stopping unrelated work such as redaction tests. Resolve a decision before implementing the behavior it controls. If a decision changes scope or the dependency graph, amend the plan instead of pushing an incompatible design through.

## Delivery sequence

Use the sequence below; tasks are not completed by writing code alone. Each task starts with a failing behavioral/contract test, then the smallest implementation, then refactoring with the tests green. Do not introduce broad platform refactoring to support this feature.

### Task 1 — Freeze contracts and establish the project boundary

**Files:** new `src/NimBus.Extensions.IntegrationIntelligence/`, new `tests/NimBus.Extensions.IntegrationIntelligence.Tests/`, `src/NimBus.sln`, relevant project/package metadata; spec/ADR clarifications from the decision table.

- Add a packable `net10.0` extension with package ID, MIT metadata, public XML documentation and the references permitted in §5. Follow actual repository package-version conventions. Add an MSTest project to the solution.
- Add architecture guards first: allowed forward references, WebApp-only reverse production reference, no pipeline/lifecycle/classifier implementation, no recovery-capable interfaces. Verify the tests detect an intentionally introduced forbidden reference, then remove the fixture violation.
- Define result/provider/input/guidance and store-operation contracts from §§8–13, including explicit JSON enum/property representations and `contractVersion = 1`. Define stable error codes beyond the two named operation errors; do not leave frontend code to parse message text.
- Define extension-owned host contracts for endpoint existence, Reader/Contributor checks, current actor and best-effort classification auditing. Keep these contracts `HttpContext`-free: the WebApp adapter obtains the current context through `IHttpContextAccessor` and delegates to the existing services. Keep audit DTOs limited to §13 data; no payload or credential fields. Custom hosts must supply adapters; missing adapters must deny access or disable activation, never allow calls.
- Add a separate host storage contract for the resolved SQL connection settings and Cosmos database name. The WebApp adapter reads registered provider options; the Cosmos implementation consumes the existing DI `CosmosClient` without owning/disposal of that singleton. Resolve only the selected backend and only when execution is enabled; do not expose storage settings through API or audit DTOs.
- Freeze idempotency, operation-state and retention decisions before storage implementation. Separate stored domain records from outbound HTTP state so identity and bookkeeping fields are not serialized unintentionally.

**Exit:** extension and tests build in Release; dependency guards pass; unresolved choices have named gates rather than implicit defaults.

### Task 2 — Add reusable full-redaction support and audit enum

**Files:** `src/NimBus.Core/Messages/PII/IEventJsonRedactor.cs` (new), `EventJsonMasker.cs`, Core PII tests, existing `MessageAuditType` declaration, `src/NimBus.WebApp/api-spec.yaml`, generated clients, `tests/NimBus.WebApp.Tests/AuditTypeContractTests.cs`, `Startup.cs`.

- First add tests for unconditional redaction of Redact/PartialReveal/Hash annotations, nested objects/collections, unknown types, invalid JSON and preservation of existing `Mask` behavior.
- Reuse traversal in `EventJsonMasker` through an explicit full-redaction path. Do not change `IEventJsonMasker` or require external implementers to add a member. Preserve fail-closed markers and sensitive-value collection.
- Register the new capability without accidentally replacing a custom existing masker or assuming every `IEventJsonMasker` implements it. Cover unavailable capability and custom registrations.
- Append `FailureClassified` after all current numeric enum values. Update all three OpenAPI enum lists, run the existing NSwag build target, and inspect the generated diff for unrelated churn.

**Exit:** Core PII tests and WebApp audit round-trip tests pass; existing enum numbers and masking contracts are unchanged.

### Task 3 — Implement activation, contained validation and host adapters

**Files:** extension options/registration/controller feature provider; WebApp project reference, `Startup.cs`, new `Services/IntegrationIntelligence/` adapters; extension and WebApp integration tests.

- Test three states through actual MVC discovery under all four auth branches (LocalDev, Entra-only, Identity-only and dual): disabled routes all 404; invalid configuration exposes status with `ProviderNotConfigured` and classification/history routes return 404; valid configuration exposes the feature with `Ready`. Use authenticated requests where required so authentication challenges do not obscure route-discovery assertions.
- Read activation switches before binding provider options. Bind/validate an immutable snapshot inside a contained boundary; no extension `ValidateOnStart`. Check malformed flags too, numeric bounds, thresholds, provider identity, HTTPS base URL and credentials without logging supplied values.
- Ensure invalid configuration starts the actual WebApp and leaves an existing authenticated management request working. Disabled/invalid modes must create no store schema or provider client and make no external call.
- Register host adapters over `IEndpointAuthorizationService`, `IAuditLogService` and resolved storage options only when needed. Preserve request-scoped principal behavior and endpoint role semantics. Test the selected database/settings and shared Cosmos client identity; adapter reuse reduces, rather than eliminates, wiring verification. Do not build a temporary service provider during registration.
- Separate status controller dependencies from execution dependencies; invalid configurations must not trigger controller activation errors due to missing execution services. Remove unused controllers with the feature provider.
- Wire registration after `AddStorage`. MVC is already registered by the authentication stack; call `services.AddControllers().ConfigureApplicationPartManager(...)` again to attach the feature provider to the shared manager, independently of the auth ladder. Verify discovered application parts and actual route pruning; absence of execution-service registration alone does not remove controllers. Schema initialization must not occur synchronously in `ConfigureServices` or bring down the host on extension storage failure.

**Exit:** real-host startup/route tests pass for all activation states; disabled service descriptors differ only by required MVC suppression plumbing.

### Task 4 — Build and redact occurrence-specific evidence

**Files:** extension `Evidence/` input builder and redactor; corresponding extension tests using existing tracking-store fakes where practical.

- First test loading by event/message ID, deriving the stored endpoint, current status on that endpoint, all rejected statuses and a dead-letter failure without `ErrorContent`.
- Apply endpoint/session filtering and strictly earlier occurrence timestamps before taking the history limit. Pin equal-timestamp exclusion, stable ordering, failure-only rows, target exclusion and attempt ordinals.
- Scrub sensitive values from each row's free text using its own source payload even when payload export is off. Unknown/missing payload withholds those fields. Never substitute a newer resubmission's payload for the target occurrence.
- Use `IEventJsonRedactor` for opted-in payloads; omit on missing capability, unknown type or invalid JSON. Exclude raw dead-letter descriptions and stack traces, including embedded exception dumps, before serialization.
- Apply recursive key/free-text secret scrubbing before truncation. Bound regex work and JSON traversal. After scrubbing, enforce the actual serialized state limit, dropping history before payload; define a safe failure if required fields alone exceed the budget.
- Assert the final captured HTTP JSON, not just intermediate objects. Include hostile field text, nested payloads and an exception created through the real dead-letter response path.

**Exit:** outbound-evidence tests demonstrate endpoint isolation, occurrence fidelity, full redaction and the hard size cap without modifying source message rows.

### Task 5 — Implement question set, TypeSafe client and guidance

**Files:** extension `Providers/TypeSafe/`, question-set data, guidance rules, named HTTP client configuration; mocked HTTP tests.

- Pin question set v1, all category IDs/descriptions and three Noul questions as test data. Wording changes require a question-set version change.
- Verify the official provider schema against §10 during implementation and maintain sanitized request/response fixtures; never require a real TypeSafe key in tests.
- Send one state object and four questions in one request. Persist the returned model, distribution and usage, not the configured alias. Validate all answer IDs/types, known categories, finite probabilities within 0–1, distribution completeness/sum and nonnegative usage.
- Configure only 429 and 529 retries, at most two, inside the total 1–20 second budget. Interpret “4xx never retried” as excluding the explicitly allowed 429. Inspect/remove inherited default HTTP resilience retries for this named client so timeout/network/5xx do not replay a possibly billed request.
- Return typed definite-failure versus unknown-outcome information to orchestration. Cancel promptly; redact authorization headers and bodies from diagnostics.
- Implement the guidance branch order exactly as §12, including boundary values and simultaneous high retry/change signals. Output only `FailureGuidance`.

**Exit:** mock HTTP tests prove exact call/retry counts, total deadline, response validation and deterministic guidance. No default test contacts TypeSafe.

### Task 6 — Implement the durable operation state machine and shared conformance suite

**Files:** extension persistence contracts/state records; in-memory implementation in the test project; reusable conformance tests in that test project.

- Write the conformance suite before provider implementations. Use an injectable clock and barriers for races rather than timing sleeps.
- Cover cache lookup and reservation as one atomic decision; first-use creation; force; repeated request keys; changed force semantics; 60-second expiry; previous definite failure; unknown outcomes; unique monotonic revisions with gaps.
- Require completion to create an immutable result and mark its operation complete atomically. Repeated completion of the same record is idempotent; changed content for that reservation is rejected. Old owners cannot complete/fail a successor or downgrade completion.
- Define read-back after an uncertain completion write, bounded bookkeeping after HTTP cancellation, and explicit new-key force recovery. GET and expiry must never create work. Keep request outcome records long enough to preserve the agreed idempotency guarantee.
- Run every conformance case against an in-memory implementation, then reuse exactly those cases for SQL/Cosmos. Do not place extension-specific contracts in `NimBus.Testing` and violate the reverse dependency constraint.

**Exit:** executable state-transition contract, including failure/cancellation paths, ready to run against both databases.

### Task 7 — Implement SQL storage and enabled-only migrations

**Files:** extension `Storage/SqlServer/`, embedded `Schema/` scripts; SQL conformance tests; existing CI only if required for discovery/configuration.

- Create the atomic per-failure aggregate table in spec §13; use `dbo.IntelligenceSchemaVersions`. Do not edit the message store's migration sequence.
- Obtain SQL connection settings through the host adapter over `SqlServerMessageStoreOptions`. Do not read or copy configuration-key precedence in the extension. Open extension-owned SQL connections with those resolved settings; keep the classification schema/journal independently owned. Test adapter wiring to the selected database. No connection string in logs.
- Implement conditional insertion and rowversion updates with safe first-row contention and monotonic allocation. No transaction or database lock stays open during provider HTTP calls. Serialize migration initialization with a database application lock.
- Run the shared suite with two independent connections, fault injection and an isolated test database. Test migrations twice and simultaneous initialization. Disabled/invalid activation must create zero extension tables.
- Apply the chosen retention policy here, including operations/head records and completion fencing if purge-with-event is selected.

**Exit:** real SQL conformance, migration and cross-client races pass; core schemas and stored messages remain unchanged by classification.

### Task 8 — Implement Cosmos storage and optional provisioning

**Files:** extension `Storage/Cosmos/`, Cosmos conformance tests; `deploy/bicep/templates/cosmosDB.bicep` and caller parameter plumbing.

- Reuse the existing DI `CosmosClient` and obtain the message store's database name from the host adapter. The current store fixes this to `MessageDatabase`; `CosmosDbMessageStoreOptions` only carries retention settings, not a database option. This inherits configured credentials, managed identity, gateway mode and custom-client behavior. Do not create/dispose a competing client or reimplement connection-key precedence.
- Store one fixed-identity aggregate under `/failureMessageId`. Use ETag conditions and conditional creates for the shared protocol; preserve completed logical revisions immutably.
- Test independent clients, missing-head races, conflicts, response loss, expiry and stale owners with the real emulator. Verify operation read-back across clients under the configured consistency policy.
- Provision the container through an opt-in Bicep parameter that defaults off. Do not call runtime container creation under Entra RBAC. Missing container returns safe feature unavailability; it must not abort the entire host.
- Implement the selected retention policy without treating the partition key as an endpoint key; account for endpoint/event/session lookup and deletion across failure partitions.

**Exit:** real Cosmos conformance passes; Bicep compiles; default deployment contains no classification container. Emulator skips do not count as success.

### Task 9 — Wire orchestration, HTTP, rate limiting, audit and telemetry

**Files:** extension application service/controllers/telemetry; WebApp adapters, rate-limit policy wiring and observability registration; extension tests plus `tests/NimBus.WebApp.Tests/` host tests.

- Implement the exact authenticated load/authorize/status/eligibility/reservation/evidence/provider/completion sequence. Authorize every cached result and history read against stored coordinates. Check any accepted allow-list before outbound work.
- Validate UUID idempotency keys and return the agreed machine-readable status/error contract. GET progress must surface latest active/unknown/failed operations instead of presenting an older revision as new success; history remains available to authorized Readers.
- Add `RateLimitPolicyNames.Intelligence` and register it through `AddNimBusRateLimiting`. Attach it only through a controller/action branch in `RateLimitPoliciesConvention.PolicyFor`; WebApp can reference the extension controller type directly. Do not put an `EnableRateLimiting` attribute on the extension controller/action: that would bypass the convention's `Enabled` kill switch. Extend `RateLimitEndpointMetadataTests` and enforcement tests for enabled/disabled limiting, POST-only attachment and untouched GET routes. Document that per-instance limiting is not a global provider budget.
- Audit every POST outcome once, including denial, cached, busy, unknown and provider/store error paths. Coordinate pre-controller authorization/rate-limit rejection auditing without double-writing. Best-effort audit failures must not cause provider replay.
- Register `NimBus.Intelligence` with the host's telemetry pipeline and test exported instruments/tags using listeners. Exclude payload, error text, credentials and high-cardinality message/session/event identifiers.
- Start two independent hosts sharing each real store with a counting fake provider. Race non-force, duplicate-key and forced calls; simulate crash, lost commit acknowledgment and late completion. Prove one active owner, no automatic unknown replay, immutable results and byte-identical core messages.

**Exit:** host API/security tests and two-host SQL/Cosmos tests pass; ordinary recovery APIs behave as before.

### Task 10 — Add the advisory SPA card

**Files:** new `ClientApp/src/components/event-details/intelligence-card.tsx`, a small typed client and tests; existing `message-listing.tsx`/tests and event-details page only as needed to pass endpoint/event/message identity.

- Add failing component tests for hidden disabled/unconfigured/forbidden/version-mismatch states, Reader/Contributor differences and scoped status calls.
- Render for eligible failure occurrences, including dead-letter rows without `errorContent`. Ensure the target is a failure message ID, not the originating event-request ID.
- Implement Analyze, progress, result, details, Re-analyze, provider failure and unknown-cost recovery states; always show the advisory disclaimer when the card is shown. Do not add recovery controls to the card.
- Generate one UUID per deliberate action; reuse it on transport retries. Poll only GET with bounded backoff while active. Cancel/ignore every stale status, result and polling completion on endpoint/message changes and unmount.
- Preserve authorized historical revisions during a re-analysis as clearly historical content if displayed; never label them as the current operation's result. Unknown outcome requires explicit Re-analyze and a new key.
- Verify keyboard interaction, accessible progress/error announcements, desktop/mobile layout and existing recovery-control behavior with mocked provider results. Capture representative screenshots for any eventual WebApp PR.

**Exit:** targeted and full frontend checks pass; disabled feature adds no visible UI and performs no POST; endpoint navigation cannot leak stale capability or results.

### Task 11 — Document, package and run the integrated release gate

**Files:** new `docs/integration-intelligence.md`; `docs/extensions.md`, `docs/features.md`, `CLAUDE.md`; ADR/index; any necessary solution/release metadata and deployment parameter docs.

- Document activation/restart, own API key, paid third-party evidence transfer, exact exported fields, redaction limits, roles, optional allow-list decision, model pinning, migrations, Cosmos provisioning, retention and unknown-outcome recovery.
- While updating `CLAUDE.md`, align its stale frontend overview with `ClientApp/package.json` (currently React 19, React Router 7 and Vite 8); this is documentation correction, not a dependency upgrade.
- Keep all committed settings, sample and AppHost defaults off; no sample provider key or live AI smoke call. Document disabling as route/service deactivation, not deletion of stored classifications.
- Confirm normal solution pack emits the extension package and normal WebApp publish includes its DLL/dependencies. A minimal custom host must be able to consume the package with adapters; Resolver publish must not acquire a reference to it.
- Check package metadata/dependency boundaries and that disabled publish startup creates no schema or outbound traffic. Existing release workflow should need no separate provider credentials.
- Run the final checks below and attach actual results. Do not publish or deploy as part of verification.

**Exit:** every §21 criterion is evidenced and unresolved release decisions are closed. Shipping the default-off skeleton alone is not completion of this plan.

## Dependencies and reviewable increments

Tasks 1 → 2/3 → 4/5/6 → 7/8 → 9 → 10 → 11 describe logical dependencies, not permission to dispatch agents. Evidence/provider work and persistence can be developed independently once contracts are fixed; API integration needs both stores. Use ordinary local execution unless delegation is explicitly requested.

Suggested local commit groups use Conventional Commits: platform redaction/audit; extension contracts/activation; evidence/provider; durable SQL storage; Cosmos/provisioning; HTTP/host integration; SPA; docs/release verification. Keep the feature default-off in every intermediate build. Push/PR creation remains a separate user action.

## Verification and evidence

Run from canonical `C:\Git\NimBus` casing on Windows to avoid duplicate analyzer/source paths. Use .NET 10 and the repository's CI Node 22 version. Respect Release `TreatWarningsAsErrors`, StyleCop and all global analyzers; do not suppress new diagnostics broadly. MSTest files use `#pragma warning disable CA1707, CA2007`, `[TestMethod]` plus `[DataRow]` for parameterization, and XML docs on public API.

During tasks, run only the affected suites, watching each new regression test fail before implementing its behavior. Example commands after creating the new test project:

```powershell
dotnet test tests/NimBus.Core.Tests/NimBus.Core.Tests.csproj -c Release --filter FullyQualifiedName~EventJsonMasker
dotnet test tests/NimBus.WebApp.Tests/NimBus.WebApp.Tests.csproj -c Release -p:SkipSpaBuild=true
dotnet test tests/NimBus.Extensions.IntegrationIntelligence.Tests/NimBus.Extensions.IntegrationIntelligence.Tests.csproj -c Release -p:SkipSpaBuild=true
```

If the new extension test project references WebApp, mirror the current `NimBus.WebApp.Tests.csproj` host-test setup explicitly: `Sdk="Microsoft.NET.Sdk.Web"`, `SkipSpaBuild=true`, `StaticWebAssetsEnabled=false`, and a project reference to `NimBus.Extensions.Identity`, plus the normal test SDK/framework references. `SkipSpaBuild` is a targeted-loop convenience, not a substitute for regenerating API contracts or building the SPA.

Final backend gate, after supplying isolated SQL and Cosmos test environments:

```powershell
dotnet restore src/NimBus.sln
dotnet build src/NimBus.sln -c Release --no-restore
dotnet test src/NimBus.sln -c Release --no-build
$intelligenceArtifacts = Join-Path ([IO.Path]::GetTempPath()) ("nimbus-intelligence-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $intelligenceArtifacts | Out-Null
dotnet pack src/NimBus.Extensions.IntegrationIntelligence/NimBus.Extensions.IntegrationIntelligence.csproj -c Release --no-build -o (Join-Path $intelligenceArtifacts 'pack')
dotnet publish src/NimBus.WebApp/NimBus.WebApp.csproj -c Release --no-build -o (Join-Path $intelligenceArtifacts 'webapp')
git diff --check
git status --short
```

Publish/pack outputs go to the unique system temporary directory, outside the worktree. Record its path with verification evidence; inspect the artifacts there. `git diff --check` checks tracked diff whitespace, not untracked files, so compare `git status --short` with the starting worktree state too. Do not delete unrelated pre-existing files during cleanup.

Use `NIMBUS_SQL_TEST_CONNECTION` and the existing `NIMBUS_COSMOS_TEST_CONNECTION` or endpoint/key alternatives. Reuse `NIMBUS_COSMOS_TEST_GATEWAY` and `NIMBUS_COSMOS_TEST_REQUIRED=1` where appropriate. Set these through the test environment, never committed credentials. Inspect executed test counts; skipped SQL/Cosmos conformance or two-host tests block release. Keep test databases/containers isolated and delete only test-owned resources.

Frontend gate, working directory `C:\Git\NimBus\src\NimBus.WebApp\ClientApp`:

```powershell
npm ci
npm run lint
npm test -- --run
npm run build
```

Use the existing jsdom/Vitest configuration and npm commands; do not prescribe a local-storage workaround without reproducing a current failure. No jest-dom matchers unless already configured. Run Bicep compilation for changed deployment entry points using the installed repository tooling, and inspect default-off resource conditions without deploying.

| Spec acceptance criteria | Required evidence |
|---|---|
| 1–3: optional activation and package boundary | Actual host startup/404 tests, service/schema/network counters, architecture tests, package/publish inspection |
| 4: endpoint authorization | A-only Contributor/B-only Reader, forged endpoint coordinate and cached/history tests; UI navigation tests |
| 5–6: provider request and stored result | Captured HTTP fixtures, validation tests, question-set snapshot and persisted round-trip |
| 7 and 9: caching, revisions and recovery | Shared conformance plus two-host real SQL/Cosmos races and lost-acknowledgment tests |
| 8: evidence minimization | Captured outbound state assertions for secrets, all mask modes, history boundaries and missing metadata |
| 10–12: processing invariant | Architecture guards, before/after core-row comparison and existing recovery regressions |
| 13: advisory UI | Component tests and desktop/mobile screenshots |
| 14: observability | Meter/activity listener assertions and host export registration checks |
| 15: complete regression gate | Release solution build/test, executed provider suites, frontend checks and default-off published-host smoke test |

Completion requires recording actual commands/results and any limitations here or in the implementation handoff. No live TypeSafe request is required to prove the specified behavior; an optional real-provider evaluation is separate from this plan's deterministic release gate.
