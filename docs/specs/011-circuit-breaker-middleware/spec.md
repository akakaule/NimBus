# Feature Specification: Circuit Breaker Middleware

Feature Branch: `011-circuit-breaker-middleware`
Created: 2026-05-28
Updated: 2026-05-28
Status: Proposed
Input: User description: "When a downstream dependency (database, external API, internal service) starts failing systematically, every in-flight message burns its retry budget hitting the same broken target. The result: a flood of dead-lettered messages, exhausted retry quotas, and an avalanche of recovery work in the WebApp once the dependency comes back. A circuit breaker pauses processing when failures cross a threshold, abandons messages back to the queue (so they redeliver later), and tests the dependency periodically before resuming. This protects the dependency from the herd, protects the retry budget, and keeps the DLQ clean. Implement a `CircuitBreakerMiddleware : IMessagePipelineBehavior` that wires through the existing pipeline infrastructure, backed by Polly V8's circuit-breaker primitive, with three states (Closed / Open / Half-Open), configurable thresholds, per-endpoint and per-event-type overrides, and `nimbus.circuit_breaker.*` metrics. When the circuit is open, the middleware abandons the message (returns it to the queue) rather than dead-lettering, so it redelivers after the break elapses."

## Problem

NimBus already has the building blocks a circuit breaker needs, but no breaker:

1. **A message pipeline.** `IMessagePipelineBehavior.Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)` (`src/NimBus.Core/Extensions/IMessagePipelineBehavior.cs:33`) is the middleware seam. Behaviors are composed inside-out by `MessagePipeline.Execute(...)` (`src/NimBus.Core/Extensions/MessagePipeline.cs:41-59`) and run in registration order. Two behaviors already ship: `LoggingMiddleware` and `ValidationMiddleware` (`src/NimBus.Core/Pipeline/`).

2. **An Abandon path that returns the message to the broker.** `MessageHandler.Handle(...)` (`src/NimBus.Core/Messages/MessageHandler.cs:37-155`) wraps the pipeline. Its `catch (TransientException transientException)` branch calls `messageContext.Abandon(transientException)` (`MessageHandler.cs:63-82`). `IMessageContext.Abandon(TransientException exception)` (`src/NimBus.Core/Messages/IMessageContext.cs:27`) releases the lock so the broker redelivers the message later. This is exactly the settlement the circuit breaker needs when open — and crucially it is keyed on `TransientException`, not on a generic `Exception`.

3. **A retry budget worth protecting.** `StrictMessageHandler.CheckForRetry(...)` (`src/NimBus.Core/Messages/StrictMessageHandler.cs:370-394`) consults `IRetryPolicyProvider.GetRetryPolicy(...)` and schedules `SendRetryResponse(...)` up to `policy.MaxRetries`. When a downstream dependency is broken, every in-flight message walks this ladder against a target that cannot succeed, exhausting the budget and ultimately dead-lettering.

4. **A permanent/transient classifier.** `StrictMessageHandler.HandleEventContent(...)` (`StrictMessageHandler.cs:324-350`) rethrows `TransientException` verbatim, and routes anything the `IPermanentFailureClassifier` (`src/NimBus.Core/Messages/DefaultPermanentFailureClassifier.cs`) deems permanent into a `PermanentFailureException` (immediate dead-letter, no retry). Everything else becomes `EventContextHandlerException`. This is the existing seam for "downstream broken" (transient) vs. "this message is poison" (permanent) — the distinction the breaker must respect when deciding what to count.

5. **A metrics convention.** `NimBusMeters` (`src/NimBus.Core/Diagnostics/NimBusMeters.cs`) holds the canonical `Meter` instances and instruments. Names are dotted-snake (`nimbus.message.processed`, `nimbus.outbox.pending`); meter names are registered centrally in `NimBusInstrumentation.AllMeterNames` (`src/NimBus.Core/Diagnostics/NimBusInstrumentation.cs:62-70`) so a single `AddMeter(...)` loop in `MeterProviderBuilderExtensions` (`src/NimBus.OpenTelemetry/Extensions/MeterProviderBuilderExtensions.cs:21`) picks them all up.

What is missing: the breaker itself. When a database, internal service, or external API starts failing, NimBus has no way to *stop hammering it*. Each message burns its full retry ladder against the broken target; the DLQ fills with messages whose only fault is bad timing; and when the dependency recovers, an operator faces a wall of dead-lettered events to resubmit. A circuit breaker closes this gap: it counts transient failures over a sampling window, *opens* when they cross a threshold, abandons subsequent messages straight back to the queue without touching them, and after a break period lets a small number of probe messages through to test recovery before resuming normal flow.

## Scope

