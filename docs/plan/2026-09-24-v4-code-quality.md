# Plan: v4.0.0 code-quality release

## Goal

Use the v4.0.0 major to fix four structural problems found in the 2026-09-24 code
review, and to remove the obsolete surface that `docs/versioning.md` lets a major
delete:

1. The storage providers disagree about what "not found" and "absent" mean.
2. Backward-compatibility bridges have piled up (`StrictMessageHandler` has five
   public constructors, and there are 21 `[Obsolete]` markers in `src`).
3. Four files are large enough that changing them is risky.
4. The code has drifted from its own conventions.

## Decisions (2026-09-24)

- **Version:** v4.0.0, a major. Every `[Obsolete]` member is deleted, and each
  deletion is listed under ⚠️ Breaking in the release notes.
- **Not-found contract:** single-row lookups return `null` when the row is
  missing. They never throw. The return types are annotated `T?`.

## Baseline (2026-09-24, `2ffb914b`)

- Release build: green with 2,229 warnings. The nullable-warning counts are CS8618 546,
  CS8625 284 (136 in `src`), CS8603 130 and CS8600 125.
- `docs/plan/` Plans 1, 2 and 5 are complete. Plan 4 (CLI) is not started, and
  this plan carries out its Phases 1, 2 and 4.

## Workstreams and order

The order keeps mechanical churn from conflicting with behavior changes. Each
workstream is one or more commits with a Conventional Commit subject.

### WS-A: conventions (goes first because it touches the most files)

1. Convert every block-scoped namespace in `src/` and `tests/` to file-scoped
   (213 files in `src`). Use `dotnet format style --diagnostics IDE0161` with
   `csharp_style_namespace_declarations = file_scoped` set in a root
   `.editorconfig`, so the rule stays in force after the conversion. The change
   is whitespace and namespace only, and is reviewed with `git diff -w`.
2. Rename `NimBus.Broker.Services` to `NimBus.Resolver.Services`. The Resolver
   is not packable, so no consumer is affected.

Commit: `style: convert to file-scoped namespaces`, `refactor(resolver): rename Broker namespace`.

### WS-B: storage contract (not-found and absent)

**Contract (documented on `IMessageTrackingStore`):**
- `GetPendingEvent`, `GetFailedEvent`, `GetDeferredEvent`,
  `GetDeadletteredEvent`, `GetUnsupportedEvent`, `GetEvent`, `GetEventById`,
  `GetPendingHandoffByExternalJobId`, `GetMessage`,
  `GetLatestEventRequestMessage`, `GetFailedMessage`, `GetDeadletteredMessage`
  and `IEndpointMetadataStore.GetEndpointMetadata` return `null` for a missing
  row. The return types become `Task<T?>`.
- `GetFailedMessage` returns the newest message carrying `ErrorContent` (Cosmos
  behavior). SQL Server returned the newest message of any type and in-memory an
  arbitrary one. `GetDeadletteredMessage` returns the newest message of any type.
- Optional string fields round-trip exactly: a field written as `null` reads
  back as `null`, and one written as `""` reads back as `""`.
- `GetEvent(endpointId, eventId)` returns the most recently updated row that is
  not removed or archived. SQL Server and in-memory already do this. Cosmos
  returned an arbitrary match including removed rows, so it has to change. Cosmos
  also writes Completed and Skipped rows with `deleted = true` (to leave the counts
  and expire by TTL), so there only a deleted row in a non-terminal status counts
  as removed.
- `GetEventById` looks up by stored document id (`{eventId}_{sessionId}`) on
  every provider. In-memory currently treats the id as an eventId.

**Changes:**
1. RED: add conformance cases to `MessageTrackingStoreConformanceTests`: a
   missing row returns null for every getter, a wrong status returns null, a
   soft-deleted row returns null, `GetEvent` returns the latest row, and null and
   empty optional fields round-trip for `UnresolvedEvent` and `MessageEntity`
   (including through `GetEventHistory`).
2. GREEN in the SQL Server provider:
   - `GetEventByStatus` and `GetEvent` return null.
   - `MapUnresolvedEventRow` and `MapMessageRow` stop coalescing nullable columns
     to `""`. Columns declared `NOT NULL` are unaffected.
   - Audit `SqlServerEndpointMetadataStore` and `SqlServerSubscriptionStore`
     for the same `?? string.Empty` pattern, and fix it where the column is
     nullable and the other providers return null.
3. GREEN in the Cosmos provider: `GetEvent` filters out `Deleted` rows and orders
   by `UpdatedAt` descending.
4. GREEN in the in-memory store (`InMemoryMessageStore`): `GetByStatus` and
   `GetEvent` return null, and `GetEventById` matches the stored id.
