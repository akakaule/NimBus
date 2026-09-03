# Resolver dead-letter monitoring and replay implementation plan

## Goal

Give a site `Owner` a safe workflow on the existing Admin → Subscriptions screen to inspect the regular dead-letter queue of the terminal Resolver subscription and replay either the operation snapshot or one exact dead-letter reason. At the same time, make Resolver emit the stable `CosmosDbThrottled` reason when Cosmos rate limiting exhausts the shared Service Bus delivery budget.

The implementation must preserve current subscription-administration behavior, scheduled throttle redelivery, transfer-DLQ separation, and all non-Cosmos transient handling.

## Review decisions

This revision incorporates the actionable findings from `docs/spec/2026-09-03-resolver-dead-letter-replay-review.md`:

- Add a real-Azure feasibility gate before committing to the cross-entity transaction design.
- Bound each inspect/replay snapshot and the request wall-clock budget, bound held locks, reject overlapping in-process replay, inventory every max-delivery literal, correct emulator/current-code claims, and describe the existing heartbeat dead-letter behavior accurately.
- Keep the reason Cosmos-specific. SQL throttling codes do not become `CosmosDbThrottled`; SQL-backed installations can still inspect/replay every reason actually present in their Resolver DLQ.
- Keep atomic replay. The source handoff explicitly requires it, and the existing non-atomic scheduled-redelivery path is not precedent for operator replay: replay deliberately assigns a fresh ID, so send-before-complete failure guarantees a processable duplicate rather than one duplicate-detectable retry.
- Keep the `subscriptionName` route parameter and full server-side guard rails because they are part of the handoff contract and protect future declared terminal Resolver subscriptions, even though today only `Resolver/Resolver` qualifies.
- Keep the plan in `docs/superpowers/plans/` as required by the repository instructions supplied for this task.

## Preconditions and branch

1. Start a dedicated branch such as `feat/resolver-dead-letter-replay` from the latest `origin/master`; do not stack this work on `feat/114-endpoint-circuit-breaker`.
2. Keep the existing untracked `.agents/skills/*` directories out of every commit.
3. Use RED–GREEN–REFACTOR within each phase and keep the commits below independently buildable.
4. Run backend tests in `Release`, because that is where the repository treats warnings as errors.
5. Complete Phase 0 against a disposable Azure Service Bus Standard or Premium namespace before starting product or emulator implementation. If the exact DLQ transaction cannot be proven, stop and revise the feature contract; do not silently substitute non-atomic replay.

## NimBus-specific design decisions

### One logical delivery budget across scheduled and broker retries

NimBus does not retry Cosmos throttling exactly like DIS. `ResolverService` calls `IMessageContext.ScheduleRedelivery`, which creates a new scheduled message carrying `ThrottleRetryCount` and completes the original. That replacement starts again at broker `DeliveryCount == 1`; if scheduling is unavailable, NimBus leaves the current message unsettled and the broker increments its delivery count.

Use the following logical attempt number so both paths consume the same budget without losing the existing delay:

```text
logicalAttempt = ThrottleRetryCount + DeliveryCount
```

- On attempts below `Constants.ServiceBusMaxDeliveryCount`, schedule the replacement with `ThrottleRetryCount = logicalAttempt`.
- On the final attempt, explicitly dead-letter once with `CosmosDbThrottled`.
- If the transport does not implement the additive delivery interface, use `DeliveryCount = 1`, preserving non-Service-Bus/test implementations.

For example, ten successful scheduled deliveries have `(0+1), (1+1), …, (9+1)` and dead-letter on attempt 10. If scheduling falls back to lock expiry, `(0+1), (0+2), …` reaches the same limit.

On the lock-expiry fallback, delivery 10 is still delivered under PeekLock; Resolver explicitly dead-letters while that lock is held. A host crash before settlement can still let the broker apply `MaxDeliveryCountExceeded`. That crash race exists for every explicit final-attempt settlement and cannot be eliminated by dead-lettering on attempt 9 without reducing the configured delivery budget.

Put the existing value `10` in `NimBus.Core.Messages.Constants.ServiceBusMaxDeliveryCount`, and use it from Resolver, both subscription-creation paths, and AsyncAPI output. Do not add delivery count to `IMessageContext`; add `IMessageDeliveryContext` and implement it only on the Service Bus context.

