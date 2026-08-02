# NimBus 3.0.0: Obsolete-API Deletion Basket + Release

## Context

2.x accumulated deliberate `[Obsolete]` bridges so consumers could migrate without breaking: the `IManagerClient` handoff-settlement members (replaced by `IHandoffClient`/`IHandoffClientFactory`), four Serilog bridge constructors + three `SerilogBridgeLogger` adapter copies (ADR-006 MEL migration), the legacy `RetryDefinitions` type (fallback consultation already removed in `e5380c2`), and the obsolete `PublisherClient`/`SubscriberClient` constructors (replaced by `CreateAsync`). All now have **zero in-repo production callers**. User decisions: delete ALL of it (no extra bridge window) and **cut the v3.0.0 release** at the end (tag-driven NuGet publish + GitHub Release in the established v1.2.0 notes format). Working tree is clean at `e13de5d`; the release also carries ~20 unreleased commits since v2.0.0, two of which are themselves behavior-breaking (retry-fallback removal, pagination caps) and must ride this major.

**Explicitly OUT of 3.0** (state in release notes):
- `IPermanentFailureClassifier` — still load-bearing (`DefaultFailureDispositionClassifier` wraps it across StrictMessageHandler/SDK/Testing); deletion is blocked on completing the `IFailureDispositionClassifier` migration. Stays `[Obsolete]`.
- `StoragehookReceiveCosmosAsync` — pinned by generated `ApiContract.g.cs` + `api-spec.yaml` operationId; renaming breaks the deployed Resolver→WebApp storage-hook webhook across version skew.

## Commits (local until verified; TDD where new tests are written)

### 1. `refactor(core,manager)!: delete RetryDefinitions and IManagerClient handoff settlement`
- **Coverage first** (verified gap): no unit test pins `HandoffClient`'s full happy-path wire shape (From=`Constants.ManagerId`, To, EventTypeId, correlation/session, `EventContent`/`ErrorContent` JSON body) — the doomed `SdkAndManagerTests` parity tests were the only ones. Port their assertions into two new tests in `tests/NimBus.SDK.Tests/HandoffClientRegistrationTests.cs` (`CompleteAsync_HappyPath_PinsAbsoluteWireShape`, `FailAsync_HappyPath_PinsErrorContentWireShape`) and see them GREEN before deleting anything.
- Delete `src/NimBus.Core/Messages/RetryDefinitions.cs` (whole file); comment touch-ups `StrictMessageHandler.cs:710`, `StrictMessageHandlerTests.cs:608`.
- `src/NimBus.Manager/ManagerClient.cs`: remove interface members `:38,:51`, impls `:129,:142`, `CoordsFor` `:155-162`, and the now-unused `HandoffControlMessageFactory` reference. `IManagerClient` = Resubmit + Skip.
- Delete the five obsolete-handoff tests in `SdkAndManagerTests.cs` (:79,:119,:144,:172,:211); the surviving Resubmit/Skip tests and seven single-arg `new ManagerClient(client)` calls stay on the MEL ctor. Drop the two stub members from `EventImplementationPlainResubmitTests.cs:213,216`. Comment rewording in `AsyncCompletionTests.cs` (:24-26,:92-93,:149,:272).
- XML/comment updates: `IHandoffClient.cs:14`, `HandoffSettlementMapper.cs:8`, `HandlerOutcome.cs:10`, `StrictMessageHandler.cs:180`, `IEventHandlerContext.cs:62`.

### 2. `refactor!: delete Serilog bridge constructors and drop dead Serilog packages`
- Delete bridges: `ManagerClient.cs:73` + adapter `:168-189`; `ResolverService.cs:61` + nested adapter `:486-507`; `ServiceBusManagement.cs:62`; `EndpointManagement.cs:25`; whole `src/NimBus.Management.ServiceBus/SerilogBridgeLogger.cs`.
- Drop PackageReferences: `NimBus.Manager.csproj:16`, `NimBus.Management.ServiceBus.csproj:9`, `NimBus.WebApp.csproj:31-33` (zero Serilog symbols in WebApp source). **Resolver keeps its Serilog packages** (`Program.cs` `AddSerilog` host provider — ADR-006-compliant).
- DI simplifications (ambiguity comments are now stale): `ManagerBuilderExtensions.cs:20` and WebApp `Startup.cs:444` → `AddSingleton<IManagerClient, ManagerClient>()`; reword Resolver `ServiceExtensions.cs:25-26`.

