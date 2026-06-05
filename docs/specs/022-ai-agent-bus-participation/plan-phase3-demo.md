# Spec 022 — Phase 3 Implementation Plan: EnrichmentAgent demo + AppHost + smoke

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (or executing-plans). Steps use checkbox (`- [ ]`).

**Goal:** An AI `EnrichmentAgent` joins the existing CrmErpDemo end-to-end on the Aspire AppHost: create a CRM contact → `CrmContactCreated` is parked on the Agent Zone → the agent pulls it, asks Claude to classify it, first-run defines `crm.contact.enriched.v1`, publishes the enriched event, settles the original → ERP/DataPlatform consumes the enriched event → the dashboard shows the full audit trail. Plus a deterministic in-memory smoke test (CI gate) and a live emulator smoke (Playwright).

**Architecture:** The agent is a C# worker that drives the loop over the **REST API directly** (deterministic; MCP is a later swap behind an `IBusGateway` seam). Claude lives behind an `IContactClassifier` seam with a deterministic fake auto-selected when `ANTHROPIC_API_KEY` is absent (CI-safe). Three enablers (Tasks A–C) close the gaps that make hosted dynamic events actually flow; Tasks D–H build the agent, wire the AppHost, and prove it.

**Tech Stack:** .NET 10 Aspire, Azure Service Bus emulator, official `Anthropic` C# SDK (`claude-haiku-4-5`, structured outputs), MSTest (in-memory smoke), Playwright (live e2e).

> **Prerequisite:** Phase 1 complete (REST API + park handler + schema registry). Phase 2 (`NimBus.Mcp`) is **not** required for this phase — the agent runs REST-direct. If Phase 2 is done, an MCP-client `IBusGateway` impl and an AppHost MCP resource can be added (Task G, optional).
> **Verify flags:** the official `Anthropic` SDK specifics (`Model.ClaudeHaiku4_5` constant, `OutputConfig`/`JsonOutputFormat` structured-output shape, Haiku-4.5 structured-output support) against the installed package version; `Aspire.Hosting.Testing` API if used instead of Playwright; stdio-MCP-under-Aspire if Task G is attempted.

---

## File structure
```
src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs   # + AddDynamicHandler(string, Func<IEventJsonHandler>)
samples/CrmErpDemo/
  CrmErpDemo.AppHost/EmulatorTopologyConfigBuilder.cs   # + dynamic-event forward rule
  CrmErpDemo.AppHost/Program.cs                          # + EnrichmentAgent (+ park host) resources
  CrmErpDemo.Contracts/                                  # AgentZoneEndpoint exists; + park handler + enriched consumer
  CrmErpDemo.AgentZone/ (NEW worker)                     # hosts AgentZoneEndpoint subscriber + park handler
  EnrichmentAgent/ (NEW worker)
    EnrichmentAgent.csproj
    Program.cs / AgentLoopWorker.cs                      # BackgroundService: receive→classify→define→publish→settle
    Bus/IBusGateway.cs + RestBusGateway.cs               # REST-direct (MCP swap later)
    Classification/IContactClassifier.cs
    Classification/ClaudeContactClassifier.cs            # Anthropic + claude-haiku-4-5 + structured output
    Classification/DeterministicContactClassifier.cs     # CI fake (no network)
tests/NimBus.EndToEnd.Tests/AgentEnrichmentSmokeTests.cs # in-memory CI gate
samples/CrmErpDemo/e2e/tests/07-agent-enrichment.spec.ts # live emulator smoke
docs/ (demo walkthrough; reconcile spec wording)
```

---

### Task A: Dynamic-event outbound routing in the emulator topology

The agent publishes `crm.contact.enriched.v1` onto the Agent Zone topic, but no auto-generated rule forwards a dynamic id. Add an explicit forward subscription so ERP/DataPlatform receive it.

**Files:** Modify `samples/CrmErpDemo/CrmErpDemo.AppHost/EmulatorTopologyConfigBuilder.cs`; test `samples/CrmErpDemo/CrmErpDemo.AppHost.Tests/` (create if absent) or a unit test near the builder.

