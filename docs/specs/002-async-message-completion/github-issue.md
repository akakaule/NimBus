# Async message completion via PendingHandoff (reuse session blocking, audit as Pending, no exceptions for control flow)

> Spec: [`spec.md`](./spec.md) ([on GitHub](https://github.com/akakaule/NimBus/blob/master/docs/specs/002-async-message-completion/spec.md))
> Companion one-pager (PDF + diagram): [`async-message-completion-onepager.pdf`](../async-message-completion-onepager.pdf) ([on GitHub](https://github.com/akakaule/NimBus/blob/master/docs/specs/async-message-completion-onepager.pdf))

## Summary

A NimBus subscriber adapter integrated with Microsoft Dynamics 365 F&O via the DMF API cannot complete its work synchronously: it triggers an async import job and the per-entity outcome arrives later. We need to settle the Service Bus lock immediately, keep the session blocked so siblings on that session don't overtake the in-flight import, and have the audit trail report **Pending** — not Failed — until the external work reports back.

This issue tracks the work to:

1. Add a new handler outcome **PendingHandoff**, signalled by an **explicit method** on `IEventHandlerContext` (not by throwing).
2. Map the audit row to the existing `ResolutionStatus.Pending` with a sub-status discriminator.
3. Reuse the existing session-blocking + Deferred-subscription machinery (no changes to the FIFO replay path).
4. Add two **terminal Manager commands** — `CompleteHandoff` / `FailHandoff` — that drive Pending → Completed / Failed **without re-invoking the user handler**. (Avoids the misuse pattern of repurposing `Resubmit` for the success case, which doubles middleware metrics, mislabels audit attribution, and forces every adapter to write defensive idempotency checks.)
5. Optional Resolver-side timeout sweeper for `ExpectedBy` deadlines.

The feature is purely additive. Adapters that don't call `MarkPendingHandoff` see zero behaviour change.

## Handler API (FR-001..FR-005)

```csharp
// IEventHandlerContext (NimBus.SDK.EventHandlers) — new method
void MarkPendingHandoff(
    string reason,
    string externalJobId = null,
    TimeSpan? expectedBy = null);

// Reading the outcome state from middleware after the handler returns
HandlerOutcome   Outcome           { get; }
HandoffMetadata? HandoffMetadata   { get; }
```

A handler reads:

```csharp
public async Task Handle(ProjectCreated evt, IEventHandlerContext ctx, CancellationToken ct)
{
    var jobId = await EnqueueForImport(evt, ct);
    ctx.MarkPendingHandoff(
        reason: "Awaiting DMF import job",
        externalJobId: jobId,
        expectedBy: TimeSpan.FromHours(2));
    // Handler returns normally — no throw.
}
```

Why a method, not an exception:

- **No control-flow-by-exception** for a happy-path outcome.
- **Composes with pipeline middleware** — middleware sees a clean return; doesn't have to special-case "is this exception type a fault or a signal?"
- **Idempotent and inspectable** — `ctx.Outcome` is queryable from middleware; second call wins.

## Message types (FR-010)

Three values added to `MessageType`:

| Type | Direction | Purpose |
|---|---|---|
| `PendingHandoffResponse` | subscriber → Resolver | Audit row recorded as Pending with sub-status Handoff. |
| `HandoffCompletedRequest` | Manager → subscriber | Settle a PendingHandoff message as Completed without invoking the user handler. |
| `HandoffFailedRequest` | Manager → subscriber | Settle as Failed with verbatim error text from the external system. |

The forwarding subscription that already routes responses to the Resolver picks up `PendingHandoffResponse` with no topology change.

## Core flow (FR-011..FR-015)

`StrictMessageHandler.HandleEventRequest`, after `HandleEventContent` returns successfully, gets a single new branch:

```csharp
if (ctx.Outcome == HandlerOutcome.PendingHandoff)
{
    await SendPendingHandoffResponse(messageContext, ctx, cancellationToken);
    await BlockSession(messageContext, cancellationToken);
    await CompleteMessage(messageContext, cancellationToken);
    return;   // do NOT send ResolutionResponse; do NOT invoke retry policy
}
await SendResolutionResponse(messageContext, cancellationToken);
await CompleteMessage(messageContext, cancellationToken);
```

Two new control-message handlers, symmetric with `HandleSkipRequest` (neither invokes the user handler):

```csharp
public override async Task HandleHandoffCompletedRequest(IMessageContext ctx, CancellationToken ct = default)
{
    AuthorizeManagerRequest(ctx);
    await VerifySessionIsBlockedByThis(ctx, ct);
    await UnblockSession(ctx, ct);
    await ContinueWithAnyDeferredMessages(ctx, ct);
    await SendResolutionResponse(ctx, ct);
    await CompleteMessage(ctx, ct);
}

public override async Task HandleHandoffFailedRequest(IMessageContext ctx, CancellationToken ct = default)
{
    AuthorizeManagerRequest(ctx);
    await VerifySessionIsBlockedByThis(ctx, ct);
    await SendErrorResponseFromContext(ctx, ct);   // carries errorText/errorType
    await CompleteMessage(ctx, ct);   // session stays blocked; operator decides Resubmit/Skip
}
```

Existing catch branches (`SessionBlockedException`, `EventContextHandlerException`) are unchanged. Existing retry policy is unchanged. If the handler both calls `MarkPendingHandoff` and throws, the failure path wins.

## Manager API (FR-020..FR-023)

```csharp
public interface IManagerClient
{
    // Existing — unchanged. Operator-initiated re-execution of the handler.
    Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson);
    Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId);

    // New — terminal settlement of a PendingHandoff message based on external outcome.
    Task CompleteHandoff(MessageEntity pendingEntry, string endpoint, string detailsJson = null);
    Task FailHandoff(MessageEntity pendingEntry, string endpoint, string errorText, string errorType = null);
}
```

`Resubmit` and `Skip` keep their meaning. The new commands are dedicated to automated settlement by an external status checker; they are not the operator path.

## Resolver & MessageStore (FR-030..FR-034)

- `MessageTypeToStatusMap[PendingHandoffResponse] = ResolutionStatus.Pending`. Audit projection via the existing `UploadPendingMessage`.
- `MessageEntity` and `UnresolvedEvent` gain four nullable fields:
  - `string PendingSubStatus` — `null` for ordinary Pending; `"Handoff"` for the new outcome.
  - `string HandoffReason` — free-text reason supplied by the handler.
  - `string ExternalJobId` — optional external system identifier.
  - `DateTime? ExpectedBy` — optional deadline used by the optional sweeper.
- Persisted by every storage provider (Cosmos DB, SQL Server, in-memory) per the conformance suite. Existing rows project these as `null`.
- `HandoffCompletedRequest` → resulting `ResolutionResponse` flips Pending → Completed.
- `HandoffFailedRequest` → resulting `ErrorResponse` flips Pending → Failed. The supplied `errorText` is preserved verbatim; `errorType` reflects the synthetic `HandoffFailedException` wrapper rather than the operator-supplied value (v1 trade-off — operators read `errorText`; tightening the `errorType` round-trip is a follow-up). See ADR-012 § Negative.

## WebApp (FR-040..FR-043)

- Pending+Handoff entries count under the existing **Pending** column; Failed count is unaffected.
- Message row shows a small "Awaiting external" badge when `PendingSubStatus = "Handoff"`.
- Message detail page renders `HandoffReason`, `ExternalJobId`, `ExpectedBy` when present.
- Existing **Resubmit / Skip** buttons remain available on Pending+Handoff entries.
- No API contract change required (new fields are optional additions to existing response shapes).

## Optional timeout sweeper (FR-050..FR-053)

- Opt-in. Default off.
- Resolver-side background pass scans Pending+Handoff entries with `ExpectedBy` in the past, emits a synthetic `HandoffFailedRequest` with `errorType = "TimeoutExpired"`.
- Resulting Failed entry follows the standard operator Resubmit / Skip flow.
- Pending entries with `PendingSubStatus = null` or `ExpectedBy = null` are never touched.

## Backwards compatibility (FR-060..FR-063)

- Pure additions: 3 new MessageType values, 1 new method on `IEventHandlerContext`, 2 new methods on `IManagerClient`, 4 new nullable fields on `MessageEntity`/`UnresolvedEvent`.
- No transport-topology change, no schema migration for existing rows, no breaking changes to public publishing/subscribing APIs.
- Adapters that don't call `MarkPendingHandoff` see zero behaviour change.

## Acceptance criteria

- [ ] **SC-001** — Handler that calls `ctx.MarkPendingHandoff(...)` and returns produces an audit row with `ResolutionStatus = Pending`, `PendingSubStatus = "Handoff"`, and the supplied metadata, without throwing any exception.
- [ ] **SC-002** — While a session is in PendingHandoff state, subsequent messages on the same session are parked on the Deferred subscription in FIFO order.
- [ ] **SC-003** — `ManagerClient.CompleteHandoff(...)` settles the message as Completed and unblocks the session **without invoking the user handler** (verified by asserting `IEventHandler<TEvent>.Handle` is not called during settlement).
- [ ] **SC-004** — `ManagerClient.FailHandoff(...)` settles as Failed with `errorText` preserved verbatim on the audit row; session remains blocked.
- [ ] **SC-005** — WebApp Pending count includes Pending+Handoff entries; Failed count does not. Message detail renders the new fields when present and is unchanged on legacy rows.
- [ ] **SC-006** — Existing operator Resubmit / Skip flows work unchanged on Pending+Handoff entries.
- [ ] **SC-007** — Shared provider conformance suite passes with the four new fields round-tripping across Cosmos DB, SQL Server, and in-memory providers.
- [ ] **SC-008** — E2E test "PendingHandoff → siblings defer → CompleteHandoff → siblings replay in FIFO" passes.
- [ ] **SC-009** — E2E test "PendingHandoff → FailHandoff → audit row carries error text → operator Skip" passes.
- [ ] **SC-010** — No existing test starts failing as a result of the changes.

## Files touched (high level)

| Where | Change |
|---|---|
| `NimBus.SDK.EventHandlers.IEventHandlerContext` | Add `MarkPendingHandoff(reason, externalJobId?, expectedBy?)` + `Outcome` / `HandoffMetadata` accessors. `EventHandlerContext` implements them. |
| `NimBus.SDK.EventHandlers.EventJsonHandler` | Construct `EventHandlerContext` so `StrictMessageHandler` can read post-handler outcome. |
| `NimBus.Core.Messages.MessageType` | +3 enum values: `PendingHandoffResponse`, `HandoffCompletedRequest`, `HandoffFailedRequest`. |
| `NimBus.Core.Messages.IResponseService` + `ResponseService` | Add `SendPendingHandoffResponse` (mirrors `SendDeferralResponse`). |
| `NimBus.Core.Messages.StrictMessageHandler` | One outcome branch in `HandleEventRequest` post-handler success path; two new control-message handlers (`HandleHandoffCompletedRequest`, `HandleHandoffFailedRequest`). Existing catch branches unchanged. |
| `NimBus.Manager.IManagerClient` + `ManagerClient` | Add `CompleteHandoff(entity, endpoint, detailsJson?)` and `FailHandoff(entity, endpoint, errorText, errorType?)`. |
| `NimBus.MessageStore.Abstractions` (or `NimBus.Abstractions`, per #001) | `MessageEntity` / `UnresolvedEvent`: +4 nullable fields (`PendingSubStatus`, `HandoffReason`, `ExternalJobId`, `ExpectedBy`). |
| `NimBus.MessageStore.CosmosDb`, `NimBus.MessageStore.SqlServer`, in-memory | Persist + project the new fields. Conformance suite gains round-trip tests. |
| `NimBus.Resolver.Services.ResolverService` | Map `PendingHandoffResponse` → `ResolutionStatus.Pending`. Copy new fields onto the entity. |
| `NimBus.WebApp` | Render Pending+Handoff badge and the new fields on the message detail page. No API contract change. |
| `NimBus.Resolver` (sweeper, opt-in) | **Follow-up — not built in v1.** Design captured in spec FR-050..FR-053 and ADR-012 § Operational; opt-in background pass that would flip Pending+Handoff past `ExpectedBy` to Failed via `HandoffFailedRequest`. |
| `tests/NimBus.Core.Tests` | Handler-success path with `MarkPendingHandoff`; failure-wins-over-handoff path; middleware observing `ctx.Outcome`; the two new `Handle*Request` methods. |
| `tests/NimBus.EndToEnd.Tests` | E2E happy path; E2E failure path; FIFO replay verification. |
| `docs/adr/012-pending-handoff.md` | New ADR — rationale, rejected alternatives. |
| `docs/error-handling.md`, `docs/message-flows.md`, `docs/sdk-api-reference.md` | Documentation updates. |

## Edge cases

- Handler calls `MarkPendingHandoff` and then throws — exception path wins.
- Handler calls `MarkPendingHandoff` twice — last call wins, idempotent.
- `CompleteHandoff` / `FailHandoff` against a non-PendingHandoff state — clear error or no-op (decided in design); state machine MUST NOT be corrupted.
- `CompleteHandoff` / `FailHandoff` against an event that did not block this session — must reject (analogous to today's `VerifySessionIsBlockedByThis`).
- Two messages on the same session both want PendingHandoff — second one's session-blocked check fires first, parks on Deferred (existing flow).
- Resolver receives `PendingHandoffResponse` for an unseen event id — must record Pending with the new fields populated.
- WebApp message detail on a legacy Pending entry (no sub-status) — renders unchanged.

## Out of scope

- Adapter-side correlation store, status checker, or DMF-specific code (lives in the adapter, per ADR-002).
- Operator-initiated `CompleteHandoff` / `FailHandoff` buttons in the WebApp (Resubmit / Skip remain the operator-facing primitives in v1).
- A general external-completion API that bypasses the Manager.
- Cross-session ordering. Sessions remain independent.
- Persistence-format migrations for existing rows.
- Renaming or restructuring `IEventHandler` / `IEventHandlerContext`.

## Open questions

- **Naming.** `MarkPendingHandoff` vs `SignalPendingHandoff` vs `DeferToExternal`.
- **Outcome state shape on the context.** Single `HandlerOutcome` enum + optional `HandoffMetadata`, or a more general `HandlerResult` discriminated union.
- **Behaviour when `CompleteHandoff` / `FailHandoff` is called against a non-PendingHandoff state.** Hard error vs no-op vs idempotent.
- **Sweeper hosting.** Resolver-side background pass vs separate worker vs adapter-side. Recommendation: Resolver-side, opt-in via configuration.
- **WebApp UX.** Whether to add a dedicated "Awaiting external" filter on the endpoint dashboard, beyond the per-row badge.
- **Authorization for the new control messages.** Reuse `AuthorizeManagerRequest` (recommended) vs add a dedicated capability for the status checker.

## Suggested labels

`enhancement` · `core` · `sdk` · `manager` · `resolver` · `webapp` · `storage` · `non-breaking`
