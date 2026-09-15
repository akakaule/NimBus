# Implementation plan: Resolver capacity controls, telemetry export, handoff projection

Date: 2026-09-15
Branch: `feat/resolver-capacity-controls` off master `84e63e6`
Specs: `docs/spec/031-resolver-capacity-controls/spec.md` (§3.1, §3.2, §3.3) and
`docs/spec/030-stale-pending-guard/spec.md` (§3.6 of 031 / ADR-012 amendment)
Decisions taken by the repo owner (2026-09-15): port the concurrency shape and the scale
ceiling; fix the telemetry export and add the settlement counters; adopt the DIS rule that
handoff settlement requests never write the audit row.

Out of scope here: the Spec 030 stale-write guard itself (next PR), the capacity library, API
and banner (031 §3.4), bounded DLQ replay.

## Workstream A — telemetry export and settlement counters (Resolver)

1. `deploy/bicep/deploy.core.bicep`: move `APPLICATIONINSIGHTS_CONNECTION_STRING` from
   `flexSecretSettings` to `sharedResolverSecretSettings` so the Elastic Premium branch also
   registers the Azure Monitor exporter (`NimBus.ServiceDefaults/Extensions.cs:79` gates on it).
   Implemented together with workstream B because both edit the same file.
2. `src/NimBus.Core/Diagnostics/MessagingAttributes.cs`: `NimBusStoreReason = "nimbus.store.reason"`,
   `NimBusRetryAction = "nimbus.retry.action"`, `NimBusDelaySource = "nimbus.delay.source"`.
3. `src/NimBus.Core/Diagnostics/NimBusMeters.cs`: counter `nimbus.resolver.store_retry` {events}
   and histogram `nimbus.resolver.retry_delay_seconds` {s}.
4. `src/NimBus.Resolver/Services/ResolverService.cs`: increment the counter in
   `HandleCosmosThrottle` (reason `throttled`) and `HandleThrottling` (reason `transient`) with
   action `rescheduled` | `dead_lettered` | `abandoned`; record the applied delay with source
   `provider` | `backoff` where the delay is chosen.
5. `tests/NimBus.Resolver.Tests/ResolverServiceTests.cs`: a `MeterListener`-based assertion for
   the three actions and both reasons, and for the delay/source pairs.

## Workstream B — concurrency shape, scale ceiling, CLI, docs (worktree agent)

1. `src/NimBus.Resolver/host.json`: `maxConcurrentSessions` 16, `sessionIdleTimeout` `00:00:01`,
   `prefetchCount` 0 (kept), `concurrency.dynamicConcurrencyEnabled` false. New MSTest
   `ResolverHostConfigurationTests` pins the file (DIS `ResolverSettlementTests` shape).
2. `deploy/bicep/deploy.core.bicep`: parameters `resolverMaxConcurrentSessions` (1–200, default
   16), `resolverMaxInstances` (0–10, default 0 = no cap), `resolverFlexMaximumInstanceCount`
   (40–1000, default 100); four `AzureFunctionsJobHost__` settings appended to
   `sharedResolverSettings`; `functionAppScaleLimit` / `maximumInstanceCount` passed per branch;
   plus item A1.
3. `deploy/bicep/templates/functionApp.bicep`: optional `functionAppScaleLimit` (0 = unset).
4. `deploy/bicep/parameters/*.bicepparam`: document the new parameters.
5. CLI: `--resolver-max-sessions` and `--resolver-max-instances` on `infra apply` and `setup`;
   `InfrastructureOptions` gains two optional members; parse in `Program.cs` (integer, >= 0),
   per-plan range check in `InfrastructureDeployer.DeployCoreInfrastructureAsync` after
   `ResolveResolverPlan` (EP 0–10 → `resolverMaxInstances=`, Flex 40–1000 →
   `resolverFlexMaximumInstanceCount=`); parameters passed only when given.
6. xUnit tests in `tests/NimBus.CommandLine.Tests`: option parsing and ranges; deployer
   arguments per branch (RecordingAzureCliRunner pattern); template text contains the four
   host-override keys.
7. Docs and entry points: `docs/cli.md`, `docs/deployment.md`, `docs/throughput-tuning.md`
   (Resolver under profile C, 1 s idle deviation, turnover ceiling), `.github/workflows/deploy.yml`,
   `pipelines/azure-pipelines-deploy.yml` (optional pass-through inputs).

## Workstream C — handoff settlement requests are history only (Resolver)

**Outcome: implemented, then reversed in code review the same day.** The history-only
projection (DIS `ShouldProjectToUnresolvedState`) broke the agent zone, which DIS does not
have: `GetAgentReceiveAsync` is a non-claiming, oldest-first read of Pending+Handoff rows and
`HandoffSettlementService` gates on that sub-status, so without the projection the zone
re-delivers the just-settled event, admits a second settlement, and head-of-line blocks until
the subscriber's terminal response lands. The projection stays; it is now pinned by
`Handle_HandoffSettlementRequest_ProjectsPlainPendingRow`, the reasoning is recorded in the
2026-09-15 note of `docs/adr/012-pending-handoff.md` and in Spec 031 §3.6, and Spec 030 keeps
the settlement requests in its control-request set.

Original steps, kept for the record:

1. `ResolverService.Handle`: after `StoreMessage`, when `MessageType` is
   `HandoffCompletedRequest` or `HandoffFailedRequest`, log, notify, complete, and return
   without `UpdateState`. Remove the two types from `MessageTypeToStatusMap`.
2. Tests: settlement requests store history, do not upload, complete the message.
3. ADR-012 amendment; `docs/message-flows.md` §13 note.
4. Spec 030: drop the settlement requests from the control-request clause.

## Post-review fixes (2026-09-15)

Applied from the branch code review before the PR: Flex Consumption instance range corrected
to 1–1000 (the "minimum 40" was stale per Microsoft Learn); `functionAppScaleLimit` written
unconditionally so 0 clears an earlier cap; only `maxConcurrentSessions` remains a template
override, `host.json` owns the fixed bounds (one owner per value); throttled and transient
store failures share one settlement path on `RetryPolicy` with one delivery budget, counters
recorded after each settlement call and the delay histogram only after a successful
reschedule; `nimbus.endpoint` tag and public `StoreRetryReason` / `RetryAction` / `DelaySource`
constants; histogram renamed `nimbus.resolver.retry.delay`; per-plan range check also runs
before login when `--resolver-plan` is explicit; test helpers reuse `ResolverTelemetryCapture`
and `ResolverHeartbeatTests.RecordingNotifier`; `host.json` linked explicitly into the test
output.

## Verification

- `dotnet build src/NimBus.sln -c Release` (CS warnings are errors in Release; CS8767 is not
  allowlisted).
- `dotnet test tests/NimBus.Resolver.Tests -c Release`, `dotnet test tests/NimBus.CommandLine.Tests -c Release`,
  `dotnet test tests/NimBus.Core.Tests -c Release`.
- `az bicep build --file deploy/bicep/deploy.core.bicep --stdout` when the Bicep CLI is present.
- `/code-review` on the branch before the PR.

## Commits (local only; no push)

1. `docs: add specs 030/031 and the capacity-controls plan`
2. `feat(resolver): bound session concurrency and expose scale controls`
3. `feat(resolver): export meters on Elastic Premium and count store retries`
4. `fix(resolver): keep handoff settlement requests out of the audit row`
