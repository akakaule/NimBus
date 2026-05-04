# ADR-011: RabbitMQ as a Second, Production-Grade Transport

## Status
Accepted (2026-05)

Supersedes in part: [ADR-001](001-session-based-ordering.md) — the "transport abstraction is explicitly out of scope" framing.

## Context
NimBus has been intentionally Azure-Service-Bus-only since inception. The roadmap's *"What NOT to Do"* section explicitly warned against transport abstraction: *"Don't abstract transports prematurely. Azure Service Bus is NimBus's strength."* ADR-001 echoes this: *"sessions are a core platform capability, not an abstraction to be swapped."*

That stance was correct at the time. It is no longer correct now: a concrete user need exists for running NimBus on-premise, against RabbitMQ, without an Azure subscription. The qualifier in the original anti-goal — *"only abstract further if there's concrete demand"* — has been satisfied.

The pluggable storage split delivered in [ADR-010](010-pluggable-message-storage.md) (`NimBus.MessageStore.Abstractions` + `SqlServer` + `CosmosDb` providers, `IStorageProviderRegistration` marker, conformance suite in `NimBus.Testing`) is the structural template we adopt for transports. The four-contract decomposition, the builder-time validation, and the abstract MSTest conformance suite all carry across.

The hard parts are not symmetric with storage: storage is request/response over an interface, transport is an asynchronous bidirectional pipe with delivery guarantees, ordering semantics, and topology. RabbitMQ does not offer a one-to-one analogue for Service Bus *sessions*, *deferred-by-session*, or *forwarding subscriptions with `From IS NULL` filter rules*. This ADR records how each gap is closed.

## Decision

### Goal
RabbitMQ is a **first-class production transport**, not a dev-only stub. A NimBus deployment can run end-to-end on-premise against a RabbitMQ broker with no Azure dependency. Service Bus remains the primary, recommended transport for greenfield Azure deployments; on-premise / cloud-agnostic teams choose RabbitMQ. No third transport (Kafka, NATS, SQS) is in scope without a fresh ADR.

### Contract decomposition
A new package `NimBus.Transport.Abstractions` houses the transport-neutral surface. Existing interfaces are promoted (not duplicated) from `NimBus.Core/Messages/`:

- `ISender`, `IMessageContext`, `IReceivedMessage`, `IMessageHandler`
- `IDeferredMessageProcessor`
- `[SessionKey]` attribute (already in `NimBus.Abstractions`, reachable from both transports)

New transport-specific markers, mirroring storage:

- `ITransportProviderRegistration` — marker validated at builder time; exactly one transport provider must be registered
- `ITransportCapabilities` — feature flags: `SupportsNativeSessions`, `SupportsScheduledEnqueue`, `SupportsAutoForward`, `MaxOrderingPartitions`. Operator tooling that only one transport can support is gated explicitly via this interface (mirrors `IStorageProviderCapabilities`)
- `ITransportManagement` — abstract topology operations (create/list/purge endpoints, replace `IServiceBusManagement` direct usage in the Resolver, WebApp, and CLI)

### Provider validation at builder time
`NimBusBuilder.Build()` enumerates `ITransportProviderRegistration` registrations and throws when zero or more than one is present, before any `IHostedService` starts. Same shape as the existing `IStorageProviderRegistration` check.

### Service Bus provider package
Existing `NimBus.ServiceBus` is retrofitted to implement the new abstractions. The existing types (`ServiceBusAdapter`, `Sender`, `MessageContext`, `ServiceBusSession`, `DeferredMessageProcessor`) already match the contracts — this is a registration change, not a behavioural change.

### RabbitMQ provider package
New project `NimBus.Transport.RabbitMQ`, depending on `RabbitMQ.Client` 7.x.

- **Sender** — `basic.publish` to per-endpoint topic exchanges. Headers carry `EventTypeId` / `From` / `To` / `SessionKey`, preserving the routing model conceptually intact.
- **Receiver** — consumer per `(endpoint, partition)` queue with `single-active-consumer` enabled.
- **Sessions** — partitioned by hash of `SessionKey` via the `rabbitmq_consistent_hash_exchange` plugin, with a fixed partition count per endpoint (default 16, configurable per endpoint at provisioning time, not reconfigurable post-provisioning). Ordering is preserved within a partition. Trade-off vs. Service Bus: the maximum concurrent ordering keys is bounded by the partition count instead of being unbounded; teams that need >16 concurrent ordered streams reconfigure or shard their endpoints.
- **Scheduled enqueue** — `x-delayed-message` exchange via the `rabbitmq_delayed_message_exchange` plugin. The provider fails at startup if the plugin is not loaded — there is no fall-back path.
- **Dead-lettering** — DLX (dead-letter exchange) per endpoint. Dead-lettered messages are mirrored into the existing `MessageStore.UnresolvedEvents` projection identically to ASB DLQ messages, so the operator UI does not differentiate.
- **Topology** — `RabbitMqTopologyProvisioner` declares exchanges, bindings, queues, DLX, and an alternate-exchange-based loop-prevention pattern. The CrmErpDemo `From IS NULL` filter rule pattern is replicated using alternate exchanges plus a `From` header check on consumers, so the cross-system forwarding model from ADR-001's session-ordered round-trip continues to work.
- **Health check** — connection liveness plus a runtime check that the `delayed_message_exchange` and `consistent_hash_exchange` plugins are loaded.

### Deferred messages move out of the transport layer
This is the single largest design change.

