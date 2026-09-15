# Implementation plan: Spec 030 stale-write guard

Date: 2026-09-15
Spec: `docs/spec/030-stale-pending-guard/spec.md` (design reviewed adversarially; §5 is the
contract, §6 the flows that must keep working, §7 the test list)
Branch: `feat/stale-write-guard` off `feat/resolver-capacity-controls` (that branch holds the
specs and the Spec 031 implementation; rebase onto master once 031 is merged)
Baseline facts (verified 2026-09-15): the Resolver projects handoff settlement requests as plain
Pending rows (ADR-012 note), so `HandoffCompletedRequest` / `HandoffFailedRequest` are control
requests for the guard; `ScheduleRedelivery` still mints a new `MessageId`; the Resolver discards
the `Task<bool>` of every `Upload*Message` call.

Conventions that bite: Release builds turn CS warnings into errors (CS8767 is not allowlisted);
`tests/NimBus.Resolver.Tests` and the conformance suite are MSTest (`[TestClass]`,
`[TestMethod]`, `[DataRow]`, `#pragma warning disable CA1707, CA1515, CA2007`);
`tests/NimBus.CommandLine.Tests` is xUnit; the Cosmos conformance run needs
`NIMBUS_COSMOS_TEST_CONNECTION` (vNext emulator, Gateway mode) and the SQL run
`NIMBUS_SQL_TEST_CONNECTION`, both otherwise skip locally; CI has a must-not-skip gate for Cosmos
only. Use `cd C:\Git\NimBus && dotnet …`.

## Order of work (each step builds and its tests pass before the next)

### 1. The rule, once
- New `src/NimBus.MessageStore.Abstractions/States/StaleWriteGuard.cs` (namespace
  `NimBus.MessageStore`): `IsTerminal`, `IsRequestStage` (EventRequest, DeferralResponse,
  Unknown), `IsControlRequest` (ResubmissionRequest, SkipRequest, RetryRequest,
  ContinuationRequest, HandoffCompletedRequest, HandoffFailedRequest), `IsGuarded(incomingStatus,
  incomingType)`, `Allows(incomingStatus, incoming, current)` exactly as Spec 030 §5.1.
- XML doc: any future `MessageType` projected as Pending or Deferred must be classified here.
- Pure unit tests (MSTest, in `tests/NimBus.MessageStore.InMemory.Tests` or a new
  `StaleWriteGuardTests` next to the conformance run): one case per row of the decision table,
  including the ancestor check and the Unknown/null-type leniency.

### 2. In-memory provider + conformance contract
- `src/NimBus.Testing/Conformance/InMemoryMessageStore.cs` `Upsert`: `AddOrUpdate` with an add
  factory that stamps and returns `content`, an update factory that evaluates
  `StaleWriteGuard.Allows(status, content, existing)` **before** stamping, stamps only on the
  winning branch, and reports the decision through a captured bool (not `ReferenceEquals`).
- `src/NimBus.Testing/Conformance/MessageTrackingStoreConformanceTests.cs`: add the private
  `Upload(store, status, …)` dispatcher and the cases of Spec 030 §7 items 1–9. Follow the file:
  `[TestMethod]` (+ `[DataRow]`), statuses looped inside ONE endpoint container with distinct
  event ids (each distinct endpoint id is a new Cosmos container on the emulator), a fresh
  `SampleEvent` per call, positive lookups via `GetPendingEvent` / `GetEvent`, negatives via
  `DownloadEndpointStateCount`. No "archive then stale copy" case (in-memory hard-deletes, so
  the outcome legitimately differs from Cosmos/SQL; document it in a comment).
- Run `dotnet test tests/NimBus.MessageStore.InMemory.Tests -c Release`: the new cases go red
  before step 2's store change and green after it.