5. Callers: replace every `catch (EndpointNotFoundException)` around these
   getters with a null check. The affected call sites are in the WebApp
   (`EndpointImplementation`, `EventImplementation`, `EventImplementation.Deferred`,
   `AdminService.Reconcile`, `HandoffSettlementService`,
   `IntegrationIntelligenceHostAdapter`), in `ClassificationRetentionWorker`,
   and in the default `TryCompletePendingMessage` implementation. Delete
   `AdminService.GetPendingRowOrNullAsync`. Anywhere read-back strings are
   compared with `!= null` or `== ""`, use `string.IsNullOrEmpty`.
6. Annotate the optional string properties on `UnresolvedEvent` as `string?`.
   `MessageEntity` stays as it is: it implements the core `IMessage` /
   `IReceivedMessage` contracts, and annotating it would ripple through the
   messaging model. Its null round-trip is still pinned by the conformance suite.
7. `EndpointNotFoundException` keeps one meaning: the endpoint's storage does not
   exist (Cosmos container translation). A missing metadata row is `null`.
8. Apply the same round-trip rule to the SQL Server metadata owner fields,
   heartbeat rows, subscription fields and service-health `Version`. The heartbeat
   overview keeps `""` for "no probe yet", because every provider projects it
   through the shared `HeartbeatRollup.BuildOverviewItem`.
9. Document the contract in `docs/storage-providers.md`.

Commits: `test(storage): pin not-found and absent-field contract`,
`fix(storage)!: return null for missing rows on every provider`,
`fix(storage)!: round-trip null optional fields on SQL Server`.

### WS-C: remove obsolete surface and collapse constructors

1. **Dead Service Bus defer API** (spec 027 §3): delete `DeferOnly`,
   `ReceiveNextDeferred`, `ReceiveNextDeferredWithPop`, `RestoreNextDeferred` and
   related members from `IMessageContext`, `MessageContext`, `IServiceBusSession`,
   `ServiceBusSession` and `InMemoryMessageContext`. Also delete the
   `SessionState.DeferredSequenceNumbers` legacy drain in `StrictMessageHandler`
   (`ContinueWithAnyDeferredMessages` legacy branch, `ReceiveNextDeferredAndVerifyEventId`,
   `RestoreDeferredBestEffort`).
   - `HandleContinuationRequest`: the only producer of a ContinuationRequest is
     the legacy drain (`StrictMessageHandler` calls
     `IResponseService.SendContinuationRequestToSelf`), so that method is
     removed too. In v4 an incoming ContinuationRequest is logged at warning
     level and completed, so one still in flight at upgrade is not
     dead-lettered. `MessageType.ContinuationRequest` stays because it is part
     of the wire format and the Resolver's audit history.
   - `SessionState.DeferredSequenceNumbers` is removed from the model.
     Serialized session state that still contains it deserializes without error
     because Newtonsoft ignores unknown members, and a regression test pins
     this. Release notes: a namespace that ever used the Service Bus defer API
     must drain legacy deferred messages with `nb` before upgrading.
   - The CLI's direct `ReceiveDeferredMessageAsync` admin paths (`Endpoint.cs`)
     stay, because they are operator recovery tools that talk to Service Bus
     directly.
2. **`IPermanentFailureClassifier`** and **`DefaultPermanentFailureClassifier`**:
   delete them. `DefaultFailureDispositionClassifier` loses its legacy adapter,
   and `NimBusSubscriberBuilder` and the SDK/Testing registration drop the
   legacy resolution. Update `docs/error-handling.md`,
   `docs/sdk-api-reference.md` and the sample TDDs.
3. **`StrictMessageHandler` constructors:** keep one public constructor:
   `(IEventContextHandler, IResponseService, ILogger? logger = null,
   IRetryPolicyProvider? retryPolicyProvider = null, MessagePipeline? pipeline = null,
   MessageLifecycleNotifier? lifecycleNotifier = null,
   IFailureDispositionClassifier? failureDispositionClassifier = null,
   InboxDuplicateDetector? inboxDuplicateDetector = null)`. Optional parameters
   cover every former arity that did not use the classifier, and DI builds it
   through the factory registrations. Remove every `#pragma warning disable CS0618`.
4. **WebApp storage-hook route:** rename `/api/storagehook/cosmos/{endpointId}` to
   `/api/storagehook/endpoint/{endpointId}` (operationId `storagehook-receive`)
   in `api-spec.yaml`. Delete `StoragehookReceiveCosmosAsync`, and point
   `HttpEndpointStateChangeNotifier` at the new route. Release notes: deploy the
   WebApp and Resolver together, because a v3 Resolver's refresh hook would get
   404 (SignalR refresh only; no data is affected).
5. Remove `CS0618` from `WarningsNotAsErrors`, so that from now on, calling an
   obsolete member from first-party code fails the Release build.

Commits: `refactor(core)!: remove dead Service Bus defer API`,
`refactor(core)!: remove IPermanentFailureClassifier`,
`refactor(core)!: collapse StrictMessageHandler constructors`,
`refactor(webapp)!: rename storage-hook route`.