### 3. `refactor(sdk)!: delete obsolete PublisherClient/SubscriberClient constructors`
- `PublisherClient.cs:97` ctor; wording updates in exception messages `:260,:270`.
- `SubscriberClient.cs:76-95` ctor (verified byte-for-byte duplicate of `CreateAsync`).
- Delete the four `ObsoleteConstructor_*` tests in `SubscriberClientTests.cs` (:113-144) — verified exact mirrors of the existing `CreateAsync_*` tests including `LastSenderEntityPath`; no migration needed.

### 4. `docs: update living docs for 3.0 deletions and add versioning policy`
- `docs/sdk-api-reference.md:224-226,249,530,717`; `docs/error-handling.md:88`; `docs/pending-handoff.md:65,73,86,172,271,276`; `docs/adr/012-pending-handoff.md:25,53,92`; `docs/architecture.md:345` — mostly `IManagerClient.CompleteHandoff` → `IHandoffClient.CompleteAsync` wording; verify each line before editing. Historical specs untouched.
- New `docs/versioning.md` (~10 lines): SemVer; tag-driven publish (`v*` → nuget-publish.yml); obsolete lifecycle "deprecate in a minor, delete in the next major"; note that single-ctor DI registrations are load-bearing.
- Copy this plan to `docs/superpowers/plans/2026-08-02-v3-deletion-basket.md`.

## Verification
1. Per commit: Debug build + affected suites; **Release build of every touched project** (CS8767 lesson — Manager, Management.ServiceBus, SDK, Core, Resolver, WebApp).
2. Final: `dotnet build src/NimBus.sln -c Release` + suites: Core.Tests, SDK.Tests, ServiceBus.Tests, WebApp.Tests, EndToEnd.Tests, MessageStore.InMemory.Tests.
3. Grep-gate before tagging: zero hits for `RetryDefinitions`, `SerilogBridgeLogger`, obsolete `CompleteHandoff/FailHandoff` members in src/; no `Serilog` in Manager/Management.ServiceBus/WebApp csproj.

## Release (mirrors the 2.0.0 flow)
Push → CI green → annotated tag `v3.0.0` from a notes file → push tag (triggers nuget-publish: Release build+test+pack+push) → `gh release edit/create` with notes in the v1.2.0 format → confirm nuget.org flat-container shows 3.0.0 (indexing lags minutes).

**Release-notes outline** (covers all commits since v2.0.0):
- What's New intro: major deleting API deprecated in 2.x + all unreleased 2.x work.
- ⚠️ Breaking: IManagerClient handoff members removed (→ IHandoffClientFactory); all Serilog bridge ctors removed (MEL only); obsolete Publisher/Subscriber ctors removed (→ CreateAsync); RetryDefinitions removed AND its StrictMessageHandler fallback (no provider = no retry); pagination caps (take<=0 now returns 100, max 1000); Serilog packages dropped from Manager/Management.ServiceBus/WebApp.
- ✨ Features: IHandoffClientFactory; relaxed handoff coords validation.
- 🔧 Improvements: MEL migrations ×3; WebApp settlement via factory; Startup split; storage-layer extractions + shared rules; self-check alignment; ServiceBusManagement reduction (from 2.0.0 era context where needed).
- 🐛 Fixes: single-flight sender creation; access-control race safety + auth hardening (collaborator commits `10ef37a`,`6efed84`,`e13de5d`); bridge nullability (superseded); log message copy-paste fixes.

## Risks
- External consumers on the deleted ctors/bridges get a hard compile break — mitigated by the 2.x deprecation window + migration table in the release notes.
- The two ride-along behavior changes (retry fallback, pagination caps) must be prominent in ⚠️ — they change runtime behavior without compile errors.
- Wire-shape regression risk from deleting parity tests — mitigated by the two ported absolute-shape tests landing BEFORE the deletion in commit 1.
