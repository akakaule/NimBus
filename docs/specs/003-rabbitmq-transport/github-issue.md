# RabbitMQ as a second, production-grade transport (on-premise NimBus)

> Spec: [`docs/specs/003-rabbitmq-transport/spec.md`](spec.md)
> ADR: [ADR-011](../../adr/011-rabbitmq-as-second-transport.md)
> Tracking issue: [#14](https://github.com/akakaule/NimBus/issues/14)

## Summary

Today NimBus is hard-wired to Azure Service Bus for transport. This issue tracks the work to:

1. Extract a provider-neutral transport abstraction (`NimBus.Transport.Abstractions`).
2. Disentangle `IMessageContext` — split transport ops from store-state ops that have been masquerading as transport ops since inception.
3. Ship a RabbitMQ provider (`NimBus.Transport.RabbitMQ`) as a separate NuGet package, production-grade, not dev-only.
4. Retrofit `NimBus.ServiceBus` to implement the new abstractions with no behavioural change.
5. Make the deployment paths (`nb topology apply`, `samples/RabbitMqOnPrem/`, CrmErpDemo `--Transport rabbitmq`) support a **true on-premise** NimBus — no Azure dependency at any layer.
6. Validate transport-provider registration in the builder so startup fails fast on zero / multiple providers.

An on-premise customer must be able to install, provision, and operate NimBus end-to-end without an Azure subscription.

## Provider-neutral contracts (FR-001..FR-005)

A new `NimBus.Transport.Abstractions` package:

- `ISender` — synchronous send, batch send, scheduled enqueue, scheduled cancellation
- `IReceivedMessage` — read-only message envelope
- `IMessageContext : IReceivedMessage` — **transport settle ops only** (Complete, Abandon, DeadLetter, Defer, ReceiveNextDeferred, ReceiveNextDeferredWithPop)
- `IMessageHandler`, `IDeferredMessageProcessor` — promoted from `NimBus.Core/Messages/`
- `ITransportProviderRegistration` — DI marker, builder-time validation (mirror of `IStorageProviderRegistration`)
- `ITransportCapabilities` — `SupportsNativeSessions`, `SupportsScheduledEnqueue`, `SupportsAutoForward`, `MaxOrderingPartitions`
- `ITransportManagement` — declare/list/purge/delete topology entities (replaces direct `IServiceBusManagement` usage in `Resolver` / `WebApp` / `CommandLine`)

Constraints:
- Contracts MUST NOT expose Azure types (`ServiceBusReceivedMessage`, `ServiceBusMessage`, `ServiceBusSessionMessageActions`, etc.) or RabbitMQ types (`IConnection`, `IChannel`, `IBasicProperties`).
- `IMessage`, `Message`, `MessageContent`, `[SessionKey]` reachable from this package.
- A provider implements all three contracts — sender, receiver-side context, topology management.

## The disentanglement (FR-002, FR-003) — the largest refactor

The current `IMessageContext` is a mixed-concerns interface. It bundles **transport settle ops** (Complete, Abandon, DeadLetter, Defer, ReceiveNextDeferred, ScheduleRedelivery) with **store-state ops that masquerade as transport ops** (BlockSession, UnblockSession, IsSessionBlocked*, GetBlockedByEventId, IncrementDeferredCount, GetDeferredCount, GetNextDeferralSequenceAndIncrement, ResetDeferredCount).

The store-state ops are *already* persisted in `MessageStore` today. They look like transport ops only because the Service Bus transport happens to call them from `IMessageContext`. RabbitMQ has no equivalent primitive but doesn't need one — the operations are already store-backed.

These methods move to a new `ISessionStateStore` contract under `NimBus.MessageStore.Abstractions`. Old `IMessageContext` methods stay as `[Obsolete]` bridges for one major version per FR-111.

`IMessageContext.ScheduleRedelivery` (originally added for Cosmos throttling) becomes a `NimBus.MessageStore.CosmosDb` internal hosted-service that calls `ISender.ScheduleMessage`. No longer a transport-context method.

## Registration & validation (FR-010..FR-013)

- Exactly one transport provider per running app instance.
- Builder validates at `Build()` time, before any `IHostedService` starts. Mirror of `ValidateStorageProvider` at `src/NimBus.Core/Extensions/NimBusBuilder.cs:91`.
- Registration via `AddNimBus()` builder pattern: `b.AddServiceBusTransport(...)`, `b.AddRabbitMqTransport(...)`, `b.AddInMemoryTransport()`.
- No implicit default. Pure publisher/subscriber adapters can call `WithoutTransport()` for unit-test scenarios.

## Service Bus provider (retrofit, FR-020..FR-023)

- Existing `NimBus.ServiceBus` retrofitted to implement the new contracts with **no behavioural change**.
- New `AddServiceBusTransport(...)` extension method composes existing `ServiceBusAdapter`, `Sender`, `MessageContext`, `ServiceBusSession`, `DeferredMessageProcessor`.
- `ServiceBusTransportCapabilities`: `SupportsNativeSessions = true`, `SupportsScheduledEnqueue = true`, `SupportsAutoForward = true`, `MaxOrderingPartitions = null` (unbounded).
- `Resolver` / `WebApp` / `CommandLine` stop directly constructing `ServiceBusClient` / `ServiceBusAdministrationClient`; everything goes through DI.

## RabbitMQ provider (FR-030..FR-040)

- New project / NuGet package: `NimBus.Transport.RabbitMQ`. Depends on `RabbitMQ.Client` 7.x.
- **Sessions** — `rabbitmq_consistent_hash_exchange` plugin per endpoint, hashing on `SessionKey` header; default 16 partition queues per endpoint; `single-active-consumer = true` per queue. **Forward-only partition count** — increasing requires `--allow-resharding`; reducing fails loud (FR-038).
- **Scheduled enqueue** — `rabbitmq_delayed_message_exchange` plugin; **hard prerequisite** — fail at startup if not loaded.
- **Dead-letter** — DLX per endpoint; messages projected into `MessageStore.UnresolvedEvents` identically to Service Bus DLQ.
- **Cross-endpoint forwarding** — alternate-exchange + `From` header check on consumers (equivalent to Service Bus's `From IS NULL` filter rule). Loops structurally impossible.
- **Topology** — `RabbitMqTopologyProvisioner` declares everything from `PlatformConfiguration`. Idempotent across runs.
- **Health check** — `RabbitMqHealthCheck`: connection liveness + both plugins loaded.
- **Provider is stateless apart from broker connection** — all durable state in `MessageStore`.

## Deferred-by-session as a transport-agnostic primitive (FR-050..FR-054)

The single largest design change after disentanglement.

The deferred-message processor moves from each provider into `NimBus.Core` and operates against `MessageStore`. When a session is blocked, subsequent messages for that session are parked in `MessageStore` (keyed by `SessionKey`, ordered by arrival) and the broker message is settled. On unblock, parked messages replay in FIFO send order.

- Service Bus MAY use native session-deferral as an internal performance optimization. External audit trail in `MessageStore` MUST be identical to portable park-and-replay.
- Park-in-MessageStore is **idempotent on `MessageId`** (FR-052).
- Crash-mid-replay resilient (FR-053).
- Both park and replay emit `MessageStore.MessageAudits` entries.

## Provider-selection knob (FR-060..FR-063)

- New env-var `NimBus__Transport` (default `servicebus`); valid: `servicebus`, `rabbitmq`, `inmemory`.
- `src/NimBus.AppHost/Program.cs` and `samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs` bridge through `WithEnvironment(...)` exactly the same way `NimBus__StorageProvider` is bridged today.
- WebApp / Resolver read `NimBus:Transport` from configuration and call the matching `Add{Transport}Transport(...)`.

## Deployment — CLI & Aspire (FR-070..FR-074)

- `nb topology apply --transport {servicebus|rabbitmq}` with `--rabbit-uri` / host / vhost / etc.
- `nb infra apply --transport rabbitmq` skips Service Bus namespace provisioning entirely. Combined with `--storage-provider sqlserver`, deploys neither Cosmos nor Service Bus — fully on-premise.
- `samples/CrmErpDemo/CrmErpDemo.AppHost` accepts `--Transport rabbitmq`.
- New `samples/RabbitMqOnPrem/` — runnable Aspire AppHost orchestrating RabbitMQ + SQL Server containers, Resolver, WebApp, sample publisher + subscriber. Zero user secrets required.

## WebApp / API surface (FR-080..FR-082)

- `NimBus.WebApp.Startup` / `AdminService` / `SeedDataService` MUST NOT reference `Azure.Messaging.ServiceBus` types directly.
- System-topology view becomes transport-aware (queues vs. subscriptions). Different rendering based on `ITransportCapabilities`.
- Transport-specific UI copy reworded to neutral terms.

## Testing (FR-090..FR-093)

- Shared `NimBus.Testing.Conformance` MSTest transport suite. Categories: send/receive, sessions/ordering, deferred-and-replay, dead-letter-and-resubmit, scheduled-enqueue, blocked-session lifecycle, capability gating.
- CI: in-memory + Service Bus suites unconditional; RabbitMQ via Testcontainers with both plugins pre-loaded.
- Existing E2E tests parametrised by transport where genuinely transport-agnostic; Service-Bus-specific behaviours stay in a Service-Bus-only test class.

## Documentation (FR-100..FR-102)

- `docs/transports.md` — install/configure/provision/test/operate; partition-count semantics on RabbitMQ.
- `docs/extensions.md` — transport-providers section pointing at `NimBus.Transport.Abstractions`.

## Backwards compatibility (FR-110..FR-113)

- Existing Service-Bus-backed deployments add `b.AddServiceBusTransport(...)` to their `AddNimBus(...)` block. No data migration. No schema migration.
- `IMessageContext` API breakage staged: old methods are `[Obsolete]` bridges to `ISessionStateStore` for one major version.
- `AddNimBusPublisher`, `AddNimBusSubscriber`, `IEventHandler<T>` unchanged.
- `samples/AspirePubSub/` continues to work unchanged.

## Acceptance criteria

- [ ] **SC-001** — Fully on-premise NimBus deployment runs end-to-end via `samples/RabbitMqOnPrem/`. No Azure SDK packages, no Azure secrets.
- [ ] **SC-002** — Existing Service-Bus deployments migrate by adding one `AddServiceBusTransport(...)` call; no data migration, no schema migration, no behavioural change.
- [ ] **SC-003** — RabbitMQ transport passes 100% of the shared transport conformance suite (excluding capability-gated tests).
- [ ] **SC-004** — CrmErpDemo runs identically on `--Transport rabbitmq` and `--Transport servicebus`: same audit trail, blocked-session display, resubmit/skip flow.
- [ ] **SC-005** — `NimBus.WebApp`, `NimBus.CommandLine`, `NimBus.Resolver`, `NimBus.Manager`, `NimBus.SDK` have **zero** compile-time references to `Azure.Messaging.ServiceBus`.
- [ ] **SC-006** — `NimBus.Transport.Abstractions`, `NimBus.ServiceBus`, `NimBus.Transport.RabbitMQ` packed and referenced independently.
- [ ] **SC-007** — `samples/RabbitMqOnPrem/` runnable from a fresh clone in under 5 minutes — no manual config.
- [ ] **SC-008** — Startup fails with a clear error when zero or multiple transport providers are registered.
- [ ] **SC-009** — 1000-message burst across 100 session keys: per-key order preserved on RabbitMQ; across-key parallelism observed.
- [ ] **SC-010** — Disentangled `IMessageContext` has fewer than 12 methods (currently 22+). Store-state methods relocated to `ISessionStateStore`.

## Sub-issue breakdown

Phase 6.1 — Extraction & disentanglement (refactor, no behavioural change). **Decision gate at end.**
- [ ] Sub-issue: **Disentangle `IMessageContext`** — split transport ops from store-state ops; introduce `ISessionStateStore`; bridge `[Obsolete]` methods on the old interface.
- [ ] Sub-issue: **Create `NimBus.Transport.Abstractions`** project — promote interfaces; add `ITransportProviderRegistration`, `ITransportCapabilities`, `ITransportManagement` markers.
- [ ] Sub-issue: **Drop `Azure.Messaging.ServiceBus` from `NimBus.SDK`** — fix `NimBusReceiverHostedService.cs`, `SubscriberClient.cs`, `PublisherClient.cs`, `Extensions/ServiceCollectionExtensions.cs`.
- [ ] Sub-issue: **Drop direct `ServiceBusClient`/`ServiceBusAdministrationClient` construction in Resolver / WebApp** — DI-injected `ITransportProvider` / `ITransportManagement`.
- [ ] Sub-issue: **Move deferred-by-session into `NimBus.Core`** — park-and-replay against `MessageStore`; both transports use it.
- [ ] Sub-issue: **Transport conformance suite** — `NimBus.Testing.Conformance` MSTest base classes; in-memory + Service Bus pass; capability-gating works.
- [ ] Sub-issue: **`NimBus__Transport` provider-selection knob** — env-var bridging in AppHost + CrmErpDemo AppHost; runtime selection in WebApp + Resolver.
- [ ] **Decision gate**: review the seam after Phase 6.1; only proceed if `NimBus.Transport.Abstractions` did not end up with leaky ASB-shaped types.

Phase 6.2 — RabbitMQ provider package (greenfield).
- [ ] Sub-issue: **`NimBus.Transport.RabbitMQ` provider** — `AddRabbitMqTransport(...)`; sender; receiver; consistent-hash partitioning; DLX; scheduled enqueue; topology provisioner; health check; conformance suite green via Testcontainers.

Phase 6.3 — Demos, CLI, operability.
- [ ] Sub-issue: **CrmErpDemo `--Transport rabbitmq`** — AppHost RabbitMQ container resource; `NimBus__Transport` bridging; updated README.
- [ ] Sub-issue: **`samples/RabbitMqOnPrem/`** — minimal Aspire AppHost with RabbitMQ + SQL Server, publisher + subscriber, no Azure dependencies.
- [ ] Sub-issue: **CLI `nb topology apply --transport`** — `--transport {servicebus|rabbitmq}`; `nb infra apply --transport rabbitmq` skips Service Bus provisioning.
- [ ] Sub-issue: **WebApp transport-aware topology view** — render queues vs. subscriptions based on `ITransportCapabilities`.

## Edge cases

- Session-key skew — document, do not silently rebalance.
- Partition-count change after provisioning — reduce fails loud; increase requires `--allow-resharding`.
- RabbitMQ broker disconnects mid-batch — publisher confirms; outbox handles.
- Plugin missing — fail loud with remediation message.
- DLX loop — DLX consumers acknowledge unconditionally and route to `MessageStore.UnresolvedEvents`.
- Header size limits — explicit error, no silent truncation.
- Receiver crash during park — broker redelivers; idempotency check on `MessageId`.
- Native vs portable deferred-processing on Service Bus — audit trail observably identical.
- Multi-tenant exchanges — out of scope; vhost separation undocumented.
- Migrating Service Bus → RabbitMQ — out of scope; recommended path is *drain-then-cutover*.
- `IMessageContext` API breakage — staged via `[Obsolete]` bridges for one major version.
- Health check — must not pass when broker is reachable but plugins are not loaded.
- Mixed-mode running (publisher SB, subscriber RabbitMQ) — out of scope.
- OpenTelemetry trace propagation — identical headers, `messaging.system` differentiates correctly.

## Out of scope

- Replacing the storage abstraction.
- Kafka, NATS, SQS, or any other transport in v1.
- Cross-transport migration tooling.
- Mixing transports per endpoint.
- Multi-tenant vhost orchestration.
- Plugin-less RabbitMQ brokers (no degraded fall-back).
- A universal AMQP abstraction.
- RabbitMQ Streams as the partitioning mechanism in v1.
- Distributed-tracing transport bridging.
- WebApp IA changes beyond transport-aware topology rendering.

## Open questions

- **`ITransportManagement` shape** — abstract by intent (`DeclareEndpoint`, `PurgeEndpoint`) or by Service-Bus-style entity?
- **`ISessionStateStore` placement** — fifth contract in `MessageStore.Abstractions`, or nested under `IMessageTrackingStore`?
- **Health-check aggregation** — discovery via `ITransportProviderRegistration.ProviderName`?
- **`ScheduleRedelivery` Cosmos-throttling consumer** — internal hosted-service in `NimBus.MessageStore.CosmosDb`?
- **In-flight messages during transport switch** — drain-then-cutover documented; tooling not provided.
- **Default `MaxDeliveryCount` parity** — Service Bus default is 10; verify RabbitMQ DLX `x-death` semantics give equivalent behaviour.

## Suggested labels

`enhancement` · `transport` · `rabbitmq` · `service-bus` · `breaking-change` · `infra` · `webapp` · `cli` · `epic`
