# ADR-007: Code-First EventCatalog and AsyncAPI Export

## Status
Accepted

## Context
NimBus needs architecture documentation that stays in sync with the code. Two approaches:

1. **Spec-first** — Write AsyncAPI/EventCatalog specs manually, generate code from them
2. **Code-first** — Generate specs from `PlatformConfiguration` and C# event classes

The .NET AsyncAPI tooling ecosystem (Saunter, Modelina) favors code-first. NimBus already has all the metadata in `PlatformConfiguration` (endpoints, event types, producer/consumer relationships) and C# attributes (`[Description]`, `[Required]`, `[Range]`).

## Decision
Implement two CLI commands that generate documentation from code:

- `nb catalog export` — Generates EventCatalog-compatible markdown (domains, services, events, channels)
- `nb catalog asyncapi` — Generates AsyncAPI 3.0 YAML specification (servers, channels, operations, messages, JSON Schema)

Both read `PlatformConfiguration` at runtime. The AsyncAPI exporter reflects on C# event classes to generate JSON Schema with types, formats, required fields, descriptions, and validation ranges.

Output is consumed by:
- EventCatalog (interactive architecture visualization)
- AsyncAPI HTML template (API documentation)
- Schema validation and contract testing tools

## Consequences

### Positive
- Documentation is always in sync with code — regenerate after any topology change
- No manual spec maintenance
- Leverages existing C# attributes for rich schema information
- Single source of truth — `PlatformConfiguration` defines topology, docs are derived
- CI/CD integration — run `nb catalog asyncapi` in pipelines to detect schema drift

### Negative
- Generated docs lack hand-written explanations (supplemented by markdown content in EventCatalog)
- AsyncAPI 3.0 tooling is less mature than OpenAPI tooling
- Schema reflects the C# type system — some nuances (nullable reference types, inheritance) may not map perfectly to JSON Schema

## Update: faithful Service Bus topology mapping (issue #69)

The first AsyncAPI exporter modelled a naive logical view (channel per producing endpoint; a
consumer's `receive` pointed at the producer's channel). That misrepresented NimBus's real
topology, where routing is done by **SQL rules on application properties** and messages are
**auto-forwarded** between per-endpoint topics — a consumer physically reads its *own* topic's
session subscription after the forward, never the producer's topic.

**Decision:** keep the portable **logical** channels/operations, but enrich them with the Service
Bus specifics via AsyncAPI **specification extensions**, because there is no official AsyncAPI
Service Bus binding — the `amqp1` binding is a v0.1.0 empty placeholder and Azure Service Bus
speaks AMQP 1.0. The server declares `protocol: amqp` + an (empty) `amqp1` binding; everything
Service-Bus-specific lives under `x-servicebus*` / `x-nimbus*`:

- `servers.production.x-nimbus-topology` — topic-per-endpoint pattern, SQL-rule routing, the
  Resolver/Deferred subscriptions, and the `user.*` routing properties.
- `channels.<endpoint>.x-servicebus` — the endpoint topic (ordering, duplicate detection).
- `operations.<...>_receive_<...>.x-servicebus-delivery` — the consumer's session delivery
  subscription **and** the forward subscription(s) on each producer topic (filter, rewrite action,
  `forwardTo`), kept in lock-step with `ServiceBusTopologyProvisioner`.
- `components.messages.<event>` — a shared `NimBusMessageHeaders` schema (the `user.*`
  application properties), session/dead-letter/`MessageId` conventions, and an example payload.

The exporter also became platform-agnostic (accepts an `IPlatform`, like the provisioner),
enumerates **all** producers of an event (fixing the single-producer / EventTypeId-collision gap),
includes `DynamicForward` events (flagged `x-nimbus-dynamic`), never drops a consumed event that
has no in-config producer, serializes valid YAML **or** JSON via YamlDotNet/Newtonsoft, and
generates richer schemas (enums, collections, nested objects, nullable-aware required). The full
concept mapping lives in `docs/asyncapi-mapping.md`.

Deferred (follow-up): management-UI download, `asyncapi validate`/`diff`, contract-first
validation, and fluent per-publish `AsyncApi` options.

## Update: full runnable EventCatalog export (native MDX)

The original `nb catalog export` predated the issue-#69 upgrade and had drifted badly behind the
AsyncAPI exporter: hardcoded `PlatformConfiguration` and versions, no schemas, no `Command`
awareness (ADR-014), no `[AsyncApiMessage]` enrichment, no `DynamicForward`s, unescaped
frontmatter, and no tests. Meanwhile the official route into EventCatalog —
`@eventcatalog/generator-asyncapi` — turned out to require a **paid EventCatalog Scale license**
(the generator hard-exits without a key), while EventCatalog core and its native markdown format
are MIT.

**Decision:** rewrite the exporter to emit EventCatalog's **native MDX format** directly (the
free path), producing a *full runnable catalog*, and attach a **per-service AsyncAPI 3.0
document** via the free `specifications:` frontmatter so specs still render in the EventCatalog
UI. The native format is also strictly more expressive here: first-class `commands/` (from the
`Command` marker), domains from `ISystem`, and channels with delivery guarantees.

Key semantics:

- Builder (`EventCatalogExporter.Build`) is pure and deterministic (relative path → content,
  ordinal ordering); `EventCatalogCli` owns disk semantics and exit codes.
- The five generated directories (`domains/ services/ events/ commands/ channels/`) are fully
  owned and regenerated every run; scaffold files (`eventcatalog.config.js` with a
  generated-once `cId`, `package.json`, `.gitignore`, `public/`) are create-if-missing only.
- `sends`/`receives` route through the endpoint's own `.topic` channel (`to:`/`from:` pointers),
  matching the auto-forward delivery reality documented above.
- Message versions come from `[AsyncApiMessage]`/fluent `Version` (default `1.0.0`) and the same
  value pins every service reference, so pointers never dangle. Enrichment resolution and JSON
  Schema generation are shared with the AsyncAPI exporter (`AsyncApiEnrichmentResolver`,
  `JsonSchemaBuilder`); each message gets a standalone `schema.json` with nested types under
  `$defs`.
- Per-service AsyncAPI documents are produced by running the existing exporter over a
  single-endpoint platform view; cross-endpoint forward-subscription detail intentionally stays
  in the platform-wide `nb asyncapi export`.

Deferred (follow-up): fluent-registry enrichment via a host provider abstraction (the
`--assembly` path sees attribute enrichment only), generated `users/`/`teams/` resources so
Owner/Team can become real `owners:` pointers instead of badges, request/reply `triggers:`
mapping (needs request→reply metadata in the catalog model), and runtime schema-registry
(spec 022) schemas for dynamic events.
