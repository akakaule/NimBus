# Simulate traffic (WebApp)

Status: implemented (2026-09-23) on branch `feat/simulate-traffic`; revision 2 of the plan.

Implementation notes: the Resolver-hosting emulator tests live in `NimBus.WebApp.Tests` (the
fallback project was not needed). The release-notes line for the new public SDK API (Tasks 1
and 2) goes in the next minor's GitHub release; the repo keeps no release-notes file.

Mockup: [NimBus Simulate Traffic](https://claude.ai/artifact/VSXHouNjRsAbyLKoXvse2a) —
three boards: Admin → Simulation tab, Simulate page (running), failure-mode editor.

Revision 2 changes (review feedback): public deferred-processor host (Task 1), explicit
simulator ownership with External as the default (Decision 7), hard production exclusion
(Decision 4), bounded Pause/Stop/shutdown with cancellation-aware publishing
(Decision 10), complete config bounds with a global traffic ceiling (Decision 11),
and tests that exercise the real `SimulatedEndpointHost`, plus emulator coverage (Tasks 4, 11).

## Goal

A site Owner can switch on a **simulate mode** from Admin. It adds a **Simulate** page
that drives synthetic traffic through the real platform:

- a simulated **publisher** for every endpoint that produces event types, publishing
  schema-valid generated payloads at a configurable rate;
- a simulated **handler** for every consuming endpoint the Owner has explicitly handed
  to the simulator, with a per-endpoint **failure mode** (healthy, random failures,
  transient, slow, poison, no handler);
- start / pause / stop, a speed multiplier, scenario presets, counters and a live feed.

Publishers and handlers are hosted **inside the WebApp process** (so the feature also
works on the deployed dev site, where there is no Aspire). Simulation is allowed in
dev by default and can never run in production.

## Non-goals (v1)

- No storage changes. Simulation config and runtime state live in the WebApp process
  (Decision 3). No new message-store interface members, so no provider/conformance work.
- No cleanup of tracking-store rows. Simulated messages stay visible in Messages, Flow,
  Insights etc.; that is the point.
- No SignalR push for the feed; the page polls.
- No publish patterns beyond "steady with jitter" (the mockup's burst / follows-parent
  patterns are deferred).
- No change to `AddNimBusSubscriber`'s one-endpoint-per-process rule.

## Decisions

1. **In-process hosting, composed from public SDK types.** `AddNimBusSubscriber` allows
   one endpoint per process (`ISubscriberClient` is a non-keyed singleton —
   `src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs:141-173`). For each
   simulator-owned endpoint, `SimulatedEndpointHost` composes the chain the SDK factory
   builds (`ServiceCollectionExtensions.cs:275-360`):
   `EventHandlerProvider` → `StrictMessageHandler` (widest ctor, with a
   `DefaultRetryPolicyProvider` and the simulator's failure classifier) →
   `ResponseService(InstrumentSender(new Sender(client.CreateSender(endpoint))))` →
   `ServiceBusAdapter` → `NimBusReceiverHostedService` (topic = subscription = endpoint
   id, as in `samples/AspirePubSub/AspirePubSub.WarehouseSubscriber/Program.cs`), plus a
   `DeferredMessageProcessorHostedService` so resubmits and skips replay deferred
   messages and unblock sessions. That service is `internal sealed` today
   (`src/NimBus.SDK/Hosting/DeferredMessageProcessorHostedService.cs:21`, options record
   likewise); Task 1 makes both public. Handlers are registered string-keyed via
   `EventHandlerProvider.RegisterHandler(string eventTypeId, Func<IEventJsonHandler>)`
   with a `DelegateEventJsonHandler`; the lookup is keyed by event-type id, so this also
   serves compiled event types. The host starts and stops these instances itself; they
   are never registered as host services.
   *Risk:* the composition can drift from the SDK factory. The guard is testing the
   host itself (Tasks 4 and 11), not a re-built copy of it. Extracting a public SDK
   factory is a follow-up.

2. **Publishing** uses `FakeEventPayloadGenerator.Generate(IEventType)`, deserializes to
   the CLR type and runs `TryValidate()` exactly like Compose
   (`EventImplementation.PostComposeNewEventAsync`). It sends through a
   cancellation-aware overload (Decision 10).

3. **State is in-memory, per WebApp instance.** Enabled flag, ownership, config and
   counters live in a singleton `SimulationService`. The enabled flag and owned
   endpoints start from config on every restart; a restart stops any run. Dev runs on a
   single-instance B1 plan; on a scaled-out plan each instance would run its own
   simulation. This is documented, not solved.

4. **Environment gate with a hard production exclusion.** Simulation is allowed only
   when all of these hold:
   - the `Environment` setting (set per environment by `deploy/bicep/deploy.webapp.bicep`;
     `appsettings.json` default `"dev"`) is present and non-blank. Missing or blank
     means **blocked**: fail closed;
   - it is in `NimBus:Simulation:AllowedEnvironments` (default `["dev", "development"]`,
     matching `isDevelopmentEnvironment` in the bicep);
   - it is **not** a production name. `SimulationEnvironmentPolicy.ProductionNames` is a
     hard-coded, case-insensitive set (`prod`, `production`, `prd`, `live`) that no
     setting can override.

   A production name in `AllowedEnvironments` fails options validation (`ValidateOnStart`)
   with a clear message, so the misconfiguration is loud rather than silently permissive.
   The runtime check blocks production names regardless, as a second layer. The gate
   deliberately does **not** use `IWebHostEnvironment.IsProduction()`: App Service runs
   the deployed dev site with the default `ASPNETCORE_ENVIRONMENT=Production`.
   Outside the gate, every simulation endpoint except `GET` returns 403, and `GET`
   reports `allowed: false` with a reason (`EnvironmentMissing`, `NotAllowed`,
   `Production`).

5. **Access = site Owner only**, the same `IsSiteOwnerAsync()` check as the other Admin
   endpoints (`Controllers/ApiContract/AdminImplementation.cs`). The sidebar item uses
   the Admin `canManageSite` gating (`components/sidebar.tsx` `useVisibleNav`) **and**
   requires `allowed && enabled` from the status endpoint.

6. **Marking simulated traffic.** `PublisherClient` has no user-property hook, so
   simulated messages carry the session and correlation id prefix `sim-`
   (`NimBus:Simulation:SessionPrefix`). All event types of a publisher share a rotating
   pool of `SessionPoolSize` session keys, so a failure blocks later messages on the
   same session. The mockup's "Message tag" becomes a read-only "Session prefix".

7. **Simulator ownership is explicit; External is the default.** A simulated handler
   reads the endpoint's real subscription and would compete with a running real
   subscriber. Heartbeat probing is off by default (`HeartbeatSettings.Enabled` defaults
   to `false`), so a missing heartbeat does not prove a subscriber is absent. Therefore:
   - every consuming endpoint defaults to **External**: the simulator publishes to it
     but never hosts its handler;
   - an endpoint becomes **Simulated** only by explicit ownership, either from config
     (`NimBus:Simulation:OwnedEndpoints`) or by an Owner on the Admin tab. Taking
     ownership is audited;
   - `OwnedEndpoints` is validated at startup against `IPlatform` consumers; an unknown
     id fails `ValidateOnStart`;
   - heartbeat data is advisory only. When probing is on and an owned endpoint answered
     recently, the Admin tab and Simulate page show a "live instance may be competing"
     warning; they never change ownership automatically;
   - the AppHost switch `NIMBUS_SIMULATION=true` supplies the known defaults. It leaves
     out the `publisher`, `subscriber` and `warehouse-subscriber` sample projects and sets
     `NimBus__Simulation__EnabledByDefault=true`,
     `NimBus__Simulation__OwnedEndpoints__0=BillingEndpoint` and `__1=WarehouseEndpoint`.
     The list is never empty, so the config binder cannot drop it.

8. **Failure modes** run inside the simulated handler delegate:

   | Mode | Behaviour | Pipeline effect |
   |---|---|---|
   | Healthy | delay 20–80 ms, complete | Completed |
   | Random | throw `SimulatedTransientException` with probability *p* | retried per policy, then Failed + session blocked |
   | Transient | throw while `context.RetryCount < N`, then complete | retries, then Completed |
   | Slow | delay `latencyMinMs`–`latencyMaxMs`, complete | lock renewal / throughput |
   | Poison | throw `SimulatedPermanentException` | the simulator's `IFailureDispositionClassifier` returns `DeadLetter` |
   | NoHandler | throw `EventHandlerNotFoundException` (`NimBus.Core.Messages.Exceptions`) | Unsupported response (`StrictMessageHandler.cs:207`) |

   Modes can be scoped to a subset of the endpoint's event types and to a session glob.
   Each uses a configurable exception message (stable text, so Insights grouping can be
   tested) and can revert to Healthy after a duration. Every delay observes the
   delivery's cancellation token. The retry policy is a fixed simulator policy
   (`DefaultRetryPolicyProvider`: `SimulatorMaxRetries` = 3, 5 s fixed backoff), which
   Transient's *N* is bounded by (Decision 11).

9. **Counters and feed are handler-side truth.** The simulator knows only what it
   published and what its handlers did: completed, threw transient, threw permanent,
   unsupported, with attempt number and latency. The mockup's "Failed" / "Dead-lettered"
   labels become "Threw (attempt n)" / "Poisoned". The feed links to Messages filtered
   by the `sim-` prefix, where the Resolver status is authoritative. Counters: Published,
   Handled OK, Handler errors, Poisoned, Publish errors, Abandoned sends, current
   throughput, and whether the ceiling is capping. The feed keeps the last 100 deliveries.

10. **Bounded Pause, Stop and shutdown.**
    - **Cancellation-aware publishing.** The overload Compose uses,
      `Publish(IEvent, sessionId, correlationId[, messageId])`, takes no token
      (`src/NimBus.SDK/PublisherClient.cs:107-115`). Task 2 adds
      `Publish(IEvent, string sessionId, string correlationId, string messageId, CancellationToken)`
      to `IPublisherClient`. It is additive: a default interface method that checks the
      token and delegates, so existing implementers keep compiling. `PublisherClient`
      implements it by passing the token to `ISender.Send(message, 0, cancellationToken)`.
      No existing overload changes.
    - **Owned senders.** The Azure SDK send may not honour cancellation promptly, since
      an AMQP send can wait out its `TryTimeout`. So each simulated publisher owns its
      `ServiceBusSender`: `new PublisherClient(InstrumentSender(new Sender(client.CreateSender(endpoint))), endpoint)`
      over the shared `ServiceBusClient`. On deadline expiry the simulator disposes that
      sender, which aborts in-flight sends, without touching the WebApp's shared client.
    - **Deadlines** (options, validated):
      - Pause: cancel publisher loops and stop receivers, bounded by `PauseDeadlineSeconds`
        (default 10).
      - Stop: cancel publishers (same deadline). Then drain receivers for up to
        `DrainSeconds` (default 30, range 0–120), then stop them within
        `StopDeadlineSeconds` (default 15).
      - Shutdown: `SimulationLifetimeService.StopAsync` skips the drain and does one
        bounded stop, capped by the host's shutdown token, so it cannot hold up the
        WebApp's own shutdown.

      When a deadline passes, the remaining loop tasks are abandoned (not awaited),
      their senders disposed, `Abandoned sends` incremented, a warning logged, and the
      state still reaches Paused/Stopped. Receivers are stopped through
      `BackgroundService.StopAsync(token)`, which returns when the token fires even if
      the processor's own stop is still finishing.
    - **Serialized transitions.** One `SemaphoreSlim` guards every transition. There are
      transient states `Pausing` and `Stopping`; Start while one of those is in progress
      returns 409. A new Start always builds fresh publishers, senders and hosts, and
      never reuses abandoned ones.
    - The mockup's "Stop & clean up" becomes "Stop"; leftover backlog is purged with the
      existing Admin purge. Auto-stop (Decision 11) calls Stop.

11. **Configuration bounds and a global traffic ceiling.**

    | Setting | Range | Default |
    |---|---|---|
    | `speed` | one of 0.5, 1, 2, 5, 10, 20 | 1 |
    | `autoStopMinutes` (settings) | 1–240 (no "never") | 60 |
    | `rateCeilingPerMinute` (settings) | 1–`MaxRateCeilingPerMinute` | 600 |
    | `MaxRateCeilingPerMinute` (options, deploy-time) | 1–6000 | 1200 |
    | per event type `ratePerMinute` | 1–`rateCeilingPerMinute` | 10 |
    | Random `rate` | 1–100 % | 30 |
    | Transient `failAttempts` | 1–`SimulatorMaxRetries` (3), so the last attempt can succeed | 2 |
    | `latencyMinMs` / `latencyMaxMs` | 0–10,000, min ≤ max | 800 / 2,500 |
    | `exceptionMessage` | 1–512 chars | per mode |
    | `sessionPattern` | ≤ 64 chars, `*`/`?` glob only | none |
    | `eventTypeIds` scope | non-empty subset of the endpoint's consumed types | all |
    | `revertAfterMinutes` | 1–240 or null | null |
    | `SessionPoolSize` (options) | 1–1,000 | 40 |

    The **ceiling applies across the whole simulation**, not per publisher: all
    publisher loops draw from one shared token bucket (`SimulationRateLimiter`,
    `rateCeilingPerMinute` tokens per minute, burst ≤ 1 s of tokens). Each loop's
    effective rate is `ratePerMinute × speed`. When the sum exceeds the ceiling, the
    config is still accepted, throughput is capped, the loops share the bucket
    fairly, and the status reports `capped: true`. The UI shows "capped at N/min". Out-of-range
    values return 400 with every violation listed, and options out of range fail
    `ValidateOnStart`.

## API (`src/NimBus.WebApp/api-spec.yaml`, NSwag-generated — edit the spec only)

All under `/api/admin/simulation`, site Owner only.

| Method | Path | Body / result |
|---|---|---|
| GET | `/api/admin/simulation` | `SimulationStatus`: `allowed`, `blockedReason`, `environment`, `enabled`, `state` (`Stopped`/`Running`/`Pausing`/`Paused`/`Stopping`), `startedAt`, `autoStopAt`, `capped`, `settings`, `config`, `counters`, `recent[]`, per-endpoint `liveInstanceWarning` |
| PUT | `/api/admin/simulation/settings` | `SimulationSettings`: `enabled`, `autoStopMinutes`, `rateCeilingPerMinute`, `ownedEndpoints[]` |
| PUT | `/api/admin/simulation/config` | `SimulationConfig`: `speed`, `publishers[]` (`endpointId`, `eventTypes[]`: `eventTypeId`, `enabled`, `ratePerMinute`), `subscribers[]` (`endpointId`, `failure`: `mode`, `rate`, `failAttempts`, `latencyMinMs`, `latencyMaxMs`, `exceptionMessage`, `eventTypeIds[]`, `sessionPattern`, `revertAfterMinutes`) |
| POST | `/api/admin/simulation/start` | 409 when disabled, not allowed, or not Stopped/Paused |
| POST | `/api/admin/simulation/pause` | |
| POST | `/api/admin/simulation/stop` | |

The config is replaced whole; scenario presets are client-side and applied with one PUT.
Validation follows Decision 11, plus: publisher event types must be produced by that
endpoint, and subscriber entries must name owned endpoints that consume those types
(`IPlatform`). A running simulation picks up config changes on its next publish or
delivery. Ownership changes take effect at the next Start; while running, the PUT
returns 409.

Audit (`MessageAuditType`, appended at the end — Cosmos persists the numeric value):
`UpdateSimulationSettings` (includes ownership changes), `ControlSimulation` (action in
Data), `UpdateSimulationConfig`. They are written through `IAuditLogService`, including
`accessDenied: true` on 403.

## Tasks

TDD per task: write the failing test first. Build with `dotnet build src/NimBus.sln -c Release`
(CS warnings are errors in Release). Test files start with
`#pragma warning disable CA1707, CA2007`.

### 1. SDK: public deferred-processor host
- `DeferredMessageProcessorHostedService`: `internal sealed` → `public sealed`.
  `DeferredMessageProcessorHostedServiceOptions`: `internal sealed record` → `public sealed record`.
- Constructor validation in the service (today only
  `AddNimBusDeferredProcessorHostedService` validates, at
  `ServiceCollectionExtensions.cs:537-542`): null checks (existing), plus blank
  `TopicName`/`SubscriptionName` → `ArgumentException`, and `MaxConcurrentCalls < 1` →
  `ArgumentOutOfRangeException`. The extension keeps its checks.
- Docs: XML docs on both types become public-API docs. Fix the summary: it says the
  service is registered by `AddNimBusSubscriber`, but it is registered by
  `AddNimBusDeferredProcessorHostedService`. Add a "hosting it yourself" section to
  `docs/throughput-tuning.md` (which already links the options type) covering direct
  construction and the single-concurrency ordering warning. Add a release-notes line:
  new public API, minor version (`docs/versioning.md`).
- Tests (`tests/NimBus.SDK.Tests/DeferredMessageProcessorHostedServiceTests.cs`, next to
  `DeferredProcessorRegistrationTests.cs`):
  - direct construction succeeds; each invalid argument throws the specified exception;
  - lifecycle, with a mocked `ServiceBusClient`/`ServiceBusProcessor` (both have virtual
    members): `StartAsync` creates a processor for the given topic/subscription with
    `AutoCompleteMessages = false` and the configured concurrency; `StopAsync` stops and
    disposes it; `StopAsync` with an already-cancelled token returns promptly;
  - start → stop → start on a fresh instance works;
  - the existing DI registration test still resolves the same type.

### 2. SDK: cancellation-aware publish overload
- `IPublisherClient.Publish(IEvent, string sessionId, string correlationId, string messageId, CancellationToken)`
  as a default interface method (`ThrowIfCancellationRequested`, then delegate).
  `PublisherClient` overrides it and passes the token to `_sender.Send(message, 0, ct)`.
- Tests (`NimBus.SDK.Tests`): the token reaches `ISender.Send`; a pre-cancelled token
  throws without sending; the old overloads' behaviour is unchanged; the default method
  works for a minimal custom implementer.

### 3. Options, environment policy, audit types
- `src/NimBus.WebApp/Services/Simulation/SimulationOptions.cs`, bound from
  `NimBus:Simulation`: `EnabledByDefault`, `AllowedEnvironments`, `OwnedEndpoints`,
  `AutoStopMinutes`, `MaxRateCeilingPerMinute`, `RateCeilingPerMinute`, `SessionPrefix`,
  `SessionPoolSize`, `DrainSeconds`, `PauseDeadlineSeconds`, `StopDeadlineSeconds`.
  `IValidateOptions` enforces Decision 11 ranges, rejects production names in
  `AllowedEnvironments`, and rejects `OwnedEndpoints` that are not `IPlatform`
  consumers; registered with `ValidateOnStart`.
- `SimulationEnvironmentPolicy.Evaluate(string? environment)` →
  `(bool Allowed, BlockedReason?)` per Decision 4.
- Append the three `MessageAuditType` members
  (`src/NimBus.MessageStore.Abstractions/MessageAuditEntity.cs`).
- `appsettings.json`: a `NimBus:Simulation` section with defaults (`OwnedEndpoints` omitted).
- Tests: dev/Development allowed; missing, blank and whitespace environments blocked
  with `EnvironmentMissing`; prod/Production/PRD/live blocked with `Production` even
  when listed; `test` blocked unless listed; options validation fails on a production
  name in the allowlist, an unknown owned endpoint, and each out-of-range option.

### 4. Failure behaviour and `SimulatedEndpointHost`
- `SimulatedFailureMode.cs`, `SimulatedExceptions.cs`, `SimulatedFailureClassifier.cs`
  (permanent → `DeadLetter`, else `Retry`), `SimulatedHandlerBehavior.cs` (Decision 8;
  injectable `Random` and `TimeProvider`; every delay honours the token; records a
  `SimulatedDelivery` to the feed sink).
- `SimulatedEndpointHost.cs` has two layers:
  - `BuildMessageHandler(...)` returns the composed `IMessageHandler`. This is the
    production code path, not a test helper.
  - `CreateAsync/StartAsync/StopAsync/DrainAsync` wrap that handler in the
    `ServiceBusAdapter` + `NimBusReceiverHostedService` + `DeferredMessageProcessorHostedService`.
- Unit tests call `SimulatedEndpointHost.BuildMessageHandler` and drive it through
  `NimBus.Testing`'s in-memory transport, which composes `IMessageHandler` directly
  (`ServiceCollectionExtensions.cs:433`). They never re-assemble the chain themselves.
  Per mode, assert the Resolver response (Resolution, retry scheduling then Error,
  dead-letter, Unsupported), scoping by event type and session glob, revert-after with
  a fake `TimeProvider`, and a random-rate tolerance on a seeded run.
- Lifecycle tests against the real `SimulatedEndpointHost` with a mocked
  `ServiceBusClient`: start creates the session processor and the deferred processor
  for the endpoint; stop disposes both within the deadline; a Slow-mode handler blocked
  in `Task.Delay` is cancelled by stop.

### 5. Publisher loops and the global ceiling
- `SimulationRateLimiter.cs` — the shared token bucket (Decision 11), on `TimeProvider`.
- `SimulatedPublisher.cs` — one loop per enabled (endpoint, event type). The loop waits
  its own interval (`60 / (rate × speed)` s ± 20 % jitter), then a limiter token, then
  generates and publishes through the Task 2 overload with the loop's token. It owns its
  sender (Decision 10). Validation and publish failures are counted and logged, never
  fatal to the loop.
- `IPublisherClient` creation goes through a factory interface so tests inject fakes.
- Tests (fake `TimeProvider`, fake `ISender`):
  - one loop publishes at rate × speed;
  - **several concurrent loops** whose summed rate exceeds the ceiling: total ≤ ceiling
    per minute, every loop makes progress (no starvation), `capped` reported;
  - summed rate below the ceiling is not throttled;
  - disabled types don't publish;
  - sessions stay within the pool and carry the prefix;
  - **stalled sends**: an `ISender` that honours cancellation → Pause returns promptly;
    an `ISender` that ignores cancellation and never completes → Pause returns within
    `PauseDeadlineSeconds`, the loop is abandoned, the sender is disposed,
    `Abandoned sends` increments, and a following Start publishes through fresh senders.

### 6. SimulationService (state machine)
- `ISimulationService` / `SimulationService` (singleton): discovery from `IPlatform`,
  settings/ownership/config, transitions (Decision 10), auto-stop, counters and the
  feed. Owned endpoints get a `SimulatedEndpointHost`; External ones get none.
  Heartbeat data is read (through `IServiceScopeFactory`, since `IHeartbeatService` is
  scoped) only for the advisory warning.
- `SimulationLifetimeService` (`IHostedService`): auto-stop timer; bounded stop on
  shutdown. Register it once, via `AddSingleton<IHostedService>(…)` (see the memory note
  on `AddHostedService` dedup).
- Tests (mocked hosts/publishers):
  - Stopped → Running → Pausing → Paused → Running → Stopping → Stopped;
  - Start refused (409) when disabled, not allowed, or mid-transition; concurrent
    Start/Stop calls are serialized;
  - disabling while running stops the run;
  - no endpoint is simulated without ownership, and a heartbeat never grants it;
  - ownership change while running → 409;
  - Stop honours drain then deadline with a host whose stop stalls;
  - shutdown with a stalled host completes within the host token;
  - config validation errors list every violation.

### 7. API
- Edit `api-spec.yaml` (paths + schemas above). Build with SPA/NSwag generation enabled
  (not `SkipSpaBuild=true`) so `ApiContract.g.cs` and
  `ClientApp/src/api-client/index.ts` regenerate.
- `Controllers/ApiContract/SimulationImplementation.cs` implementing the generated
  controller. Register it in `Startup.AddApiControllers` and the simulation services in
  a new `AddSimulation(services)` step.
- Tests (`tests/NimBus.WebApp.Tests`):
  - non-Owner → 403 plus a denied audit row;
  - each blocked reason → 403, and `GET` → `allowed:false` with that reason;
  - valid PUT/POST → audit row with Data;
  - out-of-range config → 400 listing every violation;
  - start while Stopping → 409.

### 8. Frontend — Admin tab
- `ClientApp/src/components/admin/simulation-settings.tsx`: enable switch, auto-stop,
  rate ceiling, read-only session prefix, environment-policy table (production rows
  shown as permanently blocked), and discovered endpoints. Each consuming endpoint has
  an explicit "Simulator owns this endpoint" checkbox, default off, with a confirmation
  explaining that the simulator then competes for its subscription; plus the advisory
  live-instance warning and "Open Simulate →". When blocked, show the reason and
  disable the controls.
- `pages/admin.tsx`: add the "Simulation" tab after "Failure intelligence".
- Tests: renders endpoints with ownership unchecked by default; checking asks for
  confirmation and sends `ownedEndpoints`; each blocked reason disables the controls.

### 9. Frontend — Simulate page
- `pages/simulate.tsx` + `components/simulate/`: `status-strip.tsx` (shows capped and
  abandoned sends), `scenario-bar.tsx` (presets over **owned** subscribers only),
  `speed-control.tsx` (the six allowed values), `publishers-card.tsx`,
  `subscribers-card.tsx` (External endpoints listed read-only, with a "handled by its
  own process" note), `failure-mode-dialog.tsx` (inputs clamp to Decision 11 ranges),
  `live-feed.tsx`. Poll `GET /api/admin/simulation` every 2 s while mounted and the tab is visible.
- Buttons are disabled during `Pausing`/`Stopping`.
- `app.tsx`: lazy route `/Simulate`. `components/sidebar.tsx`: "Simulate" under Manage
  with a state badge, shown only when `canManageSite` and `allowed && enabled`; status is
  fetched only when `canManageSite`. A non-Owner or disabled direct visit renders the
  not-found page.
- Tests: sidebar gating; buttons per state, including the transient states; a failure
  mode change sends the whole config; presets touch only owned endpoints; the capped
  indicator renders; the feed renders outcomes.
- Run `npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and `run build` (not in
  parallel with `dotnet build`).

### 10. AppHost switch
- `src/NimBus.AppHost/Program.cs`: `NIMBUS_SIMULATION=true` (env or config) skips the
  `publisher`, `subscriber` and `warehouse-subscriber` projects and sets the WebApp
  settings from Decision 7. Default behaviour is unchanged.

### 11. Emulator end-to-end coverage (automated)
- Share the emulator launcher. Move the private `EmulatorProcess` out of
  `tests/NimBus.ServiceBusEmulator.Tests/SdkSmokeTests.cs:727` into
  `tests/NimBus.ServiceBusEmulator.Tests/Infrastructure/EmulatorProcess.cs` (internal).
  Link it into `NimBus.WebApp.Tests` as a `<Compile Include=… Link=…>`, and add a
  `ProjectReference` to `NimBus.ServiceBusEmulator` with
  `ReferenceOutputAssembly="false"`, so the emulator is built before the tests run it
  (`dotnet run --no-build`). `NIMBUS_SBEMULATOR_TEST_CS` keeps working as the override.
- `tests/NimBus.WebApp.Tests/Simulation/SimulationEmulatorTests.cs`,
  `[TestCategory("Emulator")]`, `[Timeout]` on each test. The fixture:
  - starts the emulator and provisions `PlatformConfiguration` with
    `ServiceBusTopologyProvisioner` (emulator-safe TTLs);
  - hosts the Resolver in-process the way `samples/AspirePubSub/AspirePubSub.ResolverWorker/Program.cs`
    does (`AddResolver()` + a `NimBusReceiverHostedService` on `Resolver`), over the
    in-memory message store from `NimBus.Testing`;
  - runs the real `SimulationService` with BillingEndpoint and WarehouseEndpoint owned.

  If referencing `NimBus.Resolver` (a Functions project) from `NimBus.WebApp.Tests`
  causes build problems, put this class in a new `tests/NimBus.Simulation.EmulatorTests`
  project instead. Same tests.
- Tests:
  1. **Happy path**: Start, then within the timeout the store shows Completed rows on
     `sim-` sessions for both endpoints; Stop within its deadline.
  2. **Pause/resume**: Start, Pause. The publish counter is frozen, no new store rows
     appear on a probe interval, and messages published before the pause but not yet
     handled stay in the subscription (peek). Resume: they are handled and publishing
     continues.
  3. **Resubmit → deferred replay**: Warehouse set to Random 100 % scoped to one
     session (via `sessionPattern`), so its failures exhaust retries and block it. Wait until the session is blocked and later messages on it are
     Deferred. Switch the mode to Healthy, then resubmit the failed message with
     `ManagerClient.Resubmit`. Assert the resubmission completes, the deferred processor
     replays the deferred messages in order, and every row on that session ends
     Completed.
  4. **Stop drains**: messages in flight at Stop are handled during the drain window,
     and nothing remains locked afterwards.
- These run in the regular `dotnet test src/NimBus.sln` CI step: the emulator is a
  local process and needs no secrets. Timeouts are generous (≤ 120 s per test).

### 12. Docs
- `docs/webapp-simulate.md`: what it does; config keys and bounds; the environment gate
  and production exclusion; explicit ownership and why External is the default; the
  AppHost switch; per-instance state; bounded stop behaviour; how to purge leftovers.
  Link it from the WebApp README.

## Verification

- `dotnet build src/NimBus.sln -c Release` and `dotnet test src/NimBus.sln -c Release --no-build`,
  including the new SDK tests and the emulator category (Task 11).
- Frontend `test:ci` and `build`.
- Manual, local stack with `NIMBUS_SIMULATION=true` and the SB emulator: enable in Admin,
  Start, then check Messages shows `sim-` sessions; Random 30 % on Warehouse → Failed
  rows and blocked sessions, and Resubmit unblocks one; Poison → dead-lettered rows;
  Pause/Resume; Stop drains; the nav badge shows idle.
- Manual, default AppHost (samples running): no endpoint is owned, the Simulate page
  lists Billing/Warehouse as External, and simulated publishes are handled by the
  sample processes.
- Manual: set `Environment=prod` locally → GET reports `Production`, controls disabled.
  Add `prod` to `AllowedEnvironments` → the WebApp fails at startup with the validation
  message.
- Screenshot of the Simulate page and Admin tab for the PR (AGENTS.md WebApp rule).
- Report skipped live SQL/Cosmos conformance runs explicitly (no storage change is
  expected to need them).

## Open questions

1. Is per-instance, non-persistent state acceptable (Decision 3), or should the enabled
   flag, ownership and config persist in the selected store like the
   failure-intelligence settings? Persisting them adds a storage record and migration work.
2. Should `NIMBUS_SIMULATION=true` become the AppHost default, replacing the sample
   processes?
3. Should `test` be in the default allowed list, or stay opt-in per deployment?
