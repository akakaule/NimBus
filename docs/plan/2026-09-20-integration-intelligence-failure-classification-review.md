# Review: Spec 033 implementation plan (2026-09-20)

Reviewed: `docs/plan/2026-09-20-integration-intelligence-failure-classification.md` against spec 033 (post-pivot, §23.1) and the code on `master` at 460e6b0.

Verdict: **sound, ready to execute after six corrections.** Update 2026-09-20, later the same day: all six corrections and the smaller items are now folded into the plan (its "Review disposition" line records this), so the plan is ready for implementation. The task sequence, the decision table and the verification section are right. Every file path the plan cites exists. The plan's correction of the spec (the disabled-controller provider lives in `NimBus.WebApp`, not in the Identity extension) is accurate. Three findings change how a task is done; the rest are wording.

## Findings, most important first

### 1. Rate limiting: an attribute on the extension controller bypasses the kill switch (Task 9)

`RateLimitPoliciesConvention.Apply` returns early when `RateLimitOptions.Enabled` is false and leaves the policies registered but unattached. That is the operator kill switch. The plan says "keep rate-limit types out of the extension's WebApp dependency graph", which steers the implementer toward `[EnableRateLimiting("...")]` on the extension's POST. An attribute attaches metadata regardless of the convention, so the kill switch would stop working for that one route.

Fix: add a `RateLimitPolicyNames.Intelligence` constant and a `PolicyFor` branch for the extension's controller type in `RateLimitPoliciesConvention` (the WebApp already references the extension, so this is a plain type check), register the policy in `RateLimitingServiceCollectionExtensions`, and extend `RateLimitEndpointMetadataTests` in both directions. No rate-limit attribute in the extension.

### 2. Storage connection: do not re-implement key precedence in the extension (Tasks 7 and 8)

The plan has the extension "match the WebApp's resolved SQL configuration, including supported connection-key precedence and authentication" and "use the same resolved account/database and credential mode as the platform without referencing the Cosmos message-store project". That means copying `SqlConnection` → `ConnectionStrings:sqlserver` → `SqlServerConnection` and the Cosmos endpoint / `DefaultAzureCredential` / connection-string ladder into a second place, with drift risk and the "avoid accidentally targeting a different database" test burden the plan itself lists.

What exists today:

- `AddCosmosDbMessageStore` registers `CosmosClient` as a DI singleton. The extension can resolve it directly (Microsoft.Azure.Cosmos is already a permitted reference) and inherits managed identity, emulator gateway mode and everything else for free.
- `SqlServerMessageStoreOptions` (ConnectionString, Schema) and `CosmosDbMessageStoreOptions` (database name) live in the store projects, which the WebApp references.

Fix: treat the connection like authorization and audit. The extension declares a small host contract (SQL connection string, Cosmos database name), and the WebApp adapter fills it from the registered store options. The extension never reads connection keys from `IConfiguration`. Update Task 1's host-contract list and drop the precedence-matching bullets from Tasks 7 and 8.

### 3. Activation order vs MVC, and auth-branch coverage (Task 3)

`AddControllers()` and the Identity disabled-provider are configured in `AddAuthenticationStack`, the first `ConfigureServices` step, and only inside the `!hasNimBusIdentity` branch. `AddStorage` runs five steps later, so `AddNimBusIntegrationIntelligence` attaches its feature provider by calling `services.AddControllers().ConfigureApplicationPartManager(...)` a second time. That is safe (MVC reuses the existing `ApplicationPartManager`), but the plan should say so, because the obvious alternative of hooking into the auth ladder would tie the extension to a branch.

Two consequences for Task 3's tests:

- "Disabled routes all 404" must run under every auth branch (none, Entra, Identity-only, dual), because the precedent it mirrors is branch-specific.
- The extension is a plain class library with `FrameworkReference Microsoft.AspNetCore.App`. The Web SDK emits an `[ApplicationPart]` attribute for referenced assemblies that reference MVC, so its controllers are discovered on every build exactly like Identity's. The 404 test is what proves the provider actually prunes them; do not rely on "no registration means no route".

### 4. `ProviderNotConfigured` leaves the classification routes undefined (Tasks 3 and 9)

Spec §15 says only status handling is registered in that state. Neither the spec's §14 table nor the plan's error contract says what `GET`/`POST …/classification` return then. Task 3's "remove unused controllers with the feature provider" hints at 404; Task 9's error codes do not list the case.

Fix: pin it in Task 1's contract freeze. Recommended: prune the classification controller with the same feature provider whenever status is not `Ready`, so the SPA rule stays "404 means hide" and the status endpoint is the only route that exists in the degraded state. Add the row to the API table when the spec is amended.

### 5. The "process-local Node 22 local-storage workaround" does not exist (Verification)

No `--no-experimental-webstorage`, `localstorage-file` or `NODE_OPTIONS` setting exists anywhere in the repository, and there are no theme tests. Vitest runs on jsdom via `vite.config.ts`. Remove the sentence. The commands themselves are right: the `test` script is bare `vitest`, so `npm test -- --run` is correct, and `npm run test:ci` is the junit variant.

### 6. `artifacts/` publish output is not git-ignored (Verification)