### Central Cosmos throttle settlement

Catch `RequestLimitException` before `StorageProviderTransientException` in `ResolverService.Handle`. Let Cosmos 429s from the normal message write, endpoint projection, heartbeat write, and Resolver service-health write reach that one catch.

Heartbeat and liveness currently swallow all storage transients to leave the message unsettled. Change only their `RequestLimitException` branch to bubble to the central throttle handler. Other storage-transient heartbeat behavior remains unchanged. Update the comments which currently claim heartbeat traffic is never dead-lettered so they describe the Cosmos-final-attempt exception accurately.

Log the full exception, event/session identity, logical attempt, and broker delivery count server-side. The dead-letter description may contain the exception for trusted broker diagnostics, but no public response or audit data may contain Cosmos text.

### Replay boundary and atomicity

Use sequence numbers only as identities for one operation, never as a replacement for Service Bus ordering:

1. Peek the regular DLQ in batches of 100 from sequence `0`, advancing to `last.SequenceNumber + 1` until empty or `MaxReplaySnapshotMessages` is reached. Peek one extra record to set `isTruncated` without retaining it.
2. Retain only `(SequenceNumber, DeadLetterReason)` in the snapshot.
3. Select all snapshot sequence numbers or exact ordinal reason matches, including `null`, and record the greatest selected sequence as the boundary.
4. Receive the regular DLQ in PeekLock batches. Hold and renew locks for non-selected messages and arrivals beyond the boundary; abandon all held messages in `finally`. Because the replay boundary is inside the capped snapshot, the held set is bounded by the snapshot cap plus one receive batch.
5. For a selected message, construct the replacement, then complete the DLQ message first and send second inside an async `TransactionScope`. Count success only after scope commit.
6. Report selected snapshot entries not observed during receive as failed. Do not substitute concurrent arrivals.

Set `MaxReplaySnapshotMessages = 500` and a 180-second server-side operation budget. The inspect response includes `isTruncated` and `snapshotLimit`; the UI must state that only the first snapshot batch is represented and that the operator can repeat the operation. This keeps one request below App Service's 230-second front-end timeout, caps selected messages and held locks, and still preserves exact reason grouping within each operation snapshot. If the 180-second budget expires first, stop receiving, release held locks, count the unprocessed selected entries as failed, return the partial result, and write the audit row.

Allow only one replay per Resolver subscription within a WebApp process using a non-blocking singleton gate; a second request receives `409 Conflict` and does not inspect or settle messages. The shipped App Service plan has one instance by default. Document that operators must not scale the management WebApp out while a replay is running; adding a provider-neutral distributed lease is a separate prerequisite before replay is supported on a scaled-out WebApp.

The replay client owns a dedicated `ServiceBusClient` created with `EnableCrossEntityTransactions = true`. The normal WebApp client remains unchanged and cannot share this transactional connection. On that dedicated client, create/use the DLQ receiver before the sender, receive outside the transaction, then make `CompleteMessageAsync` the first operation inside each `TransactionScope` and `SendMessageAsync` the second. Phase 0 must prove this exact ordering against Azure.

### API, target guard rails, and audit

The routes are fixed to the Resolver topic, so the only caller-controlled target is `subscriptionName`. Before any data-plane operation, `SubscriptionAdminService` must verify:

- `TopologyDescriptor.FindSubscription("Resolver", subscriptionName, platform, isEmulator)` returns a declaration;
- the declaration is session-enabled and has no `ForwardTo`;
- the actual subscription exists;
- the actual subscription is session-enabled and currently has no forward destination.

Return 404 only when the actual subscription is missing. Return 400 for an undeclared, forwarding, or non-session target and for an invalid replay body. Both endpoints require `AccessRole.Owner`.

Reuse `MessageAuditType.ManageSubscription`. Inspection follows the existing read policy and is not audited. Replay writes one audit row for denied access and one result row for an allowed call. Result data contains only action, topic, subscription, scope, selected reason, processed/succeeded/failed counts, and overall success. For the nullable-reason selection, record `reason: null`; never add body, broker error text, dead-letter description, or returned error strings.

