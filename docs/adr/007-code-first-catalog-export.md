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