- [ ] **Step 1:** Add a small, explicit "dynamic agent event" routing declaration. Simplest demo-scoped approach: a hardcoded-for-demo entry (or a tiny config list) that, on the `AgentZoneEndpoint` topic, emits a forward subscription to the enriched-event consumer endpoint (ERP or DataPlatform) with filter `user.EventTypeId = 'crm.contact.enriched.v1' AND user.From IS NULL` and action `SET user.From = 'AgentZoneEndpoint'; SET user.EventId = newid(); SET user.To = '{consumerEndpointId}';` — mirroring the compiled forward-rule emission at `EmulatorTopologyConfigBuilder.cs:151-154`.
- [ ] **Step 2: test** the generated `UserConfig` JSON contains the `crm.contact.enriched.v1` forward subscription/rule on the AgentZone topic (assert against the builder output object, no emulator needed).
- [ ] **Step 3:** Add a code comment + a note in `spec.md` (Task H) that the **production `ServiceBusTopologyProvisioner` has the same gap** and a real-Azure run needs a parity fix (deferred).
- [ ] **Step 4: build; commit** — `feat(demo): forward rule for dynamic crm.contact.enriched.v1 on Agent Zone (spec 022)`

---

### Task B: `NimBusSubscriberBuilder.AddDynamicHandler` (hosted string-keyed handler)

**Files:** Modify `src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs`; test in `tests/NimBus.SDK.Tests/`.

- [ ] **Step 1: failing test** — registering a dynamic handler via the builder makes the hosted `EventHandlerProvider` dispatch a raw event by its string `EventTypeId` (mirror how the typed `AddHandler<T>` registration is tested; assert the provider resolves the string-keyed handler).
- [ ] **Step 2: implement** `AddDynamicHandler(string eventTypeId, Func<IEventJsonHandler> factory)` that registers via the same `EventHandlerProvider` the typed `AddHandler<T>` uses (calling the Phase 0 `RegisterHandler(string, Func<IEventJsonHandler>)`). Keep it parallel to the existing typed registration path so the hosted receiver picks it up.
- [ ] **Step 3: test PASS; build solution; commit** — `feat(sdk): AddDynamicHandler for hosted string-keyed event handlers (spec 022)`

---

### Task C: Hosted Agent Zone park subscriber

**Files:** Create `samples/CrmErpDemo/CrmErpDemo.AgentZone/` worker (or fold into an existing host) + a typed park handler; modify AppHost (Task G wires it).

- [ ] **Step 1:** Create a worker host (`Host.CreateApplicationBuilder`, mirror `Crm.Adapter/Program.cs:29-43`) that `AddAzureServiceBusClient("servicebus")` + `AddNimBusSubscriber("AgentZoneEndpoint", sub => sub.AddHandler<CrmContactCreated, AgentZoneParkHandler>())` + `AddNimBusReceiver(...)`.
- [ ] **Step 2:** `AgentZoneParkHandler : IEventHandler<CrmContactCreated>` whose `Handle` calls `context.MarkPendingHandoff("Awaiting agent pickup")` and returns. (Typed handler — `CrmContactCreated` is compiled — so the park side needs no string-keyed registration.)
- [ ] **Step 3:** A small integration check (in-memory `EndToEndFixture`) that this handler parks a `CrmContactCreated` as `PendingHandoffResponse` (reuse the Task 7 pattern). Build solution; commit — `feat(demo): hosted Agent Zone park subscriber (spec 022)`

---

### Task D: Enriched-event consumer in ERP/DataPlatform

**Files:** Modify the ERP or DataPlatform host (`samples/CrmErpDemo/Erp.Adapter.Functions/` or `DataPlatform.Adapter.Functions/`) to register a dynamic handler for `crm.contact.enriched.v1`.

