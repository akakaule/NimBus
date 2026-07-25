# Code Review Feedback

**Date:** 2026-07-13
**Scope:** Unpushed commits (`5b32dd2` receiver recovery fix, `3e6d6ed` PartnerPortal showcase) plus all uncommitted working-tree changes (~50 files, ~1,450 insertions: CLI secret hardening, Cosmos exception-translation refactor, SDK event-handler scoping rework, WebApp admin delete/skip-by-status, Bicep updates).
**Method:** 8 independent finder angles (3 correctness, 3 cleanup, altitude, conventions) → 40 raw candidates → 24 after dedup → each adversarially verified. 10 findings survived; notable refutations listed at the end.

---

## Findings (most severe first)

### 1. `EventHandlerContext.EventType` is silently null for legacy messages
**File:** `src/NimBus.SDK/EventHandlers/EventJsonHandler.cs:35` — **CONFIRMED**

`EventType` is now sourced from `context.EventTypeId` (the Service Bus application property) instead of the body's `EventContent.EventTypeId`. For legacy messages that carry the type only in the body, `MessageContext.EventTypeId` returns null (`MessageContext.cs:58`), yet the same changeset's dispatch fallback in `EventContextHandler` deliberately keeps those messages working — so the handler runs with `EventType = null` where the old code supplied the body value.

**Impact:** Handlers that forward the value (e.g. `CrmAccountCreatedHandler.cs:30` stamps `EventTypeId = context.EventType` into its handoff payload) silently emit null downstream.
**Suggested fix:** Fall back to the body value: `context.EventTypeId ?? context.MessageContent.EventContent.EventTypeId`.

### 2. All six Cosmos transient catch/log blocks are unreachable in production
**File:** `src/NimBus.MessageStore.CosmosDb/CosmosDbClient.cs:503` (also 550, 1651, 1802, 1986, 2084) — **CONFIRMED**

`CosmosContainerAdapter` now translates transient `CosmosException`s (429/503/timeout) into `RequestLimitException`/`StorageProviderTransientException` before the client sees them (`CosmosAbstractions.cs:95-108`), so the client's `catch (CosmosException)` blocks — including both the `LogWarning` transient branch and the trailing `LogError` — never fire for real traffic. Store-layer logging for throttled writes is gone; during a Cosmos throttling incident operators see Resolver retries with zero corresponding store logs.

Unit tests pass only because `FakeContainerAdapter` throws raw `CosmosException`, bypassing the real adapter's translation. The test that asserted this logging (`SetEndpointMetadata_reports_throttled_upserts_through_logger`) was deleted in this diff.

**Suggested fix:** Pick one translation layer (the adapter). If store-layer logging is wanted, catch the translated exception types there — and reduce the six near-identical blocks to one shared helper while at it. Related confirmed cleanups in the same layer:
- `CosmosExceptionTranslation.cs:115` — the `when (IsTransient(ex))` filter guarantees `ThrowIfTransient` throws, so the following `throw;` is unreachable dead code.
- `CosmosDbClient.cs:1705` — `GetEventHistory` double-translates: the iterator from the adapter is already translation-wrapped, and the loop wraps `ReadNextAsync` in `TranslateTransientAsync` again.
- `CosmosAbstractions.cs:89` — `GetItemLinqQueryable` is the only untranslated adapter member, forcing manual `Wrap` at three call sites (382, 947, 1079); the next LINQ query added will silently skip translation.

### 3. Dynamic handler DI overloads silently inverted documented lifetime semantics
**File:** `src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs:160` — **CONFIRMED**

The removed XML doc promised the factory "is called once when the ISubscriberClient singleton is resolved, so it behaves like a singleton handler." The new code invokes the factory once **per message** from a per-message `AsyncServiceScope` that is disposed after handling. This is clearly intentional (enables scoped dependencies) but is a silent breaking change to documented public-API semantics.

**Impact:** An existing subscriber whose dynamic handler accumulates cross-message state (dedup cache, batch aggregator) silently resets per message after upgrading, and its `IDisposable` dependencies are disposed after each message — no compile error, no warning.
**Suggested fix:** At minimum call it out loudly in release notes/XML docs; consider a registration option preserving resolve-once semantics for the old overloads.

