---
name: adapter-docs
description: Create, generate, or update the Technical Design Document (TDD) and events documentation for a NimBus adapter. Use when the user asks to "document an adapter", "generate TDD", "update TDD after code changes", "draft adapter documentation", "draft TDD before implementation", or mentions adapter documentation alongside a NimBus adapter project (any project that consumes or publishes events via NimBus — e.g. `Crm.Adapter`, `Erp.Adapter.Functions`, a `*.Adapter` worker, or a NimBus-backed Azure Function). The skill produces a `docs/TDD.md` and `docs/events.md` in the adapter's source repo following the NimBus adapter TDD template.
---

# adapter-docs — document a NimBus adapter

This skill produces and maintains the per-adapter Technical Design Document (TDD) and the companion events catalog for adapters built on NimBus. It operates in three modes:

| Mode | Use when |
|---|---|
| **draft-from-spec** | The adapter is not yet implemented. The user describes what the adapter should do and which events it should support. The skill drafts an initial TDD + events doc from the description alone, which then guides implementation. |
| **generate-from-code** | The adapter exists but has no TDD (or the TDD is stale). The skill scans the source repo and drafts the documentation. |
| **update-from-diff** | The adapter and the TDD both exist. The skill compares current code to the documented state and proposes an update. |

Always pick the mode first. If it is unclear, ask the user.

## What "adapter" means here

A **NimBus adapter** is any application that integrates an external system with the NimBus message bus. Concretely it is a project (Container App, Azure Functions worker, hosted service, Web API) that:

- Subscribes via `AddNimBusSubscriber(...)` and runs `IEventHandler<T>` implementations to consume events from NimBus, OR
- Publishes via `AddNimBusPublisher(...)` / `IPublisherClient` to push events into NimBus,
- And declares its event surface as a `Endpoint` subclass (with `Produces<T>()` / `Consumes<T>()`) registered on a `Platform`.

The reference shape lives in `samples/CrmErpDemo` (`Crm.Adapter`, `Erp.Adapter.Functions`, and the shared `CrmErpDemo.Contracts` project). Use that layout as the ground truth for what to look for during code generation.

## Rules of engagement

1. **One TDD per adapter.** Never split a TDD by integration. Every integration the adapter handles is documented in the same `TDD.md`, with the deep-dive in §5 and short deltas in §6.
2. **No business rationale in the TDD.** Domain descriptions, business drivers, and overall system landscapes belong in higher-level design documents (referenced from TDD §12). The TDD is implementation-and-operations only.
3. **Human sections must not be silently overwritten.** Sections marked `[HUMAN]` or `[MIXED]` in the template hold information that cannot be derived from code: business triggers (when the adapter runs — batch? realtime?), NFR targets, SLA commitments, batch schedules, operator playbooks, document governance. If any of these are missing in update mode, **flag them and ask** rather than guess.
4. **C4 for architecture, sequence diagrams for flows.** Use Mermaid `graph TB` with subgraphs and the C4 styling shown in `TDD_TEMPLATE.md` §2.1 and §2.2 — do *not* use Mermaid's `C4Context` / `C4Container` syntax. Reason: the native C4 blocks render poorly on dark backgrounds (GitHub, Azure DevOps Wiki, VS Code preview), and edge labels lose contrast. The `graph` approach preserves C4 semantics (via `[Person]` / `[Software system]` / `[Container]` annotations in the node text) while rendering correctly everywhere. Use Mermaid `sequenceDiagram` (not flowchart) for every integration flow in §5.2 and §6.x. Use `classDiagram` for event hierarchies in §5.4 and in the events doc.
5. **Events go in `events.md`.** The TDD indexes events and shows the class hierarchy; full field tables and per-integration mapping tables live in `events.md`. Every event reference in the TDD must link to `events.md#event-anchor`.
6. **Files live in `docs/` under the adapter repo.** Target path: `{repo-root}/docs/TDD.md` and `{repo-root}/docs/events.md`. If the folder does not exist, create it.
7. **Markdown only.** No HTML, no frameworks, no external assets. Diagrams render with GitHub / Azure DevOps Mermaid support.

## Templates used by this skill

- `templates/TDD_TEMPLATE.md` — copy as `docs/TDD.md` and fill in.
- `templates/events_TEMPLATE.md` — copy as `docs/events.md` and fill in.

Both templates carry inline `[AUTO]` / `[MIXED]` / `[HUMAN]` legends. Respect them.

