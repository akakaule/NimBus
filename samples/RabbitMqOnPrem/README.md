# RabbitMqOnPrem — fully on-premise NimBus sample

A minimal, runnable Aspire AppHost that orchestrates a NimBus deployment with **zero Azure dependencies**:

- **RabbitMQ container** — `rabbitmq:4-management` with both required NimBus plugins pre-loaded:
  - `rabbitmq_consistent_hash_exchange` (per-key ordering across N partition queues)
  - `rabbitmq_delayed_message_exchange` (scheduled enqueue)
- **SQL Server container** — hosts the NimBus message store (audit, parked messages, session state, outbox).
- **Publisher** — minimal HTTP API; `POST /publish/customer` and `POST /publish/customer-failed`.
- **Subscriber** — Worker host with a `CustomerCreated` handler.

> **Phase 6 status:** the build wires up cleanly today against the slices that have landed (provider scaffold, sender, topology provisioner, health check, registration). The runtime consumer loop (`RabbitMqReceiverHostedService`) lands in slice **1D** of issue [#14](https://github.com/akakaule/NimBus/issues/14); until then, messages enqueued on the broker are not yet drained by this sample. The `samples/RabbitMqOnPrem/` wiring is staged so that 1D + 1G (Testcontainers conformance) can flip the sample to a green end-to-end runtime in a single follow-up commit.

## Layout

```
samples/RabbitMqOnPrem/
  RabbitMqOnPrem.AppHost/           # Aspire orchestration
    Program.cs                      # AddRabbitMQ + AddSqlServer + project wiring
    Dockerfile.rabbitmq             # rabbitmq:4-management + delayed-message plugin
    enabled_plugins                 # bind-mounted into the broker
  RabbitMqOnPrem.Contracts/
    Events/CustomerCreated.cs       # [SessionKey(nameof(CustomerId))] demo event
  RabbitMqOnPrem.Publisher/
    Program.cs                      # HTTP minimal API
  RabbitMqOnPrem.Subscriber/
    Program.cs                      # Worker host
    Handlers/CustomerCreatedHandler.cs
```

## Run it

```bash
# From the repository root
dotnet run --project samples/RabbitMqOnPrem/RabbitMqOnPrem.AppHost
```

Aspire spins up the containers, wires `ConnectionStrings:rabbitmq` and `ConnectionStrings:nimbus` into the publisher and subscriber, and exposes the publisher on the URL printed in the dashboard.

```bash
# In another terminal
curl -X POST https://localhost:<publisher-port>/publish/customer
```

The subscriber registers the NimBus pipeline and topology declaration on startup; once 1D lands, the handler will log every received `CustomerCreated`.

## What this sample is **not**

- Not a feature-complete operator surface — there is no `nimbus-ops` WebApp wired here. For the full operator experience use the `CrmErpDemo` AppHost with `--Transport rabbitmq` once 1D lands.
- Not a guide for production deployment topology. See `docs/transports.md` (forthcoming) for the partition-count / session-key skew / forward-only constraints.

## Verifying the on-premise guarantee

```bash
dotnet list samples/RabbitMqOnPrem/RabbitMqOnPrem.Publisher/RabbitMqOnPrem.Publisher.csproj package --include-transitive
dotnet list samples/RabbitMqOnPrem/RabbitMqOnPrem.Subscriber/RabbitMqOnPrem.Subscriber.csproj package --include-transitive
```

Neither output should contain `Azure.Messaging.ServiceBus`, `Azure.Identity`, or any `Microsoft.Azure.Cosmos.*` package. SC-005 is enforced at the SDK level, and these projects depend only on the SDK + the on-prem providers (`NimBus.Transport.RabbitMQ` + `NimBus.MessageStore.SqlServer`).