Service Bus's session-deferral primitive (`ServiceBusReceiver.AcceptSessionAsync(sessionId)`, `ReceiveDeferredMessagesAsync(sequenceNumber)`) has no RabbitMQ equivalent. Rather than emulate Service Bus deferral inside the RabbitMQ provider, deferred-by-session is reframed as a **NimBus-level concern, not a transport concern**.

When a session is blocked, subsequent messages for that session are parked in the existing `MessageStore` (SQL Server or Cosmos), keyed by `SessionKey`. On unblock, a transport-agnostic deferred-processor reads them in FIFO order and republishes to the receiving endpoint. Service Bus continues to use its native session deferral as a performance optimization for the warm path; RabbitMQ uses the portable park-and-replay path. Both are correct; both produce the same operator-visible audit trail.

This is implemented in `NimBus.Core` so both transports reuse the same code, and `IDeferredMessageProcessor` becomes implementable without any transport-specific primitive.

### Provider-selection knob
A new `NimBus__Transport` env-var (default: `servicebus`) is added alongside the existing `NimBus__StorageProvider`. The Aspire AppHost (`src/NimBus.AppHost/Program.cs`) and the CrmErpDemo AppHost (`samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs`) bridge it through `WithEnvironment(...)` exactly the same way storage is bridged today. CLI: `nb topology apply --transport {servicebus|rabbitmq}`.

### Conformance test suite
`NimBus.Testing.Conformance` gains a transport conformance suite, modelled on `MessageTrackingStoreConformanceTests`. Abstract MSTest base classes cover: send/receive, sessions/ordering, deferred-and-replay, dead-letter-and-resubmit, scheduled-enqueue, blocked-session lifecycle. Each transport's test project subclasses these and supplies a fresh transport via `CreateTransport()`.

CI runs the in-memory + Service Bus suites unconditionally; the RabbitMQ suite runs against a Testcontainers-managed RabbitMQ instance with both plugins pre-installed.

## Phasing

This decision is delivered in four gated phases. The project can stop at the end of any phase and remain in a coherent state.

1. **Phase A — Roadmap commitment (docs only)**. This ADR, plus updates to roadmap, backlog, architecture, and README. No code.
2. **Phase B — Extract `NimBus.Transport.Abstractions`** (refactor, zero behavioural change). Fix the existing leakage where `Azure.Messaging.ServiceBus` types reach into `NimBus.SDK`, `NimBus.Resolver`, and `NimBus.WebApp`. Verify the in-memory and Service Bus transports both pass the new conformance suite. **Decision gate at end of Phase B**: review the seam. If the abstraction is leaky, ship the cleanup as a value-additive refactor and reconsider RabbitMQ.
3. **Phase C — `NimBus.Transport.RabbitMQ` provider**. Greenfield, gated by Phase B passing the conformance suite.
4. **Phase D — Demos, CLI, operability**. CrmErpDemo runs against `--Transport rabbitmq`. New `samples/RabbitMqOnPrem/` for the minimal on-prem story.

## Consequences

### Positive
- A NimBus solution can be deployed with zero Azure dependencies (RabbitMQ + SQL Server fully on-premise).
- Existing Service Bus deployments continue to work without configuration changes (default transport is `servicebus`).
- The `NimBus.SDK`, `NimBus.Resolver`, and `NimBus.WebApp` projects stop depending on `Azure.Messaging.ServiceBus` directly — a leak that has bothered the project since inception (verifiable via `dotnet list package --include-transitive`).
- Deferred-by-session moves out of the transport layer into `NimBus.Core`, simplifying the transport contract and making the deferred-processor reusable across transports.
- Future transports (if a fourth concrete demand appears) implement the new abstractions and pass the same conformance suite — no further architecture work required.

### Negative
- Two transport paths to maintain. CI now spins up a RabbitMQ container per build.
- Per-endpoint partition count on RabbitMQ is bounded; teams running with >16 concurrent ordering keys must reconfigure at provisioning time. Service Bus has no such bound.
- The `rabbitmq_delayed_message_exchange` and `rabbitmq_consistent_hash_exchange` plugins are hard prerequisites on the broker side. Teams running stock RabbitMQ without plugins cannot adopt this transport without enabling them.
- Cross-region geo-DR, sub-millisecond lock renewal semantics, and unbounded session counts are not portable. The on-prem story is honest about this — it is not a strict feature superset of Service Bus.
- Topology provisioning is a complete rewrite per transport (mirrors the SQL Server vs. Cosmos asymmetry from ADR-010 where SQL has DbUp and Cosmos has on-demand container creation).

## Open questions deferred to Phase B/C implementation
- Default partition count per endpoint (proposed 16) and the exact reconfiguration story.
- Whether RabbitMQ Streams (3.9+) are an alternative or complementary mechanism to consistent-hash + SAC for high-throughput per-key ordering.
- DLX naming convention and whether dead-lettered messages preserve their original `EnqueuedTime` for audit-trail comparability with ASB.
- Whether plugin-less brokers get a degraded-mode fallback (SQL outbox + scheduled-dispatcher hosted service) for scheduling, or are simply unsupported. Default position: unsupported, fail loud at startup.

## See also
- [ADR-001](001-session-based-ordering.md) — original session-based ordering decision; this ADR supersedes its "transport abstraction is explicitly out of scope" framing.
- [ADR-003](003-separated-deferred-processor.md) — separated DeferredProcessor; the move to a transport-agnostic park-and-replay implementation lands here in Phase B.
- [ADR-010](010-pluggable-message-storage.md) — pluggable storage; this ADR adopts its provider-package layout, builder-time validation, and conformance-suite pattern wholesale.
