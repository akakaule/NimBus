# Adapter resilience & local-from-packages — #186, #187, #188, #189 in one PR

## Context

Building the Dynamics 365 / Business Central demo exposed four gaps for adapter authors (issues filed 2026-10-01):

- **#189:** the two DynamicsBcDemo adapters contradict each other. `BusinessCentral.Adapter` is the reference. `D365Sales.Adapter` has hidden retries, no retry rules, no breaker and untyped failures.
- **#186:** `docs/building-adapters.md` omits deferred replay from the complete shape, never mentions the resilience-handler trap, and has no resilience or adapter-testing guidance.
- **#188:** core can't use a server's Retry-After hint for handler retries. Exception rules match on stack-trace substrings, and adapters hand-roll token caches.
- **#187:** an adapter repo using only nuget.org can't run a local stack. ServiceDefaults and the emulator aren't packages, and the Resolver and WebApp ship only as zips.

Decisions made with you:
- dotnet tools for distribution, not containers.
- The ServiceDefaults resilience handler is off by default, and existing non-adapter callers opt in.
- The optional #188 helpers (watermark, stale-write guard) are out of scope.
- CrmErpDemo keeps its behaviour and gets a pointer comment.

Branch: `feat/adapter-resilience-and-packaging` (from master `e9996280`). The first commit saves this plan as `docs/plan/2026-10-01-adapter-resilience-and-packaging.md`.

## 1. Core: Retry-After hints and typed exception rules (#188)

**Retry-After hints**
- New `NimBus.Core.Messages.IRetryAfterHint { TimeSpan? RetryAfter { get; } }`. Any exception in the chain can implement it.
- `StrictMessageHandler.CheckForRetry` (`src/NimBus.Core/Messages/StrictMessageHandler.cs:646`):
  - After `policy.GetDelay(retryCount)`, walk `exception` and its `InnerException` chain for the first `IRetryAfterHint` with a positive value.
  - The applied delay is `max(policyDelay, hint)`, capped by `policy.MaxDelay` when set.
  - The policy still decides *whether* to retry; the hint only lengthens the delay.
- Existing `SendRetryResponse(ctx, TimeSpan)` already gives sub-minute precision.

**Typed exception rules**
- `DefaultRetryPolicyProvider.AddExceptionRule<TException>(RetryPolicy policy, params string[] eventTypeIds) where TException : Exception` (`src/NimBus.Core/Messages/DefaultRetryPolicyProvider.cs`).
- Rules stay in one ordered list, so the first match wins across string and typed rules. A typed rule matches when the exception or any inner exception `is TException`.
- `IRetryPolicyProvider` gains `GetRetryPolicy(string eventTypeId, Exception exception, string? endpoint = null)`:
  - Its default interface implementation forwards to the string overload with the existing `$"{exception.InnerException} {exception}"` text, so custom providers keep working.
  - `DefaultRetryPolicyProvider` overrides it.
  - `StrictMessageHandler` calls the new overload.
  - Testing's `ServiceCollectionExtensions` and the SDK wiring are unchanged.
- Fix the stale `docs/sdk-api-reference.md` "Retry Policies" section while there:
  - It shows `RetryPolicy` as a record; it is a class.
  - It calls a non-existent `AddExceptionPolicy`.

**Tests (RED first)**
- `tests/NimBus.Core.Tests/RetryPolicyProviderTests.cs`:
  - A typed rule matches a subclass and an inner exception.
  - Mixed string and typed order is respected.
  - The string overload never matches typed rules.
- `tests/NimBus.Core.Tests/StrictMessageHandlerTests.cs`, following `HandleEventRequest_RetryPolicy_SchedulesWithSubMinutePrecision` (:828) with `FakeRetryPolicyProvider`:
  - A hint longer than the policy delay wins.
  - A shorter hint is ignored.
  - A hint is capped at `MaxDelay`.
  - A hint on an inner exception is found.
  - No retry is scheduled when the policy says no, even with a hint.

## 2. New package: Azure bearer-token handler (#188)