- [ ] **Step 1:** In the chosen host's startup, use `sub.AddDynamicHandler("crm.contact.enriched.v1", () => new DelegateEventJsonHandler(async (ctx, ct) => { /* read ctx.MessageContent.EventContent.EventJson; write enriched data downstream or log for the demo sink */ }))`.
- [ ] **Step 2:** Ensure the chosen endpoint is the one the Task A forward rule targets (`{consumerEndpointId}` must match). Keep the handler small (the demo sink can log + persist to the ERP store).
- [ ] **Step 3:** Build solution; commit — `feat(demo): ERP/DataPlatform consumes dynamic crm.contact.enriched.v1 (spec 022)`

---

### Task E: `IContactClassifier` + Claude impl + deterministic fake

**Files:** Create `samples/CrmErpDemo/EnrichmentAgent/Classification/*` (the EnrichmentAgent project is scaffolded here too — do project scaffold in this task or split a Task E0).

- [ ] **Step 1: scaffold `EnrichmentAgent`** (`Microsoft.NET.Sdk.Worker` csproj; refs `NimBus.ServiceDefaults`, `CrmErpDemo.Contracts`; pkg `Anthropic`) and add to `src/NimBus.sln`.
- [ ] **Step 2:** `IContactClassifier { Task<ContactEnrichment> Classify(ContactInput contact, CancellationToken ct); }` with records `ContactInput`(from CrmContactCreated) and `ContactEnrichment`(industry, leadScore, rationale) matching the `crm.contact.enriched.v1` schema.
- [ ] **Step 3:** `DeterministicContactClassifier` — fixed industry/leadScore from the input (no network). `ClaudeContactClassifier` — `AnthropicClient` (reads `ANTHROPIC_API_KEY`), `claude-haiku-4-5`, **structured output** (`OutputConfig`/`JsonOutputFormat` with the enrichment JSON schema) so the result is reliably schema-valid. DI selects the fake when `ANTHROPIC_API_KEY` is absent.
- [ ] **Step 4: tests** — `DeterministicContactClassifier` returns a stable enrichment for a sample contact (unit test, no network). `ClaudeContactClassifier` is exercised only when a key is present (skip/inconclusive otherwise).
- [ ] **Step 5:** build solution; commit — `feat(demo): IContactClassifier with Claude + deterministic fake (spec 022)`

---

### Task F: `EnrichmentAgent` runner loop

**Files:** `EnrichmentAgent/Bus/IBusGateway.cs` + `RestBusGateway.cs`, `EnrichmentAgent/AgentLoopWorker.cs`, `Program.cs`.

- [ ] **Step 1:** `IBusGateway` — receive/define/publish/settle/discover methods over the agent API; `RestBusGateway` is a typed `HttpClient` impl that resolves `nimbus-ops` via Aspire service discovery (`services:nimbus-ops:http:0`, mirror `Crm.Adapter/Program.cs:12-15`) and sends the `X-Agent-Id` header.
- [ ] **Step 2:** `AgentLoopWorker : BackgroundService` — loop: `receive` (long-poll) → if a `CrmContactCreated` arrives, map to `ContactInput` → `IContactClassifier.Classify` → first-run `define_event_type("crm.contact.enriched.v1", schema)` (idempotent; tolerate 409) → `publish_event` (payload from the enrichment, schema-valid) → `settle_message` (complete the original). Handle define-already-exists and transient errors; log each step.
- [ ] **Step 3: test** — drive `AgentLoopWorker` against a fake `IBusGateway` (seeded with one parked CrmContactCreated) + `DeterministicContactClassifier`; assert the ordered calls (receive→define→publish→settle) with the right args (enriched payload, settle of the original). No network.
- [ ] **Step 4:** build solution; commit — `feat(demo): EnrichmentAgent receive→classify→define→publish→settle loop (spec 022)`

---

### Task G: AppHost wiring

**Files:** Modify `samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs` + `CrmErpDemo.AppHost.csproj`.