In scope:
- A new `CircuitBreakerMiddleware : IMessagePipelineBehavior` in `src/NimBus.Core/Pipeline/CircuitBreakerMiddleware.cs`, registered through the existing `INimBusBuilder.AddPipelineBehavior<TBehavior>()` surface (`src/NimBus.Core/Extensions/INimBusBuilder.cs:19`).
- Three states — Closed, Open, Half-Open — with configurable `FailureThreshold`, `BreakDuration`, `HalfOpenTestCount`, and `SamplingDuration`, backed by Polly V8's `ResiliencePipelineBuilder.AddCircuitBreaker(...)`.
- A new `CircuitBreakerOpenException : TransientException` thrown by the middleware when it short-circuits an inbound message while the circuit is open. Because it derives from `TransientException`, the existing `MessageHandler.Handle` catch (`MessageHandler.cs:63`) already translates it to `Abandon(...)` — no dead-letter, no retry-budget burn.
- A `CircuitBreakerOptions` record carrying the four knobs, with a global default configured via a new `AddPipelineBehavior<TBehavior>(Action<CircuitBreakerOptions> configure)` overload, and per-endpoint / per-event-type overrides via the subscriber builder.
- A per-event-type configuration entry point `NimBusSubscriberBuilder.ConfigureCircuitBreaker<TEvent>(Action<CircuitBreakerOptions> configure)` so a single endpoint can run a tighter breaker for one event type (e.g., `PaymentRequested`).
- New metrics on a dedicated `NimBus.CircuitBreaker` meter: `nimbus.circuit_breaker.state` (observable gauge) and `nimbus.circuit_breaker.transitions_total` (counter), wired into `NimBusInstrumentation.AllMeterNames` so the OTel package exports them with no extra wiring.
- Only transient failures count toward the threshold. The breaker keys on `TransientException` (and its `ThrottleException` sibling); `PermanentFailureException`-class failures (validation, serialization — see `DefaultPermanentFailureClassifier`) are explicitly excluded so a flood of poison messages never trips the breaker.
- Documentation in `docs/pipeline-middleware.md` and a configuration sample in `samples/AspirePubSub/`.
- Unit tests for state transitions / threshold / half-open probing, plus an end-to-end test in `tests/NimBus.EndToEnd.Tests/` proving messages redeliver after the circuit closes.

Out of scope:
- Distributed / shared circuit state across processes. The breaker is per-process, in-memory. A subscriber endpoint is hosted one-per-process (`AddNimBusSubscriber` enforces this — `src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs:107-125`), so per-process state already aligns with the deployment topology. Cross-instance coordination (e.g., a shared Redis breaker) is a follow-up.
- Replacing or reworking the retry ladder (`StrictMessageHandler.CheckForRetry`). The breaker sits *in front of* retry: when open it abandons before the handler runs, so the retry path never executes. When closed it is transparent and retry behaves as today.
- A WebApp surface for live circuit state. The metrics make state observable in any OTel backend (Aspire dashboard, Prometheus); a bespoke WebApp panel is deferred (see Open Questions).
- Manual operator control (force-open / force-close from the WebApp). v1 is automatic only.
- Per-handler-instance (as opposed to per-event-type) breakers. The configurable scopes are global, per-endpoint, and per-event-type — which the `IEventHandler<TEvent>` → `EventTypeId` mapping already makes natural.

## User Scenarios & Testing

### User Story 1 - Open circuit abandons messages instead of dead-lettering (Priority: P1)

As a subscriber operator whose handler calls a database that has just gone down, I want the breaker to open after a burst of transient failures and abandon every subsequent message back to the queue — NOT dead-letter it — so the DLQ stays clean and the messages redeliver once the database recovers.

Why this priority: This is the core promise of the feature. Abandon-vs-dead-letter is the single behaviour that distinguishes a circuit breaker from "just let retry exhaust." If open-state messages dead-letter, the feature has failed.

Independent Test: Register `CircuitBreakerMiddleware` with `FailureThreshold = 5`. Drive 5 messages whose handler throws a `TransientException`. Confirm the circuit transitions to Open. Send a 6th message; confirm it is settled via `IMessageContext.Abandon(...)` (lock released, no DLQ row, handler never invoked) and that a `CircuitBreakerOpenException` was thrown by the middleware.

Acceptance Scenarios:

1. Given the circuit is Closed and `FailureThreshold = 5`, When 5 messages in the sampling window fail with `TransientException`, Then the circuit transitions to Open and a `nimbus.circuit_breaker.transitions_total` measurement is recorded with `to=open`.
2. Given the circuit is Open, When a new message arrives, Then the middleware throws `CircuitBreakerOpenException` *before* calling `next(...)`, the handler is not invoked, and `MessageHandler.Handle`'s `catch (TransientException)` branch calls `messageContext.Abandon(...)`.
3. Given a message is abandoned while the circuit is Open, When the broker redelivers it after the lock expires, Then it is processed normally if the circuit has since closed, or abandoned again if still open — no DLQ row is ever written by the breaker path.
4. Given the circuit is Open, When the abandoned message is inspected, Then its delivery count has incremented (broker behaviour) but `StrictMessageHandler.CheckForRetry` has NOT run, so no `RetryRequest` was scheduled and no retry budget was consumed.

---

### User Story 2 - Half-open probes test recovery before resuming (Priority: P1)

As an operator, after the break duration elapses I want a small number of probe messages let through to test whether the dependency has recovered, resuming full flow only if they succeed and re-opening immediately if they fail — so the dependency is never hit by the full herd the instant the break ends.

Why this priority: Without controlled half-open probing, the breaker re-floods the recovering dependency the moment `BreakDuration` elapses, defeating the protection. This is the second half of the core mechanism.

Independent Test: Open the circuit, advance a fake clock past `BreakDuration`, and send `HalfOpenTestCount` messages. With a now-healthy handler, confirm the circuit closes and subsequent messages flow. With a still-failing handler, confirm the first probe re-opens the circuit.

Acceptance Scenarios:

