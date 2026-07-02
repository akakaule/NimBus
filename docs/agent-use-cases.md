# Agent Use Cases — Interaction Visualizations

How AI (and human) agents participate in NimBus event flows. Every diagram shows the agent's
interaction **with** NimBus (the REST / MCP surface it calls) and its place **within** NimBus
(the park → receive → publish → settle seam, session ordering, and the Resolver audit trail).

Agents never hold a Service Bus lock. They join through a small seam: an endpoint **parks** a message
as a pending handoff, the agent **receives** it over REST (or MCP), does its work, **publishes** any
result, and **settles** the original handoff. Settlement unblocks the session and replays deferred
siblings in FIFO order. See [`message-flows.md`](message-flows.md) §13 for the underlying Service Bus
mechanics and [`agent-enrichment-demo.md`](agent-enrichment-demo.md) for an end-to-end walkthrough.

| Status | Meaning |
|---|---|
| `Pending` + `PendingSubStatus = "Handoff"` | Parked, awaiting an agent |
| `Deferred` | A session sibling parked behind the handoff |
| `Completed` / `Failed` | Settlement outcome (`complete` / `fail`) |

**Diagrams 1–4 reflect shipped code** (file references included). **Diagrams 5–9 are proposed** — each
notes the existing primitives it would reuse.

---

## Where agents plug into NimBus

```mermaid
flowchart LR
    Src[Business System<br/>e.g. CRM] --> Zone[Agent Zone Endpoint<br/>parks via MarkPendingHandoff]
    Zone --> Res[(Resolver<br/>audit + status)]
    Zone -. parked Pending+Handoff .-> API[Agent REST API<br/>/api/agent/*]
    Agent[External / AI Agent] -- receive · publish · settle --> API
    Claude[Claude / LLM] -. classify · infer · detect .-> Agent
    Client[Claude Desktop / Code] -- MCP stdio --> MCP[NimBus.Mcp<br/>7 tools]
    MCP -- maps 1:1 --> API
    API -- publish routed event --> Down[Downstream Endpoint]
    API -- settle --> Zone
```

---

## 1. Enrichment Agent  ·  **Implemented**

*Drop an LLM into an event stream as a first-class participant — it enriches messages inline with the
same ordering and audit guarantees as a native handler, and degrades to deterministic logic when the
model is unavailable.*

Source: [`AgentLoopWorker.cs`](../samples/CrmErpDemo/EnrichmentAgent/AgentLoopWorker.cs),
[`AgentZoneParkHandler.cs`](../samples/CrmErpDemo/CrmErpDemo.Contracts/Handlers/AgentZoneParkHandler.cs).

```mermaid
sequenceDiagram
    participant Src as CRM
    participant Zone as Agent Zone Endpoint
    participant Res as Resolver
    participant API as Agent REST API
    participant Agent as EnrichmentAgent
    participant Claude as Claude (Haiku 4.5)
    participant Down as DataPlatform

    Note over Src,Down: 1 — event parked for the agent
    Src->>Zone: CrmContactCreated (session = ContactId)
    Zone->>Res: PendingHandoffResponse — Pending + Handoff
    Note over Zone: BlockSession, complete inbound (no lock held)

    Note over Src,Down: 2 — agent loop (receive → classify → publish → settle)
    Agent->>API: GET /api/agent/receive (CrmContactCreated, wait 10s)
    API-->>Agent: 200 payload + HandoffCoordinates
    opt no ANTHROPIC_API_KEY
        Note over Agent: DeterministicContactClassifier fallback
    end
    Agent->>Claude: classify(contact)
    Claude-->>Agent: industry, leadScore, rationale
    Agent->>API: POST /api/agent/event-types (crm.contact.enriched.v1, first run)
    Agent->>API: POST /api/agent/publish (enriched, sessionId preserved)
    API->>Down: crm.contact.enriched.v1 routed (same session)
    Agent->>API: POST /api/agent/settle (complete)

    Note over Src,Down: 3 — handoff settled
    API->>Zone: HandoffCompletedRequest (via Manager) → UnblockSession
    Zone->>Res: status flips Pending → Completed
```

