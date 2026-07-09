# Feature request: Support AsyncAPI for NimBus event contracts and Azure Service Bus topology

## Summary

We are evaluating NimBus as an Azure Service Bus based integration platform and would like to request support for AsyncAPI.

From a customer/user perspective, this would make it much easier to adopt NimBus in an enterprise environment where event-driven integrations need to be documented, governed, reviewed, and shared across teams.

The goal is not to change how NimBus sends or receives messages. The goal is to make NimBus able to describe its messaging model using the AsyncAPI specification, similar to how HTTP APIs are commonly described using OpenAPI.

## Customer context

We have multiple systems, adapters, and teams working with asynchronous messaging.

We are interested in NimBus because it provides:

* Azure Service Bus based integration
* adapter-based development
* message tracking and auditability
* endpoint metadata
* topology provisioning
* retry, dead-letter, deferred message, and session support
* outbox-based publishing
* a management UI for operational visibility

However, for wider adoption, we need a standard way to document and share the event contracts, channels, publishers, subscribers, message schemas, and routing expectations.

Today, this kind of information often ends up spread across code, README files, diagrams, wiki pages, and manually maintained documentation. That makes it harder for new teams to understand what events exist, who publishes them, who consumes them, and what the expected payload schema looks like.

## Problem

NimBus appears to contain much of the information needed to describe an event-driven system:

* endpoints
* event types
* publishers
* subscribers
* Azure Service Bus topics and subscriptions
* message contracts
* routing configuration
* retry and operational metadata
* message tracking information

But this information is not currently exposed as an AsyncAPI document.

As a potential customer, this creates friction because we need to manually document the messaging contract outside NimBus. That documentation can easily become outdated when event contracts or subscriptions change.

Typical questions we need to answer are:

* Which events can this adapter publish?
* Which events does this adapter consume?
* Which Azure Service Bus topic or subscription is used?
* What is the JSON schema of each event payload?
* Which systems are producers and consumers?
* What message headers or metadata are expected?
* Are sessions required?
* What correlation, causation, or message id conventions are used?
* Which events are part of a specific business capability or integration flow?
* How can downstream teams discover available events without reading NimBus source code?

## Requested capability

Add support for generating AsyncAPI documents from NimBus configuration, topology, and message contracts.

This could be added through the NimBus CLI, SDK, and optionally the management UI.

### Suggested features

#### 1. Generate AsyncAPI from NimBus topology

Add a CLI command that exports the current NimBus platform configuration as an AsyncAPI document.

Example:

```bash
nb asyncapi export --output nimbus.asyncapi.yaml
```

or:

```bash
nb topology export --format asyncapi --output nimbus.asyncapi.yaml
```

The generated document should describe:

* Azure Service Bus namespace/server
* topics
* subscriptions
* endpoints
* publish operations
* subscribe/receive operations
* event/message types
* payload schemas
* relevant message headers/application properties
* correlation id/message id conventions
* session usage
* retry/dead-letter expectations where relevant

#### 2. Generate schemas from .NET event contracts

NimBus should be able to generate AsyncAPI message schemas from the .NET event contracts used by handlers and publishers.

Example:

```csharp
public sealed record CustomerCreated(
    Guid CustomerId,
    string Name,
    string Email,
    DateTimeOffset CreatedAt);
```

Should produce a corresponding AsyncAPI message schema for `CustomerCreated`.

It would be useful to support:

* records
* classes
* enums
* nullable properties
* collections
* nested objects
* required/optional fields
* custom schema names
* schema descriptions from XML comments or attributes

#### 3. Describe publishers and subscribers

The AsyncAPI document should show which NimBus endpoints publish and subscribe to each event.

Example:

* `Crm.Adapter` publishes `CustomerCreated`
* `Erp.Adapter` subscribes to `CustomerCreated`
* `Billing.Adapter` subscribes to `CustomerCreated`

This is important for impact analysis, onboarding, and governance.

#### 4. Include Azure Service Bus bindings

Because NimBus uses Azure Service Bus as the transport, the generated AsyncAPI should include Azure Service Bus specific details where possible.

Useful information could include:

* topic name
* subscription name
* queue/topic address
* session requirement
* dead-letter behavior
* scheduled enqueue support
* deferred message behavior
* message application properties
* content type
* correlation id
* message id

This would help bridge the gap between the logical event contract and the actual Azure Service Bus topology.

#### 5. Support AsyncAPI annotations

Allow developers to enrich generated AsyncAPI output using attributes or fluent configuration.

Example attribute-based approach:

```csharp
[AsyncApiMessage(
    Name = "CustomerCreated",
    Title = "Customer created",
    Summary = "Published when a new customer has been created in CRM")]
public sealed record CustomerCreated(
    Guid CustomerId,
    string Name,
    string Email,
    DateTimeOffset CreatedAt);
```

