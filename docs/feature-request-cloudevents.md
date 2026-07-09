# Feature request: Support CloudEvents 1.0 interoperability for Azure Service Bus messages

## Summary

We are evaluating NimBus as an Azure Service Bus based integration platform and would like to request first-class support for CloudEvents 1.0.

From a customer/user perspective, this would make it easier to adopt NimBus in environments where CloudEvents is already used as the standard event envelope across systems, adapters, cloud services, and internal integration platforms.

The goal is not to replace NimBus' existing abstractions, tracking, resolver, retry, session, outbox, or management capabilities. The goal is to add CloudEvents compatibility as an optional interoperability layer.

## Customer context

We have existing systems and adapters that either already publish/consume CloudEvents, or are moving toward CloudEvents as a common event format.

We would like to use NimBus for:

* Azure Service Bus based messaging
* adapter development
* message tracking and auditability
* session-based ordering
* deferred message handling
* retry/dead-letter/resubmit workflows
* outbox-based publishing

However, for adoption, we need events sent through NimBus to be compatible with non-NimBus producers and consumers that expect CloudEvents metadata.

## Problem

NimBus currently appears to be optimized around its own event abstractions and Azure Service Bus message conventions. That works well inside the NimBus ecosystem, but creates friction when integrating with external systems that expect CloudEvents.

Typical scenarios:

* An external producer publishes CloudEvents to an Azure Service Bus topic, and a NimBus subscriber should be able to handle them.
* A NimBus adapter publishes events that downstream non-NimBus consumers should recognize as CloudEvents.
* An organization has standardized on CloudEvents for event metadata such as `id`, `source`, `type`, `specversion`, `subject`, `time`, and `datacontenttype`.
* Events need to pass through tooling, gateways, or integration platforms that inspect CloudEvents attributes for routing, observability, or governance.
* Teams want to avoid writing custom translation code in every adapter just to convert between NimBus messages and CloudEvents.

## Requested capability

Add optional CloudEvents support to NimBus, preferably in the SDK and/or Azure Service Bus transport layer.

### 1. Publishing CloudEvents

Allow NimBus publishers to emit CloudEvents-compatible Azure Service Bus messages.

Desired support:

* Publish a NimBus event as a CloudEvent.
* Configure default CloudEvents attributes, for example:
  * `source`
  * `type`
  * `specversion`
  * `datacontenttype`
  * `subject`
  * `time`
  * `dataschema`
* Allow custom CloudEvents extension attributes.
* Preserve NimBus metadata such as correlation id, message id, event type, endpoint, and session id where possible.
* Support CloudEvents binary content mode at minimum.
* Optionally support structured JSON content mode.

Example configuration idea:

```csharp
builder.Services.AddNimBusPublisher("BillingEndpoint", options =>
{
    options.UseCloudEvents(cloudEvents =>
    {
        cloudEvents.Source = new Uri("urn:customer:billing");
        cloudEvents.TypeNameStrategy = CloudEventTypeNameStrategy.FullName;
        cloudEvents.ContentMode = CloudEventContentMode.Binary;
    });
});
```

Example publishing idea:

```csharp
await publisher.Publish(
    new InvoiceCreated
    {
        InvoiceId = invoice.Id,
        CustomerId = invoice.CustomerId
    },
    sessionId: invoice.CustomerId,
    correlationId: command.CorrelationId,
    messageId: $"invoice-created-{invoice.Id}");
```

This should result in an Azure Service Bus message that can be understood by CloudEvents-aware consumers.

### 2. Consuming CloudEvents

Allow NimBus subscribers to consume CloudEvents-compatible Azure Service Bus messages from external producers.

Desired support:

* Detect CloudEvents metadata on incoming Azure Service Bus messages.
* Map CloudEvents attributes into NimBus message context.
* Deserialize CloudEvent `data` into the matching NimBus event contract.
* Allow handler dispatch based on CloudEvents `type`.
* Preserve CloudEvents attributes so handlers/middleware can inspect them.
* Accept both supported CloudEvents AMQP application property prefixes where practical.
* Provide a clear error/dead-letter behavior when CloudEvents metadata is missing, invalid, or cannot be mapped to a known event type.

Example handler idea:

```csharp
public sealed class InvoiceCreatedHandler : IEventHandler<InvoiceCreated>
{
    public Task Handle(
        InvoiceCreated message,
        IEventHandlerContext context,
        CancellationToken cancellationToken)
    {
        var cloudEvent = context.GetCloudEvent();
        // cloudEvent.Id
        // cloudEvent.Source
        // cloudEvent.Type
        // cloudEvent.Subject
        // cloudEvent.Time
        return Task.CompletedTask;
    }
}
```

### 3. Mapping between NimBus and CloudEvents

Provide a documented mapping between NimBus concepts and CloudEvents attributes.

Possible mapping:

| NimBus / Azure Service Bus concept   | CloudEvents attribute                                                   |
| ------------------------------------ | ----------------------------------------------------------------------- |
| MessageId                            | `id`                                                                    |
| Event type / contract name           | `type`                                                                  |
| Endpoint / system / adapter identity | `source`                                                                |
| Serialized event payload             | `data`                                                                  |
| Content type                         | `datacontenttype`                                                       |
| CorrelationId                        | extension attribute, for example `correlationid`                        |
| SessionId                            | extension attribute, for example `sessionid`, or configurable `subject` |
| Event schema reference               | `dataschema`                                                            |

The exact mapping should be configurable so different organizations can align it with their standards.

### 4. Compatibility modes

It would be helpful if CloudEvents support could be enabled per publisher/subscriber/endpoint rather than globally.

Possible modes:

* `NimBusNative` — current behavior.
* `CloudEventsBinary` — payload remains the domain event, CloudEvents metadata is stored in Azure Service Bus application properties.
* `CloudEventsStructuredJson` — full CloudEvent is serialized as JSON in the message body.
* `AutoDetect` for subscribers — handle both NimBus-native messages and CloudEvents where possible.

### 5. Documentation and samples

Please include documentation and examples showing:

* How to publish CloudEvents from a NimBus adapter.
* How to consume CloudEvents from an external producer.
* How CloudEvents attributes map to NimBus message context.
* How this works with sessions, retries, dead-lettering, deferred messages, and message tracking.
* How CloudEvents metadata appears in the NimBus management UI/message store.
* Recommended conventions for `source`, `type`, `subject`, and extension attributes.

A sample similar to the existing CRM/ERP demo would be very useful, for example:

* CRM publishes a CloudEvent.
* NimBus routes and tracks it.
* ERP consumes it using a NimBus handler.
* A non-NimBus consumer can also consume the same event as a standard CloudEvent.

## Acceptance criteria

* NimBus can publish CloudEvents 1.0 compatible messages to Azure Service Bus.
* NimBus can consume CloudEvents 1.0 compatible messages from Azure Service Bus.
* CloudEvents metadata is preserved in message tracking/audit where possible.
* CloudEvents attributes are available to handlers and middleware.
* Existing NimBus-native messaging continues to work without breaking changes.
* CloudEvents support is opt-in and configurable.
* Documentation explains the mapping between NimBus metadata and CloudEvents attributes.
* Tests cover publishing, consuming, invalid CloudEvents, and mixed native/CloudEvents scenarios.

## Business value

CloudEvents support would make NimBus easier to adopt in organizations that already have event standards or multiple integration technologies.

It would reduce custom adapter code, improve interoperability with non-NimBus systems, and make NimBus more attractive as a platform for enterprise integration where Azure Service Bus is the transport, but CloudEvents is the shared event contract standard.