### CrmErpDemo emulator compatibility is part of completeness

The main `src/NimBus.AppHost` still targets Azure Service Bus by default. The `samples/CrmErpDemo` AppHost now defaults to `NimBus.ServiceBusEmulator`, and its Admin UI must not expose a replay workflow that always fails. The emulator spec currently lists DLQ browsing as P2 and AMQP transactions as a permanent non-goal because NimBus had no callers. Once Phase 0 proves the Azure shape, implement the narrow stock-SDK surface used here and update Spec 027; do not add queues, transfer-DLQ browsing, general transaction APIs, or a non-atomic emulator fallback.

The emulator transaction needs only one operation shape: complete one locked regular-DLQ message and publish one message to a topic on the same client/session, then commit or roll back both. `BrokerNamespace` currently serializes mutation with one namespace-wide `_gate` monitor; it has no implemented actor/mailbox boundary. Stage protocol state across declare/operations/discharge, then apply the validated remove-and-publish pair under that existing gate as one broker operation.

Production Bicep already grants the WebApp managed identity `Azure Service Bus Data Owner` on the namespace, which covers receive, settlement, and send. Keep that assignment and pin it with a regression test/documentation assertion rather than adding duplicate roles.

### Provider scope

`RequestLimitException` is intentionally Cosmos-specific. SQL Server maps resource-governance errors to the broader `StorageProviderTransientException`, so they retain the current generic delayed-retry behavior and will not be mislabeled as Cosmos failures. The replay transport and UI are provider-neutral: SQL-backed deployments can inspect and replay any regular-DLQ reasons they have, including a nullable reason. A future stable SQL throttle reason requires its own named contract and is out of scope.

## Phase 0 — Prove the Azure transaction shape

Before changing product code, create a disposable, ignored spike test or small console harness using the repository's pinned `Azure.Messaging.ServiceBus` 7.20.2 against a real Standard namespace:

1. Create a session-enabled topic subscription, dead-letter one message, and receive it through a normal receiver with `SubQueue.DeadLetter`.
2. Construct a dedicated client with `EnableCrossEntityTransactions = true`.
3. Create/use the DLQ receiver first, receive outside the transaction, then run complete-first/send-second inside `TransactionScope(TransactionScopeAsyncFlowOption.Enabled)`.
4. Prove commit removes the DLQ source and publishes exactly one replacement to the topic.
5. Force the send to fail after completion is enlisted and prove abort leaves the original DLQ message and publishes no replacement.
6. Repeat on the minimum production SKU (Standard); Premium-only success is insufficient because `deploy/bicep/templates/servicebusNamespace.bicep` provisions Standard.

Record the SDK version, SKU, exact entity paths/order, and results in the implementation PR. Do not commit credentials or a permanently credential-dependent test. If either commit or rollback fails, mark the implementation blocked and revise the feature design before Phase 1; send-then-complete is not an authorized fallback.

## Phase 1 — Characterize and centralize the delivery budget

### Tests first

Update or add tests in:

- `tests/NimBus.ServiceBus.Tests/MessageContextTests.cs`
- `tests/NimBus.CommandLine.Tests/ServiceBusTopologyProvisionerTests.cs`
- `tests/NimBus.ServiceBus.Tests/AsyncApiExporterTests.cs`
- `tests/NimBus.CommandLine.Tests/AsyncApiExporterTests.cs`

Cover:

1. `MessageContext` exposes its wrapped Service Bus `DeliveryCount` through `IMessageDeliveryContext`.
2. A non-Service-Bus `IMessageContext` remains source-compatible and does not need a new member.
3. Every newly provisioned subscription receives the shared max-delivery value through both `ServiceBusTopologyProvisioner` and `ServiceBusManagement.CreateSubscription`/Clear Endpoint paths.
4. Both AsyncAPI subscription shapes, including the spec-022 dynamic-event map, use the same value.
5. The emulator round-trips an explicitly configured shared value. Its two internal fallback `10` values remain Azure-emulation defaults, but are centralized under one emulator-local default and pinned equal to the current Azure/NimBus default so drift is visible.

### Implementation

