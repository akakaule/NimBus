# Middleware Guidelines — When, Why, and What For

This is a **decision guide** for platform and SDK consumers: it helps you decide *whether* a
cross-cutting concern belongs in a NimBus pipeline behavior, *why* middleware is the right tool
for it, and *what* to reach for when it is not. It deliberately does **not** repeat the
registration and implementation mechanics — for the how-to (signatures, DI wiring, worked
`IMessagePipelineBehavior` examples), see the companion reference:

> **How-to reference:** [Pipeline Middleware](pipeline-middleware.md) — implementing and
> registering behaviors, the built-in middleware, and lifecycle observers.

## What middleware is

A **pipeline behavior** (`IMessagePipelineBehavior`, in
`src/NimBus.Core/Extensions/IMessagePipelineBehavior.cs`) wraps message processing to handle a
cross-cutting concern *without changing handler code*. Each behavior receives the full
`IMessageContext` and a `MessagePipelineDelegate next`; calling `await next(context, ct)` passes
control inward to the next behavior or the terminal handler, and *not* calling it short-circuits
the pipeline. Behaviors run in **registration order** around the terminal handler — the first one
registered is the outermost wrapper. That is the whole mechanism; the rest of this document is
about when to use it. See [Pipeline Middleware](pipeline-middleware.md) for the mechanics.

## Why middleware (the value it provides)

Middleware exists so a concern that touches *many* handlers lives in **one** place instead of
being copy-pasted into each handler:

- **One registration point** — a single `AddPipelineBehavior<T>()` applies the behavior to every
  message flowing through the subscriber, so there is one place to add, audit, or remove it.
- **DI-resolved** — behaviors are constructed from the container (like `LoggingMiddleware` taking
  an `ILogger`), so they can depend on loggers, stores, clocks, or options.
