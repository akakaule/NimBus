# Feature Specification: Failed Message Hook (`IFailedMessageHandler<T>`)

Feature Branch: `015-failed-message-hook`
Created: 2026-05-28
Updated: 2026-05-28
Status: Proposed
Input: GitHub issue #32 ([Backlog] Failed Message Hook). User description: "After retries are exhausted, NimBus today dead-letters the message and the operator picks it up in the WebApp. Some applications need a programmatic last-chance handler before that — to enrich diagnostics, route to an alternative endpoint, modify and retry, or simply log a domain-specific incident — without forking the retry pipeline. A Failed Message Hook lets handlers register an `IFailedMessageHandler<T>` that runs after retry exhaustion and decides whether to dead-letter, retry once more, or hand off elsewhere."

## Problem

When a subscriber handler throws, NimBus runs it through the retry pipeline in `StrictMessageHandler` and, once the configured `RetryPolicy` budget is spent, lets the message fall through to its terminal failure disposition — a `Failed` audit row at the Resolver and, depending on the host, a Service Bus dead-letter. The operator then has to notice the failure in the management WebApp and act on it manually.

That terminal moment — "retries are exhausted, we are about to give up on this message" — is the single most useful extension point in the pipeline, and today there is no way to hook it. Applications that want a programmatic last-chance step have only bad options:

1. **Swallow the exception in the handler.** Defeats the retry pipeline entirely and hides the failure from the audit trail.
2. **Re-implement retry counting in the handler.** Duplicates the `IRetryPolicyProvider` / `RetryPolicy` machinery (`src/NimBus.Core/Messages/RetryPolicy.cs`, `IRetryPolicyProvider.cs`) and gets the off-by-one on `RetryCount` wrong.
3. **Build a parallel dead-letter drainer.** A separate function that polls the DLQ, re-hydrates the typed event, and routes it — heavy, out-of-band, and divorced from the original message's context.

The real retry-exhaustion decision lives in one place. `StrictMessageHandler.HandleEventRequest(...)` catches `EventContextHandlerException`, sends the error response to the Resolver, blocks the session, completes the broker message, and then calls `CheckForRetry(...)` (`src/NimBus.Core/Messages/StrictMessageHandler.cs:95-103`). `CheckForRetry` is where the budget is consulted:

```csharp
private async Task CheckForRetry(IMessageContext messageContext, EventContextHandlerException exception, CancellationToken cancellationToken = default)
{
    var eventTypeId = messageContext.MessageContent.EventContent.EventTypeId;
    var exceptionText = $"{exception?.InnerException} {exception}";
    var retryCount = messageContext.RetryCount ?? 0;

    if (_retryPolicyProvider != null)
    {
        var policy = _retryPolicyProvider.GetRetryPolicy(eventTypeId, exceptionText, messageContext.To);
        if (policy != null && retryCount < policy.MaxRetries)
        {
            var delayMinutes = policy.GetDelayMinutes(retryCount);
            await SendRetryResponse(messageContext, delayMinutes, cancellationToken);
        }
        return;          // <-- retries exhausted (or no policy): today this is a silent fall-through
    }
    // ... legacy RetryDefinitions branch, same shape ...
}
```
(`src/NimBus.Core/Messages/StrictMessageHandler.cs:370-394`)

The `return` after the `retryCount < policy.MaxRetries` check is **exactly** the "retries exhausted, before dead-letter" point the issue describes: when control reaches it without having sent a `RetryRequest`, the message will not be retried again. This spec inserts a single registered-handler invocation at that point.

NimBus already has the surrounding pieces: a typed handler convention (`IEventHandler<T>`), a DI registration surface (`NimBusSubscriberBuilder`), a per-message retry count (`IMessage.RetryCount`), a retry policy abstraction (`RetryPolicy` / `IRetryPolicyProvider`), and a Resolver that records each message's outcome as a `ResolutionStatus`. The Failed Message Hook composes these rather than adding a new pipeline.

## Scope