**Interaction notes** — receive long-poll is 10s; the enriched event carries the original `sessionId`
so downstream ordering holds; publish is validated server-side against the registered JSON schema.

---

## 2. Park / Handoff Seam  ·  **Implemented**

*Any message can be delegated out of the bus to an external worker without losing session ordering —
siblings block until the handoff settles, then replay automatically.* This is the reusable backbone
every other agent use case builds on.

Source: [`AgentZoneParkHandler.cs`](../samples/CrmErpDemo/CrmErpDemo.Contracts/Handlers/AgentZoneParkHandler.cs),
`StrictMessageHandler`; settle guard in
[`AgentImplementation.cs`](../src/NimBus.WebApp/Controllers/ApiContract/AgentImplementation.cs).

```mermaid
sequenceDiagram
    participant Src as Producer
    participant Zone as Agent Zone Endpoint
    participant Res as Resolver
    participant API as Agent REST API
    participant Agent as Agent

    Note over Src,Agent: 1 — handler parks the work
    Src->>Zone: EventRequest (event-1, session-X)
    Zone->>Zone: handler calls MarkPendingHandoff
    Zone->>Res: PendingHandoffResponse — Pending + Handoff
    Zone->>Zone: BlockSession by event-1
    Zone->>Zone: complete inbound (no Service Bus lock held)

    Note over Src,Agent: 2 — siblings on the session defer (FIFO)
    Src->>Zone: EventRequest (event-2, session-X)
    Zone->>Zone: VerifySessionIsNotBlocked throws
    Zone->>Res: DeferralResponse — Deferred (parked)

    Note over Src,Agent: 3 — agent settles, session replays
    Agent->>API: POST /api/agent/settle (coordinates of event-1, complete)
    API->>API: guard — status == Pending AND PendingSubStatus == "Handoff"
    API->>Zone: HandoffCompletedRequest (via Manager)
    Zone->>Zone: UnblockSession + ContinueWithAnyDeferredMessages
    Zone->>Res: status flips Pending → Completed
    Zone->>Zone: republish event-2 (FIFO)
```

**Interaction notes** — settle is rejected with `400` unless the target is genuinely `Pending` + `Handoff`;
the `fail` outcome leaves the session blocked for operator Resubmit/Skip (symmetric to §13 of message-flows).

---

## 3. Agent REST API  ·  **Implemented**

*A self-describing REST contract that turns NimBus into a platform any agent — in any language — can join:
discover the topology, register event types at runtime, and exchange messages with schema enforcement.*

Source: [`AgentImplementation.cs`](../src/NimBus.WebApp/Controllers/ApiContract/AgentImplementation.cs).

```mermaid
sequenceDiagram
    participant Agent as Agent
    participant API as Agent REST API
    participant Res as Resolver

    Agent->>API: GET /api/agent/catalog
    API-->>Agent: endpoints + event types (with schemas)

    Agent->>API: POST /api/agent/event-types
    alt same schema or new
        API-->>Agent: 200 stored
    else different schema exists
        API-->>Agent: 409 conflict
    end

    Agent->>API: POST /api/agent/subscribe
    API-->>Agent: 200 subscribed

    loop long-poll (waitSeconds clamped 0–60)
        Agent->>API: GET /api/agent/receive
        alt handoff parked
            API-->>Agent: 200 payload + coordinates
        else none ready
            API-->>Agent: 204 No Content
        end
    end

    Agent->>API: POST /api/agent/publish
    alt payload valid for registered schema
        API->>Res: tracked + routed
        API-->>Agent: 200 published
    else invalid or unknown type
        API-->>Agent: 400 / 404
    end

    Agent->>API: POST /api/agent/settle (complete | fail)
    alt target is Pending + Handoff
        API-->>Agent: 200 settled
    else not a pending handoff
        API-->>Agent: 400 rejected
    end
```

