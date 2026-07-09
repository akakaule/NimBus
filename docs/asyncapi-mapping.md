# NimBus → AsyncAPI mapping

`nb catalog asyncapi` generates an [AsyncAPI 3.0](https://www.asyncapi.com/) document from a
NimBus `IPlatform`. This page explains how NimBus concepts map to AsyncAPI, and — importantly —
how the document represents NimBus's **real Azure Service Bus topology** rather than a simplified
"one topic per event" model.

## The topology being described

NimBus does **not** use a topic per event category. Its runtime shape (created by
`ServiceBusTopologyProvisioner`) is:

- **Topic per endpoint.** Every endpoint gets one Service Bus topic named after its id.
- **Routing by SQL rules on application properties** — `user.To`, `user.From`, `user.EventTypeId`
  (see `NimBus.Core/Messages/UserPropertyName.cs`), not by topic name.
- **Auto-forwarding between topics.** A producer publishes only to its *own* topic
  (`user.To` = event class name, `user.From` = null). For each consumer, a forward subscription on
  the producer topic — rule `user.EventTypeId = 'X' AND user.From IS NULL`, action
  `SET user.From=<producer>; SET user.EventId=newid(); SET user.To=<consumer>` — auto-forwards a
  rewritten copy into the consumer's topic.
- **Consumers read their own topic.** Delivery is a **session-required** subscription named after
  the endpoint, filtered `user.To = '<endpoint>'`. The consumer never subscribes to the producer.

## Why specification extensions (no `servicebus` binding)

AsyncAPI has no official Azure Service Bus [binding](https://github.com/asyncapi/bindings) — the
`amqp1` binding is a v0.1.0 empty placeholder, and Service Bus speaks AMQP 1.0. So the document is
a **hybrid**: portable logical channels/operations (what developer portals expect) enriched with
Service Bus specifics carried in `x-servicebus*` / `x-nimbus*` **specification extensions**. The
server declares `protocol: amqp` and an empty `amqp1` binding for tooling that keys off it.

## Exporting the document

Two ways to produce the same full-platform document:

- **CLI** — `nb catalog asyncapi` writes it to a file (`.json` ⇒ JSON, else YAML).
- **Management WebApp** — on **Admin → Topology**, the **AsyncAPI export** panel offers
  **Download YAML** and **Download JSON** buttons. The endpoint is
  `GET /api/admin/asyncapi?format=yaml|json` (missing/empty `format` defaults to YAML; any other
  value is a `400`). It is admin-only — restricted to the `EIP_Management` security group, the same
  gate as the platform-config view — and returns the document as a `nimbus-asyncapi.yaml` /
  `nimbus-asyncapi.json` attachment. Both paths reuse the one exporter
  (`NimBus.ServiceBus.AsyncApi.AsyncApiExporter`), so the WebApp download and the CLI output are
  identical for the same platform.

## Concept mapping

| NimBus concept | AsyncAPI construct |
|---|---|
| Service Bus namespace | `servers.production` (`protocol: amqp`, host `{namespace}.servicebus.windows.net`) |
| Topology (topic-per-endpoint, SQL routing, auto-forward, Resolver/Deferred subs) | `servers.production.x-nimbus-topology` |
| Endpoint topic | `channels.<endpointId>` (`address` = topic; `x-servicebus.resourceType: topic`) |
| Event type appearing on a topic (produced here, or forwarded in) | entry in `channels.<endpointId>.messages` |
| Producer publishes an event | `operations.<producer>_send_<event>` (`action: send`) — **one per producer** |
| Consumer consumes an event | `operations.<consumer>_receive_<event>` (`action: receive`) |
| Consumer's session delivery subscription | `operations.<...>_receive_<...>.x-servicebus-delivery.deliverySubscription` (`requiresSession: true`, filter `user.To = '<endpoint>'`) |
| Auto-forward subscription(s) on producer topic(s) | `...x-servicebus-delivery.forwardSubscriptions[]` (topic, subscription, `forwardTo`, filter, action) |
| Event contract (`IEventType` / CLR class) | `components.messages.<event>` + `components.schemas.<event>` |
| `user.*` application properties | `components.schemas.NimBusMessageHeaders`, referenced by every message's `headers` |
| `[SessionKey]`, MessageId/CorrelationId conventions, session/dead-letter | `components.messages.<event>.x-servicebus` |
| `[Description]` / `[AsyncApiMessage]` on an event | message `title` / `summary` / `description` / `tags` |
| `Event`'s static example instance (`GetEventExample()`) | message `examples[0].payload` |
| `DynamicForward` (spec 022) | message flagged `x-nimbus-dynamic` + its send/receive operations |

## Schema generation

Schemas are reflected from the CLR event type (`IEventType.GetEventClassType()`):

- **Formats** — `Guid` → `string`/`uuid`; `DateTime`/`DateTimeOffset` → `string`/`date-time`;
  integers → `integer`; `decimal`/`double`/`float` → `number`; `bool` → `boolean`.
- **Enums** → `string` with an `enum` list. **Collections** → `array` with `items`. **Nested
  objects** → `$ref` to a registered schema (recursively).
- **Required** — a property is required if it has `[Required]` **or** is non-nullable (value types,
  and NRT-annotated reference types). `[Range]` maps to `minimum`/`maximum`.

## Notes & limits

- `EventTypeId` is the unqualified CLR class name and is global to the namespace; two different
  classes with the same name collide (documented hazard) — the exporter emits both producers'
  `send` operations but the shared message/schema key resolves to one type.
- Dynamically-typed events have no compiled contract, so they get no payload schema.
- Not yet included (follow-ups): `asyncapi validate`/`diff`, contract-first validation, and fluent
  per-publish `AsyncApi` options.

See [ADR-007](adr/007-code-first-catalog-export.md) for the decision record and
[`docs/cli.md`](cli.md#nb-catalog-asyncapi) for command usage.