### 4. Cancelled shutdown leaks a still-running session processor
**File:** `src/NimBus.SDK/Hosting/NimBusReceiverHostedService.cs:162` — **CONFIRMED**

`StopAndDisposeProcessorAsync` swaps `_processor` to null (line 150) **before** `StopProcessingAsync`. If the host-shutdown token cancels mid-stop, the filtered catch (160-163) rethrows with no `finally` — `DisposeProcessorAsync` is skipped and the already-nulled processor reference is unreachable: never stopped, never disposed, still holding session locks and potentially pumping `OnMessageAsync`. Scope note: only the host-shutdown-token path is affected (the recovery path passes `CancellationToken.None`, so its filter never matches), and the process usually exits shortly after — but the recovery-restart path shares this method, so a leak there would matter more.

**Suggested fix:** Wrap stop/detach/dispose in `try/finally` so `DisposeProcessorAsync` always runs on the captured reference. Related confirmed cleanup: the relocated handler-detach block (line ~179) is functionally inert — the processor is disposed and dropped immediately after — deleting it entirely is the simpler fix for the original recovery crash.

### 5. Outbox cancellation catch re-marks the full batch and can mask the cancellation
**File:** `src/NimBus.Core/Outbox/OutboxDispatcher.cs:90` — **CONFIRMED**

The catch fires for any OCE when the token is cancelled — including one thrown by the in-try `MarkAsDispatchedAsync` at line 80 (SqlServerOutbox processes id chunks, so cancellation can land after some rows committed) — then re-runs `MarkAsDispatchedAsync` for the **full** dispatched list. Two problems: (a) `IOutbox` documents no idempotency requirement and the diff's own test double appends duplicates; (b) if the retry throws (DB unreachable at shutdown), that exception replaces the OCE, so `OutboxDispatcherHostedService` misses its OCE filter and logs a spurious dispatch failure on plain shutdown.

**Suggested fix:** Only re-mark ids not yet covered by the in-try call (or track which call threw), run the compensating mark under its own try/catch, and document the idempotency expectation on `IOutbox` if it is required.

### 6. `SafeJsonSettings` silently halves allowed JSON nesting depth for all subscriber payloads
**File:** `src/NimBus.SDK/EventHandlers/EventJsonHandler.cs:28` — **PLAUSIBLE**

The old call was a bare `JsonConvert.DeserializeObject<T>` (effective MaxDepth 64 in Newtonsoft 13); the new `Constants.SafeJsonSettings` sets `MaxDepth = 32` (`Constants.cs:23`). An existing event nesting 33-64 levels now throws `JsonReaderException` on every delivery and dead-letters after retries, on an upgrade with no payload change. No evidence such payloads exist, hence plausible rather than confirmed — but it is a silent contract narrowing worth a release note.

Note: the related claim that explicit settings bypass host-configured `JsonConvert.DefaultSettings` was **refuted** — `JsonSerializer.CreateDefault(settings)` still applies DefaultSettings and only overlays the explicitly-set properties.

### 7. `Process.Kill(entireProcessTree: true)` failure skips the wait guarding secure temp-file deletion
**File:** `src/NimBus.CommandLine/ProcessRunner.cs:119` — **PLAUSIBLE**

`Kill(true)` can throw `Win32Exception`/`AggregateException` (descendant denies termination); the only handler is `catch (InvalidOperationException) when (process.HasExited)`. On that path the compensating `WaitForExitAsync` (line 127) is skipped, the exception replaces the `OperationCanceledException`, and `AzureDeploymentCommand.Dispose` deletes the parameters.json temp directory while the az child tree may still be alive and reading it — the exact race this block exists to prevent.

**Suggested fix:** Also catch `Win32Exception`/`AggregateException` around `Kill`, log, and still attempt the bounded wait before disposal.

### 8. PartnerPortal consumer can die silently while the publisher keeps running
**File:** `samples/CrmErpDemo/PartnerPortal/Program.cs:175` — **CONFIRMED** (sample code)

