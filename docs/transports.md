# Transport Providers

NimBus delivers messages through a provider-neutral transport contract defined in
`NimBus.Transport.Abstractions`. Today there are two production-grade
implementations:

| Provider | Package | When to use |
|---|---|---|
| Azure Service Bus | `NimBus.ServiceBus` | Greenfield Azure deployments. Default for the existing operator surface (Resolver, WebApp, CLI). |
| RabbitMQ | `NimBus.Transport.RabbitMQ` | Regulated, sovereign-cloud, or fully on-premise deployments where Azure resources are not available. |

A third in-process implementation lives in `NimBus.Testing` (`AddInMemoryTransport()`)
for unit and integration tests.

Exactly one provider must be registered per running application instance. The
NimBus builder validates this at `Build()` time and fails fast when zero or more
than one is present.

## Azure Service Bus

```csharp
services.AddNimBus(nimbus =>
{
    nimbus.AddServiceBusTransport(opt =>
    {
        // Either a connection string ...
        opt.ConnectionString = builder.Configuration.GetConnectionString("servicebus");

        // ... or token-based auth via Entra ID:
        // opt.FullyQualifiedNamespace = "contoso.servicebus.windows.net";
        // opt.Credential = new DefaultAzureCredential();
    });
});
```

Reads connection material from configuration in this order when options are
blank:

1. `AzureWebJobsServiceBus__fullyQualifiedNamespace` — fully-qualified namespace
   (token auth via `DefaultAzureCredential` when it does not contain
   `SharedAccessKey=`).
2. `ConnectionStrings:servicebus`
3. `AzureWebJobsServiceBus`

Capabilities (queryable via `ITransportCapabilities`):

| Flag | Value |
|---|---|
| `SupportsNativeSessions` | `true` (Service Bus sessions) |
| `SupportsScheduledEnqueue` | `true` (`ScheduledEnqueueTimeUtc`) |
| `SupportsAutoForward` | `true` (`ForwardTo`) |
| `MaxOrderingPartitions` | `null` (unbounded session keys) |

## RabbitMQ

```csharp
services.AddNimBus(nimbus =>
{
    nimbus.AddRabbitMqTransport(opt =>
    {
        // Either an AMQP URI ...
        opt.Uri = builder.Configuration.GetConnectionString("rabbitmq");

        // ... or discrete settings:
        // opt.HostName = "rabbitmq.internal";
        // opt.Port = 5672;
        // opt.VirtualHost = "/";
        // opt.UserName = "nimbus";
        // opt.Password = "***";

        opt.PartitionsPerEndpoint = 16; // default; forward-only after provisioning
        opt.MaxDeliveryCount = 10;       // default; matches Service Bus
    });
});
```

Two RabbitMQ plugins are **hard prerequisites**. The transport's startup health
check verifies both and fails loud with a remediation hint when either is
missing.

| Plugin | Why NimBus needs it |
|---|---|
| `rabbitmq_consistent_hash_exchange` | Per-key (session) ordering across N partition queues. Bundled with RabbitMQ since 3.7. |
| `rabbitmq_delayed_message_exchange` | Scheduled enqueue (`ISender.ScheduleMessage`). Not bundled — install via `rabbitmq-plugins enable rabbitmq_delayed_message_exchange` after copying the `.ez` from the [project releases](https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/releases). |

Capabilities:

| Flag | Value |
|---|---|
| `SupportsNativeSessions` | `false` (emulated via consistent-hash + single-active-consumer) |
| `SupportsScheduledEnqueue` | `true` (delayed-message plugin) |
| `SupportsAutoForward` | `false` (use bindings + alternate exchanges) |
| `MaxOrderingPartitions` | `options.PartitionsPerEndpoint` (default 16) |

### Topology shape per endpoint

`RabbitMqTransportManagement.DeclareEndpointAsync` materialises each endpoint as:

| Entity | Type | Purpose |
|---|---|---|
| `<endpoint>` | `x-consistent-hash` exchange | Publish target. Routing key (= session key) is hashed across the partition queues. |
| `<endpoint>.partition.0..N-1` | Queues, `x-single-active-consumer = true`, with DLX + `x-delivery-limit` | One consumer at a time per queue preserves FIFO. |
| `<endpoint>.delayed` | `x-delayed-message` exchange (delivery type `direct`) | `ScheduleMessage(...)` target. Re-routes via the partition queue name when the delay elapses. |
| `<endpoint>.dlx` + `<endpoint>.dlq` | DLX + DLQ | Holds dead-lettered messages, projected into `MessageStore.UnresolvedEvents` with `ResolutionStatus.DeadLettered`. |

Idempotent — re-running on an already-provisioned broker is a no-op.

### Per-key ordering

Sessions are emulated, not native. Each message's `SessionId` (or
`[SessionKey(nameof(...))]` property value) is hashed by the consistent-hash
exchange and routed to one of the `PartitionsPerEndpoint` queues.
Single-active-consumer on the queue guarantees one consumer at a time, so
within a partition the broker delivers in send order; across partitions,
processing is parallel.

This places a hard upper bound (`MaxOrderingPartitions`) on the number of
distinct session keys that can be processed in parallel. With the default 16
partitions, the broker can process 16 customers / orders / accounts at once;
the 17th key shares a queue with one of the first 16 and serialises behind it.
Tune with `PartitionsPerEndpoint`.

