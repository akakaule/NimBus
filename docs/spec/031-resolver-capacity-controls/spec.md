# Spec 031 — Resolver capacity controls and Cosmos capacity visibility (port from DIS)

Status: §3.1–§3.3 implemented on branch `feat/resolver-capacity-controls` (2026-09-15, see
`docs/plan/2026-09-15-resolver-capacity-controls-plan.md`); §3.4 (capacity library, API, banner)
and the bounded DLQ replay remain follow-ups.
Companion to: Spec 030 (stale copies must not reopen a settled audit row). 030 makes the
throttle-induced reorder harmless; this spec reduces how often the Resolver is throttled and
makes throttling visible to operators.
Source: DIS `C:\git\kl\DIS` at `38b1329d`, commits `84c962aa` (feat: add Resolver capacity
visibility and EP1 controls) and `7c229af4` (fix: harden Resolver capacity reporting and API
routes), implementing Phase 1 of `docs/plan/2026-09-14-resolver-ep1-capacity-visibility.md`.
DIS status: "Phase 1 code implemented; deployment and UAT evidence remain required".
Baseline: NimBus master `84e63e6` (v3.6.1).
Review status: reviewed adversarially (three independent passes); §9 lists what changed.

## 1. What DIS changed, and what NimBus has today

| DIS change | DIS location | NimBus today | Decision |
|---|---|---|---|
| Bounded consumer concurrency: `maxConcurrentSessions` 200 -> 16, `prefetchCount` 0 (new), `concurrency.dynamicConcurrencyEnabled` false and `snapshotPersistenceEnabled` false (new); `sessionIdleTimeout` 1 s was already there | `src/BH.DIS.Resolver/host.json`; pinned by `ResolverSettlementTests` | `maxConcurrentSessions` 200, `prefetchCount` 0, `sessionIdleTimeout` 30 s, no `concurrency` section, no test pins the file | **Port** (§3.1) |
| Deploy-time host overrides as template-owned app settings (`AzureFunctionsJobHost__extensions__serviceBus__maxConcurrentSessions`, `…prefetchCount`, `AzureFunctionsJobHost__concurrency__dynamicConcurrencyEnabled`) with validated pipeline parameters (sessions 1–200, instances 1–10, both mandatory in the DIS pipeline) | `deploy/bicep/deploy.core.bicep`, `deploy/deploy.ps1`, `build/infra-core-template.yml` | `nb setup` emits no host overrides; the Resolver's app settings are template-owned and fully replaced on every deploy (only the WebApp has a preserve list) | **Port**, as optional `nb` options with Bicep defaults (§3.2) |
| Per-app scale ceiling `siteConfig.functionAppScaleLimit` (default 2), Resolver only | `deploy/bicep/templates/functionApp.bicep`, `deploy.core.bicep` | None on Elastic Premium; the plan allows `maximumElasticWorkerCount: 10`. Flex Consumption template has `maximumInstanceCount` default 100 | **Port**, per plan branch (§3.2) |
| `RequestLimitException` carries `RetryAfter` and the inner exception (10 throw sites) | `src/BH.DIS.MessageStore/RequestLimitException.cs`, `CosmosDbClient.cs` | `CosmosExceptionTranslation` already throws `RequestLimitException(message, retryAfter)` and `HandleCosmosThrottle` honours it. The inner exception is deliberately not attached (the translation boundary strips provider details) | Already present; no change |
| Capacity telemetry: in-process 30 s windows (attempts, successes, escaped 429s, affected attempts, last successful write), `ResolverCapacityReporter` flushing every 15 s to Table Storage, state evaluator (Healthy / Throttled / Recovering / Unknown plus freshness) | `src/BH.DIS.Capacity`, `src/BH.DIS.Resolver/Program.cs`, `ResolverService.cs` | OpenTelemetry meters exist: `nimbus.resolver.outcome_written` and `audit_written` carry `error.type` (so throttled writes are countable), `nimbus.resolver.write.duration`, and the store decorator's `nimbus.store.operation.failed`. Missing: a counter on the settlement decision (rescheduled / dead-lettered / abandoned), the applied delay, and any Cosmos-independent state. **On the Elastic Premium branch the Azure Monitor exporter is not registered at all** (§3.3) | **Port in two steps** (§3.3, §3.4) |
| Capacity API `GET /api/capacity/resolver` (authenticated, minimal) and `/details` (Owner); banner "Processing delayed — Cosmos DB is limiting Resolver writes."; panel on the Heartbeat page; notice beside DLQ replay | `src/BH.DIS.WebApp/Controllers/ApiContract/CapacityImplementation.cs`, `Services/Capacity/ResolverCapacityService.cs`, `ClientApp/src/components/capacity/*`, `contexts/resolver-capacity-context.tsx` | Admin -> Health reads `IServiceHealthStore` in Cosmos, so it is blind exactly when Cosmos throttles | **Port as follow-up** (§3.4) |
| Bounded DLQ replay: `DeadLetterOverview.SnapshotTakenAt`, `DeadLetterResubmitRequest.MaxMessages` (1–500, validated) and `EnqueuedBefore` cutoff; automatic continuation with a Stop control and a capacity notice in the subscription admin | `ResolverDeadLetterClient.cs`, `api-spec.yaml`, `subscription-manager.tsx`, `ResolverDeadLetterClientTests` (+99), `subscription-manager.test.tsx` (+114) | None of these fields exist in the NimBus WebApp | **Follow-up with §3.4**; until then replay the Resolver DLQ in small batches (§4) |
| Throttle settlement: `Abandon` is a no-op, the message retries when the broker redelivers it with the same `MessageId`, budget = `DeliveryCount`, DLQ reason `CosmosDbThrottled` | `ResolverService.HandleCosmosThrottle`, `src/BH.DIS.ServiceBus/MessageContext.cs:136` | `ScheduleRedelivery` re-sends with 5 -> 300 s backoff honouring `RetryAfter`, budget = `ThrottleRetryCount + DeliveryCount`, same DLQ reason | **Do not port** (§3.5) |
| Pre-existing DIS behaviour (commit `184b74f2`, not part of the two commits): handoff settlement requests are recorded in history only, never projected onto the row (`ShouldProjectToUnresolvedState`) | `ResolverService.cs:282` | Projected as plain Pending, clearing the handoff sub-status (ADR-012) | Not a Cosmos guard; separate decision (§3.6) |

