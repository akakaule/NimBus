# Events — {Adapter Name}

<!--
============================================================
  EVENTS TEMPLATE — companion to TDD.md.

  Purpose:
    - Documents every event the adapter consumes or publishes
      via NimBus.
    - Source of truth for field-level schemas and mappings.
    - Referenced from TDD.md §3 (event index) and §5.4 (data
      entities in the worked example).

  Why a separate file?
    Event catalogs grow; keeping them in-line clutters the TDD
    and makes diffs noisy. The TDD stays human-readable and
    this file becomes the detailed reference.

  Legend:
    [AUTO]  — sections an AI agent can draft from the code
              (event class, endpoint declarations, mapping
              methods).
    [HUMAN] — business meaning, units, allowed-value rationale,
              GDPR classification.
    [MIXED] — AI drafts; human reviews.

  NimBus event conventions:
    - Events extend `NimBus.Core.Events.Event` (abstract base
      providing `GetSessionId()`, `Validate()`, etc.).
    - Public properties decorated with
      `[Required]` / `[Description("...")]` from
      System.ComponentModel.DataAnnotations / .ComponentModel.
    - `[SessionKey(nameof(Field))]` on the class declares the
      ordering key for session-aware delivery (ADR-001).
    - Serialised with Newtonsoft.Json (per project conventions).

  Document conventions:
    - One event per ### heading so anchors stay stable.
    - Field tables in the order they appear in the class —
      easy to diff against generated types.
    - Every field has: name, type, nullable, PII?, example, description.
    - Mapping tables show Source → Event or Event → Target per
      integration.
============================================================
-->

| | |
|---|---|
| **Companion to** | [`TDD.md`](./TDD.md) |
| **Adapter** | {Adapter name} |
| **Endpoint contract** | `{ContractsProject}/Endpoints/{Adapter}Endpoint.cs` |
| **Event base class** | `NimBus.Core.Events.Event` |
| **Status** | Draft / Approved |
| **Version** | 0.1 |
| **Last reviewed** | YYYY-MM-DD |

> **Scope.** Field-level schemas and mapping tables for every event the {Adapter name} consumes or publishes. This document changes in the same pull request as the corresponding event class or mapping code.

---

## 1. Event inventory

### 1.0 Endpoint declaration  <!-- [AUTO] -->

```csharp
public class {Adapter}Endpoint : Endpoint
{
    public {Adapter}Endpoint()
    {
        Produces<{EventX}>();
        Produces<{EventY}>();

        Consumes<{EventA}>();
        Consumes<{EventB}>();
    }

    public override ISystem System => new {Adapter}System();
    public override string Description => "{adapter description}";
}
```

### 1.1 Published events (adapter → NimBus)  <!-- [AUTO] -->

