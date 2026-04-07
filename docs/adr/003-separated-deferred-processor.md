# ADR-003: Separated DeferredProcessor in Subscriber Apps

## Status
Accepted (supersedes earlier design where StrictMessageHandler handled ProcessDeferredRequest)

## Context
When a session is blocked and deferred messages need to be replayed, a `ProcessDeferredRequest` message is sent. Two approaches for handling it:

1. **Integrated** — `StrictMessageHandler.HandleProcessDeferredRequest()` processes deferred messages within the main session-enabled handler
2. **Separated** — A dedicated `DeferredProcessorFunction` (or `DeferredProcessorService`) listens on a non-session subscription and handles it independently

The integrated approach was the original design. The separated approach was adopted after proving more robust in production.

## Decision
Remove `HandleProcessDeferredRequest()` from `StrictMessageHandler`. Each subscriber app hosts its own DeferredProcessor as either:
- An Azure Function triggered by the `DeferredProcessor` subscription (sessions=OFF) in production
- A `DeferredProcessorService` background hosted service in development (Aspire)

The `DeferredProcessor` subscription is provisioned with `RequiresSession = false` by the topology provisioner.

## Consequences

### Positive
- Cleaner separation of concerns — the core handler doesn't need `IDeferredMessageProcessor` or topic name injection
- DeferredProcessor can scale independently of the main handler
- Simpler `StrictMessageHandler` constructors (fewer parameters)
- Proven in production (adopted from an earlier version of the platform)
- DeferredProcessor doesn't hold a session lock (runs on non-session subscription)

### Negative
- Each subscriber app must explicitly register the DeferredProcessor (slightly more boilerplate)
- Two functions per endpoint instead of one
- ProcessDeferredRequest messages that arrive before the DeferredProcessor is ready are retried by Service Bus (eventually consistent)