1. Given the circuit is Open and `BreakDuration` has elapsed, When the next message arrives, Then the circuit is Half-Open and that message is allowed through to `next(...)` as a probe.
2. Given the circuit is Half-Open and a probe succeeds (the handler completes), When `HalfOpenTestCount` probes have all succeeded, Then the circuit transitions to Closed and a `to=closed` transition is recorded.
3. Given the circuit is Half-Open and a probe fails with `TransientException`, When the failure is observed, Then the circuit transitions back to Open, the break timer restarts, and the probe message is abandoned.
4. Given the circuit is Half-Open and `HalfOpenTestCount = 1`, When concurrent messages arrive, Then only one is admitted as the probe and the rest are abandoned with `CircuitBreakerOpenException` until the probe resolves.

---

### User Story 3 - Per-endpoint and per-event-type configuration (Priority: P1)

As an operator of a "billing" subscriber, I want a tighter breaker for `PaymentRequested` (open after 3 failures) than the endpoint default, because the payment provider is the most failure-sensitive dependency and I do not want it hammered.

Why this priority: The issue's named API explicitly requires per-endpoint *and* per-event-type configuration. A single global breaker would trip the entire endpoint when only one downstream is sick, starving healthy event types.

Independent Test: Configure an endpoint default of `FailureThreshold = 5` and `ConfigureCircuitBreaker<PaymentRequested>(o => o.FailureThreshold = 3)`. Fail 3 `PaymentRequested` messages; confirm only the `PaymentRequested` circuit opens while another event type on the same endpoint still flows.

Acceptance Scenarios:

1. Given an endpoint with a global breaker (`FailureThreshold = 5`) and a `PaymentRequested` override (`FailureThreshold = 3`), When 3 `PaymentRequested` messages fail, Then the `PaymentRequested` circuit opens but the endpoint-wide / other-event-type circuit remains Closed.
2. Given the same configuration, When 4 messages of a different event type fail, Then that event type's circuit remains Closed (it inherits the endpoint default of 5).
3. Given a breaker is configured only globally (`services.AddNimBus(b => b.AddPipelineBehavior<CircuitBreakerMiddleware>(opts => { ... }))`), When messages fail across event types, Then a single shared circuit governs the whole endpoint per the global options.
4. Given `ConfigureCircuitBreaker<TEvent>` is called without a registered global breaker behavior, When the subscriber is built, Then either the breaker is implicitly enabled for that event type, or a clear configuration error is raised at startup (see Open Questions on which).

---

### User Story 4 - Only transient failures count toward the threshold (Priority: P1)

As an operator, I do NOT want a burst of poison messages (bad JSON, validation failures) to trip the circuit, because those are the message's fault, not the dependency's — opening the circuit would needlessly pause healthy traffic.

Why this priority: Conflating poison-message failures with downstream-outage failures is the classic circuit-breaker foot-gun. NimBus already separates them via `IPermanentFailureClassifier`; the breaker must honour that separation or it will trip on the wrong signal.

Independent Test: With `FailureThreshold = 3`, send 5 messages that throw `FormatException` (classified permanent by `DefaultPermanentFailureClassifier`). Confirm the circuit stays Closed and all 5 dead-letter via the existing `PermanentFailureException` path. Then send 3 `TransientException` failures and confirm the circuit opens.

Acceptance Scenarios:

1. Given `FailureThreshold = 3`, When 5 messages fail with an exception classified permanent (`FormatException`, `ValidationException`, etc.), Then the circuit remains Closed and each message dead-letters via the existing `PermanentFailureException` flow.
2. Given `FailureThreshold = 3`, When 3 messages fail with `TransientException` (or `ThrottleException`), Then the circuit opens.
3. Given a handler throws a plain `Exception` that is NOT classified permanent, When it surfaces as `EventContextHandlerException`, Then it counts toward the threshold (it is treated as a transient/downstream-style failure — see FR-040 and Open Questions).

---

### User Story 5 - Closed circuit is fully transparent (Priority: P2)

As a subscriber author with a healthy dependency, I want the breaker to add no observable behaviour change when closed — same retries, same dead-lettering, same ordering — so enabling it costs nothing in the happy path.

Why this priority: A breaker that perturbs the closed-state happy path (extra latency, altered retry, swallowed exceptions) is not safe to enable by default. Transparency when closed is what makes the feature adoptable.

Independent Test: Run the existing subscriber test suite with the breaker registered and `FailureThreshold` set high enough never to trip. Confirm no test changes behaviour: retries still fire, permanent failures still dead-letter, successful messages still Complete.

Acceptance Scenarios:

1. Given the circuit is Closed, When a message succeeds, Then the middleware calls `next(...)` and returns with no side effect; the message Completes exactly as without the breaker.
2. Given the circuit is Closed, When a message fails with a transient error below threshold, Then `StrictMessageHandler.CheckForRetry` runs unchanged and a retry is scheduled per the configured `IRetryPolicyProvider`.
3. Given the circuit is Closed, When the breaker's per-message overhead is measured, Then it is dominated by a single in-memory state read (the Polly pipeline executes the delegate directly) — no allocation-heavy or I/O work on the hot path.

---

## Edge Cases