- Add `src/NimBus.Core/Messages/IMessageDeliveryContext.cs` with a documented read-only `int DeliveryCount`.
- Add `ServiceBusMaxDeliveryCount = 10` to `src/NimBus.Core/Constants.cs`.
- Implement the additive interface in `src/NimBus.ServiceBus/MessageContext.cs` by returning `_sbMessage.DeliveryCount`.
- Replace literals in:
  - `src/NimBus.ServiceBus/Provisioning/ServiceBusTopologyProvisioner.cs`
  - `src/NimBus.Management.ServiceBus/ServiceBusManagement.cs`
  - both `maxDeliveryCount` mappings in `src/NimBus.ServiceBus/AsyncApi/AsyncApiExporter.cs`
- Sweep the entire repository for `MaxDeliveryCount`/`maxDeliveryCount` and classify every remaining `10`. Consolidate the Azure-emulator protocol defaults in:
  - `src/NimBus.ServiceBusEmulator/Admin/AdminXml.cs`
  - `src/NimBus.ServiceBusEmulator/Broker/BrokerModels.cs`
  under one emulator-local default rather than coupling the standalone broker to `NimBus.Core` for a product constant.
- Add a test-time invariant comparing that emulator default with `Constants.ServiceBusMaxDeliveryCount`; if NimBus intentionally changes its provisioned limit later, the test forces an explicit decision about whether the Azure-compatible emulator fallback should remain 10.

### Verification

```powershell
dotnet test tests/NimBus.ServiceBus.Tests/NimBus.ServiceBus.Tests.csproj -c Release
dotnet test tests/NimBus.CommandLine.Tests/NimBus.CommandLine.Tests.csproj -c Release
dotnet test tests/NimBus.ServiceBusEmulator.Tests/NimBus.ServiceBusEmulator.Tests.csproj -c Release
```

Commit: `refactor(servicebus): centralize delivery attempt limit`

## Phase 2 — Emit the stable Resolver throttle reason

### Tests first

Extend:

- `tests/NimBus.Resolver.Tests/ResolverServiceTests.cs`
- `tests/NimBus.Resolver.Tests/ResolverHeartbeatTests.cs`
- `tests/NimBus.Resolver.Tests/ResolverLivenessProbeTests.cs`

Make the Resolver fake context implement `IMessageDeliveryContext` and independently configure `ThrottleRetryCount` and `DeliveryCount`. Add cases for:

1. Cosmos 429 before the final logical attempt schedules delayed redelivery, does not complete, and does not dead-letter.
2. The scheduled replacement receives `ThrottleRetryCount = logicalAttempt`, including a case after broker redelivery proves the two counters combine correctly.
3. Cosmos 429 on the final logical attempt dead-letters exactly once with `CosmosDbThrottled` and does not schedule/complete/abandon.
4. A final-attempt 429 from `StoreMessage`, endpoint-state projection, endpoint-heartbeat persistence, and Resolver service-health persistence uses the same stable reason.
5. A non-429 `StorageProviderTransientException` retains today’s scheduled event retry and unsettled heartbeat/liveness behavior.
6. Unexpected exceptions retain the existing generic dead-letter path.
7. Caller cancellation still escapes without settlement.

### Implementation

- Replace `MaxThrottleRetries` in `src/NimBus.Resolver/Services/ResolverService.cs` with the shared Service Bus limit.
- Add a private `CosmosThrottleDeadLetterReason = "CosmosDbThrottled"`.
- Catch `RequestLimitException` first and route it to one `HandleCosmosThrottle` method.
- Calculate the logical attempt as described above and preserve the existing exponential/provider-hinted scheduling logic below the limit.
- In heartbeat and self-probe persistence, rethrow `RequestLimitException` and retain the existing catch for other storage transients.
- Update the heartbeat remarks to describe reality: generic transient failures are left unsettled and may eventually be broker-dead-lettered as `MaxDeliveryCountExceeded`; Cosmos throttling is now explicitly settled with the stable reason on the final logical attempt.
- Note in code review that replacing the private `MaxThrottleRetries = 10` with the shared constant is numerically neutral; the behavior change comes from combining scheduled/broker counts and the Cosmos-specific final settlement.

### Verification

```powershell
dotnet test tests/NimBus.Resolver.Tests/NimBus.Resolver.Tests.csproj -c Release
```

