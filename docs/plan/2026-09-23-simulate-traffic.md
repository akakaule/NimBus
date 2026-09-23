# Simulate traffic (WebApp)

Status: planned (2026-09-23). Not started.

Mockup: [NimBus Simulate Traffic](https://claude.ai/artifact/VSXHouNjRsAbyLKoXvse2a) —
three boards: Admin → Simulation tab, Simulate page (running), failure-mode editor.

## Goal

A site Owner can switch on a **simulate mode** from Admin. It adds a **Simulate** page
that drives synthetic traffic through the real platform:

- a simulated **publisher** for every endpoint that produces event types, publishing
  schema-valid generated payloads at a configurable rate;
- a simulated **handler** for every endpoint that consumes event types, with a
  per-endpoint **failure mode** (healthy, random failures, transient, slow, poison,
  no handler);
- start / pause / stop, a speed multiplier, scenario presets, counters and a live feed.

Publishers and handlers are hosted **inside the WebApp process** (so the feature also
works on the deployed dev site, where there is no Aspire). Simulation is allowed in
dev by default and cannot be enabled in production.

## Non-goals (v1)

- No storage changes. Simulation config and runtime state live in the WebApp process
  (see Decision 3). No new message-store interface members, so no provider/conformance
  work.
- No cleanup of tracking-store rows. Simulated messages stay visible in Messages, Flow,
  Insights etc.; that is the point.
- No SignalR push for the feed; the page polls.
- No publish patterns beyond "steady with jitter" (the mockup's burst / follows-parent
  patterns are deferred).
- No change to `AddNimBusSubscriber`'s one-endpoint-per-process rule.

## Decisions

1. **In-process hosting, composed from public SDK types.** `AddNimBusSubscriber` allows
   one endpoint per process (`ISubscriberClient` is a non-keyed singleton —
   `src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs:141-173`). The simulator
   therefore does not use DI registration; for each simulated endpoint it composes the
   same chain the SDK factory builds (`ServiceCollectionExtensions.cs:275-360`), all
   of it public:
   `EventHandlerProvider` → `StrictMessageHandler` (widest ctor, with a
   `DefaultRetryPolicyProvider` and a simulator failure classifier) →
   `ResponseService(InstrumentSender(new Sender(client.CreateSender(endpoint))))` →
   `ServiceBusAdapter` → `NimBusReceiverHostedService` (topic = subscription = endpoint
   id, as in `samples/AspirePubSub/AspirePubSub.WarehouseSubscriber/Program.cs`), plus a
   `DeferredMessageProcessorHostedService` on `deferredprocessor` so resubmits/skips
   unblock sessions. The simulator starts and stops these instances itself
   (`StartAsync` / `StopAsync`); they are not registered as host services.
   Handlers are registered string-keyed via
   `EventHandlerProvider.RegisterHandler(string eventTypeId, Func<IEventJsonHandler>)`
   with a `DelegateEventJsonHandler`, so no CLR handler types are needed.
   *Risk:* the composition can drift from the SDK factory. Mitigation: a test that
   drives a simulated handler through the in-memory transport and asserts the
   same Resolver responses a DI-composed subscriber produces. Extracting a public
   SDK factory is a follow-up, not part of v1.

2. **Publishing** uses `PublisherClient.CreateAsync(serviceBusClient, producerEndpointId)`
   and `Publish(IEvent, sessionId, correlationId)` — the same path Compose uses
   (`EventImplementation.PostComposeNewEventAsync`). Payloads come from the existing
   `FakeEventPayloadGenerator.Generate(IEventType)`, deserialized to the event's CLR
   type and checked with `TryValidate()` exactly like Compose.

3. **State is in-memory, per WebApp instance.** Enabled flag, publisher/handler config
   and counters live in a singleton `SimulationService`. The enabled flag starts from
   config (`NimBus:Simulation:EnabledByDefault`) on every restart; a restart stops any
   run. Dev is a single-instance B1 plan; on a scaled-out plan each instance would run
   its own simulation. This is documented, not solved.

4. **Environment gate is server-side.** `NimBus:Simulation:AllowedEnvironments`
   (default `["dev", "development"]`, matching `isDevelopmentEnvironment` in
   `deploy/bicep/deploy.webapp.bicep`) is compared to the existing `Environment`
   setting (`appsettings.json` default `"dev"`, set by bicep per environment). Outside
   the list every simulation endpoint except `GET` returns 403 and `GET` reports
   `allowed: false`. No bicep change is needed: `prod` is blocked by default and `test`
   is opted in by adding it to the list.

5. **Access = site Owner only**, same `IsSiteOwnerAsync()` check as the other Admin
   endpoints (`Controllers/ApiContract/AdminImplementation.cs`). The sidebar item uses
   the same `canManageSite` gating as Admin (`components/sidebar.tsx`
   `useVisibleNav`) **and** requires `allowed && enabled` from the status endpoint.

6. **Marking simulated traffic.** `PublisherClient` has no user-property hook, so
   simulated messages are identified by their session id and correlation id prefix
   `sim-` (config `NimBus:Simulation:SessionPrefix`). Sessions rotate through a pool
   of `SessionPoolSize` (default 40) keys shared by all event types of a publisher, so
   mixed event types land on the same sessions and a failure blocks later messages —
   session ordering is exercised without per-event-type relationships. The mockup's
   "Message tag" field becomes a read-only "Session prefix".

7. **Handler hosting vs. a live instance.** A simulated handler reads the endpoint's
   real subscription, so it would compete with a running real subscriber (locally,
   `AspirePubSub.Subscriber` = Billing and `WarehouseSubscriber` = Warehouse). Each
   consuming endpoint therefore has a hosting choice, **Simulated** or **External**.
   The default is External when `IHeartbeatService.GetOverviewAsync()` shows a response
   (`LastEndTime`) within 2 × the heartbeat interval, else Simulated; the Owner can
   override it on the Admin tab. External endpoints still receive the simulated
   publishes; they are just handled by the real process.
   The AppHost gets an opt-in `NIMBUS_SIMULATION=true` switch that leaves the sample
   publisher and both sample subscribers out, so the simulator owns the subscriptions
   (Decision 10).

8. **Failure modes** run inside the simulated handler delegate:

   | Mode | Behaviour | Pipeline effect |
   |---|---|---|
   | Healthy | delay 20–80 ms, complete | Completed |
   | Random | throw `SimulatedTransientException` with probability *p* | retried per policy, then Failed + session blocked |
   | Transient | throw while `context.RetryCount < N`, then complete | retries, then Completed |
   | Slow | delay `minMs`–`maxMs`, complete | lock renewal / throughput |
   | Poison | throw `SimulatedPermanentException` | the simulator's `IFailureDispositionClassifier` returns `DeadLetter` |
   | NoHandler | throw `EventHandlerNotFoundException` | Unsupported response (`StrictMessageHandler.cs:207`) |

   Each mode can be scoped to a subset of the endpoint's event types and to a session
   glob, uses a configurable exception message (stable text, so Insights grouping
   can be tested), and can revert to Healthy after a duration. The retry policy is
   `DefaultRetryPolicyProvider` with a short fixed policy (3 retries, 5 s) so retry
   behaviour is visible within a demo; this is not configurable in v1.

9. **Counters and feed are handler-side truth.** The simulator only knows what it
   published and what its handlers did (completed / threw transient / threw permanent /
   unsupported, attempt number, latency). It does not claim Resolver outcomes, so
   the mockup's "Failed" and "Dead-lettered" labels become "Threw (attempt n)" and
   "Poisoned". The feed links to Messages filtered by the `sim-` session prefix, which
   shows the real Resolver status. Counters: Published, Handled OK, Handler errors,
   Poisoned, current throughput. The feed is a ring buffer of the last 100 deliveries.

10. **Stop semantics.** Pause stops publisher loops and receivers; messages wait in
    the subscriptions. Stop stops publishers first, lets receivers drain for up to
    `DrainSeconds` (default 30), then stops them. Leftover backlog is purged with the
    existing Admin purge; the mockup's "Stop & clean up" button becomes "Stop".
    Auto-stop (`AutoStopMinutes`, default 60) and application shutdown both call Stop.

## API (`src/NimBus.WebApp/api-spec.yaml`, NSwag-generated — edit the spec only)

All under `/api/admin/simulation`, site Owner only.

| Method | Path | Body / result |
|---|---|---|
| GET | `/api/admin/simulation` | `SimulationStatus`: `allowed`, `environment`, `enabled`, `state` (`Stopped`/`Running`/`Paused`), `startedAt`, `autoStopAt`, `settings`, `config`, `counters`, `recent[]` |
| PUT | `/api/admin/simulation/settings` | `SimulationSettings`: `enabled`, `autoStopMinutes`, `rateCeilingPerMinute`, per-endpoint `handlerHosting` |
| PUT | `/api/admin/simulation/config` | `SimulationConfig`: `speed`, `publishers[]` (`endpointId`, `eventTypes[]`: `eventTypeId`, `enabled`, `ratePerMinute`), `subscribers[]` (`endpointId`, `failure`: `mode`, `rate`, `failAttempts`, `latencyMinMs`, `latencyMaxMs`, `exceptionMessage`, `eventTypeIds[]`, `sessionPattern`, `revertAfterMinutes`) |
| POST | `/api/admin/simulation/start` | 409 when not enabled or already running |
| POST | `/api/admin/simulation/pause` | |
| POST | `/api/admin/simulation/stop` | |

The config is replaced whole: scenario presets are client-side and applied with one
PUT. The server validates the config: event types must be produced (publishers) or
consumed (subscribers) by that endpoint per `IPlatform`, rates are 1..ceiling, rate
is 0..100, latencies are ordered, and the session glob has a bounded length.
Changes apply to a running simulation on the next publish or delivery.

Audit (`MessageAuditType`, appended at the end — Cosmos persists the numeric value):
`UpdateSimulationSettings`, `ControlSimulation` (action in Data),
`UpdateSimulationConfig`. They go through `IAuditLogService` as other Admin actions do,
including `accessDenied: true` on 403.

## Tasks

TDD per task: write the failing test first. Build with `dotnet build src/NimBus.sln -c Release`
(CS warnings are errors in Release). Test files start with
`#pragma warning disable CA1707, CA2007`.

### 1. Options, policy and audit types
- `src/NimBus.WebApp/Services/Simulation/SimulationOptions.cs` — bound from
  `NimBus:Simulation` (`EnabledByDefault`, `AllowedEnvironments`, `AutoStopMinutes`,
  `RateCeilingPerMinute`, `SessionPrefix`, `SessionPoolSize`, `DrainSeconds`).
- `SimulationEnvironmentPolicy.cs` — `IsAllowed(string? environment)`, case-insensitive.
- Append the three members to `MessageAuditType`
  (`src/NimBus.MessageStore.Abstractions/MessageAuditEntity.cs`).
- `appsettings.json`: `NimBus:Simulation` section with the defaults.
- Tests (`tests/NimBus.WebApp.Tests/Simulation/SimulationEnvironmentPolicyTests.cs`):
  dev/Development allowed, prod/test/null blocked, a custom list is respected.

### 2. Failure behaviour (pure, no Service Bus)
- `SimulatedFailureMode.cs` (settings record), `SimulatedExceptions.cs`
  (`SimulatedTransientException`, `SimulatedPermanentException`),
  `SimulatedFailureClassifier.cs` (`IFailureDispositionClassifier`: permanent →
  `DeadLetter`, everything else → `Retry`).
- `SimulatedHandlerBehavior.cs` — `Task ExecuteAsync(IMessageContext, CancellationToken)`
  implementing the table in Decision 8. Uses an injectable `Random` and `TimeProvider`
  for deterministic tests; records a `SimulatedDelivery` into the feed sink.
- Tests: each mode's outcome; scoping by event type and session glob; revert after the
  duration (fake `TimeProvider`); random rate within tolerance over a seeded run.

### 3. Simulated endpoint host
- `SimulatedEndpointHost.cs` — composes the chain from Decision 1 for one endpoint and
  exposes `StartAsync`/`StopAsync`/`DrainAsync`. Registers one `DelegateEventJsonHandler`
  per consumed event type id that calls `SimulatedHandlerBehavior`.
- Test with `NimBus.Testing`'s in-memory transport, which composes `IMessageHandler`
  directly (`ServiceCollectionExtensions.cs:433`): build the same
  `StrictMessageHandler` + provider + classifier, send an event request, and assert the
  Resolver response type per mode (Resolution, Error with retry, dead-letter,
  Unsupported). This is the drift guard for Decision 1.

### 4. Publisher loop
- `SimulatedPublisher.cs` — one loop per enabled (endpoint, event type): wait
  `60 / (rate × speed)` s ± 20% jitter, bounded by the ceiling; generate with
  `FakeEventPayloadGenerator`, deserialize, `TryValidate`, publish with a `sim-` session
  from the pool and a `sim-<guid>` correlation id. A validation or publish failure is
  counted and logged, never fatal to the loop.
- `IPublisherClient` is created per producer endpoint through a small factory
  interface so tests use a fake.
- Tests: rate × speed produces the expected publish count against a fake time source;
  ceiling honoured; disabled types don't publish; session ids stay within the pool and
  carry the prefix.

### 5. SimulationService (state machine)
- `ISimulationService` / `SimulationService` (singleton): discovers publishers and
  consumers from `IPlatform` (`Endpoints`, `GetProducers`, `GetConsumers`), holds
  settings/config, runs Start/Pause/Stop, auto-stop, counters (`Interlocked`) and the
  100-entry feed ring buffer. Default handler hosting per Decision 7 from
  `IHeartbeatService` (scoped — resolved through `IServiceScopeFactory`).
- `SimulationLifetimeService` (`IHostedService`): auto-stop timer and Stop on
  application shutdown. Register with `AddSingleton<IHostedService>(…)` or
  `AddHostedService` once only (see memory note on `AddHostedService` dedup).
- Tests: Stopped → Running → Paused → Running → Stopped; Start refused when disabled or
  not allowed; disabling while running stops the run; External endpoints get no host;
  config replacement validated against `IPlatform` (bad endpoint/event type → error).

### 6. API
- Edit `api-spec.yaml` (paths + schemas above); build with SPA/NSwag generation
  enabled (not `SkipSpaBuild=true`) so `ApiContract.g.cs` and
  `ClientApp/src/api-client/index.ts` regenerate.
- `Controllers/ApiContract/SimulationImplementation.cs` implementing the generated
  controller interface; register it in `Startup.AddApiControllers`, and register the
  simulation services in a new `AddSimulation(services)` step in `Startup`.
- Tests (`tests/NimBus.WebApp.Tests`): non-Owner → 403 + denied audit row; environment
  not allowed → 403 (GET → `allowed:false`); valid PUT/POST → audit row with Data;
  invalid config → 400 with the validation message.

### 7. Frontend — Admin tab
- `ClientApp/src/components/admin/simulation-settings.tsx`: enable switch, auto-stop,
  rate ceiling, read-only session prefix, environment-policy table, discovered endpoints
  with a Simulated/External hosting choice, and "Open Simulate →". When not allowed, show
  the blocked state and disable the controls.
- `pages/admin.tsx`: add the "Simulation" tab after "Failure intelligence".
- Tests: renders discovered endpoints; toggling calls PUT settings; blocked
  environment disables the switch.

### 8. Frontend — Simulate page
- `pages/simulate.tsx` + `components/simulate/`: `status-strip.tsx`,
  `scenario-bar.tsx` (presets built from the discovered subscribers: Happy path,
  Flaky <first subscriber>, Retry storm, Slow handlers, Poison <first subscriber>),
  `speed-control.tsx`, `publishers-card.tsx`, `subscribers-card.tsx`,
  `failure-mode-dialog.tsx`, `live-feed.tsx`.
  Poll `GET /api/admin/simulation` every 2 s while mounted, and not while the tab is hidden.
- `app.tsx`: lazy route `/Simulate`. `components/sidebar.tsx`: "Simulate" item under
  Manage with a state badge (on / paused / idle), shown only when `canManageSite` and
  status `allowed && enabled`. Fetch status only when `canManageSite`, so non-Owners
  never trigger a 403.
- Direct navigation by a non-Owner, or when disabled, renders the existing not-found
  page.
- Tests: sidebar gating (non-Owner, disabled, enabled); Start/Pause/Stop buttons per
  state; picking a failure mode sends the whole config; scenario preset sends the
  expected modes; feed renders outcomes.
- Run `npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and `run build`
  (not in parallel with `dotnet build`).

### 9. AppHost switch
- `src/NimBus.AppHost/Program.cs`: `NIMBUS_SIMULATION=true` (env or config) skips the
  `publisher`, `subscriber` and `warehouse-subscriber` projects and sets
  `NimBus__Simulation__EnabledByDefault=true` on the WebApp. Default behaviour is
  unchanged.

### 10. Docs
- `docs/` guide page `webapp-simulate.md`: what it does, config keys, environment
  gate, handler hosting and the live-instance caveat, per-instance state, how to purge
  leftovers. Link it from the WebApp README.

## Verification

- `dotnet build src/NimBus.sln -c Release` and `dotnet test src/NimBus.sln -c Release --no-build`.
- Frontend `test:ci` and `build`.
- Manual, local stack with `NIMBUS_SIMULATION=true` and the SB emulator: enable in Admin,
  Start, then check Messages shows `sim-` sessions for both subscribers. Switch Warehouse
  to Random 30% → Failed rows and blocked sessions appear; Resubmit one → the deferred
  processor unblocks the session. Switch to Poison → dead-lettered rows. Pause → the
  publish counter stops; Stop drains and the nav badge shows idle.
- Manual, default AppHost (samples running): Billing/Warehouse default to External
  once heartbeats have answered; simulated publishes are handled by the sample
  processes.
- Screenshot of the Simulate page and Admin tab for the PR (AGENTS.md WebApp rule).
- Report skipped live SQL/Cosmos conformance runs explicitly (no storage change is
  expected to need them).

## Open questions

1. Is per-instance, non-persistent state acceptable (Decision 3), or should the enabled
   flag and config persist in the selected store like the failure-intelligence settings?
   Persisting them adds a storage record and migration work.
2. Should `NIMBUS_SIMULATION=true` become the AppHost default, replacing the sample
   processes?
3. Should `test` be in the default allowed list, or stay opt-in per deployment?