Only `*.dll` and `*.nupkg` are ignored, so `dotnet publish … -o artifacts/intelligence-webapp` leaves untracked JSON, static assets and `web.config` beside the `git diff --check` step. Either add `artifacts/` to `.gitignore` in Task 11 (CI already publishes to `./artifacts`, so that is consistent) or point `-o` at the session scratchpad.

## Smaller corrections

- **Retention (decision table, Task 7).** Name the real deletion paths in `AdminService.Purge.cs` so the retention decision is costed against all of them: `PurgeSessionAsync`, `PurgeSubscriptionAsync`, `DeleteEventAsync`, `DeleteAllEventsAsync`, `DeleteMessagesByToAsync`, `DeleteByStatusAsync` and `DeleteDeadLetteredAsync`. (An earlier version of this review listed only the two `Purge*` methods; that was an incomplete grep.)
- **In-memory store location.** Spec §5 lists the in-memory store as extension content; the plan puts it in the test project (Task 6). Either is fine, but pick one and amend the other document. If a custom host is expected to run the conformance suite, the store belongs in the extension.
- **Host adapter for audit.** `IAuditLogService.LogAuditAsync` takes `HttpContext` and resolves the actor from it. The extension's "current actor" contract should therefore be `HttpContext`-free and the WebApp adapter should read `IHttpContextAccessor`. Task 1 says this only implicitly.
- **Host test project settings.** The plan says the new test project "must carry the host test build settings if it references WebApp". Concretely: `Sdk="Microsoft.NET.Sdk.Web"`, `SkipSpaBuild=true`, `StaticWebAssetsEnabled=false`, plus a reference to `NimBus.Extensions.Identity` (WebApp.Tests carries all four).
- **CLAUDE.md.** While Task 11 adds the project line, fix the WebApp frontend section too: the installed stack is React 19, React Router 7 and Vite 8, not React 18 and Router v6. The plan already notes the docs are stale.

## Claims verified as correct

| Plan claim | Evidence |
|---|---|
| `IdentityControllersDisabledFeatureProvider` lives in `NimBus.WebApp` | `src/NimBus.WebApp/IdentityControllersDisabledFeatureProvider.cs`, attached in `Startup.cs:284` |
| `IEndpointAuthorizationService`, `IAuditLogService`, `AccessRole` are WebApp types, so the extension cannot reference them | `src/NimBus.WebApp/Services/` (`CurrentUserAccess.cs` holds `AccessRole`); Identity has no NimBus project references, confirming extensions do not depend on the host |
| Rate-limit policies attach through a controller convention | `RateLimiting/RateLimitPoliciesConvention.cs`, `PolicyFor(controller, action)` |
| `api-gen.nswag` outputs `Controllers/ApiContract.g.cs` and `ClientApp/src/api-client/index.ts`; three `auditType` enum lists | `api-gen.nswag` lines 72 and 125; `api-spec.yaml` |
| `MessageAuditType` persists numerically, `GrantRole`/`RevokeRole` appended last | `src/NimBus.MessageStore.Abstractions/MessageAuditEntity.cs:61` |
| `cosmosDB.bicep` documents that Entra data-plane RBAC cannot create containers | lines 76 to 94, `sharedContainers` array |
| CI has SQL Server and Cosmos emulator services and a Cosmos "must not skip" gate | `.github/workflows/dotnet.yml` lines 17 to 83 |
| `nuget-publish.yml` packs the whole solution and publishes the WebApp payload | lines 48 to 66 |
| `NIMBUS_SQL_TEST_CONNECTION`, `NIMBUS_COSMOS_TEST_{CONNECTION,ENDPOINT,KEY,GATEWAY,REQUIRED}` | grep over `tests/` |
| `SkipSpaBuild` is a real MSBuild switch | `NimBus.WebApp.csproj` lines 113 to 127 |
| ADR-016 is free | `docs/adr/` ends at 015 (011 is a historical gap) |
| `IMessageTrackingStore.GetMessage(eventId, messageId)`, `GetEvent(endpointId, eventId)`, `GetEventHistory(eventId)` | `IMessageTrackingStore.cs` lines 101, 171, 172 |
| `IEventJsonMasker.TryCollectSensitiveValues` exists; `Mask` honours annotation modes | `IEventJsonMasker.cs:43`, `EventJsonMasker.cs` |
| `ResponseService.FormatDeadLetterDescription` uses the exception's string form | `ResponseService.cs:151-155` |
| `NimBusActivitySources` naming precedent | `src/NimBus.Core/Diagnostics/NimBusActivitySources.cs` |
| `npm run lint` exists | `package.json` scripts: `lint`, `test`, `test:ci`, `build` |
| Central package management is off | `Directory.Packages.props`: `ManagePackageVersionsCentrally=false` |

## What the plan gets right that is worth keeping

- The decision table names every spec gap (module switch semantics, allow-list, retention, durable-protocol details) and gates dependent code on resolving it, instead of choosing silently.
- Refusing dynamic assembly loading, licensing and deploy flags matches §23.1 exactly.
- Task 6 writes the conformance suite once against an in-memory store and reuses it for SQL and Cosmos, with barriers rather than sleeps.
- The two-host race tests in Task 9 are the only way to prove the reservation protocol; keeping them mandatory and refusing emulator skips as success is correct.
- Release-configuration builds throughout, which is where CS8767 and the other CS-only promotions bite.