## Mode 1 — draft-from-spec (before code)

**Inputs from the user:**

- Adapter name and purpose.
- Source system(s) and target system(s), with protocol hints (REST/OData/SOAP/Dataverse/SAP/Plugin webhook/etc.).
- List of events the adapter should support (consumed, published, or both). If the user does not know the full list, capture the primary integration first and mark the rest as TBD.
- Known constraints: triggers (real-time vs batch), auth approach, NFR hints if any.

**Checklist:**

1. Ask the user for the items above using an AskUserQuestion block. Offer sensible defaults derived from the adapter name (e.g. "Crm adapter" → CRM system on one side, NimBus on the other; project shape mirroring `samples/CrmErpDemo/Crm.Adapter`).
2. Copy `templates/TDD_TEMPLATE.md` to `docs/TDD.md` and `templates/events_TEMPLATE.md` to `docs/events.md` in the target repo.
3. Fill `[AUTO]` sections with placeholders that reflect the stated architecture choices (C4 containers, Azure resources, endpoint patterns, hosting model — Container App, Azure Functions, or hosted service). Mark unknowns with `{TBD}` — never invent.
4. Fill `[HUMAN]` sections with the user's answers: triggers, cadence, NFR targets.
5. Draft `events.md` with one stub per event, including expected fields inferred from the source system's canonical entity (if known) and a `{TBD}` mapping table. Show the event class extending `NimBus.Core.Events.Event` and decorated with `[SessionKey(nameof(...))]` where ordering matters.
6. Add a "Backlog before coding" note at the top of §8 listing every `{TBD}` marker — this becomes the implementer's todo list.
7. Return a short summary to the user: what was drafted, what needs human confirmation, what is pending discovery during implementation.

**Hand-off rule:** the drafted TDD becomes the implementation contract. If the implementer deviates, they update the TDD in the same PR.

## Mode 2 — generate-from-code (existing adapter, no TDD)

**Inputs:** path to the adapter's source repo; optionally a path to an existing code review.

**Checklist:**

1. Locate the adapter's entry point(s): `Program.cs` (host worker, Azure Functions worker, Web API), `host.json` + worker `Program.cs` for Functions, or equivalent. Record the runtime, hosting model, and which `AddNimBus*` extensions are wired up (`AddNimBusPublisher` / `AddNimBusSubscriber` / `AddNimBusReceiver` / `AddNimBus(...)` for the pipeline / `AddNimBusOutboxDispatcher` / `AddNimBusSqlServerOutbox`).
2. Find the NimBus endpoint contract. Default search: `**/Endpoints/*Endpoint.cs` in the adapter's `*.Contracts` project (sibling project or shared NuGet). Extract every `Consumes<T>()` and `Produces<T>()` from the `Endpoint` subclass — this is the event inventory for TDD §3 and `events.md` §1. Capture the `SystemId` from the `ISystem` implementation if present.
3. Find handlers. Default pattern: classes implementing `NimBus.SDK.EventHandlers.IEventHandler<T>`. Map each to its consumed event for TDD §3.1. Note the handler signature is `Handle(T message, IEventHandlerContext context, CancellationToken cancellationToken = default)`.
4. Find publishers. Default pattern: `IPublisherClient.PublishAsync(...)` calls. Map each to its published event for TDD §3.2. Cross-reference publishes against `Produces<T>()` declarations and flag mismatches.
5. For each event class referenced by the endpoint contract: list every public property with type, nullability, `[Required]` / `[Description]` / `[SessionKey]` attributes, and any XML-doc. Populate `events.md` §2 per event. Note the base class (`NimBus.Core.Events.Event`).
6. For mapping methods (`Map...ToEntity`, `Map...ToEvent`, or whatever the adapter calls them): extract field-by-field assignments and render them as the mapping table in `events.md` §3. Flag any `dynamic` or untyped access.
7. Find the retry policy configuration. Default pattern: `IRetryPolicyProvider`, `DefaultRetryPolicyProvider`, or a `subscriber.RetryPolicies(...)` block inside `AddNimBusSubscriber`. Populate TDD §4.3 with the matched-fault predicates and the backoff parameters. If the adapter uses Polly directly (instead of NimBus retry), document that and flag it as an alignment risk.
8. Find the missing-reference stance: handlers that throw on null lookups use **Throw** (NimBus marks the message as failed and routes through Resolver); handlers that log-and-return use **Silent-skip**. Populate TDD §4.4.
9. Find Infra/orchestration. Search order: `samples/**/*AppHost*` for Aspire, then `infra/`, `bicep/`, `terraform/`, `azure.yaml`, `deploy/`. Populate TDD §2.3 (Azure resources) from resource declarations or Aspire `AddProject` / `AddAzureServiceBus` / `AddAzureCosmosDB` calls.
10. Find the companion code review file, e.g. `{Adapter}-review.md` in the same docs folder or parent. If present, populate TDD §8 from it.
11. Leave every `[HUMAN]` section as a prompt, not as silence. Each one must end with a `> TODO(human): ...` callout describing what information is needed.
12. Render C4 diagrams from the composition root. A worker / Functions app / Container App in the deployment root = a `Container` box. NimBus Service Bus namespace, Cosmos DB account, Key Vault, source/target APIs = external `System_Ext`. The NimBus WebApp + Resolver are typically external from the adapter's point of view.
13. Render sequence diagrams for the primary integration (§5.2): start from the handler entry point, follow calls into repositories/clients/`IPublisherClient`, and draw a participant per external system touched (including NimBus).
14. Return a summary with: which sections are complete, which are flagged for human input, and any review findings the skill noticed while reading the code (events published but not declared in `Produces<T>()`, untyped handlers, missing Managed Identity role assignments, PII in log messages, missing `[SessionKey]` on events that need ordering, Polly used instead of NimBus retry).