### Partition count is forward-only

`PartitionsPerEndpoint` is fixed at provisioning time. The topology provisioner
**refuses** to reduce it because re-sharding live ordering keys mid-flight
would silently break ADR-001's per-key ordering contract — a message routed
under a 32-partition layout could land in a different queue under a
16-partition layout, overtaking an in-flight predecessor.

Increasing the partition count is safe but still requires explicit operator
opt-in (`--allow-resharding` on the CLI), is logged as a `MessageStore
.MessageAudits` entry, and changes the routing of any *new* messages. Live
in-flight messages remain in their existing queues until processed.

### Session-key skew

When application code generates session keys with poor distribution
(e.g. 90% of messages use `SessionKey = "default"`), one partition becomes hot
while the rest sit idle. The broker does not silently rebalance. Mitigations:

- Choose session keys with high cardinality (e.g. `OrderId`, not `Status`).
- If a key is genuinely unbounded but skewed, use `(key, instance-shard)` as
  the session key so the load distributes across partitions.
- Increase `PartitionsPerEndpoint` so even with skew, more queues absorb traffic.

### Cross-endpoint forwarding

Service Bus's auto-forward chains (e.g. CrmEndpoint → ErpEndpoint when both
endpoints share an event type) are realised on RabbitMQ via alternate exchanges
plus a `From` header consumer-side check. The provisioner declares the
required bindings; consumers discard messages whose `From` already matches
their endpoint, preventing the cross-system forwarding loop that would
otherwise occur when an event type is produced **and** consumed by both
endpoints.

### Health check

`RabbitMqHealthCheck` verifies (a) connection liveness, (b)
`rabbitmq_consistent_hash_exchange` plugin loaded, (c)
`rabbitmq_delayed_message_exchange` plugin loaded. Returns `Unhealthy` with a
specific `rabbitmq-plugins enable …` remediation message when either plugin
is missing.

## In-memory (testing only)

```csharp
services.AddNimBus(nimbus =>
{
    nimbus.AddInMemoryTransport();
});
```

Process-local pub/sub for unit and integration tests. Not for production —
no durability, no DLQ, no scheduled enqueue.

## Provider-selection knob

The `NimBus__Transport` env-var (default: `servicebus`) lets AppHosts and
deployment scripts route the same project at either transport without code
changes. Valid values: `servicebus`, `rabbitmq`, `inmemory`. The
`samples/CrmErpDemo/CrmErpDemo.AppHost` and `src/NimBus.AppHost` both bridge
this through `WithEnvironment(...)`.

The `nb` CLI mirrors the same flag:

```bash
# Provision Service Bus topology against an Azure namespace (default)
nb topology apply --solution-id mysolution --environment dev --resource-group rg-mysolution-dev

# Provision RabbitMQ topology against a self-hosted broker
nb topology apply --transport rabbitmq --rabbit-host rabbitmq.internal --rabbit-user nimbus --rabbit-password '***'

# Skip Azure infra entirely when transport is rabbitmq + storage is sqlserver
nb infra apply --transport rabbitmq --storage-provider sqlserver
# → no Service Bus, no Cosmos: a fully on-premise deployment
```

## Migrating between transports

Cross-transport migration tooling is out of scope for v1. Both transports
satisfy the same at-least-once delivery and per-key ordering guarantees;
what differs is operational shape (broker primitives, scaling, plugin
prerequisites). Operators wishing to move from Service Bus to RabbitMQ (or
vice versa) should treat it as a new deployment with the same
`PlatformConfiguration` rather than as an in-place migration.

## Building a third transport

The provider contract lives in `NimBus.Transport.Abstractions`:

- `ISender` — synchronous send, batch send, scheduled enqueue, scheduled cancel
- `IReceivedMessage` / `IMessageContext` — settle ops only (`Complete`,
  `Abandon`, `DeadLetter`, `Defer`/`DeferOnly`,
  `ReceiveNextDeferred[WithPop]`)
- `IMessageHandler` / `IDeferredMessageProcessor` — handler dispatch and
  parked-message replay (the latter implemented in `NimBus.Core` against
  `MessageStore`, so transports do not have to implement session deferral
  themselves)
- `ITransportProviderRegistration` — marker singleton, validated at builder
  time
- `ITransportCapabilities` — feature flags consumers branch on
- `ITransportManagement` — declare/list/purge/delete topology entities

A new provider is its own NuGet package depending only on
`NimBus.Transport.Abstractions` (and optionally `NimBus.Core`). It exposes
`Add{Provider}Transport(...)` on `INimBusBuilder` and registers exactly one
`ITransportProviderRegistration`. See `docs/extensions.md` for the broader
extension framework and `docs/adr/011-rabbitmq-as-second-transport.md` for the
design rationale that produced the current shape.

> **Roadmap guardrail.** Adding a third transport (Kafka, NATS, SQS, …) requires
> a fresh ADR. The two-provider abstraction is sized for two providers, not n;
> a third provider may surface contract changes (e.g. partition-count semantics
> that don't fit Service Bus's unbounded model) that the current shape does
> not anticipate. See `docs/roadmap.md` for the explicit anti-goal.