- [ ] **Step 1:** Add `ProjectReference`s for `EnrichmentAgent` and the Agent Zone park host; `AddProject<Projects.EnrichmentAgent>("enrichment-agent").WithReference(nimbusOps).WithEnvironment("ANTHROPIC_API_KEY", builder.Configuration["ANTHROPIC_API_KEY"] ?? "").WaitFor(nimbusOps);` and the park-subscriber host with `.WithReference(servicebus).WaitFor(...)`.
- [ ] **Step 2 (optional, only if Phase 2 done):** add `NimBus.Mcp` as a resource (note: a stdio MCP server doesn't map cleanly to an Aspire long-running resource; document running it from an MCP client instead, or use HTTP/SSE transport). Keep optional/clearly-flagged.
- [ ] **Step 3:** Build solution; manually start the AppHost (emulator) once and confirm the resources boot. Commit — `feat(demo): wire EnrichmentAgent + Agent Zone host into AppHost (spec 022)`

---

### Task H: Smoke tests + docs + spec reconciliation

**Files:** Create `tests/NimBus.EndToEnd.Tests/AgentEnrichmentSmokeTests.cs`, `samples/CrmErpDemo/e2e/tests/07-agent-enrichment.spec.ts`; modify `spec.md`, add a demo walkthrough doc.

- [ ] **Step 1 (CI gate — in-memory):** `AgentEnrichmentSmokeTests` — using `EndToEndFixture` + InMemory `IEventSchemaStore` + `DeterministicContactClassifier`: define schema → publish `CrmContactCreated` → park → "receive" parked row → classify → publish `crm.contact.enriched.v1` (schema-validated) → string-keyed handler consumes → settle the original → assert the enriched event was consumed AND a Resolver audit shows the original completed. Deterministic, no key, no emulator. (This is the Phase-1 Task-12 analogue extended with classification.)
- [ ] **Step 2 (live fidelity — Playwright):** `07-agent-enrichment.spec.ts` mirroring `04-pending-handoff-success.spec.ts`: create a CRM contact via `crm-api`, poll `nimbus-ops` (`/api/messages/search` or `/api/agent/catalog`) until `crm.contact.enriched.v1` appears on the consumer endpoint and the original `CrmContactCreated` audit is `Completed`. Runs against the live emulator AppHost with the `DeterministicContactClassifier` (no `ANTHROPIC_API_KEY`) for determinism. This also satisfies Phase 0's deferred "live SB-emulator routing fidelity" item.
- [ ] **Step 3 (docs):** demo walkthrough (the spec's "Demo acceptance flow" as a runnable checklist) + reconcile any spec wording. Confirm the dashboard shows the dynamic event (Phase 0 already verified read paths; only the search-filter dropdown is catalog-limited — `/api/agent/catalog` is the typed-discovery surface).
- [ ] **Step 4:** `dotnet build src/NimBus.sln` (0 errors) + `dotnet test src/NimBus.sln` green; run the in-memory smoke; (optionally) run the live e2e against the AppHost. Commit — `test(demo): in-memory + live agent-enrichment smoke (spec 022)`

---

## Self-review notes
- **Spec coverage:** success criteria #1 (full agent loop) via Tasks D–F + smoke; #2 (CrmErpDemo end-to-end on AppHost) via Tasks A–H; #4 (routing confirmed live) via the Playwright smoke. The MCP "hero" (criterion #1's "MCP client") is satisfied by Phase 2 + an optional Task-G MCP resource; the demo runner is REST-direct by design (determinism), with `IBusGateway` as the swap point.
- **Gaps closed:** Task A (dynamic outbound routing), Task B (hosted string-keyed handler), Task C (running park subscriber) — the three cross-cutting gaps from `plan.md`.
- **Open spec decisions resolved:** runner = C# (Open Q#2); results surface via the existing dashboard + ERP UI, no bespoke agent panel (Open Q#3 — reuse-only).
- **Deferred & flagged:** production `ServiceBusTopologyProvisioner` parity for dynamic ids; real Claude calls require `ANTHROPIC_API_KEY` (fake fills CI); MCP-under-Aspire ergonomics.
