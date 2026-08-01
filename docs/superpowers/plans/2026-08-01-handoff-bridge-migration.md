# Handoff Bridge Migration: WebApp off obsolete IManagerClient + ADR-006 MEL cleanup

## Context

Audit item 2: the WebApp settles message handoffs through the obsolete `IManagerClient.CompleteHandoff`/`FailHandoff` on its live HTTP paths (`POST /api/event/handoff/*`, `POST /api/agent/settle`), each call wrapped in a CS0618 pragma. The blocker was structural: the replacement `NimBus.SDK.HandoffClient` is endpoint-bound (`HandoffClientOptions.Endpoint`) while the WebApp serves arbitrary endpoints from route params/agent zones. Related ADR-006 violation: `ManagerClient` and `ResolverService` take `Serilog.ILogger` in their constructors, forcing `AddSingleton<Serilog.ILogger>` into the Resolver's DI.

Verified facts the design rests on:
- Both clients build messages via the shared `HandoffControlMessageFactory` → byte-identical wire output; neither writes to the message store.
- The wire truly requires only EventId/SessionId/MessageId (`BuildBase` falls back `OriginatingMessageId ?? ParentMessageId`; CorrelationId/EventTypeId pass through nullable). `HandoffClient.ValidateCoords` is stricter than `ManagerClient` ever was — it would break settlement of legacy rows with null lineage.
- `HandoffSettlementService.cs:73` already owns the `PendingSubStatus == "Handoff"` guard, so ManagerClient's redundant guard is not lost.
- `NimBusOpenTelemetryDecorators.InstrumentSender(ISender, string)` is static, no options dep — a factory can build senders exactly like `RegisterHandoffClient` (ServiceCollectionExtensions.cs:513-537).
- `AgentImplementation` uses `IManagerClient` only for handoffs (param swap); `EventImplementation` also uses `Skip`/`Resubmit` (keeps it, gains factory).
- MS.DI ctor-ambiguity risk from dual ctors is real (`Serilog.ILogger` IS registered in Resolver DI); defused by making the obsolete Serilog bridge ctor's logger param REQUIRED and switching registrations to explicit factory lambdas. `new ManagerClient(client)` (×7 in SdkAndManagerTests) then uniquely binds the MEL ctor.

## Commits (local only, TDD-first where tests exist to write)

### 1. `feat(sdk): add IHandoffClientFactory for per-endpoint settlement clients`
- New `src/NimBus.SDK/HandoffClientFactory.cs`: `IHandoffClientFactory { IHandoffClient ForEndpoint(string endpointId) }` + `sealed HandoffClientFactory(ServiceBusClient, ILoggerFactory? = null)`; `ConcurrentDictionary.GetOrAdd` (ReplyDispatcher.cs:32 pattern) building `InstrumentSender(new Sender(client.CreateSender(ep)), ...)` → `new HandoffClient(sender, new HandoffClientOptions { Endpoint = ep }, loggerFactory?.CreateLogger<HandoffClient>())`. Empty endpoint → ArgumentException.
- `AddNimBusHandoffClientFactory()` in `ServiceCollectionExtensions.cs` via `TryAddSingleton`. Leave `RegisterHandoffClient` keyed singletons untouched (their DI shape is test-pinned; consolidation is follow-up).
- Tests first: new `tests/NimBus.SDK.Tests/HandoffClientFactoryTests.cs` — caching (same instance per endpoint), distinct per endpoint, empty-endpoint throw, registration idempotence.

### 2. `feat(sdk): relax HandoffClient coords validation to wire-required fields`
- `HandoffClient.ValidateCoords` (:85-102): keep EventId/SessionId/MessageId required; make CorrelationId/OriginatingMessageId/EventTypeId optional (matches what ManagerClient always allowed and what the wire needs). Update XML docs.
- Tests first: flip the two strictness tests in `HandoffClientRegistrationTests.cs:105-132` (null lineage now sends; assert wire falls back OriginatingMessageId→MessageId); add tests that null EventId/SessionId/MessageId still throw.
- Contract note: a loosening, non-breaking; call out in next release notes.