- **Composable and ordered** — behaviors stack predictably; you can layer validation, enrichment,
  and timing and reason about the order (see [Ordering](#ordering)).
- **Handlers stay focused** — `IEventHandler<T>` implementations keep only business logic; the
  cross-cutting scaffolding moves out of them.

## When to use middleware

Reach for a pipeline behavior when **all or most** of these signals are present:

1. **The concern is cross-cutting across many/all handlers** — logging, validation,
   correlation/enrichment, timing, or an auth/authorization check that should apply uniformly
   rather than being re-implemented per message type.
2. **You need behavior *around* processing** — you want to run code before and/or after the
   handler, wrap the call in a `try/catch`, or **short-circuit** (reject/skip) a message — all
   without editing each handler. Because a behavior holds the `next` delegate, it controls whether
   and when processing continues.
3. **You want it resolved from DI and applied uniformly via a single registration** — one
   `AddPipelineBehavior<T>()` call wires the concern for the whole pipeline, and it is constructed
   with its dependencies by the container.

If a concern hits only one of these — for example it is specific to a single handler, or it only
needs to *watch* without influencing flow — one of the alternatives below is usually the better
fit.

## When NOT to use middleware

Middleware is not the answer for every cross-cutting-looking need. Use the correct tool instead:

### (a) Passive observation or alerting → `IMessageLifecycleObserver`

If you only need to **observe** the lifecycle — record that a message was received, completed,
failed, dead-lettered, or that a session was blocked — and you must **not** alter the flow, use an
`IMessageLifecycleObserver` (`src/NimBus.Core/Extensions/IMessageLifecycleObserver.cs`), registered
with `AddLifecycleObserver<T>()`. Its methods (`OnMessageReceived`, `OnMessageCompleted`,
`OnMessageFailed`, `OnMessageDeadLettered`, `OnSessionBlocked`) receive a `MessageLifecycleContext`
and have default no-op bodies, so you override only the events you care about. Crucially, an
observer has **no `next` delegate**: it cannot short-circuit, transform, or gate processing. That
constraint is the point — it guarantees observation can't accidentally change message outcomes.
Choose an observer for metrics/alerting hooks; choose middleware when you actually need to *affect*
the flow.

### (b) Logic specific to one message type/handler → the handler

If the concern only applies to a single message type, it is **business logic**, not a cross-cutting
concern. Put it in that message's `IEventHandler<T>` (or a service the handler calls). Pushing
one-off, type-specific rules into the pipeline forces every message to pay for a `MessageType`
check and scatters a single handler's logic across two files.

### (c) Message metrics and the `NimBus.Process` span → transport-owned instrumentation

Message-processing metrics and the `NimBus.Process` span are emitted by the **transport adapter**
(`ServiceBusAdapter` → `NimBusConsumerInstrumentation`,
`src/NimBus.Core/Diagnostics/NimBusConsumerInstrumentation.cs`), **not** by a pipeline behavior.
Every subscriber path gets them — including callers that never registered `AddNimBus(...)` — which
is why **there is no `MetricsMiddleware`** and you should not build one. Do not reimplement
received/processed counters or process-duration histograms as middleware; consume the existing
`NimBus.Consumer` meter instead. See [Pipeline Middleware](pipeline-middleware.md#registration) for
the instrument list.

### (d) Retry and dead-letter policy → the platform's built-in retry/DLQ

Do not hand-roll retry loops or dead-letter routing inside a behavior. NimBus's retry and
dead-letter handling are part of the adapter/error-handling layer. For how transient failures are
retried and when messages are dead-lettered, see
[Error Handling](error-handling.md). (A short-circuiting behavior like `ValidationMiddleware` may
*trigger* a dead-letter for an invalid message, but the retry/DLQ *policy* itself is not middleware.)

## Use-case catalogue

Concrete concerns, why middleware fits, and whether NimBus ships it, you write it, or it is a
planned pattern. **Only `LoggingMiddleware` and `ValidationMiddleware` are shipped built-ins**
(both in `src/NimBus.Core/Pipeline/`). Rows marked *planned pattern* exist today only as specs and
must be treated as patterns you would implement, not features you can register out of the box.

| Concern | Why middleware fits | Built-in / custom / planned |
|---------|---------------------|-----------------------------|
| Processing logs (start/finish/failure with timing) | Uniform log lines around every handler, outermost so it captures the full span | **Built-in** — `LoggingMiddleware` (`src/NimBus.Core/Pipeline/LoggingMiddleware.cs`) |
| Basic context validation (reject/dead-letter messages missing `EventId`/`EventTypeId`) | Cross-cutting gate that must run before the handler and can short-circuit | **Built-in** — `ValidationMiddleware` (`src/NimBus.Core/Pipeline/ValidationMiddleware.cs`) |
| Timing / slow-message detection (warn when processing exceeds a threshold) | Needs before/after measurement around every handler | Custom |
| Context enrichment / logging scope (push correlation id, session id into the logging scope) | Applies to all handlers; wraps processing so downstream logs inherit the scope | Custom |
| Conditional handling by `MessageType` | Central place to branch on message metadata without touching each handler | Custom |
| Short-circuit deprecated event types (drop/dead-letter obsolete `EventTypeId`s) | Behavior can skip `next` to stop processing centrally | Custom |
| Idempotency / inbox (skip already-processed messages) | Cross-cutting guard around the handler; consults an inbox store before `next` | *Planned pattern* — [spec 012](specs/012-inbox-idempotent-consumers/spec.md) |
| Circuit breaker (fail fast when a downstream dependency is unhealthy) | Wraps processing to trip/open around failures | *Planned pattern* — [spec 011](specs/011-circuit-breaker-middleware/spec.md) |
| Rate limiting (throttle throughput per endpoint/type) | Gates entry to the handler centrally | *Planned pattern* — [spec 017](specs/017-rate-limiting-middleware/spec.md) |

## Ordering

Behaviors run in **registration order**, and the first one registered is the **outermost** wrapper
— it runs first on the way *in* and last on the way *out*. The pipeline is built inside-out around
the terminal handler in `src/NimBus.Core/Extensions/MessagePipeline.cs`, so the order you call
`AddPipelineBehavior<T>()` is the order the behaviors nest.

Order matters because outer behaviors see everything the inner ones do (including their failures),
and short-circuiting behaviors decide whether inner behaviors run at all. Two rules of thumb:

- **Put logging/enrichment outermost** so it captures the entire span — including work and failures
  from every inner behavior and the handler.
- **Put validation and other short-circuit/cheap gates before expensive work** so invalid or
  throttled messages are rejected before you pay for enrichment, downstream calls, or handler
  execution.

### Worked example

```csharp
builder.Services.AddNimBus(nimbus =>
{
    nimbus.AddPipelineBehavior<LoggingMiddleware>();     // outermost: logs the whole span
    nimbus.AddPipelineBehavior<ValidationMiddleware>();  // cheap gate: reject bad messages early
    nimbus.AddPipelineBehavior<EnrichmentMiddleware>();  // expensive: only runs for valid messages
});
```

With this order, `LoggingMiddleware` wraps everything (so a validation rejection is still logged),
and `ValidationMiddleware` runs **before** the costly `EnrichmentMiddleware` — an invalid message
is dead-lettered without ever paying the enrichment cost. Swap the last two and you would enrich
messages that validation is about to reject, wasting the work. See
[Pipeline Middleware](pipeline-middleware.md#registration) for the registration mechanics.

## See also

- [Pipeline Middleware](pipeline-middleware.md) — the how-to reference: implementing and registering
  `IMessagePipelineBehavior`, the built-in middleware, and lifecycle observers.
- [Error Handling](error-handling.md) — retry, dead-letter, and failure classification (the correct
  home for retry/DLQ policy instead of middleware).
- [ADR-004: Pipeline behavior pattern](adr/004-pipeline-behavior-pattern.md) — the rationale for the
  middleware design.