The larger DIS proposal (`docs/superpowers/specs/2026-09-14-resolver-cosmos-throttle-recovery.md`:
a dedicated worker with an admission gate, retry ledger and deferred-head recovery) is a draft,
not implemented in DIS, and is not proposed for NimBus.

## 2. Why this matters for the Nav09Endpoint incident

Spec 030 §2 establishes that the incident's late request copy was a `ScheduleRedelivery` copy
after five Cosmos-throttle rounds. The throttling itself came from the Resolver's consumer
shape: 200 concurrent sessions per instance, up to ten instances on the EP1 plan (Resolver-only in NimBus's template; in EET's brownfield
namespace the same plan name may still host DIS-era apps, §4 step 1), all
writing to one Cosmos container with a fixed RU budget. Every burst on the CRM side becomes a
429 storm on the Resolver, which then reschedules, reorders and, under a long enough storm,
dead-letters. NimBus's own tuning guide already describes the right shape: a consumer whose
limit is a downstream store should "match the upstream's concurrency budget, not the bus's"
(`docs/throughput-tuning.md`, profile C, 4–16 sessions). The Resolver ships with profile A
(100–200) instead.

## 3. Design

### 3.1 Concurrency defaults (`src/NimBus.Resolver/host.json`)

| Setting | Today | Proposed | Why |
|---|---|---|---|
| `maxConcurrentSessions` | 200 | 16 | The Cosmos RU budget, not the bus, is the limit (profile C). DIS's UAT starting point; measured values are promoted per environment through §3.2. |
| `prefetchCount` | 0 | 0 (explicit) | Prefetch locks messages that then wait for a slot. |
| `sessionIdleTimeout` | 30 s | 1 s (as DIS) | Profile C's table says 30–60 s, but that assumes few long-lived sessions. The Resolver sees thousands of per-aggregate sessions competing for 16 slots, and almost every Resolver message opens a session that then sits idle, so a slot is held for handler time **plus** the idle timeout per message. The idle timeout is therefore the throughput dial, not a tuning nicety (see the ceiling arithmetic below). The tuning-guide row must state this deviation from profile C explicitly. |
| `maxAutoLockRenewalDuration` | 5 min | 5 min | Unchanged. DIS uses 25 min because its retry is lock-expiry based; NimBus's is not. |
| `concurrency.dynamicConcurrencyEnabled` | absent | `false` | Dynamic concurrency can override the manual session setting; pin it. |

