# Mode: draft-from-spec — detailed playbook

Load this reference when the user wants to draft a TDD **before** the NimBus adapter is implemented. The resulting document becomes the implementation contract.

## Goal

Produce a `docs/TDD.md` and `docs/events.md` that:

- Fully specify the architecture (C4 Level 1 + 2).
- Enumerate every event the adapter will consume and publish.
- Fix the auth, retry, and missing-reference policies before the first line of handler code is written.
- Make every unknown visible as `{TBD}` — never invented.

## Intake

Ask the user for the minimum viable brief. Use an AskUserQuestion block with these four questions (this is a suggestion — trim or expand based on what the user volunteered):

1. **Adapter name and one-line purpose.** (Recommended naming: `{System}.Adapter` for a Container App / hosted-worker adapter, or `{System}.Adapter.Functions` for an Azure Functions adapter — mirroring `samples/CrmErpDemo`.)
2. **Source system(s) and protocol(s)** — REST/OData/SOAP/Dataverse/SAP/plugin webhook/other. If polled, at what cadence?
3. **Target systems and protocol(s)** — usually NimBus on one side; the adapter may also call target APIs directly on the other.
4. **Events in scope for v1** — list them. Use the NimBus naming convention: PascalCase past-tense for events (`AccountCreated`, `ContactUpdated`, `CustomerCreated`). If the full list is unknown, capture the primary integration only; the rest become follow-up items.

Optional but valuable follow-ups (ask only if the user's brief implies constraints):

- Hosting choice — Container App via Aspire, Azure Functions worker, ASP.NET Core Web API, or in-process hosted service.
- Whether the adapter is bidirectional, and if so, what correlation field will distinguish source-originated from NimBus-originated events (e.g. an `Origin` enum).
- Auth approach for each external call (Managed Identity / OAuth / Basic / shared secret).
- Non-functional targets (throughput, latency, availability).
- Trigger types (real-time webhook, scheduled poll, queue-drain, plugin push).
- Whether ordering matters (drives `[SessionKey]` decisions on event classes).
- Whether the adapter needs a transactional outbox (drives `AddNimBusSqlServerOutbox` + dispatcher in §2.3).

## Drafting rules

1. **Copy the templates verbatim** — `TDD_TEMPLATE.md` → `docs/TDD.md`, `events_TEMPLATE.md` → `docs/events.md`.
2. **Substitute the obvious.** Adapter name, source/target system names, event class names, endpoint class name (typically `{System}Endpoint`).
3. **C4 diagrams.** Draw Level 1 and Level 2 from the brief. For Level 2, default to a single Container App named `{adapter-lower}-{env}` for a worker adapter; for a Functions adapter, draw a Functions app box; if there is both an inbound API and a worker (CrmErpDemo splits `Crm.Api` from `Crm.Adapter`), draw both.
4. **Events.** For each v1 event, add:
   - A row in TDD §3 (consumed or published).
   - A `###` block in `events.md` §2 with the field table. Use `[Required]` and `[Description]` attributes from `System.ComponentModel.DataAnnotations` / `System.ComponentModel` — they are the convention NimBus events use (see `CrmErpDemo.Contracts/Events/CustomerCreated.cs`).
   - A `[SessionKey(nameof(...))]` annotation when ordering matters; otherwise note "no ordering" in the field table footer.
   - If the source system's canonical entity is known, populate the expected fields with `{TBD description}` — never invent types or semantics you cannot attribute.
   - A stub mapping table in `events.md` §3 with `{TBD}` in the resolver column.
5. **Endpoint class.** Stub the `{System}Endpoint : Endpoint` class signature in `events.md` §1 prologue, with one `Produces<>()` / `Consumes<>()` line per event from §3.1/§3.2.
6. **Flag every gap.** Use `{TBD}` for placeholders and `> TODO(human): ...` blockquotes for decisions that still need a human.
7. **Backlog callout.** At the top of TDD §8, add:
   > **Pre-implementation backlog.** This section will be populated from the first code review. Until then, it tracks `{TBD}` markers elsewhere in this document that block implementation.
8. **Document history.** First row in Appendix A: `0.1 | YYYY-MM-DD | Initial draft from spec via adapter-docs skill | {user-or-author}`.

## What to ask before writing vs what to leave as {TBD}

Ask the user before writing:

- Adapter name and purpose (cannot draft without).
- At least one event class name per direction (cannot populate §3).
- Source/target system names (drives the whole diagram).
- Hosting model (drives §2.2 Container diagram and §2.3 resources).

Leave as `{TBD}` if unknown:

- Specific field lists inside events (can be filled during development).
- Azure resource names (naming standards apply at deploy time; Aspire AppHost may also pick names).
- NFR numeric targets (should be committed with the operations team, not guessed).
- Issue-tracker / PR links (unknown until a work item exists).

## Hand-off to implementation

After drafting, the TDD should be good enough that a developer can open it and know:

- What containers / Functions apps to scaffold (§2.2).
- What event classes to create and with which fields and `[SessionKey]` (events.md §2).
- What `Endpoint` subclass to add to the contracts project, with which `Produces<>` / `Consumes<>` declarations (§3 + events.md §1).
- What `AddNimBus*` registrations to wire up in `Program.cs` (subscriber / publisher / receiver / outbox / pipeline behaviours).
- What patterns to follow for retry, echo-loop, and missing-reference handling (§4).

When the implementer writes code that deviates from the draft, the rule is simple: update this TDD in the same pull request. The skill's **update-from-diff** mode can help keep it honest from that point on.
