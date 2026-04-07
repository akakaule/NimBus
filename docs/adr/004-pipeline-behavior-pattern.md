# ADR-004: Delegate-Based Pipeline Behaviors over ASP.NET Middleware

## Status
Accepted

## Context
NimBus needed a middleware pipeline for cross-cutting concerns (logging, metrics, validation). Two patterns were considered:

1. **ASP.NET Core middleware pattern** — `IMiddleware` with `InvokeAsync(HttpContext, RequestDelegate next)`
2. **Delegate-based pipeline behavior** — `IMessagePipelineBehavior` with `Handle(IMessageContext, MessagePipelineDelegate next, CancellationToken)`

MassTransit uses `IFilter<T>`, NServiceBus uses `IBehavior<TContext>`, and MediatR uses `IPipelineBehavior<TRequest, TResponse>`. All follow the delegate/next pattern.

## Decision
Use `IMessagePipelineBehavior` with a `MessagePipelineDelegate next` parameter. Behaviors are registered via `AddNimBus(builder => builder.AddPipelineBehavior<T>())` and resolved from DI.

The pipeline wraps `MessageHandler.HandleByMessageType()` — meaning behaviors execute around the entire message type dispatch, not just the user event handler. This gives middleware access to all message types (EventRequest, RetryRequest, ResubmissionRequest, etc.).

Three built-in middleware are provided: `LoggingMiddleware`, `MetricsMiddleware`, `ValidationMiddleware`.

A separate `IMessageLifecycleObserver` interface provides passive observation (OnReceived, OnCompleted, OnFailed, OnDeadLettered) for monitoring without affecting the pipeline flow.

## Consequences

### Positive
- Familiar pattern for .NET developers (similar to MediatR, MassTransit)
- Full `IMessageContext` access — middleware can inspect/modify all message metadata
- Short-circuit capability — middleware can skip processing without calling `next`
- Exception handling — middleware can catch, transform, or swallow exceptions
- Composable — multiple behaviors execute in registration order (nesting)
- Lifecycle observers are separate from pipeline (passive vs active)

### Negative
- Middleware registered globally — cannot target specific message types (must check `context.MessageType` inside the behavior)
- No attribute-based middleware (must register in DI)
- Pipeline adds overhead for each message (minimal — delegate invocation is fast)
