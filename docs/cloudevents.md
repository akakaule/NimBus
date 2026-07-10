# CloudEvents 1.0 Interoperability

NimBus can publish and consume [CloudEvents 1.0](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md)
messages over its existing Azure Service Bus topology. This is an **opt-in
interoperability layer**, not a new transport or a replacement for NimBus's
native wire format — see [ADR-013](adr/013-cloudevents-interoperability.md)
for the design rationale. Use it when a publisher or subscriber needs to
exchange events with a system that speaks CloudEvents but not NimBus's native
envelope (a partner service, generic AMQP tooling, another vendor's event
mesh).

## Overview / opt-in model

CloudEvents is enabled **per endpoint**, not platform-wide:

- A publisher opts in via `NimBusPublisherOptions.UseCloudEvents(...)`. Every
  event that endpoint publishes is then emitted as a CloudEvent. Publishers
  that never call `UseCloudEvents` are completely unaffected — the wire
  format is byte-identical to today.
- A subscriber opts in via `NimBusSubscriberOptions.UseCloudEvents(...)`.
  Inbound messages are then detected/parsed as CloudEvents per the configured
  `CompatibilityMode`.

A single platform can mix native-only, CloudEvents-only, and `AutoDetect`
endpoints freely. Native NimBus routing metadata (`To`, `From`, `EventTypeId`,
`MessageId`, `SessionId`, `CorrelationId`, trace context) is always stamped on
the message regardless of whether CloudEvents is enabled — CloudEvents is an
*additional* view over the same message, so sessions, retry, dead-lettering,
deferred processing, and tracking all keep working (details below).

## Publishing

### Binary content mode (default)

In binary mode the message body stays the raw domain-event JSON; CloudEvents
context attributes ride as AMQP application properties under the
`cloudEvents:` prefix, and the AMQP content-type carries `datacontenttype`.

```csharp
services.AddNimBusPublisher("BillingEndpoint", options =>
{
    options.Endpoint = "BillingEndpoint";
    options.UseCloudEvents(ce =>
    {
        ce.Source = new Uri("urn:customer:billing");
        ce.TypeNameStrategy = CloudEventTypeNameStrategy.UnqualifiedName;
        ce.ContentMode = CloudEventContentMode.Binary; // default
    });
});
```

### Structured content mode

In structured mode the entire CloudEvent (context attributes + `data`) is
serialized as one JSON envelope; the AMQP content-type becomes
`application/cloudevents+json`.

```csharp
services.AddNimBusPublisher("BillingEndpoint", options =>
{
    options.Endpoint = "BillingEndpoint";
    options.UseCloudEvents(ce =>
    {
        ce.Source = new Uri("urn:customer:billing");
        ce.ContentMode = CloudEventContentMode.StructuredJson;
    });
});
```

### Publisher options reference

`CloudEventPublisherOptions` (`src/NimBus.SDK/Extensions/CloudEventPublisherOptions.cs`):