### 3. Cosmos provider
- `src/NimBus.MessageStore.CosmosDb/CosmosDbMessageTrackingStore.cs`: route guarded content from
  `UploadPendingMessage` and `UploadDeferredMessage` to a private `UploadGuarded(eventId,
  sessionId, endpointId, content, status)`; point read by id (`ReadItemAsync`, sees
  `deleted = true` documents), 404 → `CreateItemAsync` (409 → re-read), hydrate via
  `HydrateResolutionStatus`, unparseable status → refuse with a warning (fail closed), rule
  refuses → `LogInformation("COSMOS UPSERT-REFUSED …")` and return `false`, else
  `UpsertItemAsync` with `IfMatchEtag` + `EnableContentResponseOnWrite = false`; 412 → re-read;
  after three attempts throw `StorageProviderTransientException`. Only existing
  `ICosmosContainerAdapter` members; 404/409/412 are not in `CosmosExceptionTranslation.IsTransient`,
  so catch them here. Unguarded content keeps the unchanged `UploadMessage` path.
- Note the accepted echo: the adapter's `CreateItemAsync` has no options overload, so the first
  write of each event returns the document body once (bandwidth only).
- `tests/NimBus.MessageStore.CosmosDb.Tests/RecordingCosmosAdapters.cs`: make `ReadItemAsync`
  scriptable (queue of responses / 404 / 412), let `CreateItemAsync` record instead of throw,
  let `UpsertItemAsync` throw a scripted 412 once. New `CosmosDbClientGuardedWriteTests`: refused
  → no upsert, false; request-stage replace → `IfMatchEtag` equals the read ETag; 404 → create;
  one 412 → second read; three 412s → `StorageProviderTransientException`; unparseable status →
  false; `MessageType.Unknown` Pending → plain upsert without a read.
- Existing `CosmosDbClientRetentionTests` / `WriteOptionsTests` seed with `MessageType.Unknown`
  and stay green.

### 4. SQL Server provider
- `src/NimBus.MessageStore.SqlServer/SqlServerMessageTrackingStore.cs` `UpsertStatus`:
  `MERGE {T("UnresolvedEvents")} WITH (HOLDLOCK) AS target` (same hint as the `EventReports`
  MERGE; also closes the first-insert 2627 race), `WHEN MATCHED AND (<Spec 030 §5.4 predicate>)`
  with `NULLIF` on both sides of the ancestor compare and `COLLATE Latin1_General_BIN2`, all
  UPDATE columns unchanged (`Deleted = 0` kept), `WHEN NOT MATCHED` unchanged, then
  `SELECT @@ROWCOUNT;` executed with `QuerySingleAsync<int>` (not Dapper's records-affected, which
  `SET NOCOUNT ON` turns into -1). No DbUp migration: the MERGE text lives in C#.
- Comment cross-referencing `StaleWriteGuard.Allows`; the conformance suite keeps the two in step.
- `.github/workflows/dotnet.yml`: add a "SQL Server conformance must not skip" step mirroring
  the Cosmos gate, so the predicate cannot pass CI by being inconclusive.

### 5. Resolver observes the bool
- `src/NimBus.Core/Diagnostics/NimBusMeters.cs`: `ResolverOutcomeIgnored`
  (`nimbus.resolver.outcome_ignored`, `{records}`). `MessagingAttributes.NimBusOutcomeApplied =
  "nimbus.outcome.applied"`.
- `src/NimBus.Resolver/Services/ResolverService.cs`: `UpdateState` returns
  `(ResolutionStatus Status, bool Applied)`; handler dictionary becomes `Func<Task<bool>>`;
  `InstrumentOutcomeWrite` returns the bool, tags the `RecordOutcome` activity with
  `nimbus.outcome.applied`, increments `outcome_ignored` instead of `outcome_written` on a
  refusal. `Handle`: applied → today's log + `NotifyEndpointStateChanged`; refused → one warning
  with `EnqueuedTimeUtc`, `ThrottleRetryCount`, `DeliveryCount` (the `IMessageDeliveryContext`
  pattern already used in `HandleStoreFailure`), a best-effort `MessageAuditType.Comment` audit
  through `InstrumentAuditWrite` (extend it with explicit `endpointId` / `eventTypeId`;
  `StoreMessageAudit(eventId, audit, endpointId, eventTypeId)` exists), no notification;
  `Complete` regardless.
