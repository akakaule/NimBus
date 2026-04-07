# ADR-006: Microsoft.Extensions.Logging over Custom Abstraction

## Status
Accepted (supersedes the original NimBus.Core.Logging design)

## Context
NimBus originally had a custom logging abstraction (`NimBus.Core.Logging.ILogger` and `ILoggerProvider`) with adapters for Serilog and OpenTelemetry. This required:

- Handler authors to accept a NimBus-specific `ILogger` parameter in `IEventHandler<T>.Handle()`
- Adapter classes bridging NimBus logging to the actual logging framework
- A `NullLoggerProvider` for testing
- Registration of `ILoggerProvider` in DI alongside the standard logging

The sample `OrderPlacedHandler` already ignored the NimBus logger and used DI-injected `ILogger<OrderPlacedHandler>` instead — demonstrating that the abstraction added friction without value.

## Decision
Remove the entire `NimBus.Core.Logging` namespace and all adapter classes. Use `Microsoft.Extensions.Logging` throughout:

- `IEventHandler<T>.Handle()` no longer takes a logger parameter — handlers inject `ILogger<T>` via DI
- `StrictMessageHandler` and `MessageHandler` accept `Microsoft.Extensions.Logging.ILogger` in constructors
- Internal components (CosmosDbClient, ManagerClient) use Serilog directly where they were already doing so
- `NullLogger.Instance` replaces `NullLoggerProvider` for testing

Files deleted: `ILogger.cs`, `ILoggerProvider.cs`, `LoggerProvider.cs`, `SerilogAdapter.cs`, `OpenTelemetryLoggerAdapter.cs`, `OpenTelemetryLoggerProvider.cs`, `NullLoggerProvider.cs`.

## Consequences

### Positive
- Clean handler signature: `Handle(T message, IEventHandlerContext context, CancellationToken ct)`
- No framework-specific logger to learn or ignore
- Standard DI pattern — handlers use constructor-injected `ILogger<T>`
- Reduced code surface — 7 files deleted, ~200 lines removed
- No adapter maintenance burden

### Negative
- Breaking change for existing handler implementations (must remove the logger parameter)
- Internal Serilog usage in MessageStore/Manager is now a direct dependency (not abstracted)