- New project `src/NimBus.Extensions.Http`, packed as `Akaule.NimBus.Extensions.Http`. It depends on `Azure.Core` and `Microsoft.Extensions.Http`, not Azure.Identity: the caller passes a `TokenCredential`.
- `AzureBearerTokenHandler : DelegatingHandler`:
  - Takes a `TokenCredential` and scopes.
  - Caches the `AccessToken` and refreshes it at `RefreshOn`, or 5 minutes before `ExpiresOn`.
  - Refresh is thread-safe through `SemaphoreSlim` and double-checks before refreshing. A cache is needed because `AzureCliCredential` does not cache.
  - Modelled on `src/NimBus.WebApp/Services/ApplicationInsights/ApplicationInsightsAuthenticationHandler.cs`.
- `IHttpClientBuilder.AddAzureBearerToken(params string[] scopes)` resolves `TokenCredential` from DI. A second overload takes `(TokenCredential, scopes)`.
- The XML docs give the scope examples:
  - Dataverse `https://{org}.crm.dynamics.com/.default`
  - BC `https://api.businesscentral.dynamics.com/.default`
  - F&O `https://{env}.operations.dynamics.com/.default`
- New `tests/NimBus.Extensions.Http.Tests` (MSTest) uses a fake `TokenCredential` to check:
  - The header is set.
  - The token is cached across calls.
  - The token refreshes near expiry.
  - Concurrent first calls fetch once.
- Add the project to `src/NimBus.sln` and to `nuget-publish.yml`; packing is automatic via `IsPackable`.

## 3. ServiceDefaults published, resilience handler opt-in (#187.1)

- `src/NimBus.ServiceDefaults/NimBus.ServiceDefaults.csproj`: set `IsPackable=true`, producing `Akaule.NimBus.ServiceDefaults`.
- New `NimBusServiceDefaultsOptions { bool UseStandardResilienceHandler = false; }` and the overload `AddServiceDefaults(this TBuilder, Action<NimBusServiceDefaultsOptions>? configure = null)`.
- `ConfigureHttpClientDefaults` always adds service discovery, and adds the resilience handler only when the option is set.
- These callers keep today's behaviour with `o => o.UseStandardResilienceHandler = true`:
  - `src/NimBus.Resolver` and `src/NimBus.WebApp`
  - AspirePubSub projects
  - CrmErpDemo projects
  - DynamicsBcDemo APIs
- **Adapters don't opt in:** BC.Adapter and D365Sales.Adapter.
- BC.Adapter keeps its raw `HttpClient`, and its comment is updated to say why that pattern stays valid.

## 4. Tools and Aspire hosting from packages (#187.2–3)

**Emulator as a tool**
- `src/NimBus.ServiceBusEmulator`: `IsPackable=true`, `PackAsTool=true`, `ToolCommandName=nimbus-sb-emulator`, producing `Akaule.NimBus.ServiceBusEmulator`.
- Its args and env are already tool-friendly (`--port`, `NIMBUS_SBEMULATOR_*`).

**Worker-hosted Resolver as a tool**
- New `src/NimBus.Resolver.Host` (Worker SDK), `PackAsTool`, command `nimbus-resolver`, producing `Akaule.NimBus.Resolver.Host`.
- `Program.cs` = `AddServiceDefaults` (opted in) + the `ResolverStorageProvider.Select` store wiring from `src/NimBus.Resolver/Program.cs` + `AddResolver()` + `NimBusReceiverHostedService` on topic and subscription `Resolver`.
- It follows `samples/AspirePubSub/AspirePubSub.ResolverWorker/Program.cs` and references `src/NimBus.Resolver`.
- The Service Bus client comes from `ConnectionStrings:servicebus` or `AzureWebJobsServiceBus`.

**WebApp as a tool**
- `src/NimBus.WebApp`: `IsPackable=true`, `PackAsTool=true`, command `nimbus-webapp`, producing `Akaule.NimBus.WebApp`.
- The SPA is in the publish output, so it ships in the tool.
- `NimBus:PlatformType` and `NimBus:PlatformAssembly` already accept an absolute path to the adapter's contracts DLL.
- **Risk:** Web SDK + NSwag + SPA under `PackAsTool`. If packing fails, fall back to a thin `src/NimBus.WebApp.Tool` wrapper project.