Example fluent configuration:

```csharp
builder.Services.AddNimBusPublisher("Crm.Adapter", publisher =>
{
    publisher.Publish<CustomerCreated>(options =>
    {
        options.AsyncApi.Title = "Customer created";
        options.AsyncApi.Summary = "Published when a customer is created in CRM";
        options.AsyncApi.Tags.Add("CRM");
        options.AsyncApi.Tags.Add("Customer");
    });
});
```

Useful metadata:

* title
* summary
* description
* tags
* examples
* owner/team
* business capability
* version
* deprecated flag
* external documentation link

#### 6. Expose AsyncAPI from the management UI

It would be valuable if the NimBus management UI could expose generated AsyncAPI documentation.

Possible UI features:

* Download AsyncAPI as YAML or JSON
* View event catalog by endpoint
* View publishers and subscribers per event
* View payload schema
* View Azure Service Bus topology mapping
* View examples
* Show warnings when topology and documentation are out of sync

This would make NimBus more useful not only for runtime operations, but also as a discovery and governance tool.

#### 7. Support CI/CD validation

It would be useful to generate and validate AsyncAPI documents in CI/CD.

Example use cases:

* Fail a build if an event contract changes without updating the AsyncAPI document.
* Compare generated AsyncAPI with a committed baseline.
* Detect breaking changes in event schemas.
* Publish the AsyncAPI document as a build artifact.
* Publish generated documentation to a developer portal or event catalog.

Example command ideas:

```bash
nb asyncapi export --output nimbus.asyncapi.yaml
nb asyncapi validate nimbus.asyncapi.yaml
nb asyncapi diff previous.yaml current.yaml
```

#### 8. Support both generated and contract-first workflows

Different organizations work in different ways.

It would be useful if NimBus supported both:

1. Code-first
   * Developers define NimBus handlers and event contracts in .NET.
   * NimBus generates AsyncAPI from code and topology.
2. Contract-first
   * Architects or platform teams define AsyncAPI first.
   * NimBus validates that implemented publishers/subscribers match the AsyncAPI contract.

The first version could start with code-first generation, but contract-first validation would be very valuable for larger enterprise use.

### Example output

A simplified generated AsyncAPI document could look like this:

```yaml
asyncapi: 3.0.0
info:
  title: NimBus Integration Platform
  version: 1.0.0
  description: Event contracts and Azure Service Bus topology generated from NimBus.

servers:
  production:
    host: my-servicebus.servicebus.windows.net
    protocol: amqp
    description: Azure Service Bus namespace

channels:
  customer-events:
    address: CustomerEvents
    messages:
      CustomerCreated:
        $ref: '#/components/messages/CustomerCreated'

operations:
  CrmAdapterPublishesCustomerCreated:
    action: send
    channel:
      $ref: '#/channels/customer-events'
    messages:
      - $ref: '#/channels/customer-events/messages/CustomerCreated'

  ErpAdapterReceivesCustomerCreated:
    action: receive
    channel:
      $ref: '#/channels/customer-events'
    messages:
      - $ref: '#/channels/customer-events/messages/CustomerCreated'

components:
  messages:
    CustomerCreated:
      name: CustomerCreated
      title: Customer created
      summary: Published when a new customer is created in CRM.
      contentType: application/json
      payload:
        $ref: '#/components/schemas/CustomerCreated'

  schemas:
    CustomerCreated:
      type: object
      required:
        - customerId
        - name
        - email
        - createdAt
      properties:
        customerId:
          type: string
          format: uuid
        name:
          type: string
        email:
          type: string
        createdAt:
          type: string
          format: date-time
```

## Acceptance criteria

* NimBus can generate an AsyncAPI document from configured endpoints, publishers, subscribers, and event contracts.
* The generated document includes Azure Service Bus topics, subscriptions, and relevant transport metadata.
* Message payload schemas are generated from .NET event contracts.
* Publishers and subscribers are visible in the generated AsyncAPI document.
* Developers can enrich the generated documentation with descriptions, tags, examples, and ownership metadata.
* AsyncAPI can be exported as YAML and/or JSON.
* The feature is available from the CLI.
* Existing NimBus runtime behavior remains unchanged.
* Documentation explains how NimBus concepts map to AsyncAPI concepts.
* Tests cover schema generation, topology mapping, and export output.
* Optional: the management UI can display or download the generated AsyncAPI document.

## Business value

AsyncAPI support would make NimBus easier to adopt in organizations that need standard documentation and governance for event-driven architecture.

It would help teams understand the available events, payloads, producers, consumers, and Azure Service Bus topology without reverse-engineering the codebase.

For customers, this would reduce onboarding time, improve collaboration between teams, support architecture review processes, and make NimBus more compatible with existing developer portals, event catalogs, and enterprise integration governance practices.