`snapshotPersistenceEnabled` (also set by DIS) only matters when dynamic concurrency is on;
not added.

**Session-turnover ceiling.** Because a slot is held for `t_handler + sessionIdleTimeout` per
message, the Resolver's throughput ceiling is roughly
`slots / (t_handler + idle)` with `slots = maxConcurrentSessions × instances`, and every event
produces two Resolver messages (the request copy and the response):

| Configuration | Ceiling |
|---|---|
| Today: 200 sessions × 10 instances, 30 s idle | about 66 msg/s |
| 16 × 2, 5 s idle | about 6 msg/s (a 10,000-event batch drains in about 55 min) |
| 16 × 2, 1 s idle | about 27 msg/s |
| 16 × 10, 1 s idle | about 130 msg/s |

Sizing rule for §4: `slots >= peak Resolver messages/s × (t_handler + idle)`, measured, with the
Cosmos RU budget as the upper bound. This is why the idle timeout follows DIS and why the
instance cap is opt-in (§3.2). Pin the file with an MSTest in `tests/NimBus.Resolver.Tests` that parses `host.json`
(copied to output) and asserts these values, as DIS does in `ResolverSettlementTests`, so the
defaults cannot drift back silently.

### 3.2 Deploy-time controls (`nb setup` / `nb infra apply`)

The Resolver's app settings are template-owned and fully replaced on each `nb setup`
(`InfrastructureDeployer.DeployCoreInfrastructureAsync`; only the WebApp has a preserve list),
so the overrides must live in Bicep, exactly as DIS did.