**New hosting package**
- New `src/NimBus.AspireHosting`, producing `Akaule.NimBus.AspireHosting`. It references `Aspire.Hosting` and runs each tool as an `ExecutableResource`: `dotnet tool exec Akaule.NimBus.X@<version> -- <args>`, with the AppHost directory as the working directory so its `nuget.config` applies.
- The version defaults to the hosting assembly's informational version. A `version:` parameter overrides it, and so does env `NIMBUS_TOOL_VERSION` (needed for 0.0.0 dev builds and local-feed verification).
- APIs:
  - `AddNimBusResolver(name)`
  - `AddNimBusWebApp(name, platformAssemblyPath, platformType)`, with an `http` endpoint and `EnableLocalDevAuthentication` set in Development
  - `AddNimBusTopology(name, serviceBus, platformAssemblyPath, platformType)`, which runs the existing `nb` tool (`Akaule.NimBus.CommandLine`): `nb topology apply --sb-connection-string … -a … --platform …`. Check the exact flags against `src/NimBus.CommandLine/Commands/TopologyCommands.cs:51` before coding.
  - Fluent `.WithServiceBus(sb)`, `.WithSqlServerStore(db)` and `.WithCosmosStore(cs)`. These set `NimBus__StorageProvider` and the connection-string env vars that `src/NimBus.AppHost/Program.cs:120-144` sets today.
- `src/NimBus.ServiceBusEmulator.AspireHosting`: new overload `AddNimBusServiceBusEmulator(name, port?, version?)` that runs the emulator tool instead of a project. It reuses `Configure` (connection string, health check). The `<TProject>` and path overloads stay.
- Tests in a new `tests/NimBus.AspireHosting.Tests` build the app model with `DistributedApplication.CreateBuilder` and assert:
  - executable args (package@version, `--`, tool args)
  - environment variables
  - wait relations

## 5. Samples (#189)

**D365Sales.Adapter** (`samples/DynamicsBcDemo/D365Sales.Adapter`) gets the BC shape:
- Typed `Dataverse{Throttled(: IRetryAfterHint),Unavailable,RequestRejected}Exception`, built with the shared `DescribeFailureAsync` message format.
- A `DataverseClient` mapping that copies `BusinessCentralClient.ThrowOnFailureAsync`/`SendAsync`:
  - 429 → Throttled
  - 408/502/503/504/timeouts → Unavailable
  - other 4xx → Rejected
- A `D365Resilience` with `AddExceptionRule<T>` rules and bounded jitter, and no default policy.
- `WithCircuitBreaker` with `Exclude<Rejected>()` and `Exclude<Throttled>()`.
- `PrefetchCount = 0`.
- `UseInbox` on SQL, with `WithReference(nimbusDb)` added in `DynamicsBcDemo.AppHost/Program.cs:93`.
- Its factory client stays, because ServiceDefaults no longer adds hidden retries.

**BusinessCentral.Adapter**
- `BcThrottledException` implements `IRetryAfterHint`.
- `BcResilience` switches to `AddExceptionRule<T>`.

**CrmErpDemo**
- Explicit resilience opt-in, so behaviour and e2e timing don't change.
- A comment in `Crm.Adapter/Program.cs` and `Erp.Adapter.Functions/Program.cs` says the sample stays minimal and points at the DynamicsBcDemo adapters.

**Tests:** new resilience tests in `tests/DynamicsBcDemo.Tests` mirroring `BusinessCentralAdapterTests.cs` (:22-112):
- status mapping
- Retry-After capture
- retry rules (through `DefaultRetryPolicyProvider.GetRetryPolicy` with the exception overload)
- the breaker excludes rejections and throttling

## 6. Docs (#186 and the docs for 1–5)

**`docs/building-adapters.md`**
- The complete Worker shape gains `AddNimBusDeferredProcessorHostedService`, `PrefetchCount = 0`, and the resilience-handler note next to `AddHttpClient`.
- Fix the stale outbox "complete sample" pointer (L432).
- New **Resilience** section:
  - typed failures at the client boundary, with descriptive, PII-free messages
  - `AddExceptionRule<T>` versus substring matching against stack traces
  - Retry-After through `IRetryAfterHint`
  - the permanent-failure classifier's "Validation" name trap
  - what "no default policy" means (the session blocks until an operator acts)
  - `UseInbox` with deterministic MessageIds in the integration DB
  - `WithCircuitBreaker` with `Exclude<T>`, Worker-only pausing, `PrefetchCount = 0`
  - the retry budget versus lock renewal
  - the bearer-token handler
  - change detection, echo-loop prevention and idempotent upserts