Commit: `fix(resolver): identify exhausted cosmos throttling`

## Phase 3 — Add the proven regular-DLQ transaction shape to the NimBus emulator

Start this phase only after Phase 0 succeeds on Azure Standard. Mirror the observed SDK frames and ordering; do not design an independent transaction dialect from the documentation alone.

### Tests first

Add stock Azure SDK smoke tests to `tests/NimBus.ServiceBusEmulator.Tests/SdkSmokeTests.cs` and broker-level atomicity tests to `BrokerNamespaceTests.cs`:

1. A normal receiver with `SubQueue.DeadLetter` can peek and PeekLock-receive from a session-enabled subscription without accepting a session.
2. DLQ peek pagination begins at the supplied sequence number and returns broker `DeadLetterReason`, including a missing reason.
3. Completing a DLQ message and sending its replacement through a client with cross-entity transactions commits both changes.
4. An aborted transaction leaves the original in the DLQ and publishes no replacement.
5. Invalid/expired locks make the whole transaction fail without a partial publish.
6. Transfer DLQ remains inaccessible through this surface.

### Implementation

- Update `docs/specs/027-service-bus-emulator/spec.md` so regular-DLQ browsing and the single complete-plus-send transaction are required NimBus surface; retain broader AMQP transactions as out of scope.
- Extend `src/NimBus.ServiceBusEmulator/Protocol/AmqpFrontend.cs` to register regular-DLQ management nodes and a transaction coordinator.
- Extend `src/NimBus.ServiceBusEmulator/Protocol/ManagementRequestProcessor.cs` and `src/NimBus.ServiceBusEmulator/Protocol/BrokerLinkProcessor.cs` to parse the `/$DeadLetterQueue` suffix, use the regular-DLQ store, and preserve transfer-DLQ exclusion.
- Add a narrowly scoped transaction registry/coordinator under `src/NimBus.ServiceBusEmulator/Protocol/` for SDK `declare`, transactional transfer/disposition state, `discharge` commit, and rollback-on-close/abort.
- Extend `src/NimBus.ServiceBusEmulator/Broker/BrokerNamespace.cs` and `src/NimBus.ServiceBusEmulator/Broker/BrokerModels.cs` with explicit subqueue selection and one atomic validated `complete DLQ + publish topic` command. Keep staged network state outside `_gate`; acquire the existing namespace-wide monitor only to validate and commit the pair, and never hold it across a network await.
- Release locks and discard staged publishes when a transaction is aborted, disconnected, or times out.

### Verification

```powershell
dotnet test tests/NimBus.ServiceBusEmulator.Tests/NimBus.ServiceBusEmulator.Tests.csproj -c Release
```

Commit: `feat(emulator): support atomic resolver dlq replay`

## Phase 4 — Implement the replay transport

### Tests first

Add `tests/NimBus.WebApp.Tests/ResolverDeadLetterClientTests.cs`. Use hand-written subclasses of the Azure SDK client/receiver/sender (the WebApp test project deliberately has no mocking dependency) and model messages with `ServiceBusModelFactory`.

Cover every transport invariant:

1. Inspection groups exact ordinal reasons, preserves a `null` bucket, returns `long` totals, and sorts by count descending then reason ordinal.
2. `reason` scope handles case-sensitive exact matches only; null selects only missing reasons.
3. `all` selects every message in the starting snapshot.
4. Non-selected and beyond-boundary messages are renewed when necessary, abandoned, and never completed.
5. A replay gets a GUID-based new `MessageId`; body, session, correlation/reply metadata, subject/content type, TTL, partition/sendable headers, and ordinary application properties survive.
6. `DeadLetterReason` and `DeadLetterErrorDescription` are removed, while `DeadLetterOriginalMessageId` and `DeadLetterOriginalReason` are added.
7. Completion precedes send inside the transaction, and success is counted only after commit.
8. A complete/send/commit failure produces a stable per-sequence error, logs the exception, abandons or leaves the original unsettled, and increments failed.
9. A selected sequence missing from receive is counted failed and no concurrent message replaces it.
10. Receivers and sender are disposed on success, failure, and cancellation.
11. Snapshot retention stops at 500, peeks one extra item to report truncation, and never holds more than the cap plus one receive batch.
12. The 180-second operation budget returns an audited partial result after releasing held locks instead of relying on the App Service request timeout.
13. A simultaneous replay in the same WebApp process receives the stable in-progress failure and performs no broker operation.