| Event | Base class | Session key | Anchor |
|---|---|---|---|
| `{EventName}` | `NimBus.Core.Events.Event` | `{field}` | [↓](#{event-anchor}) |

### 1.2 Consumed events (NimBus → adapter)  <!-- [AUTO] -->

| Event | Handler (`IEventHandler<T>`) | Session key | Anchor |
|---|---|---|---|
| `{EventName}` | `{HandlerClass}` | `{field}` | [↓](#{event-anchor}) |

### 1.3 Event hierarchy  <!-- [AUTO] — render one diagram per family -->

```mermaid
classDiagram
    class Event {
        <<NimBus.Core.Events.Event>>
        +GetSessionId(): string
        +Validate(): void
    }
    class {BaseEvent} {
        +{Field1}: {Type}
        +{Field2}: {Type}
    }
    Event <|-- {BaseEvent}
    {BaseEvent} <|-- {CreatedEvent}
    {BaseEvent} <|-- {UpdatedEvent}
    {BaseEvent} <|-- {DeletedEvent}
    note for {BaseEvent} "Abstract — common fields only"
```

---

## 2. Event catalog

<!--
  For each event, repeat the block below. Keep the heading
  (###) exactly `{EventName}` so anchors stay stable.
-->

### `{EventName}`  <!-- [MIXED] -->

**Purpose.** {One sentence. What does this event signify? Example: "An account was created in the source CRM. Downstream subscribers should create or link the corresponding customer record."}

**Direction.** Published by adapter / Consumed by adapter.

**Trigger.** {Source-side event or schedule that causes this to be emitted — reference §3.3 of TDD.}

**Delivery semantics.** At-least-once. Consumers must be idempotent on `{primary key field}`.

**Session key.** `[SessionKey(nameof({field}))]` — ensures ordered delivery per `{entity id}`. (Or: "no session key — unordered delivery".)

**Schema (code):** `{namespace}.{EventClassName}` extending `NimBus.Core.Events.Event` — `{path/to/EventClassName.cs}`.

```csharp
[Description("{class-level description}")]
[SessionKey(nameof({SessionKeyField}))]
public class {EventClassName} : Event
{
    [Required]
    [Description("{field description}")]
    public {Type} {Field} { get; set; }

    // ...
}
```

#### Fields  <!-- [MIXED] — AI drafts from class; human fills description + PII flag -->

| Field | Type | Nullable | PII | Example | Description |
|---|---|:---:|:---:|---|---|
| `AccountId` | Guid | no | no | `4f1a...` | Source-system account id. Primary correlation key downstream. |
| `LegalName` | string | no | yes | `Contoso A/S` | Company legal name. |
| `Email` | string | yes | yes | `info@contoso.dk` | Primary business email; optional. |
| `CountryCode` | string | no | no | `DK` | ISO-3166-1 alpha-2. |
| `CreatedAt` | DateTime | no | no | `2026-04-27T10:00:00Z` | UTC. Source-system create timestamp. |
| `{…}` | | | | | |

**Notes on PII.** Fields marked PII are GDPR-protected. They must not appear in log messages or telemetry. Retention: {N} days on the bus / Resolver; downstream systems own their own retention.

#### Validation rules  <!-- [MIXED] -->

- `AccountId` must be a non-empty Guid.
- `CountryCode` must be ISO-3166-1 alpha-2.
- `Email` must be an RFC 5322 address when present.
- {Enum fields list allowed values inline or link to a values table below.}

NimBus calls `Validate()` on the event before publish (and on receive when `ValidationMiddleware` is in the pipeline). Validation failures are classified as permanent and surface in the Resolver as `Invalid`.

#### Allowed values  <!-- [HUMAN] -->

For enum-valued fields, document the closed set:

| Field | Allowed values | Meaning |
|---|---|---|
| `Origin` | `Source`, `Target` | Echo-loop discriminator — see TDD §4.2 |

---

## 3. Mappings

One subsection per integration. Each shows **Source field → Event field** (for published events) or **Event field → Target field** (for consumed events). When the relationship requires a lookup or transform, name the resolver.

### 3.1 {Source system} → `{EventName}`  <!-- [AUTO] + [HUMAN] notes -->

Canonical mapping for the `{EventName}` published by the adapter.

| Source field | Source type | → | Event field | Event type | Resolution |
|---|---|---|---|---|---|
| `Account.accountid` | Guid | → | `AccountId` | Guid | Direct copy |
| `Account.name` | string | → | `LegalName` | string | Direct copy |
| `Account.emailaddress1` | string | → | `Email` | string? | Direct copy; null if blank |
| `Account.address1_country` | string | → | `CountryCode` | string | Lookup ISO code from country name |
| `Account.createdon` | DateTime | → | `CreatedAt` | DateTime | UTC normalisation |
| `{…}` | | | | | |

**Resolvers used:**

- `{CountryResolver}` — `{path/to/CountryResolver.cs}`. Caches name-to-ISO lookups for {N} minutes.

**Fields not mapped:** {list source fields that are intentionally ignored and why}.

### 3.2 `{EventName}` → {Target system}  <!-- [AUTO] + [HUMAN] notes -->

Canonical mapping for the `{EventName}` consumed by the adapter and written to `{target entity}`.

| Event field | Event type | → | Target field | Target type | Resolution |
|---|---|---|---|---|---|
| `AccountId` | Guid | → | `customer.SourceAccountId` | Guid | Primary match key |
| `LegalName` | string | → | `customer.LegalName` | string | Direct copy |
| `CountryCode` | string | → | `customer.CountryId` (Lookup) | int | Lookup `Country` by `IsoCode` |
| `{…}` | | | | | |

**Lookups:**

- `Country` by `IsoCode` — if not found: {Throw / Silent-skip, §4.4 in TDD}.

**Unmapped event fields:** {list event fields not written to this target and why}.

---

## 4. Field-level change log

<!--
  Record field additions, type changes, deprecations. Every
  entry here should correspond to a commit in the event class
  or mapping file. The adapter-docs skill inspects this log to
  decide whether a TDD regen is needed.
-->

| Version | Date | Event | Field | Change | Reason | PR |
|---|---|---|---|---|---|---|
| 0.1 | YYYY-MM-DD | `{EventName}` | {field} | Added | {why} | {PR link} |

---

## 5. Related

- [`TDD.md`](./TDD.md) — the Technical Design Document this file is referenced from.
- `{ContractsProject}/Endpoints/{Adapter}Endpoint.cs` — authoritative `Consumes<>` / `Produces<>` declarations.
- `{ContractsProject}/Events/*.cs` — event class definitions.
- {Higher-level domain / system docs once published.}

---

<!--
============================================================
  REMINDERS
  - Keep field tables ordered exactly as the class declares
    them; generators and diffs rely on it.
  - Every PII-flagged field must be accounted for in the
    logging scrubber (see TDD §9.1).
  - When renaming a field, bump the event's major version and
    coordinate with producers/consumers.
  - `[SessionKey]` is part of the contract — changing it is a
    MAJOR version bump.
============================================================
-->
