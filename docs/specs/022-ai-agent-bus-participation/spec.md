# Feature Specification: AI Agents as Bus Participants (MCP + Agent-Ready API)

Feature Branch: `022-ai-agent-bus-participation`
Created: 2026-06-05
Updated: 2026-06-05
Status: Draft (brainstorming)
Input: User description: "I would like to have NimBus be an AI-enabled platform — make it easy for AI agents to interact with the platform: subscribe to existing events, define and create their own event types, register endpoints, publish and process messages, and hand over data to other systems (a REST interface with an OpenAPI spec, an MCP server, a CLI tool). Build a tech demo showcasing this. Also consider an SDK similar to https://github.com/microsoft/agents for building NimBus-enabled agents — including agents that monitor endpoints, troubleshoot failed messages, fix bugs, create PRs."

> **How to read this document.** The [Overview](#overview-plain-language) and [What changes](#what-changes-at-a-glance) sections are written for anyone — no NimBus internals required. The sections after that get progressively more technical for whoever implements it. Terms in **bold** on first use are defined in the [Glossary](#glossary).

---

## Overview (plain language)

Today NimBus is a messaging platform for **coded** services: a developer writes a C# service, references the SDK, and that service can publish and consume events. An AI agent (say, a Claude-powered assistant) cannot join the bus — there is no door built for it.

This feature builds that door. After it ships, an external AI agent can:

1. **Discover** what's on the platform — which endpoints exist, what event types they exchange.
2. **Define its own event type** at runtime — describe the shape of a new message as a JSON Schema, without anyone writing or compiling C#.
3. **Subscribe** to an existing event type and **receive** matching messages.
4. **Publish** its own messages (validated against the schema it defined).
5. **Settle** (mark done/failed) the work it received. Carrying results onward to external systems is done by existing coded participants, not the agent (see [Handoff in v1](#handoff-in-v1)).

> **A note on wording.** Where the original request says agents *register endpoints*, in v1 that means **creating logical subscriptions within a pre-provisioned Agent Zone** — agents do not create physical Service Bus endpoints (see decision #4).

It does all of this through a **Model Context Protocol (MCP) server** — the standard way LLM agents call tools — which sits on top of an **agent-ready REST API** with an OpenAPI spec. So both interfaces the user asked for (MCP *and* REST/OpenAPI) come from one body of work.

To make it concrete, we extend the existing **CrmErpDemo** sample with one new actor: an **AI enrichment agent**. When a contact is created in the CRM, the agent wakes up, uses Claude to classify the contact (industry, lead score), defines a `crm.contact.enriched.v1` event type the first time it runs, publishes the enriched result, and the ERP/DataPlatform picks it up — exactly like any other participant on the bus, except it's an LLM, not hand-written code.

This is the **first** of several sub-projects. A reusable .NET "NimBus agent SDK" and an autonomous **ops/SRE agent** (monitors endpoints, triages failed messages, opens PRs) are explicitly deferred — see [Future sub-projects](#future-sub-projects). They are designed to sit on the same foundation this spec builds.

---

## Design decisions (made during brainstorming)

These five decisions scope this spec. They were chosen deliberately over the alternatives noted.

| # | Decision | Chosen | Rejected alternatives |
|---|---|---|---|
| 1 | **Demo spine** | Agents as **bus participants** (NimBus is a communication fabric *for* agents) | Self-healing/SRE agent; both at once |
| 2 | **Demo scenario** | An AI agent **joins CrmErpDemo** (enrichment agent) | Greenfield multi-agent pipeline; minimal proof-of-capability |
| 3 | **Primary interface** | **MCP-first** — the agent is external; the MCP server is the hero | .NET SDK-first hosted agent; both as co-heroes |
| 4 | **Registration model** | **Hybrid** — dynamic event types (JSON-schema registry) on a **pre-provisioned agent zone**; routing by event-type filter | Fully dynamic runtime Service Bus topology; fully pre-provisioned |
| 5 | **Architecture** | **MCP-over-REST** — thin MCP server wrapping an agent-ready REST API (one capability core) | MCP-direct via SDK; MCP embedded inside the WebApp |

**Assumptions** (flag if wrong):
- **LLM = Claude** via the Anthropic API. The agent is external/MCP, so the model is swappable.
- **Processing = pull-based.** MCP is request/response, so the agent loop is `receive → reason → publish → settle`. There is no push/webhook subscription in this cycle.
- **Auth = demo-grade** (API key / localhost). Production hardening is noted but out of scope.

---

## What changes at a glance

**New projects**
- `src/NimBus.Mcp/` — standalone MCP server (C#, ModelContextProtocol SDK). Stdio transport first (works with Claude Code / Claude Desktop); HTTP/SSE optional. Contains **no business logic** — every tool is a thin wrapper over a REST endpoint.
- `samples/CrmErpDemo/EnrichmentAgent/` — a small, scripted agent runner so the demo is reproducible without a human driving a chat client.

**Changed**
- `src/NimBus.WebApp/api-spec.yaml` — add the `/api/agent/*` endpoints (this is the OpenAPI surface; the C# contract + TS client are NSwag-generated from it at build).
- `src/NimBus.WebApp/Controllers/ApiContract/…` — implement the new endpoints.
- `src/NimBus.MessageStore.Abstractions/` — add `IEventSchemaStore` (the schema registry contract).
- `src/NimBus.MessageStore.CosmosDb/` (and `…SqlServer/`) — implement `IEventSchemaStore`.
- `samples/CrmErpDemo/CrmErpDemo.Contracts/` — add the **Agent Zone** endpoint to the platform config + a subscription to the existing `CrmContactCreated`; add a handler for the agent-published `crm.contact.enriched.v1` event.
- `samples/CrmErpDemo/CrmErpDemo.AppHost/` — run `NimBus.Mcp` and `EnrichmentAgent` as Aspire resources.
- `docs/` — this spec, plus a short MCP usage guide (`docs/mcp-server.md`).

**Deliberately unchanged in this cycle:** Service Bus topology stays code-first/pre-provisioned; no `NimBus.Agents` .NET SDK; no ops/SRE agent; no production auth.

---

## Problem

NimBus's strengths — session-ordered processing, a centralized Resolver with a full audit trail, declarative topology — are reachable only by services written against the .NET SDK. An AI agent has no first-class way to participate:

- **No discovery surface for agents.** The topology exists as C# `PlatformConfiguration` and as `nb catalog asyncapi` / EventCatalog exports, but nothing presents it to an agent as callable tools.
- **No runtime event-type definition.** Event types are C# classes compiled into the platform config. An agent cannot introduce a new message contract without a human writing and deploying code.
- **No agent-shaped read/write API.** The WebApp REST API already exposes search, metrics, blocked/pending events, and resubmit — but not "publish an arbitrary event," "register an event-type schema," or "pull-and-settle messages," and it is not packaged as MCP tools.
- **No reproducible demo** that shows an LLM joining a real integration as a peer of the coded services.

The result: NimBus cannot be described as "AI-enabled," and there is nothing to show a stakeholder.

---

## Scope

**In scope**
- An **agent-ready REST API** (`/api/agent/*`) added to `NimBus.WebApp`, described in `api-spec.yaml`: discover catalog, define event type, subscribe, receive (long-poll pull), publish, settle.
- A **schema registry** (`IEventSchemaStore` + Cosmos/SQL implementations) holding agent-defined event types as JSON Schema, with payload validation on publish.
- A **pre-provisioned Agent Zone** topic/endpoint in the CrmErpDemo topology; dynamically-typed events route to subscribers by an `EventTypeId` message property using NimBus's existing rule-based filtering.
- A **`NimBus.Mcp` server** exposing tools 1:1 over the REST endpoints, plus read tools that reuse existing endpoints (`search_failures` → `messages/search`/`audits/search`).
- A **demo**: the enrichment agent joining CrmErpDemo end-to-end, runnable from the Aspire AppHost against the Service Bus emulator.
- **Tests** at unit, contract, MCP-mapping, and integration levels, plus a demo smoke test.

**Out of scope**
- Runtime mutation of Service Bus topology by agents (creating topics/subscriptions live).
- The reusable `NimBus.Agents` .NET SDK for hosted/embedded agents.
- The ops/SRE agent (monitor, troubleshoot, fix bugs, open PRs).
- Push/streaming delivery to agents (webhooks, server-initiated SSE message push).
- Production authentication/authorization, multi-tenant quotas, billing.
- A bespoke agent UI (the existing WebApp dashboard + CRM/ERP UIs are sufficient to observe the demo).

---

## Architecture

```
  External LLM agent (Claude + MCP client)          ← swappable; any MCP client
        │  stdio / HTTP
        ▼
  ┌─────────────────┐   tools 1:1 → REST
  │  NimBus.Mcp     │   discover_topology, define_event_type, subscribe,
  │  (new project)  │   receive_messages, publish_event, settle_message,
  │                 │   search_failures
  └────────┬────────┘
           │ HTTPS + API key
           ▼
  ┌──────────────────────────── NimBus.WebApp (capability core) ───────────────────────┐
  │  NEW  /api/agent/catalog · /event-types · /subscribe · /publish · /receive · /settle │
  │  REUSE /api/messages/search · /audits/search · /metrics/* · /event/blocked           │
  │            │                        │                         │                      │
  │            ▼                        ▼                         ▼                      │
  │   IEventSchemaStore         IPublisherClient            Resolver / IHandoffClient    │
  │   (new schema registry)     (publish to Agent Zone)     (settle pending audits)      │
  └────────────┼────────────────────────┼──────────────────────────────────────────────┘
               ▼                         ▼
        MessageStore (Cosmos/SQL)   Azure Service Bus  ──►  "Agent Zone" topic
                                        (EventTypeId filter routing → subscribers)
```

**Why MCP-over-REST.** The REST API is the single capability core. The MCP server is a thin, swappable adapter, and the later .NET agent SDK will also be a client of the same API. One source of truth means MCP and REST cannot drift, every capability is independently testable as an HTTP endpoint, and the user's "REST + OpenAPI for agents" ask is satisfied as a side effect of building the MCP server.

### The Agent Zone and event-type routing (the novel part)

NimBus already routes events across endpoints using **subscription rules that filter on event type**. We exploit that instead of mutating topology at runtime:

- A single endpoint, the **Agent Zone**, is added to the CrmErpDemo `PlatformConfiguration` and provisioned by the normal `nb topology apply` flow. It owns one topic.
- Agent-published events are **dynamically typed**: there is no compiled C# class. The event instance carries its `EventTypeId` (e.g. `crm.contact.enriched.v1`) as a message property, and its body is JSON validated against the registered schema.
- Subscribers (other agents, or coded handlers) declare interest in an `EventTypeId`; NimBus's existing rule-based filters forward matching messages to them — the same mechanism used today for cross-endpoint routing.
- Existing compiled events (e.g. `CrmContactCreated`) are forwarded into the Agent Zone via a normal event-type subscription, so the agent can consume them with no special casing.

This delivers the "agent invents its own contract" headline **without** creating Service Bus entities on the fly — the fragile, demo-breaking path we rejected.

> **De-risk early.** This is gated by [Phase 0](#phase-0-dynamic-routing-spike): the routing spike must pass before any REST, MCP, or schema-registry work begins. If it fails, revisit decision #4 before continuing.

### Schema registry

`IEventSchemaStore` (new, in `MessageStore.Abstractions`) stores one record per agent-defined event type:

```
EventSchema {
  id            // e.g. "crm.contact.enriched.v1" (namespaced — see rule below)
  name          // human label
  jsonSchema    // JSON Schema describing the payload
  description   // for discovery / agent context
  sessionKeyPath// optional JSONPath selecting the session key for ordering
  version       // always 1 in v1; see "Schema versioning (v1)"
  agentId       // the agent that created it (see "Agent identity")
  createdBy, createdUtc
}
```

It is implemented against the existing storage providers (Cosmos first, since the demo runs the emulator; SQL for parity). `POST /api/agent/publish` loads the schema by `eventTypeId` and validates the payload before anything touches Service Bus.

**Event-type ID convention.** Agent-defined event type IDs must be globally unique and use a namespace-style convention (e.g. `crm.contact.enriched.v1`). This distinguishes them from the PascalCase compiled event types used by coded services (e.g. `CrmContactCreated`) and avoids collisions between independently-authored agents.

### Schema versioning (v1)

In v1, agent-defined schemas are immutable after creation.

- Calling `define_event_type` with an identical schema is idempotent.
- Calling `define_event_type` with the same id but a different schema returns `409 Conflict`.
- The schema record includes `version = 1`.
- Schema evolution and compatibility rules are deferred to a later spec.

---

## Phase 0: Dynamic routing spike

Before implementing the API or MCP server, prove that NimBus can route a dynamically typed event using only an `EventTypeId` message property.

The spike must confirm:

1. A message can be published with `EventTypeId = crm.contact.enriched.v1`.
2. A subscription rule can match that property.
3. A coded NimBus participant can consume the routed message.
4. Resolver/audit behavior still works without a compiled C# event contract.
5. The dashboard can show enough event information for the demo to be understandable.

If this fails, revisit the Agent Zone architecture before continuing. **This is a gate, not just a technical task** — no REST, MCP, or schema-registry work begins until it passes.

### Phase 0 result — PASSED (2026-06-05)

The gate passed. The Agent Zone architecture (decision #4) is confirmed; no revisit needed. Key finding: `EventTypeId` is **already a first-class, routed Service Bus application property** in NimBus — set on publish (`MessageHelper.cs:25`), filtered by existing `SqlRuleFilter("user.EventTypeId='…'")` rules (`ServiceBusManagement.CreateEventTypeRule`, `ServiceBusTopologyProvisioner`), read on receive, and recorded as a plain string in the Resolver/audit. Dispatch is already string-keyed (`EventHandlerProvider.Handle`). The only code a dynamically-typed event needed was a **string-keyed handler registration** + a **raw-JSON handler** (no compiled `IEvent`):

- New: `EventHandlerProvider.RegisterHandler(string eventTypeId, Func<IEventJsonHandler>)` and `DelegateEventJsonHandler` (`src/NimBus.SDK/EventHandlers/`).

Confirmations:
1. Publish with `EventTypeId = crm.contact.enriched.v1` → ✅ (classless `Message`, the shape the future `/api/agent/publish` will build).
2. Subscription rule matches the property → ✅ at unit level (`ServiceBusFilterValidator` accepts the dotted id; the `user.EventTypeId='…'` filter already routes production traffic). *Live SB-emulator routing smoke deferred to the AppHost demo phase.*
3. Coded participant consumes the routed message → ✅ (`DynamicEventRoutingTests`, full wire round-trip; unsubscribed dynamic events degrade to `UnsupportedResponse`, not a crash).
4. Resolver/audit works without a compiled contract → ✅ (`ResolverServiceTests.Handle_DynamicallyTypedEvent_…`: pending→completed keyed on the dynamic id).
5. Dashboard surfaces dynamic events → ✅ (read/search/audit/details paths key on the `EventTypeId` string; only guard is "endpoint exists"). Minor UX note: the search filter dropdown is catalog-populated, so dynamic types won't be pre-listed filter options — `/api/agent/catalog` is the intended typed-discovery surface.

Tests: `tests/NimBus.EndToEnd.Tests/DynamicEventRoutingTests.cs`, `tests/NimBus.Resolver.Tests/ResolverServiceTests.cs`, `tests/NimBus.ServiceBus.Tests/ServiceBusFilterValidatorTests.cs`.

---

## Agent identity (demo version)

- Each agent uses an API key.
- The API key maps to an `agentId`.
- `agentId` is recorded on:
  - event type definitions
  - agent subscriptions
  - published events
  - receive/settle actions
- No RBAC, quotas, tenant isolation, or production auth in this cycle.

This gives the demo a useful audit trail without pulling production security into v1.

---

## Components & responsibilities

| Component | Responsibility | Depends on |
|---|---|---|
| `NimBus.Mcp` | Expose NimBus capabilities as MCP tools; translate tool calls → REST calls; carry auth. No business logic. | NimBus REST API; an HTTP client; MCP SDK |
| `/api/agent/*` controllers | The capability core: validate input, enforce schema, publish/receive/settle. | `IEventSchemaStore`, `IPublisherClient`, Resolver/`IHandoffClient`, message store |
| `IEventSchemaStore` (+ providers) | Persist and retrieve agent-defined event-type schemas; enforce immutability (reject changed re-registrations). | Storage provider (Cosmos/SQL) |
| Agent Zone (topology) | A pre-provisioned topic carrying dynamically-typed agent events; routes by `EventTypeId`. | `nb topology apply`, existing rule-based routing |
| `EnrichmentAgent` runner | The reproducible demo loop: `receive → ask Claude → define/publish → settle`. | `NimBus.Mcp` (as an MCP client), Anthropic API |
| CrmErpDemo wiring | Subscribe Agent Zone to `CrmContactCreated`; consume `crm.contact.enriched.v1` into ERP/DataPlatform; run new processes in AppHost. | existing demo projects |

Each unit has one job and a clear interface, so it can be understood and tested on its own. `NimBus.Mcp` is intentionally hollow — if a capability is missing, it is added to the REST API, never hidden in the MCP layer.

### MCP tool → REST mapping

Every MCP tool is a thin wrapper over exactly one REST call (the read-only `search_failures` reuses existing endpoints). This table is the contract that keeps the adapter hollow and testable.

| MCP tool | REST endpoint | Notes |
|---|---|---|
| `discover_topology` | `GET /api/agent/catalog` | Read-only: endpoints, event types, registered schemas |
| `define_event_type` | `POST /api/agent/event-types` | Idempotent when the schema is identical; `409` on a changed schema |
| `subscribe` | `POST /api/agent/subscribe` | Creates a pull-based logical subscription |
| `receive_messages` | `GET /api/agent/receive` | Long-poll pull |
| `publish_event` | `POST /api/agent/publish` | Validates payload against the registered schema before publishing |
| `settle_message` | `POST /api/agent/settle` | Complete or fail the received work |
| `search_failures` | `GET /api/messages/search`, `GET /api/audits/search` (existing) | Read-only diagnostic |

---

## Data flow — the demo walkthrough (happy path)

1. A user creates a contact in the **CRM React UI**. `Crm.Api` publishes `CrmContactCreated` (existing behavior).
2. The **Agent Zone** has a subscription to `CrmContactCreated`; NimBus forwards the event there (existing rule-based routing).
3. The **agent runner** calls the MCP tool `receive_messages` → `GET /api/agent/receive`, which returns the pending `CrmContactCreated` from the Agent Zone subscription under a peek-lock, with the coordinates needed to settle it.
4. The agent asks **Claude** to classify the contact: industry, lead score, a short rationale.
5. **First run only:** the agent calls `define_event_type` → `POST /api/agent/event-types`, registering `crm.contact.enriched.v1` with a JSON Schema. Idempotent on later runs.
6. The agent calls `publish_event` → `POST /api/agent/publish` with `eventTypeId = crm.contact.enriched.v1` and a payload. The API **validates the payload against the schema**, then publishes onto the Agent Zone topic with the `EventTypeId` property set for routing.
7. A subscriber to `crm.contact.enriched.v1` — the ERP/DataPlatform adapter (existing) or a small new handler — consumes it and writes the enriched data downstream. Where the write is to an external system, **that coded participant** uses NimBus's existing handoff mechanics (pending → complete); the agent itself does not create handoffs (see [Handoff in v1](#handoff-in-v1)).
8. The agent calls `settle_message` → `POST /api/agent/settle` to complete the original `CrmContactCreated` (Resolver audit: pending → completed).
9. **Observe:** the WebApp dashboard shows `crm.contact.enriched.v1` flowing through, with audits and metrics; the ERP UI shows the enriched contact. The whole chain — coded service → LLM agent → coded service — is visible in one trail.

### Handoff in v1

Agent-created handoffs are out of scope for v1. In the demo, handoff is shown indirectly:

1. The agent publishes `crm.contact.enriched.v1`.
2. A coded ERP/DataPlatform participant consumes the event.
3. Existing NimBus handoff mechanics are used by that coded participant where needed.

This keeps the agent API smaller and avoids mixing two concepts: message participation and external-system handoff.

---

## Error handling

| Situation | Behavior |
|---|---|
| Payload fails schema validation on publish | `400` with the JSON-Schema validation errors. The MCP tool surfaces them as a tool error the agent can read and retry. Nothing is published. |
| Publish/subscribe to an unknown `eventTypeId` | `404`. |
| `define_event_type` called again, identical schema | Idempotent no-op (returns existing). |
| `define_event_type` with the same id but **any different** schema | `409 Conflict` — agent-defined schemas are immutable in v1 (see [Schema versioning (v1)](#schema-versioning-v1)). Evolution/compatibility rules are deferred to a later spec. |
| Agent crashes / never settles | The event is **parked** (`Pending+Handoff`) off the bus with the session blocked — there is **no SB peek-lock to expire, so no automatic redelivery/DLQ**. v1 recovery is **operator-driven**: Resubmit/Skip from the WebApp (already wired), with the optional `ExpectedBy` deadline surfacing stuck tasks. An automatic timeout sweeper is deferred (see [Future sub-projects](#future-sub-projects)). |
| LLM fails or is low-confidence | Agent calls `settle_message` with a **fail** + reason (Resolver: pending → failed); failures show in `failed-insights`/audits. The event is parked, not on the bus, so there is no implicit redelivery — the agent must settle, or it stays parked until operator recovery. |
| Same parked event received twice (concurrent agents, or a re-poll before settle) | `/api/agent/receive` is a **non-claiming read** — a parked event stays `Pending+Handoff` until settled, so concurrent receives can return it more than once (**at-least-once**). The losing `settle` returns `400` (the event is no longer a pending handoff). A per-receive claim/lease is deferred to a later spec. |
| Oversized payload | Reuse the claim-check path (spec 013) for large bodies; otherwise enforce a size cap with `413`. |
| MCP → REST auth failure | `401`, surfaced as a tool error. |

The principle: **reuse NimBus's existing Resolver, audit, and handoff semantics** rather than inventing agent-specific failure handling. `/api/agent/receive` + `/settle` are built on the existing **pending-handoff** mechanism — a coded Agent Zone subscriber parks each routed event `Pending+Handoff`, `/receive` is a stateless message-store read, and `/settle` drives a handoff completion/failure through the Resolver. **No Service Bus lock is held across the agent's `receive → reason → publish → settle` round-trip** (a peek-lock can't survive two stateless HTTP requests), which is why the crash and duplicate-delivery rows above describe park-and-recover rather than SB lock expiry. The agent is otherwise just another participant in the audit trail.

---

## Testing

- **Unit** — `IEventSchemaStore` runs through the existing storage **conformance suite** (like the other stores). Payload validation and each `/api/agent/*` controller tested in isolation.
- **Contract** — `api-spec.yaml` round-trips through NSwag; the generated C# contract and TS client compile (existing build gate).
- **MCP mapping** — each tool calls the correct endpoint with the correct arguments, against a mock REST server. Verifies the adapter is faithful and hollow.
- **Integration** (EndToEnd.Tests style) — `CrmContactCreated` published → Agent Zone receives → `crm.contact.enriched.v1` published and validated → handler consumes → Resolver audit shows completed. Runs on the in-memory transport + InMemory store conformance harness for speed, and against the Aspire SB emulator for fidelity.
- **Demo smoke test** — the scripted `EnrichmentAgent` runner exercises the full chain against the AppHost; asserts the enriched contact and the completed audit trail.

A change is not done until the relevant tests above pass; the routing spike (see [Phase 0](#phase-0-dynamic-routing-spike)) is proven before API work begins.

---

## Demo acceptance flow

A final smoke test / manual checklist that defines "done" for the demo:

1. Start the Aspire AppHost.
2. Start `NimBus.Mcp`.
3. Start the scripted `EnrichmentAgent`.
4. Create a contact in the CRM UI.
5. Confirm the agent receives `CrmContactCreated`.
6. Confirm the agent defines `crm.contact.enriched.v1` if missing.
7. Confirm the agent publishes the enriched event.
8. Confirm ERP/DataPlatform receives the enriched event.
9. Confirm the WebApp dashboard shows the full audit trail.
10. Confirm the smoke test can assert the enriched contact and completed processing state.

---

## Success criteria

1. An external MCP client (Claude Code, Claude Desktop, or the scripted runner) can, with no NimBus C# written, complete the full loop: discover → define event type → subscribe → receive → publish → settle.
2. The CrmErpDemo runs end-to-end from the Aspire AppHost: creating a CRM contact results in an LLM-enriched `crm.contact.enriched.v1` event landing in ERP/DataPlatform, visible in the dashboard.
3. The OpenAPI spec (`api-spec.yaml`) describes every agent capability, so "MCP server" and "REST + OpenAPI for agents" are both satisfied.
4. All tests pass; the routing spike is confirmed.

---

## Future sub-projects

This spec is the foundation. Each item below is its own spec → plan → implementation cycle, built on the API + Agent Zone + schema registry delivered here.

- **`NimBus.Agents` .NET SDK** — a hosted-agent framework (à la microsoft/agents) so a C# worker can embed an LLM and participate via a typed client over the same REST core.
- **Ops/SRE agent** — an autonomous agent that watches endpoints, triages failed/dead-lettered messages (reusing `failed-insights`, `audits/search`, `event/blocked`), explains root cause, resubmits/skips, and can open a PR with a fix.
- **Fully dynamic topology** — let trusted agents create Service Bus entities at runtime via the existing `ServiceBusManagement` path, lifting the pre-provisioned Agent Zone constraint.
- **Push delivery** — server-initiated streaming so agents are notified rather than polling.
- **Production auth** — scoped API keys / OAuth, per-agent quotas, and audit of agent actions.

---

## Glossary

- **MCP (Model Context Protocol)** — the standard protocol by which LLM agents discover and call external tools. The NimBus MCP server presents NimBus capabilities as such tools.
- **Agent Zone** — a single, pre-provisioned NimBus endpoint/topic that carries dynamically-typed agent events, routed to subscribers by an `EventTypeId` property.
- **Dynamically-typed event** — an event whose contract is a registered JSON Schema rather than a compiled C# class; its type is identified by an `EventTypeId` message property.
- **Schema registry** — the store (`IEventSchemaStore`) holding agent-defined event-type schemas, used to validate payloads on publish and to power discovery.
- **Handoff** — NimBus's existing mechanism for marking a unit of work as pending while an external system completes it, then settling it complete/failed (`IHandoffClient`).
- **Settle** — to drive a received message's Resolver audit from pending to completed or failed.
- **Capability core** — the single place capabilities are implemented (here, the REST API); thin adapters (MCP, future SDK) call into it.

---

## Open questions

1. ~~**Routing spike outcome** — does property-based `EventTypeId` filtering behave exactly as assumed for non-compiled events?~~ **Resolved (2026-06-05): Phase 0 passed** — `EventTypeId` is already a first-class routed property; no revisit of decision #4. See [Phase 0 result](#phase-0-result--passed-2026-06-05). (Live SB-emulator routing smoke still recommended during the AppHost demo phase.)
2. **Agent runner host** — ship the demo runner as C# (consistent with the repo) or as a small Python script (closest to typical MCP-agent ergonomics)? Leaning C# for repo consistency.
3. **Where enriched results surface** — rely solely on the existing WebApp dashboard + ERP UI, or add a tiny "agent activity" panel? Leaning reuse-only for this cycle.