Add one integration test using the Phase 3 emulator to prove the real Azure SDK transaction path, not only call ordering in fakes.

### Implementation

Add `src/NimBus.WebApp/Services/ResolverDeadLetterClient.cs` containing:

- `IResolverDeadLetterClient` with overview and resubmit operations;
- a sealed async-disposable implementation which owns the dedicated transactional client;
- immutable internal snapshot entries;
- bounded peek/receive helpers, held-lock renewal, generic error creation, and replay-message cloning.

Important implementation details:

- Create receivers with `ReceiveMode = PeekLock` and `SubQueue = DeadLetter`; do not use `ServiceBusSessionReceiverOptions`.
- Use ordinal equality and ordering explicitly.
- Keep held messages in a dictionary keyed by sequence number to prevent duplicate abandon/renew attempts.
- Enforce `MaxReplaySnapshotMessages = 500`, retain at most one additional peek record to detect truncation, and stop receiving immediately after the selected boundary while preserving only the remainder of that bounded receive batch.
- Use a monotonic 180-second budget linked with caller cancellation. Budget expiry produces a partial `BulkOperationResult`; caller cancellation still propagates after cleanup.
- Put a non-blocking per-subscription `SemaphoreSlim` in the singleton replay client and translate contention to the typed 409 response at the controller boundary.
- On per-message failure, add the original to the held set only when it is still settleable; let the final abandon be best-effort and log failures.
- Use cancellation tokens throughout. Cancellation stops scanning, releases held locks in `finally`, then propagates rather than returning a misleading partial success.
- Public errors are fixed templates containing at most a sequence number/subscription target; broker exceptions remain in logs.
- Create/use the receiver before constructing the sender. Inside each async transaction, complete first and send second exactly as proven in Phase 0.

Refactor `src/NimBus.WebApp/Startup.cs` so connection-string and FQNS/credential construction can create:

- the existing ordinary singleton `ServiceBusClient` with default options;
- a distinct client owned by `ResolverDeadLetterClient`, with `EnableCrossEntityTransactions = true`.

Do not register two bare `ServiceBusClient` instances in DI.

### Verification

```powershell
dotnet test tests/NimBus.WebApp.Tests/NimBus.WebApp.Tests.csproj -c Release --filter ResolverDeadLetterClientTests
dotnet test tests/NimBus.ServiceBusEmulator.Tests/NimBus.ServiceBusEmulator.Tests.csproj -c Release --filter Resolver_dead_letter
```

Commit: `feat(webapp): add resolver dead-letter replay transport`

## Phase 5 — Add topology validation, HTTP contract, authorization, and audit

### Tests first

Extend `tests/NimBus.WebApp.Tests/SubscriptionAdminServiceTests.cs` with a recording `IResolverDeadLetterClient`, and add focused controller/HTTP tests such as `AdminResolverDeadLetterTests.cs`:

1. A declared, actual, terminal, session-enabled Resolver subscription delegates inspect and replay with the parameter order intact.
2. Unknown declarations, non-session declarations/actual entities, and declared/actual forwarding entities return 400 without touching the data plane.
3. A missing actual subscription returns 404.
4. Invalid scope values, `all` plus non-null reason, missing body, and reason-scope semantics are rejected before the broker call.
5. Owner requests succeed; lower roles and anonymous requests return 403 for both routes.
6. Inspection does not add an audit row, matching current administrative reads.
7. Allowed replay writes `ManageSubscription` with the stable action, target, selection, counts, and success only.
8. Denied replay writes an access-denied `ManageSubscription` audit without request payload or exception data.
9. Transport failures return stable, generic responses and retain details only in captured server logs.
10. A replay already running for the subscription returns 409 and writes no second result audit.
11. A truncated snapshot exposes its cap explicitly; result/audit counts refer only to that bounded operation snapshot.

Update the existing `ThrowingSubscriptionAdminService` in `AdminStatusSafetyTests.cs` when the interface grows.