The transient-retry catch wraps only `ReceiveMessageAsync`; `CompleteMessageAsync` sits outside it. An AMQP drop between receive and complete (documented emulator 2.0.0 warm-up behavior) or a lost lock faults `ConsumeErpCloudEventsAsync`, and `Task.WhenAll` never surfaces it because `PublishLeadsAsync` loops forever — PartnerPortal keeps publishing but permanently stops consuming; the unsettled message redelivers until dead-lettered.

**Suggested fix:** Move `CompleteMessageAsync` inside the transient-retry scope, and use `Task.WhenAny` (or fault observation) so a dead consumer crashes the sample visibly.

### 9. Per-message reflection on the dispatch hot path
**File:** `src/NimBus.SDK/EventHandlers/EventContextHandler.cs:145` — **CONFIRMED** (efficiency)

`BuildEventJsonHandler` is stored as the dispatch-table factory and re-runs `typeof(EventJsonHandler<>).MakeGenericType(eventType)` + `Activator.CreateInstance` for **every message**, though `eventType` is fixed at registration. Hoist `adapterType` out of the closure at registration time; a cached compiled ctor factory removes the `Activator` cost. Related: the per-message `AsyncServiceScope` (line 69) is created even for provider-ignoring factories registered via the public overloads — pure overhead for those endpoints.

### 10. Partner-interop topology defined twice across provisioning paths, already drifting
**File:** `samples/CrmErpDemo/CrmErpDemo.Provisioner/Program.cs:55` — **CONFIRMED** (reuse)

Topic/subscription names, the `cloudevents-capture` rule name, and the exact SQL filter string are duplicated between the Provisioner (real Azure) and `EmulatorTopologyConfigBuilder.cs:229-263` (emulator). The copies already diverge: Provisioner sets `MaxDeliveryCount = 5` for PartnerPortalCapture, the emulator builder uses 10. This is the known two-provisioning-paths trap — the next filter change lands in one path only while `EmulatorTopologyConfigBuilderTests` keep passing. Provisioner already references Contracts; export shared constants there.

---

## Verified but below the cut

- `src/NimBus.WebApp/Services/AdminService.Purge.cs:460` + `AdminImplementation.cs:264-308` — statuses validated/normalized twice (controller `TryNormalize*` → 400, then service `Normalize*` → `ArgumentException` → 500 for any direct caller). Keep one validation layer.
- `src/NimBus.CommandLine/Program.cs:126-133` vs `274-281` — `apply` and `setup` duplicate the SQL/secrets validation block with identical error messages; extract a shared helper (e.g. on `DeploymentSecrets`).
- `src/NimBus.Resolver/Services/ResolverService.cs:120-122` — the `GetValueOrDefault` + `HasValue` dance is exactly the lifted comparison `retryAfter > calculatedDelay`.
- `docs/adr/010-pluggable-message-storage.md:73-75` — the ADR bullet still lists the removed `--sql-connection-string`/`--sql-admin-password` flags in present tense (the security amendment eight lines later corrects it); one-line polish.
- `nb` unknown-flag UX — scripts passing the removed secret flags get a bare `Unrecognized option` with no pointer to the `NIMBUS_*` env vars; a targeted migration hint would help.

## Refuted candidates (checked, not defects)

- **EventTypeId mismatch guard** (`EventContextHandler.cs:43`): unreachable for any in-repo path — resubmit (`ManagerClient.cs:79-84`), publisher, outbox, and the CloudEvents bridge all single-source the property and body value.
- **`AddSingleton<IHostedService>` switch** (`ServiceCollectionExtensions.cs:286`): the documented intended fix for the silently-dropped second receiver; two session processors on one subscription is the standard competing-consumer pattern with no correctness impact.
- **api-spec.yaml status enums**: already drift-tested — `AdminStatusSafetyTests.cs:218-224` asserts the generated enums against `ResolutionStatus`.
- **CLI secret-flag removal without `[Obsolete]` bridge**: deliberate, documented security hardening (ADR-010 amendment); a bridge accepting secrets on the command line would defeat the purpose.
- **`JsonConvert.DefaultSettings` bypass claim**: explicit settings still merge over DefaultSettings; only `TypeNameHandling`/`MaxDepth` are overridden.
- **AdminService.Copy per-page lambda allocation**: orders of magnitude below the surrounding Cosmos I/O; noise.