In scope:
- A new `IFailedMessageHandler<T>` interface and a `FailedMessageContext` carrier type in `src/NimBus.Core/Messages/` (mirroring the new-file integration points named in the issue, adjusted to NimBus's actual `NimBus.Core` namespace — see Assumptions).
- A `FailedMessageDisposition` enum: `DeadLetter` (default), `Retry`, `Handled`.
- DI registration via `NimBusSubscriberBuilder.AddFailedHandler<TEvent, THandler>()`, aligned with the existing `AddHandler<TEvent, THandler>()` registration in `src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs:31-38`.
- Invocation of the registered failed-handler from `StrictMessageHandler.CheckForRetry(...)` at the retry-exhaustion point, before the message is allowed to fall through to its terminal failure disposition. Invoked **exactly once** per message, only after retry budget is spent (not on every transient throw).
- Wiring all three dispositions through `StrictMessageHandler`: `Retry` re-enqueues one more `RetryRequest` via the existing `SendRetryResponse(...)`; `Handled` settles the message as completed (`SendResolutionResponse` + `Complete`); `DeadLetter` is the no-op fall-through to today's behaviour.
- Recording the chosen disposition on the audit trail so the operator can see *why* a message did or did not reach the DLQ. The audit row's outcome is the Resolver's `ResolutionStatus` (`src/NimBus.MessageStore.Abstractions/States/ResolutionStatus.cs`); the disposition maps onto the response `MessageType` the handler sends (`ResolutionResponse` → `Completed`, `RetryRequest` → `Pending`, fall-through → `Failed`/`DeadLettered`).
- Unit tests for each disposition plus the handler-throws case, and an E2E test in `tests/NimBus.EndToEnd.Tests/`.

Out of scope:
- Changing the retry pipeline itself (`RetryPolicy`, `IRetryPolicyProvider`, `DefaultRetryPolicyProvider`). The hook runs *after* retry exhaustion; it does not alter how retries are counted.
- A loop guard that caps how many times `Retry` can re-trigger the hook. The issue explicitly makes loop avoidance "the caller's responsibility"; the hook is invoked once per *retry-exhaustion event*, and a `Retry` that fails again re-enters the pipeline as a fresh delivery (see Edge Cases).
- A failed hook for the Resolver, Manager, Skip/Resubmit/Handoff control flows, or the legacy `MessageHandler` (non-strict) path. The hook is a subscriber-side, event-processing concern only.
- Async handoff via `MarkPendingHandoff(...)` from inside the failed handler. The failed handler's contract is the three-disposition enum, not the handoff state machine.
- A `FailedMessageDisposition.Defer`/`Skip`/etc. beyond the three the issue specifies.
- Surfacing the disposition as a *new* dedicated field on `MessageAuditEntity`. The existing `ResolutionStatus` outcome already captures `Completed` / `Pending` / `Failed` / `DeadLettered`; v1 reuses it (see Open Questions).

## User Scenarios & Testing

### User Story 1 - Last-chance handler logs a domain incident, then dead-letters (Priority: P1)

As a subscriber author, I want a callback that fires the moment NimBus has exhausted retries for my event, so I can emit a domain-specific incident (page an on-call alias, write a richer diagnostic record) before the message reaches the DLQ — without forking the retry pipeline.

Why this priority: This is the canonical, lowest-risk use of the hook. It changes nothing about message disposition (returns `DeadLetter`, the default) and proves the invocation point fires exactly once at the right moment.

Independent Test: Register a handler whose `Handle(...)` records that it ran and returns `FailedMessageDisposition.DeadLetter`. Drive an event through `StrictMessageHandler.HandleEventRequest` whose handler always throws, with a `RetryPolicy { MaxRetries = 1 }` and `RetryCount = 1` (budget already spent). Assert the failed handler ran exactly once, that no `RetryRequest` was sent, and that the message fell through to its terminal failure exactly as it does today.

Acceptance Scenarios:

1. Given an `OrderPlaced` handler that always throws and a registered `OrderPlacedFailedHandler` returning `DeadLetter`, When the message's `RetryCount` has reached `policy.MaxRetries`, Then `OrderPlacedFailedHandler.Handle(...)` is invoked exactly once with the typed `OrderPlaced` instance and a populated `FailedMessageContext`, and no `RetryRequest` is sent.
2. Given the same setup, When the failed handler returns `DeadLetter`, Then `StrictMessageHandler` takes the unchanged terminal path (no `SendResolutionResponse`, no `SendRetryResponse`), identical to the behaviour when no failed handler is registered.
3. Given there is still retry budget left (`RetryCount < policy.MaxRetries`), When the handler throws, Then a `RetryRequest` is sent and the failed handler is **not** invoked.

---

### User Story 2 - Handler settles a message it has dealt with (`Handled`) (Priority: P1)

As a subscriber author, I want to tell NimBus "I have dealt with this poison message — route it to my parking topic / write a compensating record — so settle it as completed and do not dead-letter it," because for some events a DLQ entry is operational noise, not a real incident.

Why this priority: `Handled` is the disposition with the most behavioural change (it flips a would-be failure into a completion) and is the one most likely to surprise an operator if mis-wired. It must be correct and visible in the audit trail.

Independent Test: Register a failed handler returning `Handled`. Exhaust retries on a throwing handler. Assert that `StrictMessageHandler` sends a `ResolutionResponse` (not an error response) and completes the broker message, and that the Resolver records `ResolutionStatus.Completed` for the event.

Acceptance Scenarios:

1. Given retries are exhausted and the failed handler returns `Handled`, When `StrictMessageHandler` processes the disposition, Then it sends a `ResolutionResponse` via `IResponseService.SendResolutionResponse(...)` and completes the message, and no `RetryRequest` is sent.
2. Given `Handled` was chosen, When the Resolver processes the resulting response, Then the event's outcome is `ResolutionStatus.Completed` (per `MessageTypeToStatusMap[ResolutionResponse] = Completed` in `src/NimBus.Resolver/Services/ResolverService.cs:41`), so the operator sees the event as resolved rather than failed.
3. Given the original handler had already sent an `ErrorResponse` and blocked the session on the way into `CheckForRetry`, When `Handled` is chosen, Then the session is unblocked / the completion supersedes the prior error so the message is not left wedged (see Edge Cases and Open Questions on the error-then-complete ordering).

---

### User Story 3 - Handler re-enqueues one more attempt (`Retry`) (Priority: P2)

As a subscriber author handling a failure that I know is recoverable after a side effect (e.g. I just provisioned the missing downstream resource), I want to grant the message one more delivery, accepting that avoiding an infinite loop is my responsibility.

Why this priority: Useful but the sharpest-edged disposition (loop risk). It is a thin reuse of the existing `SendRetryResponse(...)` path, so the implementation cost is low, but it is P2 because most callers will reach for `DeadLetter` or `Handled`.

Independent Test: Register a failed handler returning `Retry`. Exhaust retries. Assert that exactly one additional `RetryRequest` is sent (via `SendRetryResponse`) even though the policy budget was already spent, and that the `RetryCount` on the re-enqueued message is incremented (`CreateRetryResponse` sets `RetryCount = messageContext.RetryCount + 1`, `src/NimBus.Core/Messages/ResponseService.cs:144`).

Acceptance Scenarios:

1. Given retries are exhausted and the failed handler returns `Retry`, When the disposition is processed, Then exactly one `RetryRequest` is sent with the existing delay derivation (the policy's `GetDelayMinutes(retryCount)` or a documented default when there is no policy), and the message is settled.
2. Given the re-enqueued message comes back, fails again, and exhausts the (now further-incremented) budget, When `CheckForRetry` runs again, Then the failed handler is invoked again — once per retry-exhaustion event — so an unconditional `Retry` will loop. This is documented behaviour, not a defect (loop avoidance is the caller's responsibility per the issue).
3. Given the failed handler returns `Retry` but a delay cannot be derived (no policy in scope), When the disposition is processed, Then a documented default delay is used (see Open Questions) rather than throwing.

---

### User Story 4 - Failed handler throws and the message is not lost (Priority: P1)

As an operator, I want a bug in someone's failed handler to never *lose* a message. If the failed handler itself throws, NimBus must fall back to the default terminal disposition (dead-letter) and log the secondary failure, not silently complete or drop the message.

Why this priority: The whole point of the hook is a *safer* last-chance step. A hook that can swallow messages on its own bug is worse than no hook. This is a hard safety guarantee.

Independent Test: Register a failed handler whose `Handle(...)` throws. Exhaust retries. Assert that the message takes the default `DeadLetter` fall-through (no `ResolutionResponse`, no extra `RetryRequest`), and that a warning naming the failed-handler exception is logged.

Acceptance Scenarios:

1. Given the failed handler throws, When `StrictMessageHandler` catches it, Then the message takes the `DeadLetter` default path (treated as if the handler returned `DeadLetter`), exactly preserving today's terminal behaviour.
2. Given the failed handler throws, When the exception is caught, Then a `LogWarning`/`LogError` is emitted naming the failed-handler type and the secondary exception, so the operator can find the buggy hook.
3. Given the failed handler is cancelled (the `CancellationToken` fires mid-`Handle`), When the `OperationCanceledException` surfaces, Then it is treated as a non-decision and the message takes the `DeadLetter` default rather than being lost.

---

### User Story 5 - No registered failed handler — zero behaviour change (Priority: P1)

As a NimBus maintainer, I want subscribers that do not register a failed handler to behave exactly as they do today, so the feature is purely additive and safe to ship.

Why this priority: Backwards compatibility. The overwhelming majority of existing subscribers register no failed handler; their retry-exhaustion path must be byte-for-byte unchanged.

Independent Test: Run the existing `StrictMessageHandlerTests` retry-exhaustion test (`HandleEventRequest_HandlerThrowsRetryCountExceeded_NoRetryResponse`, `tests/NimBus.Core.Tests/StrictMessageHandlerTests.cs:306`) with the feature merged. It passes unchanged.

Acceptance Scenarios:

1. Given no `IFailedMessageHandler<T>` is registered for the event type, When retries are exhausted, Then `CheckForRetry` returns exactly as it does today (no resolver/look-up overhead beyond a single null/absent check), and the message falls through to its terminal disposition.
2. Given a failed handler is registered for event type A but the current message is type B, When B exhausts retries, Then B's path is the unchanged default (the hook is keyed per event type, like `AddHandler<TEvent,THandler>()`).

---

## Edge Cases

- **Permanent failure short-circuits the hook.** When `HandleEventContent` classifies a throw as permanent (`_permanentFailureClassifier.IsPermanentFailure(...)`, `src/NimBus.Core/Messages/StrictMessageHandler.cs:340-343`), it throws `PermanentFailureException`, which is NOT caught by the `EventContextHandlerException` catch in `HandleEventRequest` and is instead handled by the base `MessageHandler` dead-letter path (`src/NimBus.Core/Messages/MessageHandler.cs:89-114`). v1 does **not** invoke the failed hook on the permanent-failure path — permanent failures bypass retries by design, so there is no "retry exhaustion" moment. This is called out in Open Questions as a possible follow-up.
- **Transient failure does not trigger the hook.** A `TransientException` is rethrown out of `HandleEventContent` (`StrictMessageHandler.cs:330-332`) and abandoned by the base handler for redelivery; it never reaches `CheckForRetry`. The hook fires only on `EventContextHandlerException` after the retry budget is spent.
- **No retry policy in scope.** When `_retryPolicyProvider` is null and the legacy `RetryDefinitions` branch finds no definition, today the message simply falls through with zero retries. The hook MUST still fire in this "exhausted at retry 0" case — exhaustion includes "no retries were ever configured." (See Open Questions on the `Retry` delay when no policy exists.)
- **`Retry` with no further delay source.** If the failed handler asks for `Retry` but no `RetryPolicy` is available to derive a delay, the implementation uses a documented default delay rather than throwing or sending a zero-delay retry that would hot-loop.
- **`Handled` after an error response was already sent.** On the `EventContextHandlerException` path, `StrictMessageHandler` has already called `SendErrorResponse` and `BlockSession` *before* `CheckForRetry` (`StrictMessageHandler.cs:98-101`). A `Handled` disposition therefore needs to supersede that error (send a follow-up `ResolutionResponse` and unblock the session) so the audit row flips `Failed → Completed` and the session is not left blocked. This ordering is the trickiest part of the implementation and is the subject of a Resolved/Open Question below.
- **Failed handler returns an out-of-range enum value.** Treated as `DeadLetter` (the safe default).
- **Two handlers for the same event type.** Rejected at registration, mirroring `AddHandler`'s duplicate detection (`NimBusSubscriberBuilder.AddHandlerRegistration`, `src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs:119-149`).
- **Failed handler depends on scoped services.** Resolved from the same per-message DI scope as the primary handler (registered `AddTransient`, like `AddHandler`). It can take the same constructor dependencies as the event handler.
- **`Retry` causing an unbounded loop.** Documented as the caller's responsibility. A naive `return Retry` re-enters the pipeline forever; callers are expected to gate on `context.RetryCount` (now monotonically increasing across hook-driven retries).

## Requirements

### Functional Requirements

#### Contracts

- FR-001: A new interface `IFailedMessageHandler<T>` MUST be added (alongside `IEventHandler<T>` on the SDK surface, or under `src/NimBus.Core/Messages/` — see Assumptions), constrained the same way the rest of the handler surface is constrained. The issue writes `where T : IMessage`, but NimBus's real handler constraint is `where T : IEvent` (`src/NimBus.SDK/EventHandlers/IEventHandler.cs:11`). The interface MUST match the real convention:
  ```csharp
  public interface IFailedMessageHandler<T> where T : IEvent
  {
      Task<FailedMessageDisposition> Handle(T message, FailedMessageContext context, CancellationToken cancellationToken = default);
  }
  ```
  Rationale: aligning with `IEventHandler<T>` (same generic constraint, same `Handle(T, context, ct)` shape, same `Task`-returning async contract) lets the failed handler be registered and resolved through the existing `IEventHandler`-style machinery and lets it take the same DI dependencies.
- FR-002: A `FailedMessageDisposition` enum MUST be added with exactly three members and `DeadLetter` as the zero/default value:
  ```csharp
  public enum FailedMessageDisposition
  {
      DeadLetter = 0, // default — fall through to existing terminal failure / DLQ path
      Retry,          // re-enqueue once more (caller's responsibility to avoid loops)
      Handled         // settle as completed; caller has dealt with it
  }
  ```
- FR-003: A `FailedMessageContext` carrier type MUST be added exposing the read-only fields the issue names, sourced from the real message/retry/correlation surfaces:
  - `RetryPolicy ExhaustedPolicy` — the `RetryPolicy` that was consulted and found spent (from `IRetryPolicyProvider.GetRetryPolicy(...)`, `src/NimBus.Core/Messages/RetryPolicy.cs`). May be `null` when no policy was configured (exhausted at zero retries).
  - `Exception LastException` — the `EventContextHandlerException.InnerException` (the real handler exception, not the SDK wrapper) caught in `HandleEventRequest`.
  - `int RetryCount` — `messageContext.RetryCount ?? 0` (`IMessage.RetryCount`, `src/NimBus.Core/Messages/Models/Message.cs:30`).
  - `DateTime OriginalDeliveryTimeUtc` — `messageContext.EnqueuedTimeUtc` (`IReceivedMessage.EnqueuedTimeUtc`, `src/NimBus.Core/Messages/IMessageContext.cs:11`).
  - Correlation / audit ids used by the Resolver: `EventId`, `EventTypeId`, `MessageId`, `CorrelationId`, `SessionId`, `Endpoint` (`To`). These are the exact properties the Resolver reads when it builds `MessageEntity` / `UnresolvedEvent` (`src/NimBus.Resolver/Services/ResolverService.cs:157-189`, `227-257`), so the failed handler can correlate against the same audit record the operator sees in the WebApp.
  ```csharp
  public sealed class FailedMessageContext
  {
      public RetryPolicy? ExhaustedPolicy { get; init; }    // may be null when no policy was configured
      public Exception LastException { get; init; }         // EventContextHandlerException.InnerException
      public int RetryCount { get; init; }
      public DateTime OriginalDeliveryTimeUtc { get; init; }// messageContext.EnqueuedTimeUtc
      public string EventId { get; init; }
      public string EventTypeId { get; init; }
      public string MessageId { get; init; }
      public string CorrelationId { get; init; }
      public string SessionId { get; init; }
      public string Endpoint { get; init; }                 // messageContext.To
  }
  ```

#### Registration

- FR-010: `NimBusSubscriberBuilder` MUST gain `AddFailedHandler<TEvent, THandler>()` aligned with the existing `AddHandler<TEvent, THandler>()` (`src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs:31-38`):
  ```csharp
  public NimBusSubscriberBuilder AddFailedHandler<TEvent, THandler>()
      where TEvent : IEvent
      where THandler : class, IFailedMessageHandler<TEvent>
  {
      // Same shape as AddHandler: validate the handler implements IFailedMessageHandler<TEvent>,
      // register the closed interface as AddTransient, and record the EventTypeId-keyed
      // registration so dispatch can resolve it per message.
      ...
      return this;
  }
  ```
- FR-011: Registration MUST key on the wire `EventTypeId` (`new EventType(eventType).Id`), exactly as `AddHandler` does (`NimBusSubscriberBuilder.cs:118`), so the failed handler is found by the same dispatch key the event handler uses.
- FR-012: Registering two failed handlers for the same event type, or two distinct CLR types that collapse onto the same `EventTypeId`, MUST fail loudly at startup, mirroring `AddHandler`'s existing duplicate / collision detection (`NimBusSubscriberBuilder.cs:119-149`).
- FR-013: A failed handler MUST be resolvable from the per-message DI scope with the same lifetime semantics as an event handler (`Services.AddTransient(expectedHandlerInterface, handlerType)`, `NimBusSubscriberBuilder.cs:151`). It MAY be registered independently of whether an `IEventHandler<TEvent>` is registered, but it has no effect unless the event type is actually processed by `StrictMessageHandler`.
- FR-014: The builder example in the issue (`b.AddFailedHandler<OrderPlaced, OrderPlacedFailedHandler>()` inside `AddNimBusSubscriber("orders", b => { ... })`) MUST compile against the real `AddNimBusSubscriber(string endpoint, Action<NimBusSubscriberBuilder>)` overload (`src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs:83`).

#### Invocation in `StrictMessageHandler`

- FR-020: The failed handler MUST be invoked from `StrictMessageHandler.CheckForRetry(...)` at the retry-exhaustion point — i.e. when control would otherwise `return` without having sent a `RetryRequest` (`src/NimBus.Core/Messages/StrictMessageHandler.cs:379-384`). It MUST NOT be invoked when `retryCount < policy.MaxRetries` (a `RetryRequest` is sent instead — retries are not yet exhausted).
- FR-021: The failed handler MUST be invoked **exactly once** per retry-exhaustion event. It MUST NOT be invoked on transient failures, on permanent failures (v1), on session-blocked deferrals, or on the Resubmission / Skip / Handoff control paths.
- FR-022: To resolve and invoke the typed handler, `StrictMessageHandler` (or a small dispatcher it delegates to, analogous to how `IEventContextHandler` dispatches the primary handler) MUST look up an `IFailedMessageHandler<T>` registered for the message's `EventTypeId`, deserialize the typed event from `messageContext.MessageContent.EventContent` the same way the primary dispatch does, build the `FailedMessageContext` per FR-003, and `await handler.Handle(typedEvent, ctx, cancellationToken)`.
- FR-023: If no failed handler is registered for the event type, `CheckForRetry` MUST behave exactly as today (FR-050 — zero behaviour change). The lookup MUST be a cheap absent-check, not a per-message reflection scan.
- FR-024: The disposition returned by the failed handler MUST be processed as:
  - `DeadLetter` → no additional action; the existing terminal fall-through stands (the `EventContextHandlerException` path has already sent the error response, blocked the session, and completed the broker message; the absence of a `RetryRequest` is what lets the message reach its terminal failure / DLQ disposition).
  - `Retry` → send exactly one `RetryRequest` via `SendRetryResponse(messageContext, delayMinutes, ...)`, reusing the existing path (`StrictMessageHandler.cs:382`, `ResponseService.SendRetryResponse`). `delayMinutes` comes from `ExhaustedPolicy.GetDelayMinutes(retryCount)` when a policy exists, otherwise the FR-026 default.
  - `Handled` → settle the message as completed: send a `ResolutionResponse` via `SendResolutionResponse(...)` and unblock the session, superseding the error response already sent on this path (see FR-027 / Edge Cases).
- FR-025: A failed-handler exception MUST NOT lose the message. Any exception (including `OperationCanceledException`) thrown by `handler.Handle(...)` MUST be caught inside `CheckForRetry`, logged via the handler's `_logger` (`LogWarning`/`LogError` naming the failed-handler type and the secondary exception), and the message MUST take the `DeadLetter` default path — identical to today's terminal behaviour. (Mirrors the safety posture of `MessageHandler`'s catch blocks, `src/NimBus.Core/Messages/MessageHandler.cs:73-113`.)
- FR-026: When `Retry` is chosen and no `RetryPolicy` is in scope to derive a delay, the implementation MUST use a documented non-zero default delay (proposed: 1 minute, matching `RetryPolicy.BaseDelay`'s default of `TimeSpan.FromMinutes(1)`, `src/NimBus.Core/Messages/RetryPolicy.cs:24`) rather than a zero-delay hot-loop.
- FR-027: When `Handled` is chosen, the session MUST NOT be left blocked. Because the `EventContextHandlerException` path calls `BlockSession` before `CheckForRetry` (`StrictMessageHandler.cs:99`), the `Handled` disposition MUST unblock the session (and continue any deferred messages, mirroring the success path in `HandleRetryRequest`, `StrictMessageHandler.cs:113-114`) so the session's sibling messages are not stranded.

#### Audit / disposition surfacing

- FR-030: The chosen disposition MUST be observable in the audit trail so an operator can see why a message did or did not reach the DLQ. NimBus records each message's outcome at the Resolver as a `ResolutionStatus` derived from the response `MessageType` (`MessageTypeToStatusMap`, `src/NimBus.Resolver/Services/ResolverService.cs:26-45`). The dispositions therefore surface as:
  - `Handled` → the follow-up `ResolutionResponse` flips the event to `ResolutionStatus.Completed`.
  - `Retry` → the `RetryRequest` records a `ResolutionStatus.Pending` (and a `MessageAuditType.Retry` audit row, written by the Resolver for `RetryRequest` messages, `src/NimBus.Resolver/Services/ResolverService.cs:149-153`).
  - `DeadLetter` → the already-sent `ErrorResponse` records `ResolutionStatus.Failed`, and the eventual DLQ write records `ResolutionStatus.DeadLettered` via `DeadLetterErrorDescription` (`ResolverService.cs:432-434`).
- FR-031: The dead-letter reason / error description MUST make clear that retries were exhausted before the disposition was taken, so the WebApp's existing error rendering shows the operator the failure was terminal, not transient. (Reuses the existing `DeadLetterErrorDescription` / `Reason` surfacing — no new field required for v1.)
- FR-032: A structured log entry MUST be emitted at the invocation site naming the event type, `RetryCount`, and the chosen `FailedMessageDisposition`, so the disposition is queryable in logs/OpenTelemetry even before the Resolver write lands. (The handler already has a `_logger`; reuse the `LogInfo`/`LogError` helpers, `StrictMessageHandler.cs:411-421`.)

#### Tests

- FR-040: Unit tests in `tests/NimBus.Core.Tests/StrictMessageHandlerTests.cs` (the existing strict-handler suite) MUST cover, using the existing test harness (the file already builds a `StrictMessageHandler` with a fake response service and asserts on `RetryCalls`, `tests/NimBus.Core.Tests/StrictMessageHandlerTests.cs:678,760`):
  1. `DeadLetter` — failed handler runs once, no `RetryRequest`, terminal path unchanged.
  2. `Retry` — exactly one additional `RetryRequest` sent past the policy budget; re-enqueued `RetryCount` incremented.
  3. `Handled` — a `ResolutionResponse` is sent and the session unblocked; no `RetryRequest`.
  4. Failed handler throws → `DeadLetter` default taken, warning logged, message not lost.
  5. No failed handler registered → `CheckForRetry` behaves exactly as the existing `HandleEventRequest_HandlerThrowsRetryCountExceeded_NoRetryResponse` test (`StrictMessageHandlerTests.cs:306`).
  6. Failed handler is NOT invoked while retry budget remains (`RetryCount < MaxRetries`).
- FR-041: A registration test MUST verify `AddFailedHandler<TEvent,THandler>()` registers the handler, rejects duplicates, and rejects `EventTypeId` collisions — mirroring the existing `AddHandler` registration tests.
- FR-042: An E2E test in `tests/NimBus.EndToEnd.Tests/` MUST drive a real event whose handler always throws through the full pipeline with each disposition and assert the resulting `ResolutionStatus` in the message store: `Handled → Completed`, `Retry → Pending` (then eventual terminal), `DeadLetter → Failed`/`DeadLettered`.

### Non-Functional Requirements

- NFR-001: When no failed handler is registered, the per-message overhead added to `CheckForRetry` MUST be O(1) — a single dictionary/absent lookup keyed on `EventTypeId`, no reflection, no DI scope creation. The exhaustion path stays as cheap as it is today (FR-023).
- NFR-002: The failed handler MUST run inside the existing message-processing scope and lock window; it MUST NOT spawn a background task or fire-and-forget the disposition. The disposition is awaited so the resulting `RetryRequest` / `ResolutionResponse` is sent before the broker message is settled — the same in-lock guarantee the rest of `StrictMessageHandler` relies on.
- NFR-003: The feature MUST be purely additive and backwards compatible: existing subscribers, existing tests, and the public `StrictMessageHandler` constructors (`src/NimBus.Core/Messages/StrictMessageHandler.cs:19-53`) MUST continue to work. Any new dependency the dispatcher needs (the failed-handler registry) MUST be optional / nullable, like the existing optional `_retryPolicyProvider` and `_permanentFailureClassifier`.
- NFR-004: The hook MUST NOT introduce a new NuGet dependency. It composes existing `NimBus.Core` / `NimBus.SDK` types.
- NFR-005: The failed handler runs exactly once per retry-exhaustion event and never on the success / transient / deferral paths, so it adds zero latency to the happy path.

## Key Entities

- **`IFailedMessageHandler<T>`** — new interface, `where T : IEvent`, single method `Task<FailedMessageDisposition> Handle(T message, FailedMessageContext context, CancellationToken ct)`. Mirrors `IEventHandler<T>` (`src/NimBus.SDK/EventHandlers/IEventHandler.cs`).
- **`FailedMessageDisposition`** — new enum: `DeadLetter` (default), `Retry`, `Handled`.
- **`FailedMessageContext`** — new carrier: exhausted `RetryPolicy`, last exception (the inner handler exception), retry count, original delivery time, and the Resolver correlation ids (`EventId`, `EventTypeId`, `MessageId`, `CorrelationId`, `SessionId`, endpoint).
- **`NimBusSubscriberBuilder.AddFailedHandler<TEvent,THandler>()`** — new registration method, aligned with `AddHandler<TEvent,THandler>()` (`src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs`).
- **`StrictMessageHandler.CheckForRetry(...)`** — existing method, the invocation site (`src/NimBus.Core/Messages/StrictMessageHandler.cs:370-394`). The retry-exhaustion `return` is where the hook fires.
- **`RetryPolicy` / `IRetryPolicyProvider`** — existing retry abstraction (`src/NimBus.Core/Messages/RetryPolicy.cs`, `IRetryPolicyProvider.cs`). The hook reads the exhausted policy; it does not change retry counting.
- **`IResponseService`** — existing; `SendRetryResponse` (for `Retry`) and `SendResolutionResponse` (for `Handled`) are reused (`src/NimBus.Core/Messages/ResponseService.cs`).
- **`ResolutionStatus`** — existing message-store outcome enum (`src/NimBus.MessageStore.Abstractions/States/ResolutionStatus.cs`). The disposition surfaces through this via the Resolver's `MessageTypeToStatusMap`. **This is the audit "disposition" field for v1; no new entity field is added.**
- **`MessageAuditEntity` / `MessageAuditType`** — existing audit row (`src/NimBus.MessageStore.Abstractions/MessageAuditEntity.cs`). `MessageAuditType.Retry` is already written for `RetryRequest` messages by the Resolver, so a `Retry` disposition produces a recognisable audit row.

## Success Criteria

### Measurable Outcomes

- SC-001: A registered `IFailedMessageHandler<T>` is invoked exactly once when, and only when, retries for that event type are exhausted — verified by the unit tests in FR-040 (invocation-count and budget-remaining cases) and the E2E test in FR-042.
- SC-002: Each of the three dispositions produces the documented outcome in the message store: `Handled → ResolutionStatus.Completed`, `Retry → ResolutionStatus.Pending` (with a `MessageAuditType.Retry` row) followed by terminal settlement, `DeadLetter → ResolutionStatus.Failed`/`DeadLettered`. Verified by FR-042.
- SC-003: A failed handler that throws never loses the message: the message takes the `DeadLetter` default and a warning naming the buggy handler is logged. Verified by FR-040 case 4.
- SC-004: Subscribers with no failed handler are behaviourally unchanged: the existing `StrictMessageHandlerTests` (including `HandleEventRequest_HandlerThrowsRetryCountExceeded_NoRetryResponse`) and the full `NimBus.Core.Tests` / `NimBus.SDK.Tests` suites pass with the feature merged.
- SC-005: `AddFailedHandler<TEvent,THandler>()` registers, rejects duplicates, and rejects `EventTypeId` collisions identically to `AddHandler` — verified by FR-041.
- SC-006: An operator viewing a dead-lettered event in the WebApp can tell from the audit trail / error description that retries were exhausted before disposition (FR-031) — verified by inspecting the persisted `MessageEntity` / `UnresolvedEvent` in the E2E test.

## Assumptions

- The issue's named integration points use `src/NimBus.Core/Messages/IFailedMessageHandler.cs` and `FailedMessageContext.cs`. The actual `IEventHandler<T>` lives in `src/NimBus.SDK/EventHandlers/`, while `StrictMessageHandler`, `RetryPolicy`, and `IResponseService` live in `src/NimBus.Core/Messages/`. The new interface SHOULD live next to whichever surface keeps the DI registration in `NimBus.SDK` clean — likely the SDK `EventHandlers` folder for the public interface, with `FailedMessageContext` in `NimBus.Core/Messages` next to `RetryPolicy`. The implementer chooses; the contract is what matters. (The issue's `NimBus.Core/Messages/` path is honoured for `FailedMessageContext`.)
- The issue writes `where T : IMessage`; NimBus's real handler constraint is `where T : IEvent` (`src/NimBus.SDK/EventHandlers/IEventHandler.cs:11`, `src/NimBus.Abstractions/Events/IEvent.cs`). The spec uses `IEvent` to match the real convention. `IMessage` in NimBus is the transport envelope (`src/NimBus.Core/Messages/Models/Message.cs:5`), not the typed event payload, so the issue's wording is a naming mismatch, not a different design.
- The "after retries exhausted, before dead-letter" point in NimBus is the retry-exhaustion `return` inside `StrictMessageHandler.CheckForRetry(...)`. NimBus's `StrictMessageHandler` does not itself call `messageContext.DeadLetter(...)` on the retry-exhaustion path — it sends an `ErrorResponse` to the Resolver and completes the broker message, and the message reaches the DLQ via the broker's own delivery-count policy or the base `MessageHandler`'s permanent-failure path. "Before dead-letter" is therefore interpreted as "before the message is allowed to reach its terminal failure disposition," which is the `CheckForRetry` exhaustion point.
- The typed event can be deserialized from `messageContext.MessageContent.EventContent` at the `CheckForRetry` point using the same dispatch mechanism the primary handler uses (`IEventContextHandler`). If that dispatcher is not directly reachable from `StrictMessageHandler`, a thin failed-handler dispatcher analogous to `EventContextHandler` is introduced and injected via a new optional constructor parameter (consistent with how `_retryPolicyProvider` / `_permanentFailureClassifier` are optional).
- The Resolver's existing `ResolutionStatus` outcome is a sufficient "disposition" record for v1. The issue says "audit row should record the disposition"; `Completed` / `Pending` / `Failed` / `DeadLettered` already distinguish the three dispositions' outcomes, so no new audit field is strictly required. (See Open Questions.)
- `RetryPolicy.GetDelayMinutes(retryCount)` is the canonical delay source for a `Retry` disposition (`src/NimBus.Core/Messages/RetryPolicy.cs:53`).

## Out of Scope

- Changing how retries are counted or configured (`RetryPolicy`, `IRetryPolicyProvider`, `DefaultRetryPolicyProvider`).
- A loop guard for `Retry`. Loop avoidance is explicitly the caller's responsibility (issue text).
- Invoking the hook on the permanent-failure path (`PermanentFailureException` → base `MessageHandler` dead-letter). v1 fires only on retry exhaustion of an `EventContextHandlerException`.
- Adding the hook to the Resolver, Manager, or the legacy non-strict `MessageHandler` dispatch.
- Async handoff (`MarkPendingHandoff`) as a failed-handler disposition.
- A dedicated `Disposition` column on `MessageAuditEntity`, a new `MessageAuditType` member per disposition, or a new `ResolutionStatus` value. v1 reuses existing outcome surfacing.
- Multiple failed handlers per event type or a failed-handler pipeline/chain.
- Surfacing the disposition as a distinct WebApp UI badge (it surfaces through the existing `ResolutionStatus` rendering).

## Open Questions

- **Should the disposition get a dedicated audit field?** v1 reuses `ResolutionStatus` (`Completed`/`Pending`/`Failed`/`DeadLettered`) plus the `MessageAuditType.Retry` row the Resolver already writes. If operators need to distinguish "dead-lettered because a failed handler chose `DeadLetter`" from "dead-lettered because no handler ran," a dedicated `Disposition` field on `MessageAuditEntity` (and a Resolver write of it) would be a follow-up. Carried open because it adds a storage migration the v1 scope deliberately avoids.
- **`Handled`/`Retry` ordering vs. the already-sent `ErrorResponse`.** On the `EventContextHandlerException` path, `SendErrorResponse` + `BlockSession` + `CompleteMessage` all run *before* `CheckForRetry` (`StrictMessageHandler.cs:98-101`). A `Handled` disposition must then send a *second* response (`ResolutionResponse`) that supersedes the error at the Resolver, and unblock the session. Open: is "error then resolution" cleanly idempotent at the Resolver for the same `EventId`, or should the invocation point move *before* `SendErrorResponse` so the error is only sent on a true `DeadLetter`? Moving it earlier is cleaner but changes more of `HandleEventRequest`; sending a superseding response is smaller but relies on Resolver last-write-wins per `EventId`. To be settled during implementation against the Resolver's `UpdateState` semantics (`src/NimBus.Resolver/Services/ResolverService.cs:300-323`).
- **Should the hook also fire on permanent failures?** A `PermanentFailureException` skips retries entirely and dead-letters via the base `MessageHandler` (`MessageHandler.cs:89-114`). Some callers may want a last-chance hook there too. Out of v1 scope; flagged as a possible follow-up that would invoke the same `IFailedMessageHandler<T>` from the base handler's permanent-failure catch.
- **Default `Retry` delay when no policy exists.** Proposed 1 minute (= `RetryPolicy.BaseDelay` default). Confirm this is the desired default rather than reusing the legacy `RetryDefinitions` delays.

## Resolved Questions

- **What is the real "after retries exhausted, before dead-letter" point?** The retry-exhaustion `return` in `StrictMessageHandler.CheckForRetry(...)` (`src/NimBus.Core/Messages/StrictMessageHandler.cs:379-394`). Resolved by reading the strict handler's `EventContextHandlerException` catch → `CheckForRetry` flow.
- **What generic constraint does the interface use?** `where T : IEvent`, matching `IEventHandler<T>`. Resolved — the issue's `IMessage` is the transport envelope, not the event payload.
- **How is `AddFailedHandler` registered?** Exactly like `AddHandler<TEvent,THandler>()` — validate the interface, `AddTransient` the closed interface, key the registration on `EventTypeId`, reject duplicates/collisions. Resolved against `NimBusSubscriberBuilder` (`src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs:31-164`).
- **How does `Retry` re-enqueue?** Via the existing `SendRetryResponse(...)`; `ResponseService.CreateRetryResponse` increments `RetryCount` (`src/NimBus.Core/Messages/ResponseService.cs:135-149`). Resolved.
- **How does `Handled` settle the message?** Via the existing `SendResolutionResponse(...)`, which the Resolver maps to `ResolutionStatus.Completed`. Resolved against `MessageTypeToStatusMap` (`src/NimBus.Resolver/Services/ResolverService.cs:41`).
- **What happens if the failed handler throws?** Caught inside `CheckForRetry`, logged, and the message takes the `DeadLetter` default — never lost. Resolved per the issue's hard acceptance criterion and the safety posture of the base `MessageHandler` catch blocks.
- **What does the hook fire on?** Only `EventContextHandlerException` after retry budget is spent — not transient, not permanent, not control-flow (Skip/Resubmit/Handoff) paths. Resolved by reading the catch structure of `StrictMessageHandler`.