### OpenAPI and generated code

Update `src/NimBus.WebApp/api-spec.yaml` with:

- `GET /api/admin/servicebus/resolver/subscriptions/{subscriptionName}/deadletters`;
- `POST /api/admin/servicebus/resolver/subscriptions/{subscriptionName}/deadletters/resubmit`;
- `DeadLetterOverview` (`totalMessageCount: int64`, `isTruncated: boolean`, `snapshotLimit: int32`);
- `DeadLetterReasonCount` (`reason` nullable, `count: int64`);
- `DeadLetterResubmitRequest` with generated `all`/`reason` enum values and nullable reason;
- existing `BulkOperationResult` as the replay response;
- explicit 400/403/404/409 responses.

Run the existing NSwag target and commit both generated outputs:

- `src/NimBus.WebApp/Controllers/ApiContract.g.cs`
- `src/NimBus.WebApp/ClientApp/src/api-client/index.ts`

Do not hand-edit either generated file.

### Service and controller implementation

- Extend `ISubscriptionAdminService.cs` and `SubscriptionAdminService.cs` with Resolver DLQ overview/resubmit methods and the shared target validator.
- Inject `IResolverDeadLetterClient`; keep the existing ordinary client for purge behavior.
- Add a typed unsupported-target exception next to the existing subscription admin exceptions; continue using `SubscriptionNotFoundException` for 404.
- Implement the generated controller methods in `src/NimBus.WebApp/Controllers/ApiContract/AdminImplementation.cs`, applying `AccessRole.Owner`, input validation, typed status mapping, and sanitized `ManageSubscription` audit data.
- Register the new client/service dependencies in `Startup.cs`.
- Add a Bicep regression assertion in `tests/NimBus.CommandLine.Tests/BicepTemplateProviderTests.cs` that the WebApp path retains namespace-scoped Service Bus Data Owner. No Bicep role change is expected.

### Verification

```powershell
dotnet build src/NimBus.WebApp/NimBus.WebApp.csproj -c Release
dotnet test tests/NimBus.WebApp.Tests/NimBus.WebApp.Tests.csproj -c Release
dotnet test tests/NimBus.CommandLine.Tests/NimBus.CommandLine.Tests.csproj -c Release
git diff --exit-code -- src/NimBus.WebApp/Controllers/ApiContract.g.cs src/NimBus.WebApp/ClientApp/src/api-client/index.ts
```

The final `git diff --exit-code` is run after a second WebApp build and proves generation is deterministic and checked in.

Commit: `feat(webapp): expose resolver dead-letter administration api`

## Phase 6 — Extend the existing subscription UI

### Tests first

Extend `src/NimBus.WebApp/ClientApp/src/components/admin/subscription-manager.test.tsx` and, if extracted, add `resolver-dead-letter-dialog.test.tsx`:

1. `Inspect dead letters` appears only for the Resolver topic’s terminal, session-enabled subscription.
2. Endpoint topics, forwarding subscriptions, non-session subscriptions, zero regular-DLQ counts, and transfer-only DLQs cannot start the workflow.
3. The button is disabled during another row action.
4. The dialog renders loading/error states, total count, `All dead letters (N)`, exact reason rows, and the null-reason row without conflating it with an empty-string reason.
5. Selecting `CosmosDbThrottled` sends `{ scope: "reason", reason: "CosmosDbThrottled" }` with the correct subscription.
6. Selecting all sends `{ scope: "all" }` with no reason.
7. The action label pluralizes the selected count and disables while submitting.
8. Success says `Resubmitted X of N message(s).`; partial failure states how many remain dead-lettered.
9. After replay, both the dialog snapshot and topic/subscription counters refresh.
10. If replay commits but either refresh fails, the success remains visible and a separate refresh warning is shown.
11. A truncated overview says that counts cover the first 500-message snapshot batch, changes the all label to `All messages in this snapshot (N)`, and tells the operator the operation can be repeated.
12. A 409 response reports that another replay is already running without replacing prior success feedback.

### Implementation