## Mode 3 — update-from-diff (existing adapter and existing TDD)

**Inputs:** path to repo, path to existing `docs/TDD.md` and `docs/events.md`.

**Checklist:**

1. Re-run steps 2–9 from Mode 2 to build a fresh snapshot of the code's view of the adapter.
2. Diff against the documented state:
   - Events present in code but not in TDD §3 / `events.md`.
   - Events documented but no longer present in code (deleted handler or removed `Consumes<>` / `Produces<>`).
   - Fields added, renamed, or removed on event classes.
   - `[SessionKey]` added, removed, or moved between properties.
   - Mapping changes (new field assigned, removed assignment, changed resolver/lookup).
   - New Azure resources or new endpoints in IaC / Aspire AppHost.
   - Retry policy changes (predicates, attempts, backoff).
   - Pipeline behaviour changes (`AddPipelineBehavior<T>()` registered / removed).
3. For each diff item, propose a targeted edit:
   - Add / remove rows in TDD §3.x and `events.md` §1.x.
   - Add / remove fields in `events.md` §2.
   - Update the mapping table in `events.md` §3.
   - Adjust the sequence diagram only if the call graph materially changed; do not re-draw cosmetic differences.
4. **Never touch `[HUMAN]`-only sections** (TDD §3.3 triggers, §5.1 functional description, §5.3 NFRs, §7 adapter NFRs, §11 governance, appendix A document history) unless the user explicitly asks to. If a change in code *should* trigger a human update (e.g. a new consumed event needs a trigger cadence), add a `> TODO(human): ...` callout next to the auto-applied row.
5. Append an entry to the events-doc change log (§4) for every event-field change, with the PR link if detectable.
6. Return a summary: what was updated, what was flagged for human confirmation, what was deleted.

**Safety rule.** In update mode, always dry-run first. Show the user the list of proposed edits; only apply after confirmation.

## Outputs

- `docs/TDD.md` — primary document.
- `docs/events.md` — event catalog + mappings.
- `docs/{diagrams}/` (optional) — if any diagram outgrows the Mermaid block, keep the authoritative `.mermaid` or `.drawio` source file here and embed rendered PNGs.

## Reporting back to the user

After any run, return:

1. The mode used.
2. A short bullet list of what was created or changed.
3. A short bullet list of `[HUMAN]` items that still need attention, with anchor links (`TDD.md#section-id`).
4. Any risks surfaced (drift between code and docs, missing contracts, review findings).

Keep the report terse. The user can read the files; they need the hook, not a re-narration.

## When not to use this skill

- To document NimBus itself (the platform — `NimBus.Core`, `NimBus.ServiceBus`, `NimBus.SDK`, `NimBus.Resolver`, `NimBus.WebApp`). Those are platform documents and live under `docs/` in the NimBus repo (`architecture.md`, ADRs, `sdk-api-reference.md`).
- To run a code review of the adapter — use the code review workflow (`/review`, `/security-review`) and let it feed the TDD §8 table.
- To create or update a higher-level domain / system design document — that is owned by enterprise architecture and lives above the adapter level.