- **Open circuit and the message is a Manager request (Resubmit / Skip / HandoffCompleted).** The breaker should NOT block operator-driven control messages — abandoning a Resubmit would silently lose an operator action. The middleware must short-circuit only `MessageType.EventRequest` (and `RetryRequest` / `ProcessDeferredRequest` content-bearing types), passing Manager control messages straight through. See FR-022.
- **A handler that throws `TransientException` AND the configured `IRetryPolicyProvider` returns null (no retry).** The failure still counts toward the breaker threshold; the message abandons via `MessageHandler`'s transient branch regardless of retry configuration.
- **`PendingHandoff` outcome while the circuit is Half-Open.** A probe that hands off (rather than completing synchronously) is ambiguous — has the dependency recovered? v1 treats a clean `PendingHandoff` as a successful probe (no exception thrown); the handoff settles later through the normal `CompleteHandoff` / `FailHandoff` path. Documented in Assumptions.
- **Concurrent messages arriving in the same instant the circuit opens.** Polly's circuit-breaker is thread-safe; the first N failures within the sampling window flip the state and all in-flight callers after the flip see Open. Messages already past the state check and inside `next(...)` complete their handler run; their outcome still feeds the breaker.
- **`SamplingDuration` elapses with failures below threshold.** The failure count resets (Polly's sampling window slides); the circuit stays Closed. A slow trickle of unrelated transient blips never accumulates into an open circuit.
- **`BreakDuration` set to `TimeSpan.Zero`.** Invalid — Polly requires a positive break duration. Options validation must reject non-positive `BreakDuration` / `SamplingDuration` and non-positive `FailureThreshold` / `HalfOpenTestCount` at startup (FR-052).
- **The breaker is registered but no `IMessagePipelineBehavior` chain runs because `MessagePipeline.HasBehaviors` is false.** Cannot happen — registering the behavior populates `PipelineBehaviorRegistry.BehaviorTypes` (`src/NimBus.Core/Extensions/NimBusBuilder.cs:60-61`), so `HasBehaviors` is true. Documented as a guard.
- **Cancellation during a probe.** If the `CancellationToken` fires while a Half-Open probe is executing `next(...)`, the cancellation propagates; the breaker treats `OperationCanceledException` as neither success nor counted failure (it does not feed the threshold), leaving the circuit Half-Open for the next probe.
- **Multiple pipeline behaviors with the breaker ordered after `ValidationMiddleware`.** `ValidationMiddleware` dead-letters and throws `MessageAlreadyDeadLetteredException` (`src/NimBus.Core/Pipeline/ValidationMiddleware.cs:30`) *before* the breaker runs if it is ordered first — a validation reject never reaches the breaker, which is correct (it is not a downstream failure). Ordering guidance: register the breaker after Validation, before/around the terminal handler. See FR-021.

## Requirements

### Functional Requirements

#### Middleware contract

- FR-001: A new `CircuitBreakerMiddleware` MUST be added to `src/NimBus.Core/Pipeline/CircuitBreakerMiddleware.cs`, implementing `IMessagePipelineBehavior` with the exact signature already defined in `src/NimBus.Core/Extensions/IMessagePipelineBehavior.cs:33`:
  ```csharp
  public sealed class CircuitBreakerMiddleware : IMessagePipelineBehavior
  {
      public Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default);
  }
  ```
- FR-002: When the circuit governing the inbound message's scope is **Closed** or **Half-Open (admitting a probe)**, `Handle` MUST invoke `await next(context, cancellationToken)` and let the outcome (success or thrown exception) feed the breaker.
- FR-003: When the circuit is **Open** (or Half-Open and the probe slot is taken), `Handle` MUST throw `CircuitBreakerOpenException` WITHOUT calling `next(...)`. The handler MUST NOT run.
- FR-004: `CircuitBreakerMiddleware` MUST be registered as a singleton (matching `NimBusBuilder.AddPipelineBehavior` which calls `Services.AddSingleton<TBehavior>()` — `src/NimBus.Core/Extensions/NimBusBuilder.cs:26`). The circuit state therefore lives for the process lifetime, consistent with one-endpoint-per-process hosting.

#### Open-state settlement (Abandon, not DeadLetter)

- FR-010: A new exception MUST be added:
  ```csharp
  public sealed class CircuitBreakerOpenException : TransientException
  {
      public CircuitBreakerOpenException(string message) : base(message) { }
      public CircuitBreakerOpenException(string message, Exception inner) : base(message, inner) { }
  }
  ```
  It MUST derive from `NimBus.Core.Messages.Exceptions.TransientException` (`src/NimBus.Core/Messages/Exceptions/TransientException.cs:7`).
- FR-011: Because `MessageHandler.Handle` catches `TransientException` and calls `messageContext.Abandon(transientException)` (`src/NimBus.Core/Messages/MessageHandler.cs:63-82`), throwing `CircuitBreakerOpenException` from the middleware MUST result in the message being **abandoned** (returned to the queue for redelivery), NOT dead-lettered. No new catch arm in `MessageHandler` is required — the existing transient branch already covers it. This is the cleanest integration point and avoids touching `StrictMessageHandler`.
- FR-012: The breaker MUST NOT call `messageContext.DeadLetter(...)` itself under any state. Dead-lettering remains owned by the existing permanent-failure and retry-exhaustion paths.
- FR-013: The breaker MUST NOT schedule a retry. By throwing before `next(...)` runs, the handler-side `StrictMessageHandler.CheckForRetry` (`src/NimBus.Core/Messages/StrictMessageHandler.cs:370`) is never reached for an open-circuit message — the retry budget is preserved.

> NOTE on `StrictMessageHandler` integration: the issue text says the open-circuit exception is "caught by `StrictMessageHandler` and translated to Abandon." In the real code, Abandon is performed one level up, in the `MessageHandler` base class (`MessageHandler.cs:63-82`), not in `StrictMessageHandler`. `StrictMessageHandler` overrides the `Handle*Request` methods but the transient-to-Abandon translation is in the shared `Handle(...)` wrapper. Deriving `CircuitBreakerOpenException` from `TransientException` therefore needs no `StrictMessageHandler` change at all. This discrepancy is recorded in Assumptions.

#### Failure classification (what counts)

- FR-020: Only failures that surface as `TransientException` (including its `ThrottleException` sibling, `src/NimBus.Core/Messages/Exceptions/TransientException.cs:27`) and `EventContextHandlerException` (the generic downstream-failure wrapper produced by `StrictMessageHandler.HandleEventContent` — `StrictMessageHandler.cs:345`) MUST count toward the breaker's failure threshold.
- FR-021: Failures that surface as `PermanentFailureException` MUST NOT count toward the threshold. `StrictMessageHandler.HandleEventContent` (`StrictMessageHandler.cs:340-343`) produces this for any exception the `IPermanentFailureClassifier` (`src/NimBus.Core/Messages/DefaultPermanentFailureClassifier.cs`) deems permanent (FormatException, ValidationException, serialization errors, etc.). The breaker must treat these as "poison message" not "downstream broken." `MessageAlreadyDeadLetteredException` (thrown by `ValidationMiddleware`) MUST likewise be excluded.
- FR-022: The breaker MUST only govern content-bearing inbound message types — `MessageType.EventRequest`, `MessageType.RetryRequest`, and `MessageType.ProcessDeferredRequest`. It MUST pass Manager-originated control messages (`ResubmissionRequest`, `SkipRequest`, `HandoffCompletedRequest`, `HandoffFailedRequest`, `ContinuationRequest`) straight through regardless of circuit state, so operator actions are never abandoned.

#### Polly V8 backing

- FR-030: The circuit primitive MUST be Polly V8's `ResiliencePipeline` built via `ResiliencePipelineBuilder().AddCircuitBreaker(new CircuitBreakerStrategyOptions { ... })`. NimBus does NOT currently reference Polly anywhere (verified: no `Polly` package reference in any `.csproj`), so a `Polly.Core` (V8) `PackageReference` MUST be added to `NimBus.Core`. Because `Directory.Packages.props` sets `ManagePackageVersionsCentrally=false` (`Directory.Packages.props:3`), the version MUST be declared inline on the `NimBus.Core` `PackageReference` (`Version="..."`), NOT as a central `<PackageVersion>` entry — matching the project-local versioning the rest of the solution uses (and the analyzer-package approach in `docs/specs/018-source-generators/spec.md`).
- FR-031: The breaker MUST map `CircuitBreakerOptions` onto the Polly strategy options:
  - `FailureThreshold` → derived `FailureRatio` + `MinimumThroughput` (Polly V8 is ratio-based; with a count-style threshold the implementation sets `MinimumThroughput = FailureThreshold` and `FailureRatio = 1.0`, i.e. "open after N consecutive/sampled failures"). The exact mapping is documented in FR-051.
  - `BreakDuration` → `CircuitBreakerStrategyOptions.BreakDuration`.
  - `SamplingDuration` → `CircuitBreakerStrategyOptions.SamplingDuration`.
  - `HalfOpenTestCount` → the number of probe permits in Half-Open. (Polly V8 admits a single probe per Half-Open transition; emulate `HalfOpenTestCount > 1` by requiring that many consecutive probe successes before Close — documented in Open Questions if Polly's single-probe model is kept verbatim.)
- FR-032: `ShouldHandle` on the Polly strategy MUST be configured to handle ONLY the exception types enumerated in FR-020 (transient / `EventContextHandlerException`) and explicitly NOT handle `PermanentFailureException`, `MessageAlreadyDeadLetteredException`, or `OperationCanceledException`.
- FR-033: The breaker's `OnOpened` / `OnClosed` / `OnHalfOpened` Polly callbacks MUST drive the metrics in FR-060/FR-061.

#### Configuration surface

- FR-040: A new `CircuitBreakerOptions` MUST be added carrying the four knobs from the issue, with sensible defaults:
  ```csharp
  public sealed class CircuitBreakerOptions
  {
      public int FailureThreshold { get; set; } = 5;        // failures before opening
      public TimeSpan BreakDuration { get; set; } = TimeSpan.FromMinutes(2);
      public int HalfOpenTestCount { get; set; } = 1;       // probes during half-open
      public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromMinutes(1);
  }
  ```
- FR-041: `INimBusBuilder` MUST gain an overload to configure the global breaker, matching the issue's proposed API:
  ```csharp
  INimBusBuilder AddPipelineBehavior<TBehavior>(Action<CircuitBreakerOptions> configure)
      where TBehavior : class, IMessagePipelineBehavior;
  ```
  used as:
  ```csharp
  services.AddNimBus(b =>
  {
      b.AddPipelineBehavior<CircuitBreakerMiddleware>(opts =>
      {
          opts.FailureThreshold = 5;
          opts.BreakDuration = TimeSpan.FromMinutes(2);
          opts.HalfOpenTestCount = 1;
          opts.SamplingDuration = TimeSpan.FromMinutes(1);
      });
  });
  ```
  The existing parameterless `AddPipelineBehavior<TBehavior>()` (`src/NimBus.Core/Extensions/INimBusBuilder.cs:19`) MUST remain, registering the breaker with `CircuitBreakerOptions` defaults.
- FR-042: `NimBusSubscriberBuilder` MUST gain per-endpoint and per-event-type configuration mirroring the issue:
  ```csharp
  services.AddNimBusSubscriber("billing", b =>
  {
      b.AddPipelineBehavior<CircuitBreakerMiddleware>();                 // endpoint default
      b.ConfigureCircuitBreaker<PaymentRequested>(opts => { opts.FailureThreshold = 3; });
  });
  ```
  Note: `NimBusSubscriberBuilder` (`src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs`) does NOT today expose `AddPipelineBehavior` — it exposes `AddHandler`, `ConfigureRetryPolicies`, and `ConfigurePermanentFailureClassifier`. Pipeline behaviors are currently registered through the core `INimBusBuilder` inside `AddNimBus`. This spec MUST add an `AddPipelineBehavior<TBehavior>()` passthrough and a `ConfigureCircuitBreaker<TEvent>(Action<CircuitBreakerOptions>)` method to `NimBusSubscriberBuilder`, both routing into the core `PipelineBehaviorRegistry` / a new per-event-type options store. This builder gap is recorded in Assumptions.
- FR-043: Configuration resolution order for a given message MUST be: per-event-type override (most specific, keyed on `EventTypeId`) → endpoint/global options → built-in defaults. The breaker MUST maintain one Polly `ResiliencePipeline` instance per resolved scope key (event-type id, or a single endpoint-wide key when no override exists).
- FR-044: The `EventTypeId` used as the per-event-type key MUST be derived the same way the rest of NimBus derives it — `new EventType(eventType).Id` (the unqualified type name), matching `NimBusSubscriberBuilder.AddHandlerRegistration` (`src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs:118`). This keeps the breaker key consistent with the dispatch key.

#### Metrics

- FR-050: A new `Meter` named `"NimBus.CircuitBreaker"` MUST be added to `NimBusMeters` (`src/NimBus.Core/Diagnostics/NimBusMeters.cs`) following the existing pattern (one static `Meter` field + pre-created instruments), and its name added to `NimBusInstrumentation` (`PublisherMeterName`-style constant) and to `NimBusInstrumentation.AllMeterNames` (`src/NimBus.Core/Diagnostics/NimBusInstrumentation.cs:62-70`) so `MeterProviderBuilderExtensions`'s `foreach` (`src/NimBus.OpenTelemetry/Extensions/MeterProviderBuilderExtensions.cs:21`) exports it with no further wiring.
- FR-060: `nimbus.circuit_breaker.transitions_total` MUST be a `Counter<long>` incremented on every state change, tagged with `from`, `to` (closed / open / half_open), and the resolved scope (`endpoint` and, when present, `event_type`). Mirrors the existing counter style (`NimBusMeters.MessagesProcessed`, etc.).
- FR-061: `nimbus.circuit_breaker.state` MUST be an `ObservableGauge<int>` reporting the current state per scope as an enum-coded int (e.g., 0 = Closed, 1 = Open, 2 = Half-Open), registered via `CreateObservableGauge(...)` exactly as `NimBusGaugeBackgroundService` registers `nimbus.outbox.pending` (`src/NimBus.OpenTelemetry/Instrumentation/NimBusGaugeBackgroundService.cs:62-66`). The gauge MUST emit one measurement per active scope, tagged with `endpoint` and `event_type`.
- FR-062: When the OTel package is not referenced, the meters MUST still record (they are plain `System.Diagnostics.Metrics` instruments on `NimBusMeters`); export simply does not happen — matching how every other NimBus meter behaves.

#### Documentation & samples

- FR-070: `docs/pipeline-middleware.md` MUST gain a "Circuit Breaker" section documenting the three states, the four options, the abandon-not-dead-letter behaviour, the transient-only counting rule, ordering relative to `ValidationMiddleware`, and the two metrics.
- FR-071: A sample in `samples/AspirePubSub/` MUST show both global and per-event-type configuration. (The issue names `samples/NimBus.Aspire/`; that path does not exist — the Aspire sample is `samples/AspirePubSub/`. The other sample is `samples/CrmErpDemo/`. This path discrepancy is recorded in Assumptions.)

#### Tests

- FR-080: Unit tests in `tests/NimBus.Core.Tests/` MUST cover:
  1. Closed → Open after `FailureThreshold` transient failures within `SamplingDuration`.
  2. Open → throws `CircuitBreakerOpenException` without invoking `next`.
  3. Open → Half-Open after `BreakDuration` (using a fake `TimeProvider` so the test does not sleep).
  4. Half-Open probe success → Closed; probe failure → Open with restarted break timer.
  5. `HalfOpenTestCount > 1` requires that many probe successes before Close.
  6. Permanent failures (`PermanentFailureException`) do NOT count toward the threshold.
  7. Manager control messages pass through regardless of state (FR-022).
  8. Per-event-type override opens only that event type's circuit.
- FR-081: An end-to-end test in `tests/NimBus.EndToEnd.Tests/` MUST prove redelivery after the circuit closes: drive enough transient failures to open the circuit, confirm subsequent messages are abandoned (not dead-lettered, no DLQ rows), then make the handler healthy, advance past `BreakDuration`, and confirm the abandoned messages are redelivered and Complete successfully.
- FR-082: A metrics test MUST assert `nimbus.circuit_breaker.transitions_total` increments with the right `from`/`to` tags on each transition and that `nimbus.circuit_breaker.state` reports the current state, using a `MeterListener` (the pattern already used by `NimBus.OpenTelemetry.Tests`).

### Non-Functional Requirements

- NFR-001: Closed-state per-message overhead MUST be bounded by a single Polly pipeline execution wrapping the existing delegate — no I/O, no per-message allocation beyond what Polly's `ResiliencePipeline.ExecuteAsync` requires. The hot path stays in-memory.
- NFR-002: The breaker's state store MUST be thread-safe under the concurrent message processing the Service Bus session processor drives. Polly V8's circuit-breaker strategy is documented thread-safe; the per-scope pipeline dictionary MUST use a concurrent collection.
- NFR-003: One new NuGet dependency (`Polly.Core`, V8) on `NimBus.Core`, with the version declared inline on the project's own `PackageReference` (the repo sets `ManagePackageVersionsCentrally=false`, so there is no central `<PackageVersion>` to pin). No other package additions. (Verified: NimBus has zero Polly references today.)
- NFR-004: No breaking change to the public `IMessagePipelineBehavior` contract, `MessageHandler`, or `StrictMessageHandler`. The breaker is additive: a new behavior class, a new exception subtype, new builder overloads, and new meters.
- NFR-005: With the breaker registered but configured to never trip (high `FailureThreshold`), the existing subscriber/end-to-end test suites MUST pass unchanged (transparency, per User Story 5).
- NFR-006: Metric cardinality MUST be bounded: `event_type` tags are drawn from the finite set of registered handler event-type ids; `endpoint` is the single endpoint per process. No unbounded tag (e.g., per-message id) is emitted.
- NFR-007: Tests MUST use an injectable `TimeProvider` (Polly V8 supports `TimeProvider`) so break/sampling windows are testable without real-time sleeps.

## Key Entities

- **`CircuitBreakerMiddleware`** — new `IMessagePipelineBehavior` in `src/NimBus.Core/Pipeline/`. Singleton. Owns the per-scope Polly pipelines. Throws `CircuitBreakerOpenException` when open.
- **`CircuitBreakerOpenException`** — new `TransientException` subtype. Its base type is the load-bearing detail: `MessageHandler.Handle`'s `catch (TransientException)` (`MessageHandler.cs:63`) already translates it to `Abandon`.
- **`CircuitBreakerOptions`** — new options record with `FailureThreshold`, `BreakDuration`, `HalfOpenTestCount`, `SamplingDuration`. Defaults per FR-040.
- **`IMessagePipelineBehavior` / `MessagePipelineDelegate`** — existing seam (`src/NimBus.Core/Extensions/IMessagePipelineBehavior.cs`). The breaker plugs in unchanged.
- **`MessagePipeline` / `PipelineBehaviorRegistry`** — existing composition + registration (`src/NimBus.Core/Extensions/MessagePipeline.cs`, `NimBusBuilder.cs:71-80`). The breaker is one more registered type.
- **`TransientException` / `PermanentFailureException` / `EventContextHandlerException`** — existing failure taxonomy that decides what counts toward the threshold (`src/NimBus.Core/Messages/Exceptions/`, `StrictMessageHandler.cs:324-350`).
- **`IPermanentFailureClassifier` / `DefaultPermanentFailureClassifier`** — existing classifier that separates poison from downstream-broken (`src/NimBus.Core/Messages/`). The breaker honours its verdict via FR-021.
- **`NimBus.CircuitBreaker` meter + `nimbus.circuit_breaker.state` / `nimbus.circuit_breaker.transitions_total`** — new instruments on `NimBusMeters`, exported via `NimBusInstrumentation.AllMeterNames`.
- **`NimBusSubscriberBuilder.ConfigureCircuitBreaker<TEvent>` / `AddPipelineBehavior<TBehavior>`** — new builder methods for per-endpoint and per-event-type configuration (`src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs`).

## Success Criteria

### Measurable Outcomes

- SC-001: After `FailureThreshold` transient failures within `SamplingDuration`, the circuit is Open and the next message is settled via `IMessageContext.Abandon(...)` with zero DLQ rows written. Verified by unit + end-to-end test (FR-080.2, FR-081).
- SC-002: An open circuit consumes zero retry budget: `StrictMessageHandler.CheckForRetry` is not invoked and no `RetryRequest` is scheduled for an open-circuit message. Verified by FR-080.2 and a no-retry assertion.
- SC-003: After `BreakDuration` elapses and a probe succeeds (`HalfOpenTestCount` met), the circuit closes and previously-abandoned messages redeliver and Complete. Verified by FR-081 with a fake `TimeProvider`.
- SC-004: A burst of permanent failures (≥ `FailureThreshold` `FormatException`s) leaves the circuit Closed and dead-letters each message via the existing permanent path. Verified by FR-080.6.
- SC-005: A per-event-type override opens only that event type's circuit while sibling event types on the same endpoint keep flowing. Verified by FR-080.8.
- SC-006: With the breaker registered but never tripping, the existing subscriber + end-to-end suites pass unchanged. Verified per NFR-005.
- SC-007: `nimbus.circuit_breaker.transitions_total` and `nimbus.circuit_breaker.state` are observable in an OTel backend (Aspire dashboard) with correct `from`/`to`/`event_type` tags. Verified by FR-082.

## Assumptions

- **Abandon happens in `MessageHandler`, not `StrictMessageHandler`.** The issue says the open-circuit exception is "caught by `StrictMessageHandler` and translated to Abandon." In reality the transient-to-Abandon translation lives in the shared `MessageHandler.Handle` wrapper (`src/NimBus.Core/Messages/MessageHandler.cs:63-82`), which runs the pipeline. Deriving `CircuitBreakerOpenException` from `TransientException` reuses that path with zero changes to either handler class. The spec is written against the real code, not the issue's wording.
- **`NimBusSubscriberBuilder` does not currently expose pipeline-behavior registration.** It exposes `AddHandler`, `ConfigureRetryPolicies`, `ConfigurePermanentFailureClassifier` (`src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs`). Per-endpoint breaker configuration requires adding `AddPipelineBehavior<TBehavior>()` and `ConfigureCircuitBreaker<TEvent>()` to this builder, routing into the core `PipelineBehaviorRegistry` and a new per-event-type options store. The issue's per-endpoint API is therefore *aspirational* against today's builder and is part of this feature's work.
- **The Aspire sample is `samples/AspirePubSub/`, not `samples/NimBus.Aspire/`.** The issue's sample path does not exist; the spec targets the real `samples/AspirePubSub/` (the other sample is `samples/CrmErpDemo/`).
- **Polly is a new dependency.** No NimBus project references Polly today; `Polly.Core` (V8) is added to `NimBus.Core` with an inline `Version` on the project's `PackageReference` (the repo uses project-local package versions — `ManagePackageVersionsCentrally=false`).
- **One endpoint per process.** `AddNimBusSubscriber` enforces single-endpoint hosting (`src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs:107-125`), so per-process in-memory circuit state aligns with deployment topology; distributed state is out of scope.
- **A clean `PendingHandoff` probe in Half-Open counts as success.** The handler returned without throwing; the dependency is presumed reachable. The handoff settles later through the normal `CompleteHandoff` / `FailHandoff` path.
- **`EventTypeId` is the unqualified type name** (`new EventType(eventType).Id`), matching the dispatch key (`NimBusSubscriberBuilder.cs:118`), and is used as the per-event-type breaker scope key.

## Out of Scope

- Distributed / cross-process circuit state (shared Redis/SQL breaker). Per-process in-memory only.
- A WebApp UI panel showing live circuit state or manual force-open / force-close controls. Observability is via metrics in v1.
- Reworking the retry ladder or `IRetryPolicyProvider`. The breaker sits in front of retry and is transparent when closed.
- Per-handler-instance breakers. Scopes are global, per-endpoint, and per-event-type.
- A bespoke half-open admission policy beyond Polly's model (e.g., percentage-based probing). v1 uses count-based probing (`HalfOpenTestCount`).
- Emitting circuit transitions as lifecycle observer notifications (`IMessageLifecycleObserver`). Metrics are the v1 observability surface; a lifecycle hook is a possible follow-up.

## Open Questions

- **Per-handler, per-endpoint, or both?** Resolved toward **per-endpoint default with per-event-type override** (the two scopes the builders make natural: endpoint = process, event-type = `EventTypeId`). Per-handler-instance is out of scope. The remaining question is whether a true endpoint-wide *shared* circuit (one breaker for the whole endpoint, ignoring event type) should also be selectable independently of the global default — current design treats "global" and "endpoint-wide" as the same single circuit when no per-event-type override exists.
- **Does Polly V8's single-probe Half-Open model satisfy `HalfOpenTestCount > 1`?** Polly admits one probe per Half-Open transition. Emulating `HalfOpenTestCount = 3` (require 3 consecutive successes before Close) needs either a custom wrapper around Polly's callbacks or accepting Polly's single-probe semantics and documenting `HalfOpenTestCount` as "successes required to fully close" rather than "concurrent probes." To be decided at implementation against the Polly.Core V8 API.
- **Should a plain (non-classified) `Exception` count toward the threshold?** Today such failures become `EventContextHandlerException` (`StrictMessageHandler.cs:345`) and the spec counts them (FR-020). If a future Poison Message Classification feature widens the permanent set, the breaker automatically counts fewer of them — the two features are complementary, but the exact boundary may need revisiting when that feature lands.
- **Should circuit state be observable in the WebApp?** Deferred. The metrics make state observable in any OTel backend; a WebApp panel (and/or an `IMessageLifecycleObserver`-style notification) is a candidate follow-up once the metric surface has shipped.
- **Should the breaker emit a Resolver/lifecycle notification when it opens** so operators see "endpoint X paused processing of event Y at HH:MM" without scraping metrics? Out of scope for v1; flagged for a follow-up.

## Resolved Questions

- Abandon, never dead-letter, when open. Resolved — derive `CircuitBreakerOpenException` from `TransientException` so `MessageHandler.Handle`'s existing transient branch abandons it (`MessageHandler.cs:63-82`); no handler changes.
- Only transient/downstream failures count; permanent (poison) failures do not. Resolved — honour the existing `IPermanentFailureClassifier` / `PermanentFailureException` split (`StrictMessageHandler.cs:340-345`).
- Manager control messages (Resubmit / Skip / Handoff) bypass the breaker. Resolved — abandoning an operator action would silently lose it (FR-022).
- Polly V8 `ResiliencePipeline` is the circuit primitive. Resolved — add `Polly.Core` to `NimBus.Core`; NimBus has no prior Polly dependency.
- Metrics live on a new `NimBus.CircuitBreaker` meter, registered through `NimBusInstrumentation.AllMeterNames`. Resolved — matches the existing meter-registration convention so the OTel package exports them automatically.
- The breaker is a normal `IMessagePipelineBehavior` registered via the existing builder, singleton-scoped. Resolved — reuses `AddPipelineBehavior` (`NimBusBuilder.cs:23-28`) with a new options overload.
- Per-process in-memory state. Resolved — one endpoint per process (`ServiceCollectionExtensions.cs:107-125`) makes per-process state the right granularity; distributed state is a follow-up.