**Interaction notes** — discovery (`catalog`) makes the bus self-describing so agents bootstrap with no
hard-coded contracts; `receive` is at-least-once (non-claiming), so handlers must tolerate duplicates.

---

## 4. MCP Bridge  ·  **Implemented**

*Make the integration platform itself an agent tool — an operator or LLM can discover topology, replay
failures, and publish corrective events in natural language, with no integration code.*

Source: [`NimBusAgentTools.cs`](../src/NimBus.Mcp/Tools/NimBusAgentTools.cs).

```mermaid
sequenceDiagram
    participant Claude as Claude (Desktop / Code)
    participant MCP as NimBus.Mcp
    participant API as Agent REST API

    Note over Claude,API: 7 tools map 1:1 onto /api/agent/* (stdio · JSON-RPC · X-Agent-Id header)

    Claude->>MCP: discover_topology
    MCP->>API: GET /api/agent/catalog
    API-->>MCP: topology JSON
    MCP-->>Claude: topology JSON

    Claude->>MCP: receive_messages(eventTypeId, waitSeconds)
    MCP->>API: GET /api/agent/receive
    alt message parked
        API-->>MCP: 200 message
        MCP-->>Claude: message JSON
    else none ready
        API-->>MCP: 204
        MCP-->>Claude: "no message available"
    end

    Claude->>MCP: publish_event / settle_message
    MCP->>API: POST /api/agent/publish, /api/agent/settle
    API-->>MCP: 200 (or mapped error)
    MCP-->>Claude: "published" / "settled"

    Note over MCP: also define_event_type · subscribe · search_failures
```

**Interaction notes** — the bridge is a thin translator over the same REST contract in §3; tool errors are
mapped to readable strings (e.g. a `409` becomes a "conflict" message the model can reason about).

---

## 5. AI Dead-Letter Triage & Auto-Resubmit  ·  **Proposed**

*Turn the WebApp's manual failure triage into an assisted one — cluster failures, diagnose the likely
cause, and auto-resubmit transient ones while escalating poison messages.*

```mermaid
sequenceDiagram
    participant Bus as Endpoint
    participant Res as Resolver
    participant Agent as Triage Agent
    participant Claude as Claude
    participant API as Agent / Manager API

    Note over Bus,API: PROPOSED — reuses Resolver audit, ErrorPatternNormalizer, resubmit/skip
    Bus->>Res: handler fails — Failed / DeadLettered

    Agent->>API: search_failures (messages + audits)
    API-->>Agent: failure records
    Agent->>Agent: cluster via ErrorPatternNormalizer
    Agent->>Claude: classify cause (transient / poison / schema drift)
    Claude-->>Agent: classification + confidence

    alt transient
        Agent->>API: resubmit(eventId)
        API->>Bus: replay → Completed
    else poison or low confidence
        Agent->>API: annotate "needs human" (or skip)
    end
```

**Reuses** — resubmit/skip + audit trail, the existing `ErrorPatternNormalizer` failure grouping, and the
`search_failures` capability already exposed to agents.

---

## 6. Schema-Drift Mediation / Auto-Mapping  ·  **Proposed**

*Close the loop on contract evolution — when a publish is rejected for schema drift, an agent infers the
mapping to the registered shape and republishes the corrected event.*

```mermaid
sequenceDiagram
    participant Prod as Producer Agent
    participant API as Agent REST API
    participant Med as Mediation Agent
    participant Claude as Claude

    Note over Prod,Claude: PROPOSED — reuses runtime event-type definition + server-side schema validation
    Prod->>API: POST /api/agent/publish (legacy payload)
    API-->>Prod: 400 schema-invalid

    API->>Med: rejected payload routed to mediation
    Med->>Claude: infer mapping (old shape → registered schema)
    Claude-->>Med: field mapping
    opt new contract version needed
        Med->>API: POST /api/agent/event-types (vNext)
    end
    Med->>API: POST /api/agent/publish (corrected)
    API-->>Med: 200 published
```

**Reuses** — runtime `event-types` registration and the server-side JSON-schema validation that already
produces the `400`/`409` rejection paths.