| Property | Purpose | Default |
|---|---|---|
| `Source` (`Uri`) | CloudEvents `source` — identifies the producing system. Required for real interoperability. | `null` |
| `TypeNameStrategy` | How the event's CLR type maps to `type`: `UnqualifiedName` or `FullName`. | `UnqualifiedName` |
| `TypeOverride` (`Func<IEvent, string>`) | Overrides `type` entirely; takes precedence over `TypeNameStrategy`. | `null` |
| `ContentMode` | `Binary` or `StructuredJson`. | `Binary` |
| `DataContentType` | CloudEvents `datacontenttype`. | `application/json` |
| `Subject` (`Func<IEvent, string>`) | CloudEvents `subject` factory. | `null` |
| `Time` (`Func<IEvent, DateTimeOffset>`) | CloudEvents `time` factory; defaults to publish time when unset. | publish time |
| `DataSchema` (`Uri`) | CloudEvents `dataschema`. | `null` |
| `Extensions` (`Action<IEvent, IDictionary<string,string>>`) | Adds custom extension attributes per event. Runs before the mapped `correlationid`/`sessionid` are written, so it cannot shadow them. | `null` |
| `Mapping` | Where `CorrelationId`/`SessionId` land — see [Configurable mapping](#configurable-mapping). | new `CloudEventMapping()` |

Publishing itself is unchanged — call `IPublisherClient.Publish(@event)` as
usual; the CloudEvent projection happens transparently inside the SDK.

## Consuming

### AutoDetect (recommended for mixed traffic)

`AutoDetect` handles both native NimBus messages and CloudEvents (binary or
structured) on the same subscription, detected per message. This is the
right mode when a subscription may receive from both NimBus publishers and
external CloudEvents producers.

```csharp
services.AddNimBusSubscriber(
    configure: options =>
    {
        options.Endpoint = "BillingEndpoint";
        options.UseCloudEvents(ce =>
        {
            ce.Mode = CompatibilityMode.AutoDetect; // default
        });
    },
    configureBuilder: builder => builder.AddHandler(() => new InvoiceCreatedHandler()));
```

### Explicit CloudEvents modes

Use `CloudEventsBinary` or `CloudEventsStructuredJson` when the subscription
only ever receives one shape and you want stricter detection (a native
control message is still parsed natively regardless of mode).

```csharp
services.AddNimBusSubscriber(
    configure: options =>
    {
        options.Endpoint = "BillingEndpoint";
        options.UseCloudEvents(ce => ce.Mode = CompatibilityMode.CloudEventsStructuredJson);
    },
    configureBuilder: builder => builder.AddHandler(() => new InvoiceCreatedHandler()));
```

`NimBusSubscriberOptions.UseCloudEvents` takes the same
`Action<NimBusSubscriberOptions>` overload of `AddNimBusSubscriber` shown
above (`src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs`); the
string-endpoint overload does not expose subscriber options and cannot enable
CloudEvents.

### Subscriber options reference

`CloudEventSubscriberOptions` (`src/NimBus.SDK/Extensions/CloudEventSubscriberOptions.cs`):

| Property | Purpose | Default |
|---|---|---|
| `Mode` | `NimBusNative`, `CloudEventsBinary`, `CloudEventsStructuredJson`, or `AutoDetect`. | `AutoDetect` |
| `AcceptedPrefixes` | AMQP application-property prefixes accepted when detecting/parsing binary CloudEvents. | `["cloudEvents:", "ce-"]` |
| `TypeToEventTypeId` (`Func<string,string>`) | Maps a CloudEvents `type` to a NimBus dispatch key. | last dot-delimited segment (`com.acme.OrderPlaced` → `OrderPlaced`) |
| `Mapping` | Where `correlationid`/`sessionid` are read from — see below. | new `CloudEventMapping()` |

NimBus always **writes** the canonical `cloudEvents:` AMQP property prefix,
but **accepts** the widely-used alternate `ce-` prefix on read, because
several non-Microsoft CloudEvents SDKs emit properties under `ce-` rather than
`cloudEvents:`. This is a read-side compatibility affordance only.

## NimBus ↔ CloudEvents mapping

| CloudEvents attribute | NimBus field |
|---|---|
| `id` | `MessageId` |
| `source` | `CloudEventPublisherOptions.Source` |
| `type` | Event contract name (unqualified class name by default) |
| `specversion` | Always `"1.0"` |
| `data` | `EventContent.EventJson` (the domain event) — **not** the NimBus `MessageContent` envelope |
| `datacontenttype` | `CloudEventPublisherOptions.DataContentType` (default `application/json`) |
| `dataschema` | Optional, `CloudEventPublisherOptions.DataSchema` |
| `subject` | Optional, `CloudEventPublisherOptions.Subject` factory — or `CorrelationId`/`SessionId` when mapped to `subject` (see below) |
| `time` | Optional, `CloudEventPublisherOptions.Time` factory (defaults to publish time) |
| extension `correlationid` | `CorrelationId` (default location; configurable) |
| extension `sessionid` | `SessionId` (default location; configurable) |

> **Type strategy vs. routing.** The native `EventTypeId` used for routing,
> retry, and tracking is always the unqualified CLR class name — it does not
> change with `TypeNameStrategy`. If you set `TypeNameStrategy` to `FullName`
> or supply a `TypeOverride`, the CloudEvents `type` attribute on the wire and
> `context.GetCloudEvent().Type` will differ from the tracked `EventTypeId`.
> Routing and audit always use the native `EventTypeId`; only the CloudEvent
> view reflects the configured strategy.

## Configurable mapping

`CorrelationId` and `SessionId` have no natural CloudEvents core attribute.
`CloudEventMapping` (`src/NimBus.Core/CloudEvents/CloudEventMapping.cs`)
controls where they are written on publish and read on consume — the mapping
is applied symmetrically, so an override is honored in both directions.

Defaults: `correlationid` and `sessionid` extension attributes.

### Mapping SessionId to `subject`

```csharp
options.UseCloudEvents(ce =>
{
    ce.Source = new Uri("urn:customer:billing");
    ce.Mapping.SessionIdAttribute = CloudEventMapping.SubjectAttribute; // "subject"
});
```

With this override, `SessionId` is written to (and read from) the CloudEvents
core `subject` attribute instead of an extension. Use the same
`CloudEventMapping.SubjectAttribute` sentinel on the matching subscriber's
`ce.Mapping.SessionIdAttribute` (or `CorrelationIdAttribute`) so publish and
consume agree.

### Custom extension name

```csharp
options.UseCloudEvents(ce =>
{
    ce.Mapping.CorrelationIdAttribute = "acmecorrelationid";
});
```

Any string other than `"subject"` is treated as an extension attribute name.

### Custom extension attributes beyond correlation/session

```csharp
options.UseCloudEvents(ce =>
{
    ce.Extensions = (@event, extensions) =>
    {
        extensions["tenantid"] = ((InvoiceCreated)@event).TenantId.ToString();
    };
});
```

`Extensions` runs before the mapped `correlationid`/`sessionid` values are
written, so a caller cannot accidentally shadow them by reusing those names.

## Handler / middleware access

Inbound CloudEvents are exposed to handler and middleware code via
`IEventHandlerContext.GetCloudEvent()` /
`IMessageContext.GetCloudEvent()` (`src/NimBus.SDK/EventHandlers/IEventHandlerContext.cs`).
It returns `null` for a native (non-CloudEvents) message.

```csharp
public class InvoiceCreatedHandler : IEventHandler<InvoiceCreated>
{
    public Task Handle(InvoiceCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        var cloudEvent = context.GetCloudEvent();
        if (cloudEvent is not null)
        {
            // cloudEvent.Id, .Source, .Type, .Subject, .Time, .DataContentType,
            // .DataSchema, and .TryGetExtension("tenantid", out var tenantId)
        }

        // message is already the deserialized domain event either way.
        return Task.CompletedTask;
    }
}
```

## Behavior with sessions, retries, dead-lettering, deferred messages, and tracking

CloudEvents is layered over the native message, not a replacement for it, so
every existing subscriber capability keeps working unmodified:

- **Sessions.** `SessionId` is still stamped on the native message (per the
  configured `CloudEventMapping`, it is also visible as a CloudEvent
  attribute); Service Bus session-ordered delivery is unaffected.
- **Retries.** Retry policy operates on the native message/handler outcome
  exactly as for a non-CloudEvents message; a CloudEvent that a handler fails
  to process is retried the same way.
- **Dead-lettering.** Two distinct cases:
  - A handler exception on a *valid* CloudEvent dead-letters through the
    normal permanent-failure path, same as any other message.
  - An **invalid CloudEvent** (missing `id`/`source`/`type`, wrong
    `specversion`) or one whose `type` maps to **no registered handler** is
    rejected by `CloudEventValidatingContextHandler`
    (`src/NimBus.SDK/EventHandlers/CloudEventValidatingContextHandler.cs`) and
    dead-lettered via `InvalidCloudEventException` with a clear, inspectable
    reason — never silently dropped.
- **Deferred messages.** The deferred-processor flow is unaffected; CloudEvents
  detection happens per delivered message regardless of whether it arrived via
  normal delivery or deferred replay.
- **Message tracking / audit.** Tracking uses the native fields — `MessageId`,
  `EventTypeId`, `CorrelationId`, `SessionId` — which under the default
  mapping already carry the CloudEvents `id`/`type`/`correlationid`/
  `sessionid` values. In addition, `MessageAuditEntity` carries optional
  `CloudEventId`, `CloudEventSource`, `CloudEventType`, and `CloudEventSubject`
  fields so the CloudEvents identity (`id`/`source`/`type`/`subject`) is
  preserved in the audit record where the store supports it (verified against
  the in-memory store); these default to `null` for native messages. Remaining
  attributes (`dataschema`, custom extensions) stay inspectable at handler time
  via `context.GetCloudEvent()`.

## CloudEvents metadata in the management UI / message store

The WebApp message detail page and `IMessageTrackingStore` show the same
native fields they always have (`MessageId`, `EventTypeId`, `CorrelationId`,
`SessionId`, `To`/`From`). There is no CloudEvents-specific UI surface yet —
under the default mapping those fields already reflect the CloudEvents
`id`/`type`/`correlationid`/`sessionid` attributes, so an operator reading the
message detail page sees the same identifiers a CloudEvents consumer would.
`source`, `subject`, and extension attributes are only inspectable at handler
time via `context.GetCloudEvent()`, not from the WebApp.

`nb catalog asyncapi` annotates CloudEvents-enabled endpoints in the exported
document: an endpoint whose definition implements `ICloudEventsAware` gets an
`x-cloudevents` channel extension (content mode, `source`, the CloudEvents
attribute set) plus a shared `CloudEventsMessageHeaders` schema, while native
endpoints emit exactly their previous output. See
[ADR-013](adr/013-cloudevents-interoperability.md#asyncapi-export).

## Error / dead-letter behavior summary

| Condition | Outcome |
|---|---|
| CloudEvents disabled on subscriber | No CloudEvents parsing; message handled natively even if it happens to look like a CloudEvent. |
| Valid CloudEvent, registered `type` | Dispatched to the matching `IEventHandler<T>`; `context.GetCloudEvent()` returns the parsed CloudEvent. |
| Missing `id`, `source`, or `type` | Dead-lettered with `InvalidCloudEventException: CloudEvents message missing required attribute '<name>'.` |
| `specversion` present but not `"1.0"` | Dead-lettered with `InvalidCloudEventException: CloudEvents message has unsupported specversion '<value>' (expected '1.0').` |
| `type` maps to no registered handler | Dead-lettered with `InvalidCloudEventException: CloudEvents type '<type>' maps to no registered event contract.` (unlike a native unknown `EventTypeId`, which sends an `UnsupportedResponse` instead of dead-lettering) |
| Handler throws on a valid CloudEvent | Normal retry / permanent-failure path, unchanged. |

## Recommended conventions

- **`source`** — a stable URI identifying the producing system or bounded
  context, e.g. `urn:customer:billing` or `https://billing.example.com`. Set
  it once per publisher via `CloudEventPublisherOptions.Source`; do not vary
  it per event.
- **`type`** — keep the default `UnqualifiedName` strategy unless an external
  consumer requires reverse-DNS-style types (`com.acme.billing.InvoiceCreated`),
  since the default keeps the CloudEvents `type` equal to the native
  `EventTypeId` used for routing and audit. If you do switch to `FullName` or
  a `TypeOverride`, document the mismatch with `EventTypeId` for consumers of
  your tracking data.
- **`subject`** — reserve for a genuinely subject-of-the-event identifier
  (e.g. the invoice number) unless you have deliberately mapped `SessionId` or
  `CorrelationId` onto it via `CloudEventMapping`; don't use it for both at
  once.
- **Extension attributes** — prefix tenant- or domain-specific extensions
  clearly (`tenantid`, `acmecorrelationid`) and keep names lower-case per the
  CloudEvents spec's attribute-naming convention.

## See also

- [ADR-013: CloudEvents 1.0 interoperability layer](adr/013-cloudevents-interoperability.md) — design rationale and rejected alternatives.
- [`samples/CloudEventsInterop`](../samples/CloudEventsInterop) — worked
  example: a NimBus publisher emits a CloudEvent, a NimBus subscriber
  consumes it via `context.GetCloudEvent()`, and a plain
  `Azure.Messaging.ServiceBus` consumer (no NimBus dependency) reads the same
  wire message.
- [`samples/CrmErpDemo`](../samples/CrmErpDemo#showcase-cloudevents-partner-interop-external-system-zero-nimbus) —
  realistic bidirectional showcase: the external **PartnerPortal** (zero NimBus)
  publishes raw CloudEvents leads that AutoDetect routes into the CRM flow, and
  consumes ERP events published as CloudEvents through the SQL transactional
  outbox.
- [`docs/asyncapi-mapping.md`](asyncapi-mapping.md) — AsyncAPI export, which
  reflects CloudEvents-enabled endpoints via the `x-cloudevents` channel
  extension and `CloudEventsMessageHeaders` schema.
- [`docs/sdk-api-reference.md`](sdk-api-reference.md) — general SDK API guide.
