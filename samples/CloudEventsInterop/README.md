# CloudEventsInterop sample

Demonstrates NimBus's [CloudEvents 1.0 interoperability layer](../../docs/cloudevents.md)
end-to-end: a NimBus publisher emits a CloudEvent, NimBus routes and tracks it through its
normal Azure Service Bus topology, a NimBus event handler consumes it, and a consumer with
**zero NimBus dependency** reads the exact same wire message using only
`Azure.Messaging.ServiceBus`.

This sample is self-contained (its own solution file, own folder) and is **not** part of
`src/NimBus.sln` or CI. See [ADR-013](../../docs/adr/013-cloudevents-interoperability.md) for the
design rationale.

## The 4-step flow

1. **`CloudEventsInterop.Publisher`** publishes an `InvoiceCreated` event via
   `IPublisherClient.Publish(...)`. The publisher has `UseCloudEvents(...)` enabled (binary
   content mode), so NimBus emits it as a CloudEvents 1.0 message: the body is the raw domain
   event JSON, and the CloudEvents context attributes (`id`, `source`, `type`, `specversion`, …)
   ride as `cloudEvents:*` AMQP application properties. Native NimBus routing metadata
   (`MessageId`, `EventTypeId`, `SessionId`, `CorrelationId`, …) is stamped exactly as it would be
   without CloudEvents enabled.
2. **NimBus routes and tracks it** through the standard `SalesEndpoint -> InvoicingEndpoint`
   topology (topic-per-endpoint, SQL-filtered auto-forward — see
   [`docs/asyncapi-mapping.md`](../../docs/asyncapi-mapping.md)), provisioned by
   `CloudEventsInterop.Provisioner`. Routing, retry, and audit tracking all use the native
   metadata, unaffected by the CloudEvents projection.
3. **`CloudEventsInterop.Subscriber`** runs a NimBus subscriber (`CompatibilityMode.AutoDetect`)
   with an `IEventHandler<InvoiceCreated>` — `InvoiceCreatedHandler`. The handler receives the
   already-deserialized `InvoiceCreated` domain event and calls `context.GetCloudEvent()` to read
   the CloudEvents view (`id`, `source`, `type`, `specversion`) when the message arrived as one.
4. **`CloudEventsInterop.NonNimBusConsumer`** — a plain console app referencing only
   `Azure.Messaging.ServiceBus` (**no** `NimBus.*` package or project reference anywhere in this
   project) — reads the *same* message from a second, plain subscription
   (`RawCloudEventsCapture`) that the provisioner creates directly on the `SalesEndpoint` topic.
   Because a subscription's default rule matches every message published to its topic, this
   subscription receives an untouched copy of the exact wire message NimBus published — same
   `MessageId`, same body, same application properties — before any NimBus forward/rewrite rule
   runs. The consumer parses the `cloudEvents:*` properties (binary mode) or the
   `application/cloudevents+json` envelope (structured mode) by hand, proving the message is
   readable by any CloudEvents-aware consumer, not just NimBus.

## Projects

| Project | Role |
|---|---|
| `CloudEventsInterop.Contracts` | Shared `InvoiceCreated` event + `SalesEndpoint`/`InvoicingEndpoint` topology, referencing only `NimBus.Abstractions`. |
| `CloudEventsInterop.Provisioner` | One-shot console app: provisions the NimBus topology, then creates the extra `RawCloudEventsCapture` subscription for step 4. |
| `CloudEventsInterop.Publisher` | Publishes one `InvoiceCreated` CloudEvent and exits. |
| `CloudEventsInterop.Subscriber` | Long-running NimBus subscriber/worker; logs whether each message arrived natively or as a CloudEvent. |
| `CloudEventsInterop.NonNimBusConsumer` | Long-running plain Service Bus consumer with **no NimBus dependency**; prints the raw CloudEvent it parsed. |

## Prerequisites

- .NET 10 SDK
- An Azure Service Bus namespace connection string with Manage rights, **or** the
  [Service Bus emulator](https://learn.microsoft.com/azure/service-bus-messaging/test-locally-with-service-bus-emulator)
  running locally (`ServiceBusTopologyProvisioner` auto-detects
  `UseDevelopmentEmulator=true` in the connection string and adjusts entity limits accordingly).

Set the connection string once for all four projects:

```bash
export ConnectionStrings__servicebus="Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=..."
# or, for the local emulator:
export ConnectionStrings__servicebus="Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
```

(PowerShell: `$env:ConnectionStrings__servicebus = "..."`)

## Run it

From `samples/CloudEventsInterop/`:

```bash
# 1. Provision the topology + raw-capture subscription (run once, or after any topology change)
dotnet run --project CloudEventsInterop.Provisioner

# 2. In separate terminals, start the two long-running consumers
dotnet run --project CloudEventsInterop.Subscriber
dotnet run --project CloudEventsInterop.NonNimBusConsumer

# 3. Publish an event -- both consumers above should print it within a few seconds
dotnet run --project CloudEventsInterop.Publisher
```

Run the Publisher again as many times as you like; each run publishes one new `InvoiceCreated`
event that both the NimBus subscriber and the non-NimBus consumer receive independently.

### Building the whole sample

```bash
dotnet build CloudEventsInterop.slnx
```

This solution is separate from `src/NimBus.sln` and is not referenced by it; NimBus source is
pulled in via `ProjectReference` so the sample always builds against the current SDK.

## Seeing native vs. CloudEvents side by side

Comment out the `options.UseCloudEvents(...)` block in
`CloudEventsInterop.Publisher/Program.cs` and re-run the Publisher: the Subscriber's handler will
log the native-message branch (`context.GetCloudEvent()` returns `null`) instead, and the
NonNimBusConsumer will no longer see any `cloudEvents:*` properties on the message it reads —
demonstrating that CloudEvents support is strictly opt-in and additive.