### 3. `refactor(webapp): settle handoffs via IHandoffClientFactory`
- `AgentImplementation.cs`: ctor `IManagerClient` → `IHandoffClientFactory`; `:332/:348` → `_handoffClients.ForEndpoint(zoneId).CompleteAsync(Coords(pendingEntry), body.Result)` / `.FailAsync(..., body.ErrorText, body.ErrorType)`; private `static Coords(MessageEntity)` maps exactly like `ManagerClient.CoordsFor` (ParentMessageId = entry.MessageId). Remove pragmas.
- `EventImplementation.cs`: ADD factory ctor param (keep `IManagerClient` for Skip/Resubmit); `:289/:305` same pattern with route `endpointId`; remove pragmas.
- `EndpointsController.cs:24-31`: remove dead `IManagerClient` dependency.
- `Startup.cs`: `services.AddNimBusHandoffClientFactory();`.
- Tests first: `AgentImplementationTests` + `AgentLoopIntegrationTests` — replace `CapturingManagerClient` with capturing `IHandoffClientFactory`/`IHandoffClient` fakes (capture coords/result/errorText/endpoint); update `BuildAgent` helpers, direct ctor calls, and settle assertions. `EventImplementationPlainResubmitTests.cs:158` + `EventImplementationReportTests.cs:104` (named-arg ctors): add `handoffClientFactory` arg, keep their ManagerClient fakes for Resubmit.

### 4. `refactor(manager): migrate ManagerClient logging to MEL (ADR-006)`
- Field → MEL `ILogger`; primary ctor `(ServiceBusClient, ILogger<ManagerClient> = null)`; `[Obsolete]` bridge ctor `(ServiceBusClient, Serilog.ILogger)` with REQUIRED param, adapting via a private `SerilogBridgeLogger` (pattern from pre-2.0.0 `CosmosDbClient`). `_logger?.Verbose` ×4 → structured `LogTrace`.
- DI → explicit lambdas: `ManagerBuilderExtensions.cs:17`, `Startup.cs:396` (`sp => new ManagerClient(sp.GetRequiredService<ServiceBusClient>(), sp.GetService<ILogger<ManagerClient>>())`). Keep Manager's Serilog PackageReference (bridge needs it).

### 5. `refactor(resolver): migrate ResolverService to MEL, drop Serilog DI registration (ADR-006)`
- `ResolverService.cs`: primary MEL ctor; required-param `[Obsolete]` Serilog bridge; 10 log call sites map directly (Verbose→LogTrace, Information→LogInformation, Warning→LogWarning, Error→LogError — templates already structured).
- `ServiceExtensions.cs`: DELETE `:25 AddSingleton<Serilog.ILogger>(Log.Logger)`; register `IMessageHandler` via explicit lambda. `Program.cs` `AddSerilog` MEL provider stays (ADR-006-compliant: Serilog as sink behind MEL). Resolver.Tests 1-/2-arg ctor calls compile unchanged.

### 6. `chore(sdk): drop unused Serilog package reference`
- `NimBus.SDK.csproj` — zero Serilog usage in SDK source (verified).

## Risks
- Sender lifetime: cached process-long senders replace ManagerClient's per-call senders — matches existing keyed registrations; ServiceBusClient owns disposal.
- Coords relaxation is an SDK behavior loosening — documented, tested, release-noted.
- Out of scope / follow-ups: `Management.ServiceBus` Serilog usage; deleting the obsolete `CompleteHandoff`/`FailHandoff` (zero in-repo production callers post-migration — 3.0 cleanup); `RegisterHandoffClient`-on-factory consolidation.

## Verification
1. `dotnet build src/NimBus.sln` — pragma removal must not resurface CS0618.
2. `dotnet test`: NimBus.SDK.Tests (factory + relaxed validation), NimBus.WebApp.Tests, NimBus.ServiceBus.Tests, NimBus.Resolver.Tests, NimBus.EndToEnd.Tests.
3. **Wire-parity proof**: `SdkAndManagerTests` (:79-226) green with UNCHANGED assertions demonstrates ManagerClient and HandoffClient stay byte-identical on the wire.
4. Grep: no CS0618 pragmas left in WebApp; no `Serilog` in Resolver ServiceExtensions.

Also: copy this plan to `docs/superpowers/plans/` in commit 1.
