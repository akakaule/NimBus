# Feature Specification: AI Integration Mapper (agent-authored, platform-executed event mappings)

Feature Branch: `023-ai-integration-mapper`
Created: 2026-06-07
Updated: 2026-06-07
Status: Draft — design approved via brainstorming; not yet implemented. Builds on [spec 022](../022-ai-agent-bus-participation/spec.md).
Input: "Based on spec 022 (AI agents as bus participants), what other use cases exist for agents in a NimBus setup?" — selected use case: an **AI integration mapper** that translates one system's event contract into another's at runtime, with no hand-written integration code.

> **How to read this document.** The [Overview](#overview-plain-language) and [What changes](#what-changes-at-a-glance) are written for anyone. The sections after that get progressively more technical. Terms in **bold** on first use are defined in the [Glossary](#glossary).

---

## Overview (plain language)

Spec 022 made it possible for an AI agent to join the bus as a participant. This feature uses that foundation for the job an integration platform exists to do: **making two systems that disagree about message shape talk to each other — without anyone writing integration code.**

The core idea is a deliberate split of labour:

- **The LLM thinks once.** When a new integration is needed (system A's event must become system B's event), an AI agent reads both contracts, looks at sample messages, and **authors a reusable mapping** — a declarative transform from A's shape to B's shape.
- **A human approves it.** The proposed mapping, its rationale, and worked examples (a real source message and the output it produces) are shown in the WebApp. An operator approves it before it touches live traffic.
- **The platform does the work forever.** Once approved, a first-class NimBus component applies that mapping to every matching message — deterministically, cheaply, with **no LLM call at runtime** — validating each result against the target contract and delivering it downstream, all on NimBus's existing routing, ordering, recovery, and audit.

To make it concrete, we extend **CrmErpDemo** with a new **Marketing** source app that emits leads in its own shape. The mapper translates them into the canonical ERP customer contract. The agent proposes the mapping, an operator approves it, and from then on every marketing lead lands in ERP correctly — no code written or deployed.

This is the second sub-project built on spec 022's foundation (schema registry, dynamic-typed routing, Agent Zone, MCP, pending-handoff recovery, audit). A later spec can lift the constraint that both contracts already be registered (see [Future sub-projects](#future-sub-projects)).

---

## What changes at a glance

**New projects / components**
- **Mapping Executor** — a `NimBus.SDK`-based hosted worker (new) that applies approved mappings at runtime. The only new component in the message hot path.
- **`MappingAgent`** (in `samples/CrmErpDemo/`) — a scripted agent that authors mappings, so the demo is reproducible without a human driving a chat client.
- **Marketing source app** (in `samples/CrmErpDemo/`) — emits `marketing.lead.created.v1` in its own contract.

**Changed**
- `src/NimBus.MessageStore.Abstractions/` — add `IEventMappingStore` (the mapping registry contract) + the `EventMapping` record. *Mirrors `IEventSchemaStore` from spec 022.*
- `src/NimBus.MessageStore.CosmosDb/`, `…SqlServer/`, and the in-memory store — implement `IEventMappingStore`; add it to the storage **conformance suite**.
- `src/NimBus.WebApp/api-spec.yaml` — add `/api/agent/mappings/*` endpoints (NSwag-generated C# contract + TS client).
- `src/NimBus.WebApp/Controllers/ApiContract/…` — implement the mapping endpoints; add a **Mappings** review page to `ClientApp/`.
- `src/NimBus.Mcp/` — add `propose_mapping` (and `list_mappings`) tools, 1:1 over the new REST endpoints.
- `samples/CrmErpDemo/CrmErpDemo.Contracts/` + `CrmErpDemo.AppHost/` — register the Marketing source + canonical ERP customer contracts; run the Marketing app, `MappingAgent`, and Mapping Executor as Aspire resources.
- `docs/` — this spec + a short usage guide.

**Deliberately unchanged:** the spec-022 agent API, Agent Zone, schema registry, and MCP server are reused as-is. No production auth (inherits spec-022 demo-grade `X-Agent-Id`). No new transport.

---

## Design decisions (made during brainstorming)

These six decisions scope this spec. Each was chosen deliberately over the alternatives noted.

| # | Decision | Chosen | Rejected alternatives |
|---|---|---|---|
| 1 | **Use case** | **AI integration mapper** (translate event contracts) | Ops triage copilot; unstructured→structured extractor; multi-agent pipeline |
| 2 | **Mapping strategy** | **Author once, apply deterministically** — LLM authors a reusable artifact; runtime applies it with no LLM call | LLM transforms every message; hybrid deterministic + LLM fallback |
| 3 | **Artifact form** | **Declarative transform (JSONata-style)**, executed by a sandboxed engine | Field-mapping table + coercions only; LLM-generated executable code |
| 4 | **Promotion** | **Human review & approve** (Draft→Active), with worked examples | Auto-promote with guardrails; fully autonomous |
| 5 | **Discovery** | **Registry-driven** — source + target both registered in the spec-022 schema registry; operator supplies an intent note | Sample-driven source inference; operator-provided schemas |
| 6 | **Executor** | **First-class Mapping Executor** (reusable NimBus.SDK worker) | Demo-scoped executor; agent applies it in the hot path |

**Assumptions** (flag if wrong):
- **LLM = Claude** via the Anthropic API; used only during authoring (the agent is external/MCP, so the model is swappable). Runtime never calls an LLM.
- **Source and target contracts are both registered** as event types (JSON Schema) before a mapping is authored.
- **One source → one target** per mapping in v1.
- **Auth = demo-grade**, inherited from spec 022 (`X-Agent-Id`); production hardening is out of scope.

---

## Problem

NimBus exists to integrate systems, but every integration today requires a developer to write and deploy a C# handler that reads one event type and produces another. When two systems disagree about message shape — different field names, nesting, units, or derived fields — that mismatch is reconciled only by hand-written, hand-deployed code. There is:

- **No way to introduce an integration without code.** Even a trivial field rename needs a compiled handler.
- **No reusable, auditable mapping artifact.** The mapping logic is buried in imperative code, not a reviewable declaration.
- **No safe place for AI to help.** Spec 022 lets an agent participate, but an LLM transforming every message in the hot path is expensive, non-deterministic, and hard to audit — unacceptable for production integration data.

The result: NimBus can host agents, but it can't yet let an agent do the platform's own core job — connecting mismatched systems — safely.

---

## Scope

**In scope (v1)**
- A **mapping registry** (`IEventMappingStore` + Cosmos/SQL/in-memory implementations) holding agent-authored, declarative **JSONata** transforms keyed by `(sourceEventTypeId → targetEventTypeId)`, with a lifecycle state and a `sourceSchemaHash` for drift detection.
- A **mapping API** (`/api/agent/mappings/*`): propose (agent), list/get, approve/reject/pause/resume (operator), described in `api-spec.yaml`.
- A **Mapping Executor**: a `NimBus.SDK` hosted worker that registers a dynamic handler (spec-022 `AddDynamicHandler`) for each source type with an **Active** mapping, applies the transform, validates the output against the target schema, publishes the target event, and parks failures via the existing pending-handoff path.
- A **sandboxed JSONata engine** for deterministic JSON→JSON transformation.
- **MCP tools** `propose_mapping` and `list_mappings`, 1:1 over the REST endpoints.
- A **WebApp Mappings page**: review queue (draft + rationale + worked examples), approve/reject, and an active/stale/paused view with drift alerts.
- A **demo**: a Marketing source app + `MappingAgent` joining CrmErpDemo end-to-end, runnable from the Aspire AppHost against the Service Bus emulator.
- **Tests** at unit, contract, MCP-mapping, integration, and demo-smoke levels.

**Out of scope (future — see [Future sub-projects](#future-sub-projects))**
- Sample-driven source **schema inference** (unregistered/legacy sources).
- N→1 / 1→N / many-to-many mappings.
- **LLM self-repair** of failing mappings (auto re-author).
- Auto-promotion (removing the human gate).
- Mapping versioning/compatibility rules beyond re-authoring.
- Non-JSON formats.
- Production authentication/authorization, quotas, billing (inherits spec-022 demo-grade identity).

---

## Architecture

```
  Authoring (cold path — LLM runs here, once)        Execution (hot path — NO LLM)
  ──────────────────────────────────────────        ─────────────────────────────
   MappingAgent (Claude, external/MCP)                source event published
     reads source+target JSON Schemas + samples              │
     from /api/agent/catalog                                 ▼
          │ propose_mapping (MCP → REST)             ┌──────────────────────────┐
          ▼                                          │  Mapping Executor         │  NEW
   ┌──────────────────────────┐                      │  (NimBus.SDK worker)      │
   │  Mapping registry          │  reads Active      │  1 validate input + hash  │
   │  IEventMappingStore        │◄───────────────────│  2 apply JSONata transform│
   │  (Cosmos / SQL / InMemory) │                    │  3 validate vs target     │
   │  Draft/Approved/Active/    │                    │  4 publish target event   │
   │  Stale/Paused              │                    │  5 settle / audit         │
   └──────────▲────────────────┘                     └────────────┬─────────────┘
              │ approve/reject/pause/resume                        ▼
     WebApp "Mappings" review page                       target consumer (existing)
     (operator: transform + rationale +                  full chain audited:
      worked examples → approve)                          source → mapper → target
```

**Why author-once / platform-executes.** Putting an LLM in the per-message path is expensive, non-deterministic, and unauditable. By having the LLM emit a **declarative artifact** that the platform applies deterministically, the runtime is cheap and reproducible, the artifact is reviewable and version-pinned, and every transformed message is gated by the **target JSON Schema** before it ships. The Executor is just another bus participant, so it inherits ordering, recovery, and audit for free.

### The Mapping Executor (the novel part)

- For each mapping in state **Active**, the Executor registers a **dynamically-typed handler** (spec-022 `AddDynamicHandler(sourceEventTypeId, …)`) on the source endpoint/Agent Zone.
- On each message it (1) validates the input against the source schema and compares `sourceSchemaHash`; (2) applies the JSONata transform; (3) validates the output against the **target** JSON Schema; (4) publishes the target event with its `EventTypeId` set for routing (reusing spec-022 dynamic routing); (5) settles via the Resolver/handoff path.
- The Executor **never calls an LLM**. It loads transforms from the registry and caches Active mappings, refreshing on lifecycle changes.

### Mapping registry

`IEventMappingStore` (new, in `MessageStore.Abstractions`) stores one record per mapping, mirroring `IEventSchemaStore`:

```
EventMapping {
  id                 // e.g. "marketing.lead.created.v1->erp.customer.upsert.v1"
  sourceEventTypeId  // registered source type
  targetEventTypeId  // registered target type
  transform          // JSONata expression (the reusable artifact)
  rationale          // LLM's short explanation, for review
  workedExamples     // [{ sourceSample, output }] computed at authoring time
  sourceSchemaHash   // fingerprint of the source schema at authoring (drift detection)
  state              // Draft | Active | Paused | Stale | Rejected
  version            // bumped on each re-author
  createdBy, createdUtc      // the authoring agentId
  approvedBy, approvedUtc    // the approving operator (null until approved)
}
```

It is implemented against the existing storage providers and runs through the storage **conformance suite**, exactly like the schema registry.

### Lifecycle

```
Draft ──approve──► Active ──source drift──► Stale (paused) ──re-author──► Draft
  │                  │
  │                  └──operator pause/resume──► Paused ──► Active
  └──reject──► Rejected
```

- **Draft** — authored, awaiting review. **Active** — applied to live traffic. **Paused** — operator-suspended (messages park, not dropped). **Stale** — source schema changed; auto-paused; agent asked to re-author. **Rejected** — operator declined, with feedback.

---

## Data flow — the demo walkthrough (happy path)

**Authoring (once):**

1. An operator (or the agent autonomously) declares: map `marketing.lead.created.v1` → `erp.customer.upsert.v1`, with a one-line intent note.
2. The `MappingAgent` reads both JSON Schemas from `GET /api/agent/catalog` and pulls a few sample source messages.
3. It asks Claude (structured output) for a **JSONata transform** + rationale, then runs the transform against the samples to produce **worked examples**.
4. It calls `propose_mapping` → `POST /api/agent/mappings`, storing the transform, rationale, worked examples, and `sourceSchemaHash`. State = **Draft**.
5. The draft appears on the WebApp **Mappings** page with the transform, rationale, and worked examples side-by-side.
6. The operator approves → `POST /api/agent/mappings/{id}/approve`. State = **Active**; the Executor registers a handler for the source type.

**Runtime (every message, no LLM):**

7. The Marketing app publishes `marketing.lead.created.v1`.
8. The Mapping Executor receives it, validates the input + hash, applies the transform, and validates the output against the ERP customer schema.
9. It publishes `erp.customer.upsert.v1`; the existing ERP/DataPlatform consumer writes it downstream.
10. **Observe:** the WebApp dashboard shows the full chain — `marketing.lead.created.v1` → mapper → `erp.customer.upsert.v1` — with audits and metrics; the ERP UI shows the new customer. A coded service → AI-authored mapping → coded service, in one trail.

---

## Error handling

| Situation | Behavior |
|---|---|
| Transformed output fails the **target** schema | Park as a failed handoff with the validation errors + offending payload; **nothing is published**; surfaced in the WebApp for resubmit/skip (reuses spec-022 Resolver/audit/handoff). |
| Transform **throws** at runtime (unexpected input) | Same park-and-recover path; the offending message is preserved. |
| **Source schema drift** (`sourceSchemaHash` mismatch / input no longer schema-valid) | Mark the mapping **Stale**, pause it, raise a drift alert on the Mappings page, and request re-author → back through approval. In-flight messages **park, not drop**. |
| **No Active mapping** for a received source type | The Executor ignores it; normal NimBus routing applies. |
| `propose_mapping` for an unknown source/target event type | `404` — both contracts must be registered first. |
| Low-confidence authoring | The agent still submits a Draft but flags low confidence; the operator sees the flag before approving. |
| LLM unavailable during authoring | Authoring fails gracefully; **runtime is unaffected** (it never calls the LLM). |
| Approving a Draft whose source schema already drifted | The approve call re-checks the hash and routes it to **Stale** instead of Active, prompting re-author. |

The principle mirrors spec 022: **reuse NimBus's existing Resolver, audit, and handoff semantics** rather than inventing mapper-specific failure handling.

---

## Testing

- **Unit** — `IEventMappingStore` runs through the storage **conformance suite** across all 3 backends (mirrors `EventSchemaStoreConformanceTests`). JSONata apply (mapping + input → exact expected output), the target-schema output gate, drift detection (`sourceSchemaHash` mismatch → Stale), and every lifecycle transition are tested in isolation.
- **Contract** — `api-spec.yaml` mapping endpoints round-trip through NSwag; the generated C# contract and TS client compile (existing build gate).
- **MCP mapping** — `propose_mapping`/`list_mappings` call the correct REST endpoints with the correct arguments, against a mock server.
- **Integration** (in-memory + EndToEnd style) — publish source → Executor applies the Active mapping → target published and schema-valid → audit shows source→mapper→target completed; invalid output → parked failed handoff; drift → Stale.
- **Demo smoke** — the Marketing app + scripted `MappingAgent` exercise the full chain against the AppHost; asserts the upserted ERP customer and the completed audit trail.
- **WebApp** — vitest for the Mappings review page: renders a draft + worked examples; approve transitions state.

A change is not done until the relevant tests pass.

---

## Demo acceptance flow

1. Start the Aspire AppHost (with the Marketing app, `MappingAgent`, and Mapping Executor wired in).
2. Confirm `marketing.lead.created.v1` and `erp.customer.upsert.v1` are registered event types.
3. Trigger (or let the agent trigger) authoring of the mapping; confirm a **Draft** appears on the Mappings page with transform + worked examples.
4. Approve the mapping in the WebApp; confirm state → **Active**.
5. Emit a marketing lead; confirm the Executor transforms it and publishes `erp.customer.upsert.v1`.
6. Confirm the ERP/DataPlatform consumes it and the ERP UI shows the new customer.
7. Confirm the WebApp dashboard shows the full source→mapper→target audit trail.
8. Negative path: emit a lead that produces target-invalid output; confirm it is parked and recoverable (not delivered malformed).
9. Drift path: change the source schema; confirm the mapping goes **Stale** and pauses until re-authored & re-approved.

---

## Success criteria

1. With **no code written**, an agent authors a JSONata mapping from a registered source type to a registered target type; an operator approves it in the WebApp; subsequent source messages are automatically transformed, validated, and delivered to the target consumer — all audited end-to-end.
2. An invalid transformed message is **parked and recoverable**, never silently dropped or delivered malformed.
3. Changing the source schema marks the mapping **Stale** and pauses it until re-authored & re-approved.
4. The Executor calls **no LLM at runtime** (deterministic, cheap).
5. All tests pass at unit / contract / MCP / integration / demo levels.

---

## Future sub-projects

Each is its own spec → plan → implementation cycle, built on the mapping registry + Executor delivered here.

- **Sample-driven source inference** — connect a new/legacy system whose source schema is not registered; the agent infers it from observed messages. The most-requested next step.
- **Complex cardinalities** — N→1 (merge), 1→N (fan-out/split), and many-to-many mappings.
- **LLM self-repair** — on a transform/validation failure, automatically re-author with the failing example and re-submit for approval.
- **Auto-promotion with guardrails** — shadow-run + schema-validate a new mapping and promote without a human gate when confidence is high.
- **Mapping evolution** — versioning and compatibility rules so a mapping can evolve in place rather than via full re-author.
- **Non-JSON formats** — XML/CSV/flat-file sources and targets.

---

## Glossary

- **Mapping** — a reusable, declarative transform from a source event type to a target event type, authored by an agent and stored in the mapping registry.
- **Mapping registry** — the store (`IEventMappingStore`) holding mappings and their lifecycle state.
- **Mapping Executor** — the NimBus.SDK worker that applies Active mappings to messages at runtime (no LLM).
- **JSONata** — a declarative JSON query-and-transformation language; the form of the mapping artifact.
- **Worked example** — a real source sample plus the output the proposed transform produces, shown to the operator during review.
- **Source schema drift** — a change to the source event type's schema after a mapping was authored against it; detected via `sourceSchemaHash` and resolved by re-authoring.
- **Author once, apply deterministically** — the strategy of using the LLM to produce a reusable artifact (once) rather than transforming each message (every time).

---

## Open questions

1. **JSONata library** — select a maintained .NET JSONata evaluator (e.g. `Jsonata.Net.Native`) and confirm availability, license, and sandboxing/timeout behavior before implementation. If none is suitable, fall back to a constrained mapping DSL that compiles to a deterministic transform.
2. **Drift sensitivity** — `sourceSchemaHash` is an exact-match fingerprint, so any source-schema edit (even an additive, compatible one) marks a mapping Stale. Acceptable for v1; a compatibility-aware check is a candidate for the mapping-evolution future spec.
3. **Worked-example sourcing** — pull sample source messages from live/recent traffic (the message store) or require the operator to supply representative samples at authoring time? Leaning store-sourced with an operator override.