- New **Adapter testing** section:
  - `AddNimBusTestTransport`
  - retry-rule tests through the provider
  - the breaker with a manual `TimeProvider`
  - `PlatformValidation.ValidateCommandConsumers` / `Event.TryValidate()`
- New **Run locally from packages** section: an AppHost using `Akaule.NimBus.AspireHosting`.

**Other docs**
- Cross-link `docs/error-handling.md` ("Configuring retry"), `docs/circuit-breaker.md` and `docs/testing.md`.
- Update `docs/sdk-api-reference.md`.

**Docs test**
- A guard test like `tests/NimBus.WebApp.Tests/RateLimitWiringTests.cs:209`, placed in Core.Tests or a suitable tests project.
- It asserts that the complete-shape block contains `AddNimBusDeferredProcessorHostedService` and that the Resilience and Adapter testing headings exist.

## Verification

1. **Builds and tests**
   - `dotnet build src/NimBus.sln -c Release` reaches 0 errors.
   - `dotnet test src/NimBus.sln -c Release --no-build`.
   - The frontend `test:ci` and build run, because the WebApp packaging changes.
2. **Pack:** `dotnet pack src/NimBus.sln -c Release -o <scratch>/feed /p:Version=4.3.0-local`. Confirm these packages exist and that each tool package contains its command shim:
   - `Akaule.NimBus.ServiceDefaults`
   - `Akaule.NimBus.ServiceBusEmulator`
   - `Akaule.NimBus.Resolver.Host`
   - `Akaule.NimBus.WebApp`
   - `Akaule.NimBus.AspireHosting`
   - `Akaule.NimBus.Extensions.Http`
3. **#187 acceptance with a fresh adapter repo**, in the scratchpad, outside the repo:
   - Its `nuget.config` points at only the local feed and nuget.org.
   - It contains a contracts project, an adapter worker using `Akaule.NimBus.SDK` + `ServiceDefaults`, and an AppHost using `AddNimBusServiceBusEmulator`, `AddNimBusTopology`, `AddNimBusResolver`, `AddNimBusWebApp` and a SQL container.
   - `aspire run` should bring every resource to Running or Finished.
   - Publish one event, then confirm it is Completed in the WebApp through the browser pane.
   - No NimBus source may be referenced.
4. **DynamicsBcDemo:** `aspire run` with the D365 adapter healthy. Before starting it, check that no other demo stack is running.
5. CI's live conformance suites are unaffected, because there are no storage changes.

## Out of scope

- #188's optional watermark and stale-write helpers.
- Containers.
- CrmErpDemo behaviour changes.
- Releasing: a follow-up v4.3.0, once merged.

## Implementation notes (where the result differs from the plan)

- `AddNimBusWebApp` exposes an external `https` endpoint as well as `http`, with `httpsPort`/`httpPort`
  parameters instead of a single `port`. The SPA builds its API base URL as `https://{host}:{port}`
  (`CookieAuth` in `ClientApp/src/api-client/index.extensions.ts`), so an HTTP-only WebApp loads but
  every API call fails. Found during the end-to-end check.
- The WebApp sets its content root to its own directory when it starts in a directory without the
  built SPA (`Program.ResolveContentRoot`); a tool runs in the AppHost directory.
- `NimBusTools` (the `dotnet tool exec` helper) lives in `NimBus.ServiceBusEmulator.AspireHosting`,
  exposed to `NimBus.AspireHosting` with `InternalsVisibleTo`; both packages ship at one version.
- The D365 adapter's SQL inbox uses its own table, `D365SalesInboxMessages`, in the shared platform
  database, so the two adapters never race to create the same table.
- `IRetryPolicyProvider.GetRetryPolicy(string, Exception, string?)` makes a call with a `null` literal
  as the second argument ambiguous; pass `(string)null` or `(Exception)null`.
- Verified end to end: every package packed to a local feed; a scratch adapter repository that referenced
  only that feed and nuget.org ran the emulator, topology, Resolver, WebApp and an adapter under
  `aspire run`, and a published event was Completed in the WebApp.
