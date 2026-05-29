# Feature Specification: Rate Limiting Middleware

Feature Branch: `017-rate-limiting-middleware`
Created: 2026-05-28
Updated: 2026-05-28
Status: Proposed
Input: User description (GitHub issue #34): "Use case: A subscriber with high concurrency can easily overwhelm a downstream service or exceed an external API quota — Service Bus will happily fan out faster than the dependency can absorb. Rate limiting at the pipeline level lets operators cap throughput per endpoint or per event type without changing handlers, and (unlike a circuit breaker) it's a steady-state mechanism, not an incident response. Combined with the existing retry pipeline, throttled messages return to the queue and redeliver, so no work is lost. Proposed API: `services.AddNimBus(b => { b.AddPipelineBehavior<RateLimitingMiddleware>(opts => { opts.Strategy = RateLimitStrategy.TokenBucket; opts.TokensPerPeriod = 100; opts.Period = TimeSpan.FromMinutes(1); opts.BurstCapacity = 200; }); }); services.AddNimBusSubscriber(\"billing\", b => { b.ConfigureRateLimit<PaymentRequested>(opts => { opts.Strategy = RateLimitStrategy.SlidingWindow; opts.PermitLimit = 50; opts.Window = TimeSpan.FromSeconds(10); }); });`. Strategies map to System.Threading.RateLimiting: TokenBucket, SlidingWindow, FixedWindow, Concurrency. Integration points: src/NimBus.Core/Pipeline/RateLimitingMiddleware.cs (new); System.Threading.RateLimiting (BCL — no third-party dep); when the limit is exceeded the middleware throws RateLimitExceededException, caught in StrictMessageHandler and translated to Abandon (returns to queue) — never DLQ; per-endpoint and per-event-type configuration via NimBusSubscriberBuilder; metrics nimbus.rate_limit.permits_acquired_total, nimbus.rate_limit.permits_rejected_total, nimbus.rate_limit.queue_depth (gauge). Acceptance: RateLimitingMiddleware implementing IMessagePipelineBehavior; all four strategies wired through configuration; limit-exceeded path abandons (does NOT dead-letter); per-endpoint and per-event-type configuration; metrics emitted via OpenTelemetry; unit tests for each strategy; E2E test proving abandoned messages redeliver and eventually drain; documentation in docs/pipeline-middleware.md. Open questions: should the middleware acquire-with-wait (block briefly) or fail-fast and abandon? (probably fail-fast — blocking holds the session lock and creates head-of-line stalls); how does this interact with the Circuit Breaker (#7)? (complementary: circuit breaks on failures, rate-limit shapes successful throughput)."

## Problem

A NimBus subscriber consumes from a Service Bus topic via a session processor with a configurable concurrency. When the broker has a backlog, the processor fans out as many concurrent sessions as the configured concurrency allows and drains them as fast as the handler returns. That is the right behaviour when the handler is self-contained, but it is exactly wrong when the handler calls a rate-limited downstream — a payment gateway with a 100-requests/minute quota, a legacy SOAP endpoint that falls over above N concurrent calls, a third-party API that returns HTTP 429. NimBus today has no application-level throughput shaping. The only back-pressure knobs are broker-level (`MaxConcurrentSessions`, prefetch) which are coarse, process-wide, and cannot express "100 `PaymentRequested` per minute, but `OrderPlaced` is unbounded."

NimBus already has the right insertion point. `IMessagePipelineBehavior` (`src/NimBus.Core/Extensions/IMessagePipelineBehavior.cs:33`) wraps message handling with the exact signature the issue assumes:

```csharp
Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default);
```

Behaviors are composed inside-out by `MessagePipeline.Execute` (`src/NimBus.Core/Extensions/MessagePipeline.cs:41-59`) — the loop builds the chain from the terminal handler outward so the first-registered behavior is outermost — and they run before the terminal `HandleByMessageType` dispatch in `MessageHandler.Handle` (`src/NimBus.Core/Messages/MessageHandler.cs:44-56`). Built-in behaviors already live in `src/NimBus.Core/Pipeline/` (`LoggingMiddleware`, `ValidationMiddleware`). A rate-limiting behavior slots in alongside them: it acquires a permit before calling `next`, and when no permit is available it must return the message to the queue so redelivery drains the backlog at the permitted rate rather than dropping work.

The abandon-on-back-pressure mechanism the issue wants already exists and is the same one the Circuit Breaker spec (`011-circuit-breaker-middleware`) reuses. The settlement decision is owned by the base `MessageHandler.Handle`, NOT by `StrictMessageHandler`: the issue says "caught in StrictMessageHandler," but the relevant catch is in the base handler. `MessageHandler.Handle` wraps the pipeline in a `try` and catches `TransientException`, then calls `IMessageContext.Abandon(...)` (`src/NimBus.Core/Messages/MessageHandler.cs:63-82`):

```csharp
catch (TransientException transientException)
{
    // ... NotifyFailed ...
    try
    {
        await messageContext.Abandon(transientException);
    }
    catch (Exception ex) { /* logged, swallowed */ }
}
```

On the Service Bus transport, `MessageContext.Abandon` (`src/NimBus.ServiceBus/MessageContext.cs:178-183`) is deliberately a no-op:

```csharp
/// We actually don't do anything when Abandon is called.
/// The intention of abandoning a message is to make a retry attempt, and if we actually call
/// IMessageSession.AbandonAsync, then the lock will be released and the message will be picked
/// up again immediately. By doing nothing, the lock will expire before the message is picked up again.
public Task Abandon(TransientException exception) => Task.CompletedTask;
```

It does NOT call `IMessageSession.AbandonAsync`, because abandoning immediately releases the lock and the broker redelivers instantly (a hot loop). By doing nothing, the session lock simply expires and the broker redelivers after the lock timeout, giving a natural cool-down. Crucially, `IMessageContext.Abandon` takes a `TransientException` (`src/NimBus.Core/Messages/IMessageContext.cs:27`) — so the throttle signal must be a `TransientException` (or derive from it). Rate limiting reuses this exact path: a rejected permit becomes a transient-shaped exception that flows through the abandon route, never to the dead-letter queue. (`PermanentFailureException` is the path that dead-letters — `src/NimBus.Core/Messages/MessageHandler.cs:89-114`.)

This spec adds `RateLimitingMiddleware` as a first-class pipeline behavior backed by `System.Threading.RateLimiting` (the .NET BCL, no third-party dependency), with per-endpoint and per-event-type configuration, an abandon-not-DLQ throttle outcome, and OpenTelemetry counters/gauge consistent with the existing `NimBusMeters` pattern.

## Scope

In scope:
- A new `RateLimitingMiddleware : IMessagePipelineBehavior` in `src/NimBus.Core/Pipeline/RateLimitingMiddleware.cs`, alongside the existing built-in middleware.
- A `RateLimitOptions` options type and a `RateLimitStrategy` enum (`TokenBucket`, `SlidingWindow`, `FixedWindow`, `Concurrency`) that map 1:1 onto `System.Threading.RateLimiting` limiters.
- A new `RateLimitExceededException : TransientException` in `src/NimBus.Core/Messages/Exceptions/` so that the existing `MessageHandler.Handle` `catch (TransientException) → Abandon` path returns throttled messages to the queue and never dead-letters them.
- A registration surface: a default-rate-limit configuration on the `AddNimBus` / `INimBusBuilder` path and a per-event-type override on `NimBusSubscriberBuilder` (`ConfigureRateLimit<TEvent>`).
- A new OpenTelemetry meter (`NimBus.RateLimit`) with `nimbus.rate_limit.permits_acquired`, `nimbus.rate_limit.permits_rejected` counters and a `nimbus.rate_limit.queue_depth` observable gauge, registered through `NimBusInstrumentation.AllMeterNames` so `AddNimBusInstrumentation()` observes them.
- Unit tests for each of the four strategies; an E2E test proving abandoned (throttled) messages redeliver and the backlog eventually drains.
- Documentation in `docs/pipeline-middleware.md`.

Out of scope:
- Broker-level back-pressure tuning (`MaxConcurrentSessions`, prefetch). Rate limiting is application-level shaping, complementary to those knobs.
- Circuit-breaking on downstream failures (the separate Circuit Breaker feature, spec `011-circuit-breaker-middleware`, issue #7). Rate limiting shapes *successful* steady-state throughput; the circuit breaker is incident response on a failing dependency. The two are complementary and may both wrap a handler.
- Distributed / cross-process rate limiting (a shared token bucket across N subscriber instances). v1 is per-process; the limiter lives in the subscriber's memory. See Open Questions.
- Rate limiting on the publisher / `ISender` path. This is a consumer-side concern.
- Acquire-with-wait (blocking) semantics as the default — see Open Questions; v1 is fail-fast-and-abandon to avoid holding the Service Bus session lock.

## User Scenarios & Testing

### User Story 1 - Cap an endpoint's throughput to protect a downstream quota (Priority: P1)

As an adapter author whose handler calls a payment gateway capped at 100 requests/minute, I want to declare a token-bucket limit on the subscriber so NimBus never dispatches more than 100 handler invocations per minute regardless of how fast Service Bus fans out, without changing the handler.

Why this priority: This is the central use case the feature exists for. Without it, a backlog spike causes the subscriber to blow the downstream quota and earn HTTP 429s, which today only surface as handler exceptions and retries.

Independent Test: Register `RateLimitingMiddleware` with `TokenBucket`, `TokensPerPeriod = 100`, `Period = 1 minute`, `BurstCapacity = 100`. Enqueue 500 messages. Measure handler invocations over time. The handler is invoked at ~100/minute; the remaining messages stay on the broker and drain over subsequent minutes.

Acceptance Scenarios:

1. Given a token-bucket limit of 100/minute and 500 enqueued messages, When the subscriber runs, Then no more than `BurstCapacity` handler invocations occur in the first instant and the long-run rate does not exceed 100/minute.
2. Given a permit is acquired, When the handler completes, Then `nimbus.rate_limit.permits_acquired` increments by 1 and the message is completed normally.
3. Given a permit is rejected, When the middleware runs, Then `RateLimitExceededException` is thrown, the message is NOT dead-lettered, and `nimbus.rate_limit.permits_rejected` increments by 1.
4. Given the backlog of 500 messages, When enough time elapses, Then every message is eventually processed exactly as many times as a successful handler run requires — no message is lost or dead-lettered solely due to throttling.

---

### User Story 2 - Per-event-type limits within one endpoint (Priority: P1)

As the owner of a `billing` subscriber that handles both `PaymentRequested` (quota-bound) and `InvoiceGenerated` (cheap, local), I want to rate-limit only `PaymentRequested` and leave `InvoiceGenerated` unbounded, configured per event type rather than for the whole endpoint.

Why this priority: A single endpoint commonly hosts several handlers with different downstream cost profiles. A process-wide limit either over-throttles the cheap handler or under-protects the expensive one.

Independent Test: Register a default (no limit, or a generous limit) and a `ConfigureRateLimit<PaymentRequested>(SlidingWindow, PermitLimit = 50, Window = 10s)` override. Enqueue interleaved `PaymentRequested` and `InvoiceGenerated`. Confirm `PaymentRequested` is throttled to 50/10s while `InvoiceGenerated` is never throttled.

Acceptance Scenarios:

1. Given a per-event-type override for `PaymentRequested` and no override for `InvoiceGenerated`, When both event types are enqueued, Then `PaymentRequested` permits are accounted against the override limiter and `InvoiceGenerated` is dispatched without permit acquisition.
2. Given both a default endpoint limit and a `PaymentRequested` override, When a `PaymentRequested` arrives, Then the most-specific (per-event-type) limiter governs it; the default does not double-charge.
3. Given the per-event-type limiter is keyed by the event's `EventType.Id` (the same wire id `NimBusSubscriberBuilder.AddHandlerRegistration` dedupes on, `src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs:118`), When two distinct event types share a limit configuration, Then each maintains its own independent permit pool.

---

### User Story 3 - Throttled messages redeliver, they do not dead-letter (Priority: P1)

As an operator, I need certainty that throttling never loses a message and never silently routes one to the dead-letter queue — a throttled message must return to the queue and be redelivered after the session-lock cool-down.

Why this priority: The whole value proposition is "no work is lost." A throttle that DLQ'd messages would turn a transient back-pressure event into operator toil and data loss.

Independent Test: Inject a limiter that rejects every permit. Enqueue one message. Assert the message is never completed and never dead-lettered; the lock expires and the broker redelivers. After switching the limiter to accept, the next delivery completes.

Acceptance Scenarios:

1. Given the limiter rejects a permit, When `RateLimitingMiddleware` runs, Then it throws `RateLimitExceededException` and the pipeline aborts before the terminal handler dispatch.
2. Given `RateLimitExceededException` (a `TransientException`) propagates, When `MessageHandler.Handle` observes it, Then its `catch (TransientException) → IMessageContext.Abandon(...)` branch runs (no `Complete`, no `DeadLetter`) — so the Service Bus session lock simply expires per `MessageContext.Abandon`'s documented no-op behaviour and the broker redelivers.
3. Given a throttled message redelivers after the lock timeout, When a permit is then available, Then the handler runs and the message completes — the throttle added latency, not loss.
4. Given a session-enabled endpoint, When a head-of-line message is throttled and abandoned, Then sibling messages in the same session remain ordered behind it (the session lock is not released to another consumer mid-session) — see Edge Cases.

---

### User Story 4 - Choose the strategy that matches the downstream contract (Priority: P2)

As an adapter author, I want to pick among token-bucket (burst-tolerant average rate), sliding-window and fixed-window (request-count-per-window quotas), and concurrency (max in-flight) so the limiter matches how the downstream expresses its limit.

Why this priority: Different downstreams express limits differently (a quota of N/window vs. a max-concurrent connection cap). One strategy cannot model all of them faithfully.

Independent Test: For each strategy, configure the corresponding limit, drive load past it, and assert the observed shaping matches the strategy's semantics (burst then refill for token bucket; hard count per window for the window strategies; max-in-flight for concurrency).

Acceptance Scenarios:

1. Given `Strategy = TokenBucket` with `BurstCapacity = 200, TokensPerPeriod = 100, Period = 1m`, When 200 messages arrive at once, Then up to 200 are admitted immediately and the bucket refills at 100/minute.
2. Given `Strategy = FixedWindow` with `PermitLimit = 50, Window = 10s`, When 60 messages arrive in one window, Then 50 are admitted and 10 are rejected; the count resets at the window boundary.
3. Given `Strategy = SlidingWindow` with `PermitLimit = 50, Window = 10s`, When load is sustained, Then admissions track the sliding window without the fixed-window boundary burst.
4. Given `Strategy = Concurrency` with `PermitLimit = 10`, When 50 messages arrive, Then at most 10 handler invocations are in flight at once and a permit is released when each handler returns.

---

## Edge Cases

- **Session ordering vs. abandon.** On a session-enabled endpoint, abandoning the head-of-line message (lock expiry) keeps the session locked to this consumer until the lock fully expires; the broker then redelivers the same head-of-line message before siblings. Throttling therefore stalls the *session*, which is correct for ordered processing but means one throttled session does not free its slot for another session until cool-down. Document this trade-off (it is why v1 is fail-fast, not acquire-with-wait — see Open Questions).
- **Concurrency strategy permit release.** A `ConcurrencyLimiter` lease must be released after the handler returns (success OR failure). The middleware MUST dispose the lease in a `finally` so a throwing handler does not leak permits and starve the pool.
- **Limiter disposal / lifetime.** `RateLimiter` instances are stateful and must outlive a single message; they are process-scoped singletons keyed by endpoint or event-type id. The middleware itself is registered singleton (matching `NimBusBuilder.AddPipelineBehavior`'s `Services.AddSingleton<TBehavior>()`, `src/NimBus.Core/Extensions/NimBusBuilder.cs:23-28`), so it owns and disposes its limiters.
- **Missing `EventTypeId`.** A control message (`RetryRequest`, `ResubmissionRequest`, `ProcessDeferredRequest`) has no business `EventTypeId` in the same sense as an `EventRequest`; on the Service Bus transport `EventTypeId` can even be `null` (`src/NimBus.ServiceBus/MessageContext.cs:42-45`). The middleware MUST only rate-limit `MessageType.EventRequest` and pass all control/manager message types straight to `next` — otherwise a resubmit could be throttled, which is wrong (manager actions are not steady-state load).
- **No configuration.** If neither a default nor a per-event-type limit is configured, the middleware is effectively a pass-through. Registering it with no limits MUST NOT throttle anything.
- **Cancellation during acquire.** In fail-fast mode the limiter `AttemptAcquire` is synchronous and non-blocking; the `cancellationToken` cancels the surrounding handler, not the permit check. The middleware MUST honour `cancellationToken` before and after acquisition.
- **Burst capacity smaller than tokens-per-period.** A misconfiguration (`BurstCapacity < TokensPerPeriod`) is a valid `TokenBucketRateLimiter` configuration but unusual; validate and either warn or accept per the BCL limiter's own rules. No throw on construction unless the BCL limiter throws.
- **Throttle vs. the existing `ThrottleRetryCount` path.** A separate, pre-existing throttle path (`IMessageContext.ScheduleRedelivery` + `ThrottleRetryCount`, `src/NimBus.Core/Messages/IMessageContext.cs:80-90`, `src/NimBus.ServiceBus/MessageContext.cs:496-539`) exists for Cosmos-throttle exponential backoff. This feature MUST NOT reuse or interfere with that path: rate-limit throttling is lock-expiry redelivery (abandon), not scheduled redelivery, and MUST NOT increment `ThrottleRetryCount`.

## Requirements

### Functional Requirements

#### Middleware contract

- FR-001: A new sealed class `RateLimitingMiddleware` MUST be added to `src/NimBus.Core/Pipeline/RateLimitingMiddleware.cs` implementing `IMessagePipelineBehavior` with the exact signature `Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)`, alongside the existing `LoggingMiddleware` and `ValidationMiddleware` (same `NimBus.Core.Pipeline` namespace). (Confirmed against `src/NimBus.Core/Extensions/IMessagePipelineBehavior.cs:33` — the delegate type is `MessagePipelineDelegate`, matching the issue.)
- FR-002: On entry, the middleware MUST select the governing limiter: the per-event-type limiter for `context.EventTypeId` if one is configured, otherwise the default endpoint limiter if configured, otherwise no limiter (pass through to `next`).
- FR-003: The middleware MUST only apply rate limiting when `context.MessageType == MessageType.EventRequest`. All other message types (`RetryRequest`, `ResubmissionRequest`, `SkipRequest`, `Handoff*`, `Continuation`, `ProcessDeferred`) MUST pass straight to `next` without consuming a permit.
- FR-004: For non-concurrency strategies, the middleware MUST acquire a permit via the limiter's non-blocking `AttemptAcquire(1)`; if the lease `IsAcquired` is false, it MUST throw `RateLimitExceededException` WITHOUT calling `next`.
- FR-005: For the `Concurrency` strategy, the middleware MUST acquire a permit before `next` and dispose the lease in a `finally` after `next` returns or throws, so the in-flight handler count is the bound.
- FR-006: When a permit is acquired, the middleware MUST call `await next(context, cancellationToken)` and dispose the lease appropriately.
- FR-007: The middleware MUST be ordering-aware: it slots into the pipeline in registration order (`MessagePipeline.Execute` composes inside-out — first-registered is outermost). Documentation MUST recommend registering it AFTER `ValidationMiddleware` (so invalid messages are rejected without consuming a permit) and near the terminal handler.

#### Throttle outcome — abandon, never DLQ

- FR-010: A new exception `RateLimitExceededException` MUST be added under `NimBus.Core.Messages.Exceptions` (same folder as `TransientException`, `ThrottleException`, `src/NimBus.Core/Messages/Exceptions/TransientException.cs`). It MUST derive from `TransientException` and carry the strategy, the limit, and the `EventTypeId` for diagnostics.
- FR-011: Because `RateLimitExceededException : TransientException`, the existing `catch (TransientException) → IMessageContext.Abandon(...)` branch in the base `MessageHandler.Handle` (`src/NimBus.Core/Messages/MessageHandler.cs:63-82`) MUST be what redelivers the throttled message. (Correction to the issue: the abandon-vs-dead-letter decision is owned by the base `MessageHandler`, not `StrictMessageHandler`. `StrictMessageHandler`'s own catch blocks handle `EventContextHandlerException` / `SessionBlockedException` / `EventHandlerNotFoundException`; the `TransientException → Abandon` path is in the base handler, and `HandleEventContent` already re-throws `TransientException` unwrapped, `src/NimBus.Core/Messages/StrictMessageHandler.cs:330-333`.) The message MUST NOT be completed and MUST NOT be dead-lettered. `IMessageContext.Abandon` accepts a `TransientException` (`src/NimBus.Core/Messages/IMessageContext.cs:27`), which `RateLimitExceededException` satisfies by inheritance.
- FR-012: The throttle path MUST NOT use the `MessageAlreadyDeadLetteredException` pattern used by `ValidationMiddleware` (`src/NimBus.Core/Pipeline/ValidationMiddleware.cs:29-30`) — that pattern DLQs the message, which is the opposite of the throttle requirement.
- FR-013: On the Service Bus transport, abandon is the documented no-op in `MessageContext.Abandon` (`src/NimBus.ServiceBus/MessageContext.cs:178-183`); the session lock expires and the broker redelivers. The middleware and handler MUST rely on this existing behaviour and add no new transport call.
- FR-014: A throttled `EventRequest` MUST NOT consume retry budget. Throttling is not a handler failure; it MUST NOT increment `RetryCount` or trigger `StrictMessageHandler.CheckForRetry` / the retry-policy path (`src/NimBus.Core/Messages/StrictMessageHandler.cs:370-394`). It is a redelivery of the same message, not a scheduled retry, and the `TransientException` path does not run `CheckForRetry`.

#### Configuration & registration

- FR-020: A `RateLimitStrategy` enum MUST define `TokenBucket`, `SlidingWindow`, `FixedWindow`, `Concurrency`, mapping 1:1 to `System.Threading.RateLimiting` limiter types (`TokenBucketRateLimiter`, `SlidingWindowRateLimiter`, `FixedWindowRateLimiter`, `ConcurrencyLimiter`).
- FR-021: A `RateLimitOptions` type MUST expose the union of limiter settings: `Strategy`, `PermitLimit`, `Window`, `TokensPerPeriod`, `Period`, `BurstCapacity` (token bucket), and `QueueLimit` (default 0 — fail-fast, no internal queueing). Only the fields relevant to the chosen `Strategy` are read.
- FR-022: A default (endpoint-wide) rate limit MUST be configurable. NOTE: the existing `INimBusBuilder.AddPipelineBehavior<TBehavior>()` (`src/NimBus.Core/Extensions/INimBusBuilder.cs:19`, `src/NimBus.Core/Extensions/NimBusBuilder.cs:23-28`) takes NO options delegate — the issue's `AddPipelineBehavior<RateLimitingMiddleware>(opts => ...)` overload does not exist today. This feature MUST add a configuration surface (e.g. an `AddNimBusRateLimiting(Action<RateLimitOptions>)` companion that registers the middleware via `AddPipelineBehavior<RateLimitingMiddleware>()` AND a singleton options/registry the middleware consumes), not assume the overload exists. This mirrors the approach the Circuit Breaker spec (`011`) takes for the same builder limitation. See Open Questions.
- FR-023: A per-event-type override MUST be configurable via a new `ConfigureRateLimit<TEvent>(Action<RateLimitOptions>)` method on `NimBusSubscriberBuilder` (`src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs`). NOTE: this method does not exist today and MUST be added. It MUST key the limiter by the event's `EventType.Id` (the wire id `AddHandlerRegistration` dedupes on, `src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs:118`), NOT the CLR type, so dispatch and limiting agree on the same key.
- FR-024: Per-event-type overrides MUST take precedence over the default endpoint limit; a message governed by an override MUST NOT also be charged against the default limiter.
- FR-025: Limiter instances MUST be created once (process lifetime), held by the singleton middleware, and disposed with it. They MUST NOT be created per message.

#### Metrics

- FR-030: A new meter named `NimBus.RateLimit` MUST be added to `NimBusMeters` (`src/NimBus.Core/Diagnostics/NimBusMeters.cs`) following the existing `Meter` + pre-created instrument pattern, a `RateLimitMeterName` constant added to `NimBusInstrumentation` (`src/NimBus.Core/Diagnostics/NimBusInstrumentation.cs`), and its name added to `NimBusInstrumentation.AllMeterNames` (`src/NimBus.Core/Diagnostics/NimBusInstrumentation.cs:62-70`) so the `foreach … AddMeter(meterName)` loop in `AddNimBusInstrumentation()` (`src/NimBus.OpenTelemetry/Extensions/MeterProviderBuilderExtensions.cs:17-25`) observes it.
- FR-031: The meter MUST emit a counter `nimbus.rate_limit.permits_acquired` (unit `{permits}`) incremented on each successful acquisition, tagged with destination and `nimbus.event_type`.
- FR-032: The meter MUST emit a counter `nimbus.rate_limit.permits_rejected` (unit `{permits}`) incremented on each rejection, tagged identically.
- FR-033: The meter MUST emit an observable gauge `nimbus.rate_limit.queue_depth` reporting the limiter's queued/available permit statistic. NOTE: the existing observable-gauge cadence pattern is the `NimBusGaugeBackgroundService` (`src/NimBus.OpenTelemetry/Instrumentation/NimBusGaugeBackgroundService.cs`) which polls an `I*MetricsQuery` provider and caches the value for a synchronous callback. The gauge SHOULD follow that pattern (an `IRateLimitMetricsQuery` provider polled on `GaugePollInterval`) rather than reading the limiter from inside the OTel export thread. See Open Questions for whether the in-process limiter exposes a queue-depth statistic worth surfacing.
- FR-034: The counter tag names MUST reuse the existing `MessagingAttributes` constants — `DestinationName` (`messaging.destination.name`) and `NimBusEventType` (`nimbus.event_type`), `src/NimBus.Core/Diagnostics/MessagingAttributes.cs:15,28` — used by `NimBusConsumerInstrumentation` so rate-limit metrics correlate with `nimbus.message.*` metrics in dashboards. (The issue's metric names carry a `_total` suffix; NimBus's existing counters omit it — e.g. `nimbus.message.published`, `nimbus.outbox.enqueued` in `NimBusMeters` — so the implemented names drop `_total` to match the established convention.)

#### Tests

- FR-040: Unit tests MUST cover each of the four strategies (`TokenBucket`, `SlidingWindow`, `FixedWindow`, `Concurrency`): a permitted message calls `next`; an over-limit message throws `RateLimitExceededException` without calling `next`; the concurrency limiter releases its lease in `finally` on both success and handler throw.
- FR-041: Unit tests MUST verify the message-type gate (FR-003): a `RetryRequest` / `ResubmissionRequest` passes through without consuming a permit.
- FR-042: Unit tests MUST verify per-event-type precedence over the default (FR-024) and limiter keying by `EventType.Id` (FR-023).
- FR-043: A test (alongside the built-in middleware tests in `tests/NimBus.Core.Tests/`) MUST verify metric emission: the acquired/rejected counters increment on the corresponding outcomes.
- FR-044: An E2E test in `tests/NimBus.EndToEnd.Tests/` (alongside `PipelineAndLifecycleTests`) MUST prove the abandon-redeliver-drain loop: with a rejecting-then-accepting limiter (or a tight token bucket), a backlog is throttled, no message is dead-lettered, and the backlog eventually drains to completion.

#### Documentation

- FR-050: `docs/pipeline-middleware.md` MUST gain a "Rate Limiting" section documenting the four strategies, the registration surface (default + per-event-type), the abandon-not-DLQ behaviour and its session-ordering implication, the metrics, and the recommended ordering relative to `ValidationMiddleware`.
- FR-051: The doc MUST state the relationship to the Circuit Breaker feature (spec `011-circuit-breaker-middleware`, issue #7): rate limiting shapes successful throughput; circuit breaking is failure-driven incident response; they are complementary and may be stacked (a throttle must not look like a failure to the breaker — see Open Questions).

### Non-Functional Requirements

- NFR-001: No third-party NuGet dependency. `System.Threading.RateLimiting` is part of the .NET BCL (the projects target `net10.0`); it MUST be used directly. (Unlike the Circuit Breaker feature, which introduces a Polly dependency, this feature adds no new package reference.)
- NFR-002: The non-concurrency permit check MUST be non-blocking (`AttemptAcquire`, not `AcquireAsync`) so the middleware never holds the Service Bus session lock waiting for a permit. Holding the lock to wait would create head-of-line stalls and risk `SessionLockLost` (which `MessageContext` surfaces as `TransientException`, e.g. `src/NimBus.ServiceBus/MessageContext.cs:205-207`).
- NFR-003: The middleware MUST add no measurable overhead when no limit governs the message (the message-type gate + dictionary lookup are O(1)); it MUST short-circuit to `next` for control messages and unconfigured event types.
- NFR-004: Limiters MUST be thread-safe for concurrent acquisition — the BCL limiters are; the middleware MUST NOT add non-thread-safe shared state around them. A subscriber processes multiple sessions concurrently and the same limiter is shared across them.
- NFR-005: The feature MUST be opt-in. A subscriber that does not register rate limiting behaves exactly as today (no behaviour change, no new allocations on the hot path).
- NFR-006: Limiter disposal MUST be deterministic — held by the singleton middleware and disposed on container disposal — to avoid leaking timer-backed limiters across test runs / host reloads.

## Key Entities

- **`RateLimitingMiddleware`** — new sealed `IMessagePipelineBehavior` in `src/NimBus.Core/Pipeline/`. Singleton. Owns the limiter instances, performs the per-message permit check, throws `RateLimitExceededException` on rejection, emits metrics.
- **`RateLimitStrategy` enum** — `TokenBucket | SlidingWindow | FixedWindow | Concurrency`. Maps to the four BCL limiter types.
- **`RateLimitOptions`** — union of limiter settings (`Strategy`, `PermitLimit`, `Window`, `TokensPerPeriod`, `Period`, `BurstCapacity`, `QueueLimit`). Consumed when constructing the limiter.
- **`RateLimitExceededException`** — new exception under `NimBus.Core.Messages.Exceptions`, deriving from `TransientException` so the existing `MessageHandler.Handle` abandon path redelivers the message. Never leads to DLQ.
- **`NimBusSubscriberBuilder.ConfigureRateLimit<TEvent>`** — new builder method (per-event-type override), keyed by `EventType.Id`.
- **`NimBus.RateLimit` meter** — new `Meter` on `NimBusMeters`, registered via `NimBusInstrumentation.AllMeterNames`; emits `permits_acquired`, `permits_rejected` counters and a `queue_depth` gauge.
- **`MessageContext.Abandon`** (existing, Service Bus) — the no-op whose lock-expiry redelivery is the throttle's redelivery mechanism. Reused, unchanged.
- **`MessageHandler.Handle`** (existing, base handler) — its `catch (TransientException) → Abandon` branch is the settlement contract the throttle relies on. Unchanged.

## Success Criteria

### Measurable Outcomes

- SC-001: With a token-bucket limit of N/period, the observed long-run handler invocation rate does not exceed N/period under a backlog of ≥ 5×N messages. Verified by the E2E test.
- SC-002: Zero throttled messages are dead-lettered. Verified by asserting the DLQ count stays 0 across the E2E backlog-drain run.
- SC-003: A throttled backlog drains to 100% completion once throughput headroom returns — no message lost. Verified by the E2E test.
- SC-004: Each of the four strategies admits/rejects per its documented semantics. Verified by per-strategy unit tests (FR-040).
- SC-005: A per-event-type override governs only its event type; other event types on the same endpoint are unthrottled. Verified by FR-042.
- SC-006: Control / manager message types (resubmit, retry) are never throttled. Verified by FR-041.
- SC-007: `nimbus.rate_limit.permits_acquired` and `nimbus.rate_limit.permits_rejected` increment on the matching outcomes and are observed via `AddNimBusInstrumentation()`. Verified by FR-043.
- SC-008: A subscriber with no rate-limit registration is behaviourally unchanged (existing pipeline/lifecycle tests stay green).

## Assumptions

- `System.Threading.RateLimiting` is available in the BCL on `net10.0` with the four limiter types and the synchronous `AttemptAcquire` API. It ships in-box; no `PackageReference` is required. (To confirm at implementation time; the BCL package has shipped these types since .NET 7.)
- The pipeline composes behaviors inside-out and runs them before the terminal handler dispatch (`MessagePipeline.Execute` + `MessageHandler.Handle`). Verified (`src/NimBus.Core/Extensions/MessagePipeline.cs:41-59`, `src/NimBus.Core/Messages/MessageHandler.cs:44-56`).
- Abandoning a Service Bus message is a no-op that relies on lock expiry for redelivery (`MessageContext.Abandon` returns `Task.CompletedTask` by design). Verified (`src/NimBus.ServiceBus/MessageContext.cs:178-183`) — this is the redelivery mechanism the throttle reuses.
- `TransientException` is already caught in the base `MessageHandler.Handle` and routed to `Abandon`; deriving `RateLimitExceededException` from it is the minimal change to get abandon-not-DLQ. Verified the catch exists (`src/NimBus.Core/Messages/MessageHandler.cs:63-82`).
- The in-memory transport in `NimBus.Testing` honours Abandon→redeliver semantics, enabling the FR-044 E2E test without Azure. (To confirm during implementation; if not, the test uses the in-memory pipeline harness already exercised by `PipelineAndLifecycleTests`.)
- A per-process limiter is acceptable for v1. Most NimBus subscribers run a single replica per endpoint in the sampled topologies (`samples/`); distributed limiting is deferred. (The Circuit Breaker spec relies on the one-endpoint-per-process `AddNimBusSubscriber` guard for the same reasoning.)

## Out of Scope

- Distributed (cross-replica) rate limiting via a shared store. v1 limiter is in-process.
- Publisher-side / `ISender` rate limiting.
- Acquire-with-wait (blocking) as the default behaviour (it holds the session lock — see Open Questions).
- A WebApp UI for viewing or live-tuning limits. v1 is code-configured; metrics surface in the existing OTel pipeline.
- Circuit breaking (spec `011`, issue #7) — complementary, separate feature.
- Per-tenant / per-session rate limiting (a limiter keyed by `SessionId`). v1 keys by endpoint or event type only.
- Reuse of the existing Cosmos-throttle `ScheduleRedelivery` / `ThrottleRetryCount` backoff path. Rate-limit throttling uses lock-expiry abandon, not scheduled redelivery.

## Open Questions

- **Acquire-with-wait vs. fail-fast.** Fail-fast-and-abandon is the proposed default because a blocking `AcquireAsync` holds the Service Bus session lock while it waits, creating head-of-line stalls and risking `SessionLockLost` (surfaced as `TransientException` in `MessageContext`). Should a *bounded* short wait (e.g. ≤ a few seconds, well inside the lock duration) be offered as an opt-in for endpoints where redelivery churn is costlier than a brief hold? *Leaning fail-fast for v1 (matches the issue's own leaning); revisit with the `QueueLimit` / `AcquireAsync` path behind an explicit opt-in.*
- **Registration API shape.** The issue proposes `b.AddPipelineBehavior<RateLimitingMiddleware>(opts => ...)`, but `INimBusBuilder.AddPipelineBehavior<T>()` has no options overload today and `NimBusSubscriberBuilder` has no `ConfigureRateLimit<TEvent>`. Both surfaces must be added. Should the default-limit configuration live on `INimBusBuilder` (process/extension scope, where `AddPipelineBehavior` lives) or on `NimBusSubscriberBuilder` (endpoint scope, where the per-event override lives and where the endpoint name is known)? *Endpoint scope reads more naturally since both default and override would then live together on the subscriber builder — but this is a joint decision with the identical question raised in the Circuit Breaker spec.*
- **Queue-depth gauge semantics.** A fail-fast limiter with `QueueLimit = 0` has no internal queue, so `nimbus.rate_limit.queue_depth` may always be 0 in the default configuration. Should the gauge instead report available-permits, or the broker-side backlog (which is the real "depth"), or only be emitted when `QueueLimit > 0`? *Resolve alongside the acquire-with-wait decision.*
- **Interaction with the Circuit Breaker pipeline ordering (issue #7).** Where exactly should `RateLimitingMiddleware` sit relative to `CircuitBreakerMiddleware` in the chain? A throttle must not be counted as a failure by the breaker. Recommended order: `Validation → RateLimiting → CircuitBreaker → handler` (rate-limit rejects before the breaker can sample them), but the stacking order needs a joint decision with spec `011`.

## Resolved Questions

- Throttled messages abandon, never dead-letter. Resolved — the feature's core guarantee is no work lost; DLQ would defeat it. The abandon path (lock expiry → redelivery) already exists (`MessageHandler.Handle`'s `catch (TransientException) → Abandon`) and is reused via `RateLimitExceededException : TransientException`.
- The abandon decision lives in the base `MessageHandler`, not `StrictMessageHandler`. Resolved — correcting the issue's "caught in StrictMessageHandler" wording; `StrictMessageHandler.HandleEventContent` re-throws `TransientException` unwrapped and the base handler owns the `Abandon` call.
- Throttling does not consume retry budget. Resolved — a throttle is a redelivery of the same message, not a handler failure; the `TransientException` path does not increment `RetryCount` or run `CheckForRetry`.
- Only `EventRequest` messages are rate-limited. Resolved — control/manager messages (resubmit, retry, handoff settle) are operator/system actions, not steady-state load, and must pass through.
- Per-event-type overrides win over the endpoint default. Resolved — most-specific wins; no double-charging.
- BCL `System.Threading.RateLimiting`, no third-party dependency. Resolved — the four limiter types map 1:1 to the proposed strategies and ship in-box on net10.0.
- Metric names drop the issue's `_total` suffix. Resolved — to match the established `NimBusMeters` naming (`nimbus.message.published`, `nimbus.outbox.enqueued`, etc., which carry no `_total` suffix).
- Rate limiting is complementary to, not a replacement for, the Circuit Breaker (spec `011`, issue #7). Resolved — rate limiting shapes successful throughput (steady state); the breaker reacts to failures (incident response).
