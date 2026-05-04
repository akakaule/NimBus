# Feature Specification: RabbitMQ as a Second, Production-Grade Transport

Feature Branch: `003-rabbitmq-transport`
Created: 2026-05-04
Updated: 2026-05-04
Status: Draft
Tracking issue: [#14](https://github.com/akakaule/NimBus/issues/14)
Driving ADR: [ADR-011](../../adr/011-rabbitmq-as-second-transport.md)
Input: User description: *"NimBus can use different storage providers, e.g. SQL Server and Cosmos DB. I'm looking into having NimBus have support for RabbitMQ for those users that would like to run it locally on-premise and not rely on cloud infrastructure."*

## Deployment Mode (resolved)

NimBus v1 (Phase 6) MUST support a **true on-premise deployment**: organizations whose approved infrastructure does not include any Azure resources must be able to install, provision, and operate NimBus end-to-end against a self-hosted RabbitMQ broker plus a self-hosted SQL Server. No Service Bus namespace, no Cosmos DB account, no Azure subscription required at any layer (CLI, Bicep, runtime, samples).

This commitment shapes the rest of the spec — the Aspire AppHost, the CrmErpDemo AppHost, the `nb` CLI, and the `NimBus.WebApp` runtime configuration must all support a no-Azure path.

## Provider Scope (resolved)

The RabbitMQ provider is a **full replacement** for the current `NimBus.ServiceBus` transport surface. The transport surface is decomposed into three provider-neutral contracts in a new `NimBus.Transport.Abstractions` package:

- **Sender** — synchronous send, batch send, scheduled enqueue, scheduled cancellation
- **Receiver-side message context** — settle (complete / abandon / dead-letter), defer (transport-level), receive-next-deferred
- **Topology management** — declare endpoints, list endpoints, purge, delete, list subscriptions

A transport provider package implements all three contracts and registers exactly one `ITransportProviderRegistration` marker. Mixing providers per contract is out of scope for v1.

RabbitMQ is the only target. Kafka, NATS, SQS are explicitly out of scope for v1; the abstractions are sized for two providers, not n.

## Critical Design Insight (resolved)

The current `NimBus.Core.Messages.IMessageContext` is a **mixed-concerns interface**: it bundles transport-level operations (`Complete`, `Abandon`, `DeadLetter`, `Defer`, `ReceiveNextDeferred`, `ScheduleRedelivery`) with *store-level* operations that masquerade as transport (`BlockSession`, `UnblockSession`, `IsSessionBlocked*`, `GetBlockedByEventId`, `IncrementDeferredCount`, `GetDeferredCount`, `GetNextDeferralSequenceAndIncrement`, `ResetDeferredCount`).

The store-level operations are not transport concerns. Their state already lives in `NimBus.MessageStore` today (Cosmos DB session-state document or SQL Server table). The Service Bus transport happens to call into them from `IMessageContext` because, historically, sessions and ordering were Service-Bus-shaped. RabbitMQ has no equivalent primitive, but it *also doesn't need one* — the operations are already store-backed.

**Disentangling these two concerns is the largest refactor in this feature** and the prerequisite for a clean transport split. Done correctly, both transports become simpler, the deferred-by-session implementation becomes portable, and `NimBus.Transport.Abstractions` does not contain Service-Bus-shaped types it cannot satisfy on RabbitMQ.

## User Scenarios & Testing

### User Story 1 — Run NimBus end-to-end against RabbitMQ + SQL Server (Priority: P1)

As a NimBus solution owner in a regulated / on-premise / cloud-agnostic environment, I want to run NimBus against a RabbitMQ broker and a SQL Server database with no Azure dependency, so that NimBus can fit into infrastructure my organization already has approved.

Why this priority: Core value of the feature.

Independent Test: Build and run a NimBus application that references `NimBus.Transport.RabbitMQ` and `NimBus.MessageStore.SqlServer`, points at a Testcontainers-managed RabbitMQ + SQL Server, and processes a multi-message round-trip with ordering, deferral, and dead-letter scenarios. No Azure SDK packages are referenced or transitively pulled in.

Acceptance Scenarios:

1. Given a NimBus application registered with `AddRabbitMqTransport(...)` and `AddSqlServerMessageStore(...)`, When messages flow through the system, Then they are routed via RabbitMQ exchanges/queues and tracked in SQL Server identically to how they would be routed via Service Bus + Cosmos.
2. Given a publisher that sends N messages with the same `[SessionKey]`, When the receiver consumes them, Then they are processed in the same order they were sent, regardless of how many other concurrent sessions are in flight.
3. Given a handler that fails until `MaxDeliveryCount` is reached, When the message is dead-lettered, Then it appears in `MessageStore.UnresolvedEvents` with the same `ResolutionStatus.DeadLettered` value and `DeadLetterReason` shape as a Service-Bus dead-lettered message.
4. Given a session that becomes blocked, When subsequent messages for that session arrive, Then they are parked (not abandoned to the broker) and replayed in FIFO order after the session unblocks.
5. Given a NimBus application registered with `AddServiceBusTransport(...)` (the existing default), When messages flow through the system, Then existing Service Bus behaviour is unchanged.

---

### User Story 2 — Install transport as an independent NuGet package (Priority: P1)

As a NimBus user, I want each transport provider to be delivered as a separate NuGet package, so that I only take the dependency I need.

Why this priority: Distribution model is separate package, not built-in. Mirrors ADR-010.

Independent Test: Build a sample NimBus subscriber that references `NimBus.Transport.RabbitMQ` and not `NimBus.ServiceBus`. Verify via `dotnet list package --include-transitive` that the resulting assembly does not pull in `Azure.Messaging.ServiceBus`. Repeat mirrored for `NimBus.ServiceBus` not pulling in `RabbitMQ.Client`.

Acceptance Scenarios:

1. Given a NimBus application that does not reference `NimBus.Transport.RabbitMQ`, When it builds, Then it does not transitively reference `RabbitMQ.Client`.
2. Given a NimBus application that does not reference `NimBus.ServiceBus`, When it builds, Then it does not transitively reference `Azure.Messaging.ServiceBus` or `Azure.Messaging.ServiceBus.Administration`.
3. Given a NimBus application that references `NimBus.SDK`, When it builds, Then it does not transitively reference *any* concrete transport package.

---

### User Story 3 — Preserve operator experience across transports (Priority: P1)

As an operator using `nimbus-ops` (the WebApp + Resolver), I want the same audit trail, resubmit/skip workflow, and live state updates regardless of the backing transport.

Independent Test: Run the CrmErpDemo against `--Transport servicebus` and again against `--Transport rabbitmq`. Compare WebApp screenshots and audit-trail content for the documented happy-path and failure-and-resubmit scenarios. They should be visually and functionally identical.

Acceptance Scenarios:

1. Given any transport, When a message moves through its lifecycle, Then the recorded `ResolutionStatus` enum values are identical across transports.
2. Given a session is blocked on RabbitMQ, When the operator opens `nimbus-ops`, Then the blocked session and parked messages are visible with the same UI affordances as a blocked Service Bus session.
3. Given an operator clicks "Resubmit" on a dead-lettered message, When the resubmit flow completes, Then the message re-enters processing on whichever transport the deployment is using; no transport-specific code path is taken in the operator UI.
4. Given a transport-specific feature that one transport cannot support (e.g., Service Bus auto-forwarding chains, RabbitMQ alternate-exchange fall-back), When the operator UI surfaces the feature, Then it is gated by `ITransportCapabilities` and either greyed out or hidden when unsupported.

---

### User Story 4 — Sessions and per-key ordering on RabbitMQ (Priority: P1)

As a developer publishing events with `[SessionKey(nameof(AccountId))]`, I want strict per-key FIFO ordering on RabbitMQ — even though RabbitMQ has no native session primitive — so that my application's ordering invariants are preserved without changing handler code.

Why this priority: Loss of ordering would break ADR-001's contract and silently introduce bugs (e.g., a CrmAccountUpdated processed before its CrmAccountCreated).

Independent Test: Publish a burst of 1000 messages spread across 100 `SessionKey` values to a RabbitMQ-backed endpoint. Assert that within each session key, messages are processed in send order; across session keys, parallel processing is observed (latency does not scale linearly with the burst size).

Acceptance Scenarios:

1. Given the RabbitMQ provider is registered with default `PartitionsPerEndpoint = 16`, When a burst of multi-key messages arrives, Then per-key order is preserved and across-key parallelism is observed.
2. Given two messages share the same `SessionKey`, Then they are routed to the same RabbitMQ partition queue (consistent hashing on the header).
3. Given a queue has multiple consumers, Then `single-active-consumer` ensures only one consumer processes messages from that queue at a time.
4. Given the operator wants to scale a deployment beyond `PartitionsPerEndpoint` concurrent ordering keys, Then the documentation explains the partition-count trade-off and how to increase it at provisioning time.
5. Given an attempt to *reduce* partition count post-provisioning, Then provisioning fails with a clear error explaining that this would re-shard live ordering keys (forward-only).

---

### User Story 5 — Deferred-by-session as a transport-agnostic primitive (Priority: P1)

As a platform maintainer, I want deferred-by-session to be implemented in `NimBus.Core` against the existing `MessageStore`, so that both transports get the same correct behaviour without each having to emulate Service Bus's session-deferral primitive.

Why this priority: Without this refactor, the RabbitMQ provider would have to invent a session-deferral primitive that doesn't exist in AMQP. With this refactor, the transport contract becomes simpler and Service Bus's native deferral becomes a performance optimization rather than a load-bearing primitive.

Independent Test: With either transport, when a session is blocked, send N messages with that session key. Verify the messages are persisted in `MessageStore` (not parked on the broker), and after unblock are republished in FIFO order. Compare audit-trail output across transports — should be identical.

Acceptance Scenarios:

1. Given a session is blocked, When a message for that session arrives at the receiver, Then it is parked in `MessageStore` (keyed by `SessionKey`, ordered by arrival) and the broker message is settled (not abandoned, not dead-lettered).
2. Given the session is unblocked (manually or via successful handling of the blocking message), When the deferred-message processor runs, Then parked messages are replayed in FIFO send order to the receiving endpoint.
3. Given the same scenario on Service Bus, Then native session-deferral may be used as an internal optimization, but the externally-observable audit trail is identical to RabbitMQ's park-and-replay path.
4. Given the deferred-message processor crashes mid-replay, When it restarts, Then no messages are lost and replay resumes idempotently.
5. Given parked messages are visible in `nimbus-ops`, Then the operator can manually cancel deferral and skip them with the same UI affordance available today.

---

### User Story 6 — RabbitMQ topology declared from code (Priority: P2)

As an operator, I want `nb topology apply --transport rabbitmq` to declare exchanges, bindings, queues, DLX, and the alternate-exchange-based loop-prevention pattern from the same `PlatformConfiguration` that drives Service Bus topology today.

Independent Test: Run `nb topology apply --transport rabbitmq --rabbit-host ...` against a fresh RabbitMQ broker. Verify via the RabbitMQ Management UI that the expected exchanges, bindings, queues, and DLX are declared. Run the CrmErpDemo against this topology and verify the cross-system round-trip works without loops.

Acceptance Scenarios:

1. Given a `PlatformConfiguration` with two endpoints, When `nb topology apply --transport rabbitmq` runs, Then exactly two endpoint exchanges, the corresponding partition queues, the DLX, and the cross-endpoint forwarding bindings are declared.
2. Given `nb topology apply --transport rabbitmq` runs twice in a row, Then it is idempotent — the second run is a no-op or makes only the changes implied by configuration drift.
3. Given a `PlatformConfiguration` change adds a new endpoint, When `nb topology apply --transport rabbitmq` runs, Then the new endpoint's topology is declared without disturbing existing endpoints.
4. Given the cross-system forwarding pattern from CrmErpDemo (Service Bus's `From IS NULL` filter rule guard), Then the equivalent RabbitMQ pattern using alternate exchanges + a `From` header check on consumers prevents the same forwarding loop.

---

### User Story 7 — Schedule message delivery on RabbitMQ (Priority: P2)

As a developer using `ISender.ScheduleMessage(...)` for orchestration timeouts, I want the same API to work on RabbitMQ via the `rabbitmq_delayed_message_exchange` plugin.

Independent Test: Schedule a message 30 seconds in the future. Verify it is *not* delivered before the target time and *is* delivered within ±1 second of the target time.

Acceptance Scenarios:

1. Given the RabbitMQ broker has the `rabbitmq_delayed_message_exchange` plugin loaded, When a publisher calls `ISender.ScheduleMessage(msg, DateTimeOffset.UtcNow.AddSeconds(30))`, Then the message is delivered approximately 30 seconds later and not before.
2. Given the plugin is *not* loaded, When the application starts, Then startup fails fast with a clear error explaining that the plugin is a hard prerequisite for the RabbitMQ transport.
3. Given a scheduled message is cancelled before its target time via `CancelScheduledMessage(seqNum)`, Then it is not delivered.
4. Given the same API on Service Bus, Then native `ScheduledEnqueueTimeUtc` is used; behaviour is identical from the caller's perspective.

---

### User Story 8 — On-premise sample (Priority: P2)

As a developer evaluating NimBus, I want a runnable on-premise sample that has zero Azure dependencies, so that I can validate the no-cloud story end-to-end on a laptop.

Independent Test: Clone the repo, run `dotnet run --project samples/RabbitMqOnPrem/RabbitMqOnPrem.AppHost`. Aspire dashboard opens; RabbitMQ + SQL Server containers start; publisher and subscriber wire up; manually trigger the publisher endpoint; observe the subscriber processes the message; observe `nimbus-ops` shows the audit trail. No `dotnet user-secrets set ConnectionStrings:servicebus ...` or any Azure credential is required at any point.

Acceptance Scenarios:

1. Given a fresh clone and `dotnet run`, When `samples/RabbitMqOnPrem/RabbitMqOnPrem.AppHost` starts, Then RabbitMQ + SQL Server containers come up and the publisher/subscriber/Resolver/WebApp wire up against them with no user-supplied secrets.
2. Given the sample is running, When the user posts an event to the publisher's HTTP endpoint, Then the event flows through RabbitMQ to the subscriber, is processed, and is recorded in `MessageStore`.
3. Given the user opens `nimbus-ops`, Then they see the same UI as the Service-Bus-backed CrmErpDemo, with audit trail, blocked sessions, dead-letter handling, and resubmit working identically.

---

## Edge Cases

- **Session-key skew**. If application code generates session keys with poor distribution (e.g., 90% of traffic uses `SessionKey = "default"`), one RabbitMQ partition becomes hot. Document this; do not silently rebalance.
- **Partition-count change after provisioning**. Reducing the partition count would re-shard live ordering keys mid-flight. Provisioning MUST refuse and explain. Increasing partition count is safe but requires operator opt-in (drift-allow flag).
- **RabbitMQ broker disconnects mid-batch**. Sender uses publisher confirms; on disconnect, in-flight unconfirmed messages must be resent (the outbox pattern handles this transparently for outbox-using callers).
- **Plugin missing**. `rabbitmq_consistent_hash_exchange` and `rabbitmq_delayed_message_exchange` are hard prerequisites. The provider MUST verify both at startup and fail loud with a remediation message ("enable the X plugin via `rabbitmq-plugins enable`").
- **DLX loop**. A poison message that fails repeatedly in the DLX target queue must not loop indefinitely. DLX consumers must have their own `MaxDeliveryCount` or unconditionally route to `MessageStore.UnresolvedEvents` and acknowledge.
- **Message-property size limits**. RabbitMQ has practical limits on header sizes. The transport MUST reject (with a clear error) attempts to publish messages whose headers exceed the configured limit, rather than truncating silently.
- **Receiver crash during park**. Park-in-MessageStore is the atomic step; if the receiver crashes after parking but before settling the broker message, the broker redelivers, the receiver detects the message is already parked (idempotency check on `MessageId`), and settles without re-parking.
- **Native vs portable deferred-processing on Service Bus**. Service Bus continues to use its native session-deferral as a performance optimization. The portable park-and-replay path is the canonical one; native deferral MUST produce an audit trail that is observably identical to portable deferral.
- **Multi-tenant exchanges**. Out of scope for v1; document that vhost separation can be used for tenant isolation but is not exposed as a NimBus configuration knob.
- **`nb infra apply --transport rabbitmq`**. There is no Azure infrastructure to provision in this mode; the command becomes a no-op for infrastructure but still applies topology if combined with `topology apply`. Document this clearly.
- **Migrating an existing Service Bus deployment to RabbitMQ**. Out of scope for v1. Cross-transport migration tooling is not provided.
- **`IMessageContext` API breakage**. The disentanglement (transport ops vs. store ops) is a public API change. Breakages must be staged: deprecated bridge methods on the old interface for one major version, with `[Obsolete]` warnings pointing at the new home.
- **Health checks**. RabbitMQ provider's health check must not silently pass when the broker is reachable but the required plugins are *not* loaded.
- **Mixed-mode running** (e.g. publisher on Service Bus, subscriber on RabbitMQ via cross-broker bridging). Out of scope for v1; not supported.
- **OpenTelemetry trace propagation**. The W3C `traceparent` header pattern from `NimBusDiagnostics` is identical for both transports — RabbitMQ headers carry it the same way Service Bus `ApplicationProperties` do today.

## Requirements

### Functional Requirements

#### Contracts and disentanglement

- **FR-001**: NimBus MUST define provider-neutral transport contracts in a new `NimBus.Transport.Abstractions` package, organized as separately-injectable abstractions:
  - `ISender` — synchronous send, batch send, scheduled enqueue, scheduled cancellation
  - `IReceivedMessage` — read-only message envelope
  - `IMessageContext : IReceivedMessage` — transport settle ops only: `Complete`, `Abandon`, `DeadLetter`, `Defer`, `DeferOnly`, `ReceiveNextDeferred`, `ReceiveNextDeferredWithPop`
  - `IMessageHandler` — handler dispatch contract
  - `IDeferredMessageProcessor` — replays parked messages on session unblock (transport-agnostic; implementation lives in `NimBus.Core` and operates against `MessageStore`)
  - `ITransportProviderRegistration` — marker singleton, validated at builder time (mirror of `IStorageProviderRegistration`)
  - `ITransportCapabilities` — feature flags: `SupportsNativeSessions` (bool), `SupportsScheduledEnqueue` (bool), `SupportsAutoForward` (bool), `MaxOrderingPartitions` (int? — null = unbounded, mirror of `IStorageProviderCapabilities` shape)
  - `ITransportManagement` — declare/list/purge/delete topology entities; abstract surface that replaces direct `IServiceBusManagement` usage in `NimBus.Resolver`, `NimBus.WebApp`, `NimBus.CommandLine`

- **FR-002**: `IMessageContext` in the new abstractions package MUST NOT contain store-state operations. The following methods MUST move to a new contract `ISessionStateStore` (or equivalent name) in `NimBus.MessageStore.Abstractions`, behind `IMessageTrackingStore`'s composition root:
  - `BlockSession`, `UnblockSession`, `IsSessionBlocked`, `IsSessionBlockedByThis`, `IsSessionBlockedByEventId`, `GetBlockedByEventId`
  - `GetNextDeferralSequenceAndIncrement`
  - `IncrementDeferredCount`, `DecrementDeferredCount`, `GetDeferredCount`, `HasDeferredMessages`, `ResetDeferredCount`

  Callers that today reach for these via `IMessageContext` (currently the deferred-processor and the `StrictMessageHandler`) MUST be refactored to inject `ISessionStateStore` directly from DI.

- **FR-003**: `IMessageContext.ScheduleRedelivery` (originally added to handle Cosmos-throttled redelivery) MUST be reframed: the Cosmos throttling concern moves into the Cosmos DB storage provider and uses `ISender.ScheduleMessage` internally rather than introducing a transport-context method.

- **FR-004**: Provider-neutral transport contracts MUST NOT contain Azure-specific types (`ServiceBusReceivedMessage`, `ServiceBusMessage`, `ServiceBusSender`, `ServiceBusClient`, `ServiceBusAdministrationClient`, `ServiceBusSessionMessageActions`, `ProcessSessionMessageEventArgs`) or RabbitMQ-specific types (`IConnection`, `IChannel`, `IBasicProperties`).

- **FR-005**: `IMessage`, `IReceivedMessage`, `Message`, `MessageContent`, `MessageType`, `EventContent`, `ErrorContent`, retry/throttle types, and `[SessionKey]` MUST be reachable from `NimBus.Transport.Abstractions` (either directly hosted there or referenced via `NimBus.Abstractions`). They are the canonical wire model.

#### Provider registration & validation

- **FR-010**: NimBus MUST require exactly one transport provider to be registered per running application instance.
- **FR-011**: The NimBus builder MUST validate transport-provider registration at `Build()` time and fail before any `IHostedService` starts when zero or more-than-one providers are registered. Mirrors the existing `ValidateStorageProvider` pattern in `src/NimBus.Core/Extensions/NimBusBuilder.cs:91`.
- **FR-012**: Transport-provider registration MUST be exposed through the existing `AddNimBus()` builder pattern via provider-specific extension methods:
  - `builder.AddServiceBusTransport(Action<ServiceBusTransportOptions>?)`
  - `builder.AddRabbitMqTransport(Action<RabbitMqTransportOptions>?)`
  - `builder.AddInMemoryTransport()` (the existing `NimBus.Testing` transport, retrofitted)
- **FR-013**: There MUST NOT be an implicit default transport. Pure publisher/subscriber adapters that never read or write the message store today have `WithoutStorageProvider()`; they MUST also be able to opt out of registering a transport via `WithoutTransport()` for unit-test scenarios that wire their own fake.

#### Service Bus provider (retrofit)

- **FR-020**: The existing `NimBus.ServiceBus` package MUST implement the new abstractions without behavioural change. Existing types (`ServiceBusAdapter`, `Sender`, `MessageContext`, `ServiceBusSession`, `DeferredMessageProcessor`) already satisfy the contracts; the work is registration + capability declaration.
- **FR-021**: A new `AddServiceBusTransport(...)` extension method on `INimBusBuilder` MUST register all Service Bus services as a single composed unit, replacing the current scattered `services.AddSingleton<ServiceBusClient>(...)` pattern in consuming projects.
- **FR-022**: `ServiceBusTransportProviderRegistration` MUST declare `ProviderName = "Azure Service Bus"` and `ServiceBusTransportCapabilities` MUST declare `SupportsNativeSessions = true`, `SupportsScheduledEnqueue = true`, `SupportsAutoForward = true`, `MaxOrderingPartitions = null` (unbounded).
- **FR-023**: Existing `Resolver`, `WebApp`, and `CommandLine` direct construction of `ServiceBusClient` / `ServiceBusAdministrationClient` MUST be replaced with DI-injected `ITransportProvider` / `ITransportManagement`.

#### RabbitMQ provider (greenfield)

- **FR-030**: The RabbitMQ provider MUST be implemented as `NimBus.Transport.RabbitMQ`, a separate project and NuGet package, depending on `RabbitMQ.Client` 7.x.
- **FR-031**: The RabbitMQ provider MUST persist nothing of its own; all durable state remains in `MessageStore`. The provider is stateless apart from the broker connection.
- **FR-032**: Per-key ordering MUST be implemented via a per-endpoint **consistent-hash exchange** (`x-consistent-hash` type from the `rabbitmq_consistent_hash_exchange` plugin). The hash is computed from the `SessionKey` header value. Default partition count: 16 per endpoint, configurable per endpoint at provisioning time, not reconfigurable post-provisioning (forward-only — see FR-038 below).
- **FR-033**: Each `(endpoint, partition)` queue MUST be configured with `single-active-consumer = true` so only one consumer processes messages from that queue at a time, preserving per-queue ordering even when multiple receiver instances are scaled out.
- **FR-034**: Scheduled enqueue MUST be implemented via the `rabbitmq_delayed_message_exchange` plugin (`x-delayed-message` type with the original exchange type as the delivery-time exchange type). The provider MUST refuse to start if the plugin is not loaded.
- **FR-035**: Dead-lettering MUST be implemented via a per-endpoint DLX (dead-letter exchange) with a single deadletter queue per endpoint. Dead-lettered messages MUST be projected into `MessageStore.UnresolvedEvents` with `ResolutionStatus.DeadLettered` identically to the Service Bus DLQ projection today.
- **FR-036**: Cross-endpoint forwarding (used by CrmErpDemo to forward `CrmAccountCreated` from `crmendpoint` to `erpendpoint`) MUST be implemented via the alternate-exchange pattern combined with a `From` header check on consumers — equivalent to Service Bus's forwarding subscription with `From IS NULL` filter rule. Cross-system loops MUST be structurally impossible without runtime checks.
- **FR-037**: `RabbitMqTopologyProvisioner` MUST declare exchanges, bindings, queues, DLX, and the alternate-exchange forwarding pattern from a `PlatformConfiguration`. It MUST be idempotent across runs.
- **FR-038**: Provisioning a partition count change MUST be additive only. Reducing partitions MUST fail with a clear error explaining the data-correctness risk. Increasing partitions MUST require an explicit `--allow-resharding` flag, document the operator implications, and be logged via `MessageStore.MessageAudits`.
- **FR-039**: The RabbitMQ provider MUST expose configuration options for: connection URI / host / port / vhost / credentials, `PartitionsPerEndpoint` (default 16), `MaxDeliveryCount` (default matches Service Bus default of 10), `LockDuration` analogue (consumer prefetch + ack-deadline timer), TLS settings, and connection-recovery settings.
- **FR-040**: The RabbitMQ provider MUST include a health check that verifies (a) connection liveness, (b) `rabbitmq_consistent_hash_exchange` plugin is loaded, (c) `rabbitmq_delayed_message_exchange` plugin is loaded. Health check class lives at `NimBus.Transport.RabbitMQ.HealthChecks.RabbitMqHealthCheck`, mirroring `ServiceBusHealthCheck`.

#### Deferred-by-session as a transport-agnostic primitive

- **FR-050**: The transport-agnostic deferred-processor MUST be implemented in `NimBus.Core` (not in any provider package). It operates against `MessageStore` to park messages keyed by `SessionKey` and replay them in FIFO order on session unblock.
- **FR-051**: The Service Bus transport MAY use native session-deferral as an internal performance optimization. When it does, the externally-observable audit trail in `MessageStore` MUST be identical to the portable park-and-replay path. The optimization is invisible to operators.
- **FR-052**: Park-in-MessageStore MUST be idempotent on `MessageId` — a re-delivered message that is already parked MUST NOT be parked twice.
- **FR-053**: The deferred-processor MUST be resilient to crash mid-replay. Restart MUST resume from the next un-replayed parked message; no parked message MUST be lost or replayed twice.
- **FR-054**: The deferred-processor MUST emit `MessageStore.MessageAudits` entries for each park, replay, and skip operation, identical in shape across transports.

#### Provider-selection knob

- **FR-060**: A new `NimBus__Transport` env-var (default: `servicebus`) MUST be introduced alongside the existing `NimBus__StorageProvider`. Valid values: `servicebus`, `rabbitmq`, `inmemory`.
- **FR-061**: `src/NimBus.AppHost/Program.cs` and `samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs` MUST bridge `NimBus__Transport` through `WithEnvironment(...)` exactly the same way `NimBus__StorageProvider` is bridged today (the existing storage-provider routing block at `src/NimBus.AppHost/Program.cs` is the template).
- **FR-062**: `NimBus.WebApp.Startup` and `NimBus.Resolver.Program` MUST read `NimBus:Transport` from configuration and call the corresponding `Add{Transport}Transport(...)` extension method, mirroring the existing storage-provider selection block.
- **FR-063**: When `NimBus__Transport=rabbitmq`, the AppHost MUST refuse to start if `ConnectionStrings:servicebus` is the only configured connection string. When `NimBus__Transport=servicebus` and `RabbitMq:Host` is set but `ConnectionStrings:servicebus` is not, the AppHost MUST emit a clear startup error.

#### Deployment (CLI & Aspire)

- **FR-070**: `nb topology apply` MUST accept a `--transport {servicebus|rabbitmq}` option. When omitted, defaults to the value of `NimBus__Transport` in the calling environment, or `servicebus`.
- **FR-071**: When `--transport rabbitmq` is used, `nb topology apply` MUST accept `--rabbit-uri` (or `--rabbit-host`, `--rabbit-port`, `--rabbit-vhost`, `--rabbit-user`, `--rabbit-password`) and use `RabbitMqTopologyProvisioner`.
- **FR-072**: `nb infra apply` MUST accept a `--transport rabbitmq` option that *skips* Service Bus namespace provisioning entirely. When the storage provider is also `sqlserver`, the resulting Bicep deploys neither Cosmos nor Service Bus — the deployment is a fully on-premise NimBus.
- **FR-073**: `samples/RabbitMqOnPrem/` MUST be a runnable Aspire AppHost that orchestrates: a RabbitMQ container with both required plugins pre-loaded, a SQL Server container, the Resolver, the WebApp, a sample publisher, and a sample subscriber. No user-supplied secrets MUST be required.
- **FR-074**: `samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs` MUST accept `--Transport rabbitmq`. When set, the AppHost replaces the `ConnectionStrings:servicebus` requirement with a RabbitMQ container resource, and bridges `NimBus__Transport=rabbitmq` to all dependents.

#### WebApp / API surface

- **FR-080**: `NimBus.WebApp.Startup` and `NimBus.WebApp.Services.AdminService` / `SeedDataService` MUST NOT reference `Azure.Messaging.ServiceBus` types directly. All transport operations MUST go through `ITransportProvider` / `ITransportManagement`.
- **FR-081**: The system-topology view in the WebApp UI MUST become transport-aware: queues vs. subscriptions, exchanges vs. topics. The frontend renders different shapes based on `ITransportCapabilities` reported by the running deployment.
- **FR-082**: WebApp UI copy that names a specific transport (e.g., "Service Bus session" labels in the operator UI) MUST be reworded to neutral terms (e.g., "Session" / "Per-key partition"), with transport-specific terms appearing only in transport-detail tooltips or expand-on-demand sections.

#### Testing

- **FR-090**: NimBus MUST include a **shared transport conformance test suite** in `NimBus.Testing.Conformance`. All transport providers MUST run the same suite. Mirrors `MessageTrackingStoreConformanceTests` exactly.
- **FR-091**: Conformance test categories: *Send/Receive*, *Sessions and per-key ordering*, *Deferred-and-replay*, *Dead-letter and resubmit*, *Scheduled enqueue*, *Blocked-session lifecycle*, *Capability gating* (transport-feature flags result in correct test skips for transports that don't support a feature).
- **FR-092**: CI MUST run the in-memory + Service Bus suites unconditionally, and the RabbitMQ suite against a Testcontainers-managed RabbitMQ broker with both required plugins pre-loaded.
- **FR-093**: Existing Service-Bus-coupled E2E tests in `tests/NimBus.EndToEnd.Tests/` MUST be parametrised by transport where the test is genuinely transport-agnostic. Service-Bus-specific behaviours (e.g., auto-forwarding chain validation) MUST stay in a Service-Bus-only test class.

#### Documentation

- **FR-100**: NimBus MUST document how to install, configure, provision, test, and operate the RabbitMQ transport, including the on-premise deployment path. Page: `docs/transports.md`.
- **FR-101**: NimBus MUST document the partition-count choice on RabbitMQ — what partitions mean, how to size them, the forward-only constraint, and the consequences of session-key skew. Page: `docs/transports.md` § *RabbitMQ ordering*.
- **FR-102**: NimBus MUST document how contributors implement and distribute additional transport providers. Page: `docs/extensions.md` (existing) — add a *Transport providers* section pointing at `NimBus.Transport.Abstractions`.

#### Backwards compatibility

- **FR-110**: Existing Service-Bus-backed deployments MUST NOT require code changes beyond updating registration calls (`services.AddNimBus(b => b.AddServiceBusTransport(...))` instead of the legacy scattered registrations).
- **FR-111**: The `IMessageContext` API breakage (per FR-002 disentanglement) MUST be staged: old methods on `IMessageContext` remain as `[Obsolete]` bridges that delegate to the new `ISessionStateStore` for one major version.
- **FR-112**: Existing public publishing and subscribing APIs (`AddNimBusPublisher`, `AddNimBusSubscriber`, `IEventHandler<T>`) MUST NOT change.
- **FR-113**: `samples/AspirePubSub/` (the existing minimal Service Bus sample) MUST continue to work unchanged.

### Non-Functional Requirements

- **NFR-001**: Transport-neutral abstractions MUST NOT expose Azure or RabbitMQ-specific types.
- **NFR-002**: RabbitMQ package install MUST NOT be required for users who continue using Service Bus.
- **NFR-003**: Service Bus package install MUST NOT be required for users who use RabbitMQ only.
- **NFR-004**: Both transports MUST satisfy the same per-key ordering guarantee (within `MaxOrderingPartitions` on RabbitMQ; unbounded on Service Bus).
- **NFR-005**: Both transports MUST satisfy the same at-least-once delivery guarantee. Exactly-once is not promised by either; the inbox pattern (Phase 3.2 backlog item) is the answer when it's needed.
- **NFR-006**: RabbitMQ provider operations MUST be safe under concurrent message processing. Connection pooling, channel-per-publisher / channel-per-consumer where appropriate.
- **NFR-007**: RabbitMQ provider errors MUST include actionable messages without exposing credentials.
- **NFR-008**: The RabbitMQ conformance suite MUST run in CI against a disposable RabbitMQ container with both plugins pre-loaded. Documented Aspire-friendly setup. Container image: `rabbitmq:management` with a Dockerfile or runtime `rabbitmq-plugins enable` step.
- **NFR-009**: The feature MUST be compatible with the repository's `net10.0` target and existing packaging conventions.
- **NFR-010**: Transport conformance tests MUST verify behaviour, not provider implementation details.
- **NFR-011**: The disentanglement refactor (FR-002) MUST NOT introduce a behavioural change. The transport conformance suite running against the *current* Service Bus implementation (after retrofit) MUST produce identical results to the pre-refactor E2E suite.
- **NFR-012**: OpenTelemetry trace propagation MUST work identically across transports — `NimBus.Publish` and `NimBus.Process` activities, W3C `traceparent` propagation through transport headers (RabbitMQ headers / Service Bus `ApplicationProperties`), and the same span tags (`messaging.system` correctly differentiating `azureservicebus` vs. `rabbitmq`).

## Key Entities

- **Transport Provider** — implementation set covering sender, receiver-side message context, and topology management contracts.
- **Transport Provider Registration** (`ITransportProviderRegistration`) — DI marker singleton, validated at builder time.
- **Transport Capabilities** (`ITransportCapabilities`) — feature flags for operator-tooling gating.
- **Transport Management** (`ITransportManagement`) — declare/list/purge/delete topology entities.
- **Session State Store** (`ISessionStateStore`) — relocated from `IMessageContext`; lives in `MessageStore.Abstractions`.
- **Deferred-Processor** — transport-agnostic park-and-replay against `MessageStore`, lives in `NimBus.Core`.
- **RabbitMQ Endpoint Topology** — exchange (consistent-hash) + N partition queues + DLX + alternate-exchange forwarding pattern.
- **Partition Count** — fixed integer per endpoint (default 16); forward-only after provisioning.
- **Required Plugins** — `rabbitmq_consistent_hash_exchange`, `rabbitmq_delayed_message_exchange`. Both hard prerequisites.

## Success Criteria

### Measurable Outcomes

- **SC-001**: A NimBus solution can be deployed entirely without Azure resources by running `samples/RabbitMqOnPrem/` (or production equivalent) and registering `AddRabbitMqTransport(...)` + `AddSqlServerMessageStore(...)`. No Azure SDK packages are referenced or transitively pulled in; no Azure secrets are required.
- **SC-002**: Existing Service-Bus-backed solutions migrate by adding `AddServiceBusTransport(...)` to their `AddNimBus(...)` configuration. No data migration, no schema migration, no behavioural change.
- **SC-003**: The RabbitMQ transport passes 100% of the shared transport conformance suite (excluding tests gated by `ITransportCapabilities` flags it doesn't claim to support).
- **SC-004**: CrmErpDemo runs end-to-end against `--Transport rabbitmq` with operator-visible behaviour identical to `--Transport servicebus`: same audit trail, same blocked-session display, same resubmit/skip flow, same metrics dashboards.
- **SC-005**: `NimBus.WebApp`, `NimBus.CommandLine`, `NimBus.Resolver`, `NimBus.Manager`, and `NimBus.SDK` assemblies have **zero compile-time references to `Azure.Messaging.ServiceBus`** after the refactor (verifiable via `dotnet list package --include-transitive`). Direct Service Bus usage is confined to `NimBus.ServiceBus`.
- **SC-006**: `NimBus.Transport.Abstractions`, `NimBus.ServiceBus`, and `NimBus.Transport.RabbitMQ` can be packed and referenced independently. Installing one transport provider does not pull in the other.
- **SC-007**: A developer can run the full on-premise sample (`samples/RabbitMqOnPrem/`) from a fresh clone in under 5 minutes — `git clone` → `dotnet run` → working end-to-end with no manual configuration.
- **SC-008**: Startup fails with a clear error when zero or multiple transport providers are registered, mirroring the storage-provider validation behaviour.
- **SC-009**: For a burst of 1000 messages spread across 100 session keys on RabbitMQ, per-key order is preserved (verified by the conformance suite) and across-key parallelism is observed (latency does not scale linearly with the burst size).
- **SC-010**: The disentangled `IMessageContext` (transport ops only) has fewer than 12 methods (currently 22+ counting deferred-count helpers and session-state queries). Store-state methods land in `ISessionStateStore` and are reachable only from `NimBus.Core` and `NimBus.MessageStore.*`.

## Assumptions

- Storage provider remains pluggable independent of transport. Any combination of `{servicebus, rabbitmq, inmemory}` × `{sqlserver, cosmos, inmemory}` is supported.
- RabbitMQ versions ≥ 3.12 with both required plugins available. Earlier versions are not supported.
- The `single-active-consumer` queue feature is reliable enough to underpin per-key ordering. (RabbitMQ 3.8+; we target 3.12+.)
- The `rabbitmq_consistent_hash_exchange` plugin's hashing distribution is acceptable for typical session-key distributions. We do not implement application-level rebalancing.
- Operators can enable plugins on their RabbitMQ broker. Locked-down brokers without plugin access are not supported in v1.
- Transport selection is application-level, not per-endpoint or per-message-type.
- The RabbitMQ provider is for production use, not just dev. The on-premise sample exercises the same code paths a production deployment would.
- Cross-region geo-DR, sub-millisecond lock renewal, and unbounded session counts are *not* portable to RabbitMQ. Documented honestly.

## Out of Scope

- Replacing the storage abstraction; this feature is purely transport-side.
- Kafka, NATS, SQS, or any other transport provider in v1.
- Cross-transport migration tooling (Service Bus → RabbitMQ or vice versa).
- Mixing transports per endpoint (e.g., publisher on Service Bus, subscriber on RabbitMQ via cross-broker bridging).
- Multi-tenant vhost orchestration as a NimBus configuration knob.
- Plugin-less RabbitMQ brokers (degraded-mode fall-back is rejected — fail loud at startup).
- A universal AMQP abstraction.
- RabbitMQ Streams (3.9+) as the partitioning mechanism in v1; consistent-hash + SAC is the chosen path. Streams may be revisited in a future ADR.
- Distributed-tracing transport bridging (e.g., trace context surviving Service Bus → RabbitMQ → Service Bus paths). Each deployment uses one transport.
- WebApp information-architecture changes beyond what transport-aware topology rendering requires.

## Open Questions

- **`ITransportManagement` shape.** What's the minimum surface? Today `IServiceBusManagement` has methods like `CreateForwardSubscription`, `UpdateForwardTo`, `PurgeSubscription`, `DeleteSubscription`, `ListRules`. Some of these are Service-Bus-shaped (subscription rules) and don't map cleanly. Decision deferred to design phase: do we abstract by intent (`DeclareEndpoint`, `PurgeEndpoint`, `RemoveEndpoint`) or by Service-Bus-style entity (`CreateSubscription`, etc.)?
- **`ISessionStateStore` placement.** Does it become a fifth contract under `NimBus.MessageStore.Abstractions` (alongside `IMessageTrackingStore`, `ISubscriptionStore`, `IEndpointMetadataStore`, `IMetricsStore`), or is it nested inside `IMessageTrackingStore`? Decided in design.
- **Health-check aggregation.** When both transports are theoretically registerable in DI (e.g., test harness scenarios), how does `IHealthCheckBuilder` discover the active one? Probably via `ITransportProviderRegistration.ProviderName`; confirm in design.
- **`ScheduleRedelivery` Cosmos-throttling consumer.** Where does the `IMessageContext.ScheduleRedelivery` call site go after the refactor? Likely into `NimBus.MessageStore.CosmosDb.Throttling` as an internal hosted-service-driven retry loop rather than a per-context API. Confirm in design.
- **Migration path for in-flight messages.** If an existing deployment switches from Service Bus to RabbitMQ, what happens to messages already on Service Bus? Documented out of scope (FR/Out-of-Scope above), but the spec should note that the recommended migration is *drain-then-cutover*.
- **Default `MaxDeliveryCount` on RabbitMQ.** Service Bus default is 10. RabbitMQ DLX semantics around `x-death` headers behave differently after multi-stage retries; confirm parity in design.

## Resolved Questions (from prior revision / ADR-011)

- v1 supports a true on-premise deployment, including CLI and AppHost changes (User Stories 1, 6, 8).
- RabbitMQ provider is a full replacement for the Service Bus transport surface. See FR-001/FR-002/FR-030.
- Per-key ordering on RabbitMQ uses consistent-hash exchange + single-active-consumer queues, default 16 partitions per endpoint, forward-only.
- Scheduled enqueue uses `rabbitmq_delayed_message_exchange`. Plugin is a hard prerequisite — no fall-back path.
- Dead-lettering uses DLX per endpoint; mirrored into `MessageStore.UnresolvedEvents` identically to ASB.
- Deferred-by-session moves out of the transport layer into `NimBus.Core` (park-and-replay against MessageStore). Service Bus may use native deferral as an internal optimization.
- Builder validates at `Build()` time; mirrors `ValidateStorageProvider`.
- Transport selection is application-level via `NimBus__Transport` env-var / `--Transport` flag.
- Conformance suite is shared; both Service Bus and RabbitMQ run the same MSTest suite.
- Kafka, NATS, SQS are explicitly out of scope; no third transport without a fresh ADR.

## Phasing Reference

Delivered in three gated phases per ADR-011 § *Phasing*. Sub-issue breakdown of issue #14 in `docs/specs/003-rabbitmq-transport/github-issue.md`. See `docs/roadmap.md` § *Phase 6* for the high-level summary.

| Phase | Scope | Decision Gate |
|---|---|---|
| 6.1 | Disentangle `IMessageContext`; extract `NimBus.Transport.Abstractions`; retrofit Service Bus + InMemory; conformance suite green for both | **Yes** — review the seam; stop and ship as cleanup if too leaky |
| 6.2 | `NimBus.Transport.RabbitMQ` provider; topology provisioner; conformance suite green | None |
| 6.3 | CrmErpDemo `--Transport rabbitmq`; `samples/RabbitMqOnPrem/`; CLI; transport-aware WebApp topology view | None |