---

## 7. Content-Based Routing / Intelligent Triage  ·  **Proposed**

*Route events whose destination isn't statically known — classify intent, priority, and PII sensitivity,
then publish a typed, routed event the topology already knows how to deliver.*

```mermaid
sequenceDiagram
    participant Src as Inbound
    participant Zone as Agent Zone Endpoint
    participant API as Agent REST API
    participant Agent as Routing Agent
    participant Claude as Claude
    participant Bill as Billing Endpoint
    participant Tech as Technical Endpoint

    Note over Src,Tech: PROPOSED — reuses first-class routing (Spec 022) + routing lineage (#62)
    Src->>Zone: SupportTicketReceived (generic, parked)
    Agent->>API: GET /api/agent/receive
    API-->>Agent: ticket payload
    Agent->>Claude: classify intent / priority / PII
    Claude-->>Agent: intent = billing, priority = high, pii = true

    alt billing intent
        Agent->>API: publish ticket.billing.v1
        API->>Bill: routed
    else technical intent
        Agent->>API: publish ticket.technical.v1
        API->>Tech: routed
    end
    Agent->>API: POST /api/agent/settle (complete)
```

**Reuses** — first-class routing (Spec 022 Phase 0) and routing-lineage visualization (#62) to record the
AI's decision; pairs naturally with the PII-masking work (spec 021).

---

## 8. Human-in-the-Loop Approval Gate  ·  **Proposed**

*The same park/handoff seam, but the "agent" is a person — high-value or low-confidence events wait for an
approver in the WebApp, and siblings replay on approval.*

```mermaid
sequenceDiagram
    participant Src as Producer
    participant Zone as Agent Zone Endpoint
    participant Res as Resolver
    participant Web as WebApp UI
    participant Human as Approver
    participant API as Agent REST API

    Note over Src,API: PROPOSED — identical seam to §2, settled by a human instead of an LLM
    Src->>Zone: high-value event (parked Pending + Handoff)
    Zone->>Res: Pending + Handoff
    Web->>Res: list pending approvals
    Human->>Web: Approve (or Reject)
    Web->>API: POST /api/agent/settle (complete | fail)
    API->>Zone: HandoffCompletedRequest → UnblockSession
    Zone->>Res: Completed
    Note over Zone: deferred siblings replay (FIFO)
```

**Reuses** — the deferred-replay + handoff machinery from §2 verbatim; only an approval surface in the
WebApp is new. Demonstrates the seam is not AI-only.

---

## 9. AI Reconciliation / Anomaly Detection  ·  **Proposed**

*Reason over a session-ordered stream to catch mismatches, duplicates, and missing counterparts, emitting
typed exception events for downstream handling.*

```mermaid
sequenceDiagram
    participant Ord as Orders
    participant Pay as Payments
    participant Zone as Agent Zone Endpoint
    participant API as Agent REST API
    participant Agent as Reconciliation Agent
    participant Claude as Claude
    participant Down as Exception Handler

    Note over Ord,Down: PROPOSED — reuses session ordering (per entity) + audit store history
    Ord->>Zone: OrderPlaced (session = orderId)
    Pay->>Zone: PaymentReceived (session = orderId)
    Agent->>API: GET /api/agent/receive (same-session window)
    Agent->>API: query audit history
    API-->>Agent: prior events for orderId
    Agent->>Claude: detect mismatch / duplicate / missing counterpart
    Claude-->>Agent: verdict

    alt anomaly
        Agent->>API: publish reconciliation.exception.v1
        API->>Down: routed
    else reconciled
        Agent->>API: POST /api/agent/settle (complete)
    end
```

**Reuses** — session-based ordering gives the agent a coherent per-entity stream; the audit store supplies
the historical context for "is this anomalous?".

---

> Diagrams 5–9 are forward-looking designs, not shipped features. They reuse the same four primitives the
> implemented cases are built on: the **park/handoff seam**, the **Agent REST API**, **runtime event-type
> definition with schema validation**, and the **Resolver audit trail**.
