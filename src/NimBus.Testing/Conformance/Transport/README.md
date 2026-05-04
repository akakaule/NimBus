# Transport Conformance Suite

A provider-agnostic test suite that every NimBus transport (in-memory, Service Bus, RabbitMQ, ...) must pass. Mirrors the pattern of `MessageTrackingStoreConformanceTests` in the parent `Conformance/` directory.

> Status: **scaffolded skeletons** wired to the real `NimBus.Transport.Abstractions` types. Test method names are locked; test bodies will be filled in alongside the transport-provider implementations. Tracked in [#21](https://github.com/akakaule/NimBus/issues/21).

## What is here

Seven abstract `[TestClass]`es, one per scenario category:

| Class | Category | FR / SC |
|---|---|---|
| `SendReceiveConformanceTests` | Envelope round-trip, header preservation, single-producer FIFO, batch publish | FR-091 *Send/Receive* |
| `SessionOrderingConformanceTests` | Per-key FIFO, cross-key parallelism, 1000-msg / 100-key burst | FR-091 *Sessions and per-key ordering*, **SC-009** |
| `DeferredReplayConformanceTests` | Park on block, FIFO replay on unblock, idempotent park, crash-resilient replay, operator skip | FR-091 *Deferred-and-replay* |
| `DeadLetterConformanceTests` | Retry exhaustion, dead-letter projection, reason preservation, operator resubmit | FR-091 *Dead-letter and resubmit* |
| `ScheduledEnqueueConformanceTests` | Delivery within ~1s of target, never before target, cancellation | FR-091 *Scheduled enqueue* (capability-gated) |
| `BlockedSessionLifecycleConformanceTests` | Block / unblock persistence, `BlockedByEventId`, per-session independence | FR-091 *Blocked-session lifecycle* |
| `CapabilityGatingConformanceTests` | Meta-tests: unsupported features SKIP not FAIL; declared flags match actual behaviour | FR-091 *Capability gating* |

## How a transport provider plugs in

For each abstract class above, create a concrete subclass in your transport's test project that overrides the template method(s):

```csharp
// tests/NimBus.Transport.RabbitMQ.Tests/RabbitMqSendReceiveConformanceTests.cs
[TestClass]
public sealed class RabbitMqSendReceiveConformanceTests : SendReceiveConformanceTests
{
    protected override ITransportProviderRegistration CreateTransport()
        => new RabbitMqTransportProvider(/* Testcontainers-managed broker */);
}
```

Each concrete class becomes its own MSTest class, so the test runner reports per-transport, per-scenario results.

A typical transport test project will contain seven concrete classes (one per abstract class). The `InMemoryTransport`, `ServiceBusTransport`, and `RabbitMqTransport` runs are tracked as follow-up tests, not part of this skeleton drop.

## Capability gating

Some categories are optional. A transport declares which optional features it supports via `ITransportCapabilities` (e.g. `SupportsScheduledEnqueue`). When a flag is `false`:

- The corresponding conformance class (e.g. `ScheduledEnqueueConformanceTests`) reports all its tests as **skipped**, not failed.
- The meta-test `CapabilityGatingConformanceTests.UnsupportedFeature_TestsAreSkippedNotFailedAsync` verifies this gating actually works.
- The meta-test `CapabilityFlags_AccuratelyReportTransportSupportAsync` verifies a transport does not lie about its capabilities (e.g. claiming scheduling support but failing to deliver scheduled messages).

The gating mechanism itself (how a transport-test class is told to skip) will be implemented when test bodies are filled in. Today the classes carry a `// TODO` in their type-level comment pointing at this.

## Running the suite

```bash
# Once concrete transport runs exist, e.g.:
dotnet test tests/NimBus.Transport.InMemory.Tests/NimBus.Transport.InMemory.Tests.csproj
dotnet test tests/NimBus.ServiceBus.Tests/NimBus.ServiceBus.Tests.csproj
dotnet test tests/NimBus.Transport.RabbitMQ.Tests/NimBus.Transport.RabbitMQ.Tests.csproj
```

Per **FR-092**, CI runs the in-memory + Service Bus suites unconditionally and the RabbitMQ suite against a Testcontainers-managed RabbitMQ broker (`rabbitmq:management` with both required plugins pre-loaded — see NFR-008).

## Follow-up work

1. Fill in test bodies — one PR per category is fine.
2. Stand up the concrete InMemory / Service Bus / RabbitMQ runs in their respective test projects.
3. Wire CI per FR-092 / NFR-008.
4. Migrate transport-agnostic E2E tests under `tests/NimBus.EndToEnd.Tests/` to be parametrised by transport (FR-093).