- `src/NimBus.MessageStore.Abstractions/IMessageTrackingStore.cs`: XML doc on the seven
  `Upload*Message` members (true = created/replaced; false = refused by `StaleWriteGuard`;
  failures throw).
- Tests: hoist `CreateMessageContext` / `FakeMessageContext` out of `ResolverServiceTests` into
  an internal helper with `messageId` / `parentMessageId` parameters (today every context is
  `message-1` / `self`); `FakeCosmosDbClient.PendingUploadResult`; refused write → completes, no
  dead-letter, history stored, Comment audit recorded, no notification; applied write still
  notifies. New `ResolverStaleCopyTests` over the real `InMemoryMessageStore` (add a project
  reference to `src/NimBus.Testing`; no cycle): the incident order, the fan-out-lag order, a late
  request over Pending+Handoff, a late request over a Pending row written by
  `HandoffCompletedRequest` (refused), a `ResubmissionRequest` over Failed (applied), a late
  `DeferralResponse` or `PendingHandoffResponse` after Completed (refused), a same-id
  `ResubmissionRequest` after its own response (refused). Keep the settlement-projection pin from
  031 green.

### 6. Same MessageId on re-sends (Spec 030 §5.7)
- `src/NimBus.ServiceBus/MessageContext.cs` `ScheduleRedelivery`: `MessageId =
  receivedMessage.MessageId` instead of a fresh Guid; correct the two stale comments there and in
  `Abandon` that describe per-message lock expiry; update the `IMessageContext.ScheduleRedelivery`
  XML doc. Flip `MessageContextTests.ScheduleRedelivery_CopiesBodyAndStandardProperties_WithNewMessageId`
  to assert the original id.
- `src/NimBus.WebApp/Services/ResolverDeadLetterClient.cs` `CloneForReplay`: keep the source
  `MessageId` (still stamp `DeadLetterOriginalMessageId`). Flip the assertion in
  `ResolverDeadLetterClientTests`; update `docs/plan/resolver-dead-letter-replay.md` acceptance
  item 5.
- Precondition to document (deployment.md, throughput-tuning.md): `RequiresDuplicateDetection`
  must stay off on the Resolver topic (the provisioner never enables it; brownfield namespaces
  must be checked before deploying).

### 7. Docs
- `docs/storage-providers.md`: the `Upload*` bool contract and the rule.
- `docs/message-flows.md`: which message types may reopen a row; late fan-out, rescheduled or
  replayed copies stay visible in the Flow tab but never change a settled row; a throttle chain
  now shares one history entry.
- Spec 030 status line → implemented; Spec 031 §9 trail unchanged.

## Verification
- `dotnet build src/NimBus.sln -c Release`
- `dotnet test tests/NimBus.MessageStore.InMemory.Tests -c Release`
- `dotnet test tests/NimBus.MessageStore.CosmosDb.Tests -c Release` (with the emulator env vars)
  and `dotnet test tests/NimBus.MessageStore.SqlServer.Tests -c Release` (with the SQL env var);
  otherwise rely on CI and say so
- `dotnet test tests/NimBus.Resolver.Tests -c Release`, `tests/NimBus.ServiceBus.Tests`,
  `tests/NimBus.WebApp.Tests --filter ResolverDeadLetterClientTests`, `tests/NimBus.Core.Tests`,
  `tests/NimBus.OpenTelemetry.Tests`
- `/code-review master..feat/stale-write-guard high` before the PR

## Commits (local only; no push)
1. `feat(store): add the stale-write guard and pin it in the conformance suite` (steps 1–2)
2. `feat(store): apply the stale-write guard in the Cosmos and SQL Server providers` (3–4)
3. `feat(resolver): observe refused outcome writes and audit them` (5)
4. `fix(resolver): re-send throttled and replayed copies under the original MessageId` (6)
5. `docs: stale-write guard contract and flows` (7)

## Out of scope
The production repair of the Nav09Endpoint rows (Spec 030 §9) runs after deployment, not from
this branch; the `nb container reconcile-stale-pending` verb is a separate decision. The
capacity library / API / banner (Spec 031 §3.4) and bounded DLQ replay are separate follow-ups.