- Extract `resolver-dead-letter-dialog.tsx` under `ClientApp/src/components/admin/` so snapshot/selection/replay state does not further enlarge `subscription-manager.tsx`.
- Represent selection as a discriminated union (`all` or `reason` carrying `string | null`) so null reason and empty-string reason remain distinct.
- Reuse the existing modal, radio, button, spinner, feedback, count formatting, and row busy-state patterns.
- In `subscription-manager.tsx`, gate the action on exact Resolver topic identity plus `requiresSession`, empty `forwardTo`, and `deadLetterMessageCount > 0`; do not use combined regular+transfer count.
- Let replay completion call the existing refresh path for topics/subscriptions and a dialog callback for its snapshot.
- Preserve the handoff's `All dead letters (N)` wording only for a complete snapshot; use the explicit bounded-snapshot wording when `isTruncated` is true.

### Verification

```powershell
Set-Location src/NimBus.WebApp/ClientApp
npm test -- --run subscription-manager resolver-dead-letter-dialog
npm run build
```

Commit: `feat(webapp): add resolver dead-letter replay dialog`

## Phase 7 — Documentation and end-to-end acceptance

Update `docs/service-bus-subscription-admin.md` with:

- the regular-vs-transfer DLQ boundary;
- Resolver-only eligibility and Owner authorization;
- snapshot and exact-reason behavior;
- new message IDs, provenance properties, and ordering caveat;
- atomic transaction guarantee and required namespace support;
- 500-message snapshot/180-second request bounds, repeat workflow, and the single-management-instance concurrency requirement;
- stable `CosmosDbThrottled` interpretation;
- Cosmos-only naming and SQL-backed replay availability for other reasons;
- production identity permission and default emulator support.

Update `docs/message-flows.md` where it currently describes generic Resolver transient/dead-letter behavior, and link the operator guide from the relevant section. Do not add a new monitoring page or advertise arbitrary subscription replay.

Commit: `docs: document resolver dead-letter replay`

### Full automated gate

From the repository root:

```powershell
dotnet restore src/NimBus.sln
dotnet build src/NimBus.sln -c Release --no-restore
dotnet test src/NimBus.sln -c Release --no-build
Set-Location src/NimBus.WebApp/ClientApp
npm test -- --run
npm run build
```

Then rebuild the WebApp once more and require a clean generated-code diff.

### Operational acceptance

Run the scenario first against the default Aspire emulator and then against an Azure Standard/Premium namespace:

1. Force Resolver Cosmos writes to return 429s.
2. Observe delayed retries below the shared logical attempt limit.
3. Observe final-attempt regular DLQ placement with exact reason `CosmosDbThrottled`.
4. Confirm topic/subscription counters keep regular and transfer dead letters separate.
5. Inspect the Resolver DLQ and verify exact reason counts, including one different reason; with more than 500 messages, verify explicit truncation and bounded held locks.
6. Replay only `CosmosDbThrottled`.
7. Confirm matching messages are re-enqueued and processed, the other reason remains in the DLQ, and concurrent arrivals were not selected.
8. Confirm every replay has a new ID plus original ID/reason provenance in trusted telemetry.
9. Induce a send failure and confirm transaction rollback leaves the source message in the DLQ.
10. Start an overlapping replay and confirm it is rejected rather than reporting locked snapshot messages as failed.
11. Confirm the audit contains target, selection, counts, and success only, including partial results caused by the operation budget.

Capture the emulator and Azure results in the implementation PR. If the Azure namespace SKU/configuration rejects cross-entity transactions, treat that as a deployment blocker; do not weaken atomicity or add send-then-complete fallback behavior.

## Explicit non-goals

- Transfer-DLQ replay or inspection.
- Endpoint-topic or arbitrary-topic replay.
- Editing message body/properties before replay.
- Preserving original enqueue time, sequence number, or relative session ordering.
- Scheduled/automatic replay.
- Inferring Cosmos throttling from `MaxDeliveryCountExceeded`.
- Adding a second admin page or changing existing purge/recreate semantics.
- General-purpose AMQP transactions in the emulator beyond this one complete-plus-send shape.
- Relabeling Azure SQL resource-governance errors as `CosmosDbThrottled`; a future provider-neutral stable throttle taxonomy is separate work.
- Distributed replay leasing for a scaled-out management WebApp; until that exists, replay requires the shipped single-instance deployment shape.
