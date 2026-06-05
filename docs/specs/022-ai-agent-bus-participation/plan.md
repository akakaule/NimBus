# Spec 022 — Master Implementation Plan (whole spec)

This is the top-level roadmap for delivering **all in-scope** of spec 022 (`spec.md`). It maps the spec to four phases, tracks status, records the cross-cutting gaps discovered during research, and links the per-phase plans. Each phase is its own plan document with bite-sized, TDD-oriented tasks.

> **Execution model:** subagent-driven (a fresh subagent per task, two-stage review). Work happens on `master` (trunk-based, per user preference). Each task ends in a small, targeted commit.

---

## Scope of "the entire specification"

In scope for spec 022 (this roadmap delivers all of it):
- **Phase 0** — dynamic-routing gate (prove a classless `EventTypeId`-only event routes end-to-end).
- **Phase 1** — capability core: `IEventSchemaStore` schema registry (InMemory/Cosmos/SQL) + the `/api/agent/*` REST API.
- **Phase 2** — `NimBus.Mcp`: a thin stdio MCP server wrapping the REST API 1:1.
- **Phase 3** — the demo: an `EnrichmentAgent` joins CrmErpDemo end-to-end on the Aspire AppHost, plus a smoke test, plus live SB-emulator routing fidelity.

**Explicitly OUT of scope** (the spec's "Future sub-projects" — each its own future spec→plan→build): the reusable `NimBus.Agents` .NET SDK; the ops/SRE agent; fully-dynamic runtime topology; push/streaming delivery; production auth (scoped API keys/OAuth, quotas).

---

## Status

| Phase | Deliverable | Plan | Status |
|---|---|---|---|
| 0 | Dynamic-routing gate | (in `spec.md` → "Phase 0 result") | ✅ **Done & committed** (`21b3174`) |
| 1 | Schema registry + agent REST API | [`plan-phase1.md`](plan-phase1.md) | ✅ **Done** — all 14 tasks committed; full solution test sweep green |
| 2 | `NimBus.Mcp` server | [`plan-phase2-mcp.md`](plan-phase2-mcp.md) | ✅ **Done** — 6 tasks committed; 36 MCP tests green; Release build clean |
| 3 | EnrichmentAgent demo + AppHost + smoke + emulator fidelity | [`plan-phase3-demo.md`](plan-phase3-demo.md) | ⏳ **Planned, not started** |

Phase dependencies: 0 → 1 → 3 (the demo needs the REST core). Phase 2 (MCP) depends on Phase 1 and can run in parallel with Phase 3; the demo runs **REST-direct** for determinism and treats MCP as a later swap (see Phase 3 plan, decision on `IBusGateway`).

---

## Cross-cutting gaps discovered during research (addressed in the phases noted)

These were found while planning Phases 2–3; they are real and must be handled for the demo to work end-to-end:

1. **Dynamic-event OUTBOUND routing is not auto-provisioned.** `Endpoint.Produces<T>/Consumes<T>` require a *compiled* `IEvent`, and both topology generators (`EmulatorTopologyConfigBuilder`, `ServiceBusTopologyProvisioner`) derive `user.EventTypeId='…'` forward rules only from compiled `EventTypesProduced`/`GetConsumers`. So an agent-published `crm.contact.enriched.v1` on the Agent Zone topic matches **no** forward subscription and reaches no consumer. Phase 0 proved the *routing mechanism* (a property filter routes a classless event — true and unchanged); what's missing is the *rule that auto-generates for a dynamic id*. **Addressed in Phase 3, Task A** (emulator topology emits an explicit forward rule for the dynamic id). The production `ServiceBusTopologyProvisioner` has the same gap — **noted as deferred** (real-Azure runs need the parity fix; out of scope for the emulator demo, flagged so it isn't silently broken).
2. **No hosted API to register a string-keyed (dynamic) handler.** `EventHandlerProvider.RegisterHandler(string, Func<IEventJsonHandler>)` exists (Phase 0), but `NimBusSubscriberBuilder` only exposes typed `AddHandler<TEvent,THandler>` — a hosted subscriber can't register a raw-JSON dynamic handler. **Addressed in Phase 3, Task B** (`NimBusSubscriberBuilder.AddDynamicHandler(string, Func<IEventJsonHandler>)`).
3. **No running Agent Zone park subscriber.** Parking (`MarkPendingHandoff`) only happens in in-memory tests; nothing hosts the `AgentZoneEndpoint` subscriber so `/api/agent/receive` has parked rows to return. **Addressed in Phase 3, Task C** (a hosted park subscriber; the `CrmContactCreated` park handler can be a *typed* `IEventHandler<CrmContactCreated>` calling `MarkPendingHandoff`, sidestepping gap #2 for the park side).

---

## Definition of done (spec success criteria → where satisfied)

1. *An external MCP client can complete discover→define→subscribe→receive→publish→settle with no NimBus C# written* → Phase 2 (MCP tools) over Phase 1 (REST).
2. *CrmErpDemo runs end-to-end from the Aspire AppHost: a CRM contact yields an LLM-enriched `crm.contact.enriched.v1` landing in ERP/DataPlatform, visible in the dashboard* → Phase 3.
3. *The OpenAPI spec describes every agent capability* → Phase 1, Task 8 (`api-spec.yaml`).
4. *All tests pass; the routing spike is confirmed* → Phase 0 (spike) + each phase's tests + Phase 3 emulator smoke.

---

## How to run the rest

Continue subagent-driven execution per phase plan. Order: finish **Phase 1 (T12–T14)** → **Phase 2** and **Phase 3 Tasks A–C** (the SDK/topology enablers) can interleave → **Phase 3 Tasks D–H** (classifier, runner, AppHost, smoke, docs). Each task: implement → spec-compliance review → code-quality review → targeted commit.