- `deploy/bicep/deploy.core.bicep`:
  - `@minValue(1) @maxValue(200) param resolverMaxConcurrentSessions int = 16`
  - `@minValue(0) @maxValue(10) param resolverMaxInstances int = 0` (Elastic Premium; `0` means
    no per-app cap, which is today's behaviour; the plan template's `maximumElasticWorkerCount`
    is 10). Opt-in on purpose: with `host.json` at 16 sessions, an uncapped plan is already
    16 × 10 = 160 sessions, twelve times below today's 200 × 10, and the instance cap is the value
    with the least evidence behind it (DIS calls its 2 a UAT hypothesis).
  - `@minValue(40) @maxValue(1000) param resolverFlexMaximumInstanceCount int = 100` (Flex
    Consumption's `maximumInstanceCount` has a platform minimum of 40, so the EP ceiling cannot
    be reused; today's template default is kept). On Flex the session ceiling is therefore at
    least 40 × sessions; only the per-instance sessions value bounds Cosmos concurrency there.
    Note also that sessions and instances are coupled on both plans: target-based scaling uses
    the per-instance concurrency as its target, so lowering sessions raises the instance count
    the scale controller wants. Change one, measure, then the other.
  - append to `sharedResolverSettings`: `AzureFunctionsJobHost__extensions__serviceBus__maxConcurrentSessions = string(resolverMaxConcurrentSessions)`,
    `AzureFunctionsJobHost__extensions__serviceBus__prefetchCount = '0'`,
    `AzureFunctionsJobHost__extensions__serviceBus__sessionIdleTimeout = '00:00:01'`,
    `AzureFunctionsJobHost__concurrency__dynamicConcurrencyEnabled = 'false'`
  - move `APPLICATIONINSIGHTS_CONNECTION_STRING` from `flexSecretSettings` into
    `sharedResolverSecretSettings` so both plan branches register the Azure Monitor exporter
    (§3.3 depends on it)
  - pass `functionAppScaleLimit: resolverMaxInstances` to `resolverFunctionElastic` and
    `maximumInstanceCount: resolverFlexMaximumInstanceCount` to `resolverFunctionFlex`.
- `deploy/bicep/templates/functionApp.bicep`: `@minValue(0) param functionAppScaleLimit int = 0`
  and `siteConfig.functionAppScaleLimit: functionAppScaleLimit > 0 ? functionAppScaleLimit : null`
  (the template has one caller today; the optional parameter keeps the packaged template
  reusable).
- CLI (`Program.cs`, both `setup` and `infra apply`): `--resolver-max-sessions <N>` and
  `--resolver-max-instances <N>`. `Program.cs` parses to `int` and rejects non-integers and
  values below 1. The per-plan range check happens in `DeployCoreInfrastructureAsync`, because
  the effective plan is only known after existing-plan pinning (`PlanSelection.ResolveResolverPlan`):
  EP requires 1–10 and emits `resolverMaxInstances=`, Flex requires 40–1000 and emits
  `resolverFlexMaximumInstanceCount=`; violations throw `CommandException`. New optional members
  `ResolverMaxConcurrentSessions` and `ResolverMaxInstances` on `InfrastructureOptions`; the
  parameters are passed only when given, so the Bicep defaults apply otherwise. This is a
  deliberate difference from DIS, whose pipeline variables are mandatory.
- Tests: `PlanSelectionTests`-style cases for parsing and per-plan ranges; an
  `InfrastructureDeployerSecretTests`-style case asserting the parameters reach the
  `deployment group create` arguments for each branch; a `BicepTemplateProviderTests` string
  check that `deploy.core.bicep` carries the four `AzureFunctionsJobHost__` settings and both
  templates declare their ceiling parameter. CI has no Bicep compile step today (the Bicep CLI
  is a deployment prerequisite only); `az bicep build --stdout` on both entry templates is part
  of the manual verification and worth adding to CI separately (§6).
- Docs and entry points: `docs/cli.md` (the option reference `deployment.md` delegates to),
  `deployment.md` (brownfield note), `throughput-tuning.md` (Resolver row under profile C with
  the idle-timeout deviation), release notes; `.github/workflows/deploy.yml` and
  `pipelines/azure-pipelines-deploy.yml` gain the two options as optional pass-through inputs,
  as they already do for `--resolver-plan`.

**Brownfield note.** An unmodified `nb setup` with this version changes exactly one thing on an
existing deployment: sessions per instance drop from 200 to 16 (and the idle timeout to 1 s)
through the template-owned overrides. No instance cap is applied unless `--resolver-max-instances`
is passed. EET's `deploy-stage-template.yml` passes nothing today, so the pipeline change that
pins per-environment values must land in the same PR as the version bump, and every throughput
knob is an app setting so that a rollback is another `nb setup` with different values, never a
binary rollback (which would also remove Spec 030's guard).

### 3.3 Telemetry, step 1 (this change): settlement counters through the existing meters

**Precondition found during review.** `AddServiceDefaults` registers the Azure Monitor exporter
only when `APPLICATIONINSIGHTS_CONNECTION_STRING` is present (`NimBus.ServiceDefaults/Extensions.cs:79`),
and `deploy.core.bicep` sets that key only on the Flex Consumption branch; the Elastic Premium
branch, which EET deploys, receives `APPINSIGHTS_INSTRUMENTATIONKEY` only. Unless the Functions
host forwards worker-side OpenTelemetry meters on its own (platform fact, §7), the existing
`outcome_written` counters have never reached Application Insights on EP1. The one-line Bicep
move in §3.2 fixes the export path; §5 adds the check that proves it.

What is added, in `NimBusMeters` and `MessagingAttributes`:

- Counter `nimbus.resolver.store_retry` {events}, tags `nimbus.store.reason` = `throttled`
  (from `HandleCosmosThrottle`, `RequestLimitException`) | `transient` (from `HandleThrottling`,
  `StorageProviderTransientException`) and `nimbus.retry.action` = `rescheduled` |
  `dead_lettered` | `abandoned` (the fallback when `ScheduleRedelivery` itself throws).
  Provider-neutral on purpose: `transient` covers Cosmos 408/410/449/5xx and every SQL Server
  transient error, so it must not be labelled as Cosmos throttling. Alert on
  `reason == throttled`: sum over 1 min > 0 for two consecutive evaluations (Azure Monitor metric
  alerts evaluate at 1 min granularity at best; a 30 s window needs a log-based KQL alert or the
  §3.4 evaluator).
- Histogram `nimbus.resolver.retry_delay_seconds` recording the **applied** delay
  (`max(backoff, RetryAfter)` as computed in `ScheduleStorageRedelivery` / `HandleThrottling`)
  with tag `nimbus.delay.source` = `provider` | `backoff`. The applied delay is the number that
  matters for Spec 030 (how far a copy is pushed behind its session); the raw hint is in the
  existing warning log.

About twenty lines plus unit tests on `FakeCosmosDbClient` throwing `RequestLimitException`
(with and without `RetryAfter`) and `StorageProviderTransientException`, asserting the tag values
for all three actions. Together with Spec 030's `outcome_ignored`, operators get: how often the
store pushes back and why, how far messages were pushed back, how often that produced a stale
copy, and how often a message was lost to the DLQ. The pre-change baseline in §4 step 1 can be
taken today from `outcome_written{error.type = RequestLimitException}` once the exporter is live.

### 3.4 Telemetry, step 2 (follow-up spec): operator-facing capacity state

Port `BH.DIS.Capacity` as `src/NimBus.Capacity` with NimBus adjustments:

- Library as in DIS: `ResolverCapacityMetrics` (30 s windows, bucketed at record time),
  `ResolverCapacityReporter` (`BackgroundService`, 15 s flush, cumulative replacement rows keyed
  `WorkerId|WindowStart`, bounded 10-entry retry buffer), `IResolverCapacitySampleStore` with
  Table and in-memory implementations, `ResolverCapacityStateEvaluator` (coverage counts
  distinct fresh workers, never the configured ceiling; fresh idle samples yield Unknown with
  reason `idle`). DIS's `ResolverCapacityStateEvaluatorTests` (217 lines) port with it. In DIS
  `RecordThrottle(true)` is the single call site, so `Escaped429s` and `AffectedAttempts` are
  always equal; NimBus should keep one "affected attempts" field, or count escaped 429s at
  `CosmosExceptionTranslation` where per-call 429s are actually visible.
- Storage: a `ResolverCapacity` table on the existing functions storage account
  (`funcstorageaccount` in `deploy.core.bicep`). Use DIS's existing `ResolverCapacity:StorageUri`
  plus `DefaultAzureCredential` path (DIS deployed the connection-string variant; the library
  supports both): Storage Table Data Contributor for the Resolver identity and Storage Table
  Data Reader for the WebApp identity in `templates/roleAssignments.bicep` (the WebApp must have
  a system-assigned identity for this; verify in `deploy.webapp.bicep`). This deviates from DIS's
  deployment, which used the message-store storage account and a connection string. The signal stays
  independent of Cosmos, which is the point: `IServiceHealthStore` liveness is Cosmos-backed and
  goes stale exactly when Cosmos throttles.
- Configuration surface (both apps, template-owned): `Environment` (partition key; NimBus's
  Resolver has no such setting today), `ResolverCapacity__Enabled`, `__StorageUri`,
  `__TableName`, and on the Resolver `__ConfiguredSessions` / `__ConfiguredMaxInstances`, fed
  from the §3.2 parameters so every sample carries the configured limits. `Azure.Data.Tables`
  pinned in `Directory.Packages.props` (central package management, as in DIS).
- Resolver: `IResolverCapacityMetrics` injected as an optional constructor argument of
  `ResolverService` (as DIS did), `RecordAttempt` / `RecordSuccess` / `RecordThrottle` at the
  same points including the heartbeat path, reporter registered in `Program.cs` behind
  `ResolverCapacity:Enabled` with the in-memory store as the fallback.
- WebApp: `GET /api/capacity/resolver` (any authenticated user, minimal fields) and
  `GET /api/capacity/resolver/details` (site Owner) declared in `api-spec.yaml` and generated
  through `api-gen.nswag`, implemented as the generated-interface `CapacityImplementation`
  (DIS's `7c229af4` replaced an attributed controller because a second attributed controller
  produced ambiguous routes); `ResolverCapacityService` reads the last five minutes of samples
  and evaluates; banner mounted in `app.tsx` with a shared polling context (15 s, browser
  freshness expiring independently of polling success), panel on `pages/heartbeat.tsx` beside
  the Resolver liveness card, notice in the subscription admin next to DLQ replay. DIS shipped one
  flag, `ResolverCapacity:Enabled`; a separate UI flag is optional.
- Tests to port: `ResolverCapacityStateEvaluatorTests`, `CapacityApiTests` (real HTTP pipeline:
  anonymous 401, non-Owner 403 on details, redaction), `resolver-capacity-context.test.tsx`, the
  subscription-manager component tests.
- Size, from the DIS commits: about 800 lines of product code and 500 lines of tests.

Decision for the owner: port now, or after §3.2 has been measured in EET dev. Recommendation:
after, because §3.1–3.3 are small and remove the cause, while §3.4 is a medium port that only
reports on it.

### 3.5 Not ported: lock-expiry settlement

DIS keeps a throttled message unsettled (`Abandon` is a no-op) and lets the broker redeliver it.
NimBus re-sends a scheduled copy with exponential backoff that honours `RetryAfter`. Reasons to
keep NimBus's approach:

- Budget. DIS's retry cadence is not under application control. The Resolver is a session
  trigger and `Abandon` is a no-op, so the unsettled message stays locked under the session lock
  until the processor releases the session: after the idle timeout once the session drains (1 s
  in DIS) or at the renewal cap (25 min in DIS). Ten deliveries can therefore be spent in under a
  minute on a quiet session or stretched to 25 min on a busy one, and `RetryAfter` is ignored.
  NimBus's schedule is deterministic (5 s doubling to 300 s, 1215 s over the nine reschedules
  before the tenth logical attempt dead-letters) and waits at least `RetryAfter`. NimBus's own
  comments in `MessageContext.Abandon` and the `ScheduleRedelivery` fallback ("~30s") encode the
  per-message-lock model too and should be corrected as part of this change.
- Order. Neither approach preserves session order: a locked but unsettled head is not
  redelivered until the session is released, and other messages in the session can be delivered
  meanwhile. DIS's own proposal lists "current delay is coupled to broker locks and delivery
  counts" as a weakness of its model.
- Identity. The one advantage of lock-expiry retry, the same `MessageId` and a single history
  document, is delivered by Spec 030 §5.7.

This section supersedes the "hold the session lock instead of re-sending" follow-up that Spec
030 §11 listed; that bullet now points here.

### 3.6 Noted, not in scope: handoff settlement projection

DIS records `HandoffCompletedRequest` / `HandoffFailedRequest` in history only and leaves the
Pending+Handoff row untouched until the subscriber's terminal response. NimBus projects them as
plain Pending (ADR-012). Adopting the DIS behaviour would remove the "row written by a handoff
settlement" case from Spec 030's rule, because settlements would never write the row. Decide
together with the Spec 030 implementation; default is to keep ADR-012.

## 4. Rollout for EET

1. Capture the baseline on the prod Resolver before changing anything: effective app settings
   (including whether `APPLICATIONINSIGHTS_CONNECTION_STRING` is present out-of-band),
   `siteConfig.functionAppScaleLimit`, the plan's minimum and pre-warmed instance settings and
   every site hosted on `asp-{solution}-{env}-core` (the name is shared with DIS, so DIS-era apps
   may still sit on it), instance count over a day, **peak incoming messages per second on the
   Resolver subscription** (two per event; this number sizes §3.1), median Resolver handler
   time, Cosmos 429 count and normalized RU on the `Nav09Endpoint` container over the incident
   window, and Resolver-subscription active message count and age. Once the exporter is live,
   `outcome_written{error.type = RequestLimitException}` gives the throttle baseline without
   waiting for the new counter.
2. Ship NimBus 3.7.0 with Spec 030 and §3.1–3.3 (or Spec 030 alone first as 3.6.2, §6). In the
   same PR, add `--resolver-max-sessions` and `--resolver-max-instances` to
   `EET.Deploy/pipelines/deploy-stage-template.yml`, fed from the `DIS-{env}` variable groups,
   sized from step 1 with the ceiling formula in §3.1. Start dev at 16 sessions with no instance
   cap unless the measured peak says otherwise. After the first deploy per environment, read
   back `functionAppScaleLimit` and the four `AzureFunctionsJobHost__` settings.
3. Measure in dev and test: 429s should drop to near zero. Rollback trigger: active-message age
   on the Resolver subscription above an agreed limit (say 5 min at steady state) with 429s at
   zero means the ceiling is too low; raise sessions first, then relax the instance cap, one
   control at a time, each through `nb setup` (settings only). Sessions and instances are
   coupled (§3.2), so measure after every single change.
4. Promote measured values to prod. Do not restore 200 sessions: that is the configuration that
   produced the incident.
5. Watch after deployment: `nimbus.resolver.store_retry{reason = throttled}`, Spec 030's
   `outcome_ignored`, the Cosmos 429 metric, and the subscription admin page's in-transit count.
   Until the bounded-replay port lands, replay the Resolver DLQ in small batches; one unbounded
   replay recreates the burst this spec is trying to prevent.

The Cosmos RU allocation of the `Nav09Endpoint` container is the other lever and is outside
NimBus; the container is partitioned by `/id`, so a hot partition is unlikely and RU is the
budget to check.

## 5. Verification

- Unit: option parsing and per-plan ranges; deployment arguments per branch; template string
  checks; the `host.json` pin test; `HandleCosmosThrottle` / `HandleThrottling` increment
  `store_retry` with the right reason and action tags for all three actions and record the
  applied delay with its source.
- Bicep: `az bicep build --stdout` on `deploy.core.bicep` and `deploy.webapp.bicep`; a
  `what-if` against a sandbox resource group must show only the Resolver app changing
  (`functionAppScaleLimit`, four `AzureFunctionsJobHost__` settings, the connection-string
  setting on EP) and no change to the WebApp or plans.
- Live, after `nb setup` on an EP1 deployment: read back `az functionapp config appsettings list`
  and `az functionapp show --query siteConfig.functionAppScaleLimit`; confirm the extension
  honours the override by observing at most 16 concurrent session locks on the Resolver
  subscription under load; run a KQL query on `customMetrics` for `nimbus.resolver.outcome_written`
  and, after the change, `nimbus.resolver.store_retry`. If the first is empty, the export path
  is the first thing to fix, not the counters.
- Commands: `dotnet build src/NimBus.sln -c Release`, `dotnet test tests/NimBus.CommandLine.Tests`,
  `dotnet test tests/NimBus.Resolver.Tests`.

## 6. Open decisions for the repo owner

1. Default `host.json` values (16 sessions, 1 s idle) versus keeping today's defaults and
   relying on the deploy-time overrides only (the overrides now include the idle timeout, so
   both routes reach the same effective configuration). Recommendation: change the defaults; a
   fresh deployment should not start in the incident configuration.
2. Whether §3.4 (capacity library, API, banner) and the bounded DLQ replay are scheduled now or
   after measuring §3.2.
3. Whether to add a Bicep compile step to CI in the same PR.
4. Whether to open the ADR-012 question in §3.6 alongside Spec 030.
5. Whether Spec 030 ships alone first (3.6.2, correctness only) and this spec follows (3.7.0),
   so a throughput problem can never force a rollback of the guard. With every knob being an app
   setting, a settings-only rollback is available either way; shipping separately is still the
   cleaner story.
6. Whether `nb setup` should refuse to run on Elastic Premium without an explicit
   `--resolver-max-instances`, instead of the opt-in default proposed in §3.2.

## 7. Facts to verify before implementing

- Flex Consumption `maximumInstanceCount` minimum is 40 (Azure platform limit, not a NimBus
  choice); confirm against current Azure documentation.
- `AzureFunctionsJobHost__*` host overrides are honoured on Flex Consumption as on Elastic
  Premium (they are on Windows EP; Flex rejects only the legacy content-share settings).
- `functionAppScaleLimit` is honoured on Elastic Premium plans (documented for Premium and
  Consumption; DIS relies on it on EP1 but has not yet produced UAT evidence).
- Whether the Functions host forwards worker-side OpenTelemetry meters to Application Insights
  without `UseAzureMonitor` and without `telemetryMode: openTelemetry` in `host.json`. The
  Microsoft Learn page "Use OpenTelemetry with Azure Functions" settles it; the §5 KQL check
  settles it empirically. The Bicep move in §3.2 is correct either way.
- Service Bus session semantics for an unsettled message under a no-op abandon (it becomes
  available when the session is released; whether later session messages are delivered
  meanwhile). Affects only the wording of §3.5, not its conclusion.
- Whether a per-app `functionAppScaleLimit` below the Premium plan's minimum or pre-warmed
  instance count is rejected or ignored (read both before choosing a cap; §4 step 1).
- Target-based scaling for the Service Bus trigger uses the per-instance session concurrency as
  its target, which is why sessions and instances are coupled (§3.2).
- Azure Monitor metric alerts on custom metrics evaluate at 1 min granularity at best (§3.3).

## 8. Alternatives rejected

- **Porting DIS's lock-expiry settlement.** See §3.5.
- **Porting the capacity library first.** It reports on throttling but does not reduce it;
  §3.1–3.3 remove the cause at a fraction of the size.
- **A post-receive semaphore inside the Resolver.** Leaves more messages locked while waiting
  for a permit; the DIS plan rejects it for the same reason. Concurrency must be bounded at the
  trigger.
- **Making the ceilings mandatory pipeline inputs as in DIS.** `nb` is also used interactively
  and from the GitHub workflow; optional options with Bicep defaults keep every entry point
  working and still let EET pin values per environment.

## 9. Review trail

Three adversarial reviews of the first draft, none refuting the approach, changed: the
Application Insights export gap on Elastic Premium became an explicit precondition with a Bicep
fix and a live check; the throttle counter became a provider-neutral `store_retry` counter with
reason and action tags (including `abandoned`) plus an applied-delay histogram, and the existing
`error.type` tagging is credited; the bounded DLQ replay DIS shipped in the same commit is now a
row with a decision; §3.4 gained the configuration surface, the storage-identity path DIS already
supports, the generated-controller constraint, the package pin and the full test list; per-plan
range validation moved to the deployer where the plan is known; `docs/cli.md` and the two
workflow entry points were added; the idle-timeout deviation from profile C is stated; the
DIS host.json diff and the handoff-projection attribution were corrected; §3.5's budget
arithmetic was softened to what the repos show. The third pass then corrected the DIS retry
model in §3.5 (session release, not per-message lock expiry), added the session-turnover ceiling
arithmetic that made the idle timeout follow DIS's 1 s and the instance cap opt-in (default 0),
added the sizing rule, the rollback trigger and the plan-inventory step to §4, noted the
sessions/instances coupling and the Flex 40-instance floor, restated the alert at 1 min
granularity, and retired the conflicting "hold the lock" follow-up in Spec 030 §11.