### WS-D: nullable hygiene

1. Fix the 136 CS8625 sites in `src` (`= null` defaults and null arguments to
   non-nullable parameters) by annotating each parameter `T?` when null is
   legitimately accepted. Otherwise, remove the null.
2. Fix CS8625 in `tests/` where that is mechanical.
3. Remove `CS8625` from `WarningsNotAsErrors` so the drift cannot come back.
   The other nullable codes stay non-fatal; they belong to a separate backlog.

Commit: `fix: annotate nullable parameters and make CS8625 an error`.

### WS-E: split the large files (moves only, no behavior change)

Each split relocates members into `partial` files (or extension classes for
`Startup`) with identical bodies, and is checked with the move-aware diff
`git diff --color-moved=dimmed-zebra`.

| File | Target |
|---|---|
| `CosmosDbMessageTrackingStore.cs` (1,866) | `partial` files: `.EndpointState.cs` (counts/paging), `.Writes.cs` (Upload*/Try*/Remove/Purge/UploadGuarded), `.Lookups.cs` (Get*Event, handoff), `.Search.cs` (filter/search/blocked/pending/invalid/error list), `.Messages.cs` (StoreMessage/history), `.Audits.cs`, `.Reports.cs` (resubmit counts, reports, archive) |
| `SqlServerMessageTrackingStore.cs` (1,194) | Same partial layout, so the two providers can be compared file by file |
| `EventImplementation.cs` (1,167) | `partial` files by concern: `.Queries.cs`, `.OperatorActions.cs` (resubmit/skip/compose/resubmit-with-changes), `.Handoff.cs`, `.Search.cs`, alongside the existing `.Deferred.cs` |
| WebApp `Startup.cs` (861) | Each `Add*` section moves to `Startup/*ServiceCollectionExtensions.cs` (`Authentication`, `WebPipeline`, `PlatformCatalog`, `ServiceBusClients`, `Storage`, `Management`, `Observability`, `AuthorizationAndAudit`, `ApiControllers`). `Startup` keeps orchestration and `Configure`. |
| CLI `Program.cs` (1,005) | Plan 4 Phases 1, 2 and 4: characterization tests, then `CliApplicationFactory`, then the eight `Configure*Commands` methods move into `Commands/*.cs`. Phases 3 and 5 (parser centralization, dependency injection) remain Plan 4 follow-ups. |

Commits: one `refactor(<scope>): split <file> by concern` per file, plus
`test(cli): characterize command graph and validation`.

## Verification

After each workstream:

```bash
dotnet build src/NimBus.sln -c Release
dotnet test src/NimBus.sln -c Release --no-build
```

- WS-B must also pass the live conformance suites: SQL Server via
  `NIMBUS_SQL_TEST_CONNECTION`, and Cosmos via the emulator with
  `NIMBUS_COSMOS_TEST_REQUIRED=1`. See the local-container recipe in
  project memory. If they cannot run locally, CI runs them and rejects skips.
- WS-C changes `api-spec.yaml`, so its build must leave NSwag generation enabled
  (no `SkipSpaBuild`). Also run `npm --prefix src/NimBus.WebApp/ClientApp run test:ci`.
- WS-E: `nb --help`, `nb infra apply --help` and `nb topology apply --help` produce the same output before and after.
- Final gate: a warning count below the baseline, and zero CS0618/CS8625 warnings.

## Release-notes ledger (⚠️ Breaking)

- `IMessageTrackingStore` single-row getters return `null` for a missing row on
  every provider. SQL Server and in-memory used to throw `EndpointNotFoundException`.
- SQL Server returns `null` (not `""`) for optional string fields stored as NULL.
- `GetEvent` on Cosmos ignores removed and archived rows and returns the latest update.
- `IEndpointMetadataStore.GetEndpointMetadata` returns `null` when no metadata is
  stored (SQL Server and in-memory used to throw `EndpointNotFoundException`).
- `GetFailedMessage` returns the newest message carrying `ErrorContent` on every
  provider; `GetEventById` matches the stored id on every provider.
- SQL Server returns `null` for NULL endpoint-owner, subscription, heartbeat
  `SdkVersion` and service-health `Version` columns.
- `IMessageContext` defer members, `IServiceBusSession.DeferAsync`/receive-deferred
  and `SessionState.DeferredSequenceNumbers` are removed. Drain legacy
  SB-deferred messages before upgrading.
- `IPermanentFailureClassifier` / `DefaultPermanentFailureClassifier` are removed;
  use `IFailureDispositionClassifier`.
- `StrictMessageHandler` has a single constructor with optional parameters.
- WebApp route `/api/storagehook/cosmos/{endpointId}` is now
  `/api/storagehook/endpoint/{endpointId}`. Deploy the WebApp and Resolver together.
- The Resolver namespace `NimBus.Broker.Services` is now `NimBus.Resolver.Services`.
