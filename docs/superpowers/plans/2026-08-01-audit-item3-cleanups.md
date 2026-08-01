# Audit Item 3: Retry Fallback Removal, "self" Alignment, Management MEL, Startup Split, Gate Honesty

## Context

Last audit basket: (a) `StrictMessageHandler` ships hardcoded demo retry rules (`"AliceSaidHelloWithRetry"`) as a production fallback when no `IRetryPolicyProvider` is registered; (b) the WebApp's three `"self"`-origin checks are case-sensitive outliers (everything else platform-wide is `OrdinalIgnoreCase`) with an NRE risk; (c) `Management.ServiceBus` still takes `Serilog.ILogger` (ADR-006) — all 36 log calls are dead in production since no caller passes a logger, and several messages are copy-paste-wrong; (d) `Startup.ConfigureServices` is one ~440-line method; (e) CLAUDE.md overstates the quality gates (11 src projects opt out of `EnforceCodeStyleInBuild`; analyzer warnings never fail builds). User decision: **document gate reality, no tightening**.

Verified grounding:
- Retry fallback (`StrictMessageHandler.cs:707-731`): consulted ONLY when `_retryPolicyProvider == null` (provider path `return`s first). No provider + no matching demo rule = no retry already. Nothing outside `RetryDefinitions.cs` + two tests references the demo names. `RetryDefinitions` is public `[Obsolete]` package API — keep the type, remove the consultation (type deletion rides 3.0).
- `"self"`: writer default is `Constants.Self = "self"` (`MessageHelper.cs`); `ResponseService`/`PublisherClient` compare `OrdinalIgnoreCase`; only `EventImplementation.cs:203/:252/:663` use `Ordinal` and can NRE. `BlockedEventRules.ResolveOriginatingId` is NOT a drop-in (returns id, not bool) — need a sibling predicate.
- Management.ServiceBus: constructed at `Startup.cs:414` (single-arg) and 6 inline `new EndpointManagement(serviceBusManagement)` sites in `EndpointImplementation.cs`; logger always null.
- Startup groups A–X mapped with 4 order-sensitive edges: Identity opt-in → auth ladder (reflection probe), `IClaimsTransformation` last-wins inside the ladder, ServiceBus clients → management services, `storageProvider` local → store + health checks.

## Commits (local only, TDD-first)

### 1. `refactor(core)!: remove legacy RetryDefinitions fallback from StrictMessageHandler`
- Tests FIRST (`tests/NimBus.Core.Tests/StrictMessageHandlerTests.cs`): rewrite `:604` → `HandleEventRequest_HandlerThrows_NoProvider_NoRetryResponse` (keep eventTypeId `"AliceSaidHelloWithRetry"`, assert zero retries — proves the demo rule no longer fires); fold `:668` (redundant once no-provider = no-retry); convert `:683` user-property lookup test to `FakeRetryPolicyProvider` (`:1557`) so the user-property assertion survives. RED → delete the pragma block `StrictMessageHandler.cs:724-730` → GREEN.
- Keep `RetryDefinitions.cs` (obsolete, now fully dead — 3.0 deletion note). Update `docs/architecture.md` mention; check spec 015 references.
- Behavior note for release notes: consumers with no registered provider AND one of the 8 legacy event ids silently lose retries — intended (demo rules were never production config).

### 2. `refactor(webapp): shared IsSelfOriginating predicate replaces case-sensitive self checks`
- Tests FIRST in `tests/NimBus.MessageStore.InMemory.Tests/BlockedEventRulesTests.cs`: `IsSelfOriginating` — "self"/"Self"/"SELF" true; null/empty/other false.
- `BlockedEventRules.IsSelfOriginating(string?)` = `string.Equals(x, Constants.Self, OrdinalIgnoreCase)`; refactor `ResolveOriginatingId` to use it (one definition of "self").
- Replace the three `Equals("self", Ordinal)` sites in `EventImplementation.cs` (:203/:252/:663). Fixes NRE + aligns casing with `ResponseService`/`PublisherClient`.
- Add one WebApp test (existing `EventImplementationPlainResubmitTests` harness) with `originatingMessageId: "Self"` asserting the self-branch picks `To`.

### 3. `refactor(management): migrate Management.ServiceBus to MEL (ADR-006)`
- Same pattern as `ManagerClient` (d09fa6b): primary MEL ctor (`ILogger<T> = null`), required-param `[Obsolete]` Serilog bridge ctor, private `SerilogBridgeLogger` per class — **copy the `Exception?`/`IDisposable?` nullability exactly** (CS8767 is not Release-allowlisted; broke CI once).
- Convert all 36 calls to structured MEL (Verbose→LogTrace, Information→LogInformation, Error→LogError) and fix the copy-paste bugs: DeleteSubscription logging "Creating subscription", DeleteRule logging "Deleting rule"/"Created rule successfully"/"Could not create rule", `succesfully` typos ×5.
- Wire a real logger at `Startup.cs:414`; leave the six inline `new EndpointManagement(...)` single-arg calls (bind MEL ctor; threading a second logger through six inline constructions is noise). Keep per-project bridge copies (centralizing would add a Serilog dependency edge to a shared project — worse); all bridges die together at 3.0.

### 4. `refactor(webapp): split Startup.ConfigureServices into named steps`
- Pure move into ~9 instance-private methods in `Startup.cs` (no partials; ServiceDefaults private-helper precedent), called in exactly the current order: `AddAuthenticationStack` (:67-243, returns auth flags; keeps the reflection-probe and IClaimsTransformation ordering internal), `AddWebPipeline` (:245-303), `AddPlatformCatalog` (:305-336), `AddServiceBusClients` (:338-367), `AddStorage` (:368-395, returns `storageProvider`), `AddManagementServices` (:397-414), `AddObservability(storageProvider)` (:416-472), `AddAuthorizationAndAudit` (:473-489), `AddApiControllers` (:490-505).
- Instance-private (uses `Configuration`/`Environment` members) — no parameter threading. Keep every comment with its registration. No registration reordering.

### 5. `docs: document quality-gate reality`
- CLAUDE.md "Analyzers & Quality": state that 11 src projects opt out of `EnforceCodeStyleInBuild` (Abstractions, Testing, ServiceBus, Core, Extensions.Notifications, SDK, MessageStore.{SqlServer,CosmosDb,Abstractions}, Outbox.SqlServer, Resolver); Release promotes only CS compiler warnings (`CodeAnalysisTreatWarningsAsErrors=false`) with the `WarningsNotAsErrors` allowlist (CS0618/CS8600-series; CS8767 NOT allowlisted); analyzer CA/S/SA warnings never fail builds. Tightening = explicit backlog line. Adjust `Directory.Build.props` comments if misleading.

## Verification
- Per commit: Debug build + affected tests (1: Core.Tests; 2: MessageStore.InMemory.Tests + WebApp.Tests; 3: WebApp.Tests + any Management tests; 4: full WebApp.Tests 249).
- **Release build** of touched src projects on commits 1, 3, 4 (CS8767 lesson).
- Commit 4: `git diff --color-moved` eyeball to confirm pure move / preserved order.
- Final: full solution build + WebApp/Core/InMemory suites green.
- Copy this plan to `docs/superpowers/plans/` in commit 1.

## Out of scope
Gate tightening (documented instead); deleting `RetryDefinitions`/bridge ctors/obsolete handoff members (3.0 basket); registering `EndpointManagement` in DI.
