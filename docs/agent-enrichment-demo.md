# Agent Enrichment Demo (spec 022)

A runnable checklist that proves the AI agent enrichment loop end-to-end on the
CrmErpDemo Aspire AppHost: **create a CRM contact → the Agent Zone parks it → the
EnrichmentAgent receives, classifies, defines the event type, publishes the
enriched event, and settles the handoff → DataPlatform consumes
`crm.contact.enriched.v1` → the dashboard shows the full audit trail.**

This is the "Demo acceptance flow" from `docs/specs/022-ai-agent-bus-participation/spec.md`,
adapted to what was actually built: the agent drives the loop **REST-direct** against
the agent API hosted on `nimbus-ops` (MCP is a later swap behind the `IBusGateway`
seam), and classification runs behind an `IContactClassifier` seam.

## Resources involved

| Aspire resource        | Role                                                                       |
| ---------------------- | -------------------------------------------------------------------------- |
| `crm-api` / `crm-web`  | CRM API + UI. Creating a contact publishes `CrmContactCreated`.            |
| `agent-zone`           | Park host. Subscribes to `AgentZoneEndpoint`, parks each `CrmContactCreated` as `Pending+Handoff`. |
| `enrichment-agent`     | The agent loop: receive → classify → define → publish → settle.            |
| `nimbus-ops`           | NimBus management WebApp. Hosts the agent REST API + the operator dashboard. |
| `dataplatform-adapter` | Consumes the dynamic `crm.contact.enriched.v1` on `DataPlatformEndpoint`.  |

## ANTHROPIC_API_KEY: real Claude vs deterministic fake

The agent selects its classifier from the environment:

- **`ANTHROPIC_API_KEY` unset (default / CI):** `DeterministicContactClassifier`
  runs — no network, stable output (industry inferred from email/name keywords,
  lead score derived from a stable hash of the contact id). Use this for a
  reproducible demo.
- **`ANTHROPIC_API_KEY` set:** `ClaudeContactClassifier` calls Claude
  (`claude-haiku-4-5`, structured output) so the enrichment is schema-valid by
  construction. The AppHost forwards the key to `enrichment-agent` when present.

Either way the published `crm.contact.enriched.v1` payload is validated server-side
against the registered JSON Schema on `/api/agent/publish`, so an invalid payload
is rejected before it ever reaches the bus.

## Enable the Service Bus emulator

The demo runs entirely locally on Microsoft's Service Bus emulator container (no
Azure namespace needed). Enable it with either the CLI flag or the env var — the
AppHost pre-declares the topology (including the dynamic forward rule for
`crm.contact.enriched.v1` → `DataPlatformEndpoint`) from `EmulatorTopologyConfigBuilder`.

```bash
# from the repo root — flag form
dotnet run --project samples/CrmErpDemo/CrmErpDemo.AppHost -- --UseEmulator=true

# or env-var form
#   PowerShell:  $env:NIMBUS_SB_EMULATOR = "true"
#   bash:        export NIMBUS_SB_EMULATOR=true
dotnet run --project samples/CrmErpDemo/CrmErpDemo.AppHost
```

Storage defaults to the Aspire-managed SQL Server container; pass
`--NIMBUS_STORAGE_PROVIDER cosmos` to use Cosmos instead. To exercise real Claude,
set `ANTHROPIC_API_KEY` (env var or AppHost user-secret) before starting.

## Walkthrough

1. **Start the AppHost** (emulator mode, as above). Wait until `nimbus-ops`,
   `agent-zone`, `enrichment-agent`, and `dataplatform-adapter` are all Running in
   the Aspire dashboard. The emulator + SQL containers take ~30–60s on first boot.

2. **Create a CRM contact.** Open the `crm-web` UI (or POST `crm-api`
   `/api/contacts` with `firstName`/`lastName`/`email`/`phone`). Use an email like
   `ada@techcorp.com` to steer the deterministic classifier toward `Technology`.
   CRM publishes `CrmContactCreated` (its session key is the `ContactId`).

3. **Confirm the Agent Zone parks it.** In `nimbus-ops`, open
   **Endpoints → AgentZoneEndpoint**. The `CrmContactCreated` row appears as
   `Pending` with sub-status `Handoff` ("Awaiting agent pickup").

4. **Confirm the agent runs the loop.** Watch the `enrichment-agent` logs in the
   Aspire dashboard. You should see, in order:
   - `Received CrmContactCreated event … on session …`
   - `Classified contact …: industry=…, leadScore=…`
   - `Ensured event type crm.contact.enriched.v1 is defined.` (first run only)
   - `Published crm.contact.enriched.v1 for contact …`
   - `Settled handoff … as complete.`

5. **Confirm the event type was defined.** GET `nimbus-ops` `/api/agent/catalog`
   (the typed-discovery surface) — `crm.contact.enriched.v1` is listed with its
   JSON Schema. (The dashboard search-filter dropdown is catalog-limited; the
   catalog endpoint is the discovery surface.)

6. **Confirm DataPlatform consumes the enriched event.** In `nimbus-ops`, open
   **Endpoints → DataPlatformEndpoint** — a `crm.contact.enriched.v1` row appears
   and settles `Completed`. The `dataplatform-adapter` logs a
   `DataPlatform received enriched contact … industry=… leadScore=…` line.

7. **Confirm the audit trail.** Back on **AgentZoneEndpoint**, the original
   `CrmContactCreated` row has flipped from `Pending+Handoff` to `Completed`
   (the agent's settle drove the Resolver transition). The session has no
   lingering `Pending`/`Failed`/`Deferred` rows.

## Automated equivalents

- **CI gate (in-memory, deterministic, no key/emulator):**
  `tests/EnrichmentAgent.Tests/AgentEnrichmentSmokeTests.cs` drives
  `AgentLoopWorker.ProcessNextAsync` against a realistic in-memory `IBusGateway`
  that performs **real NJsonSchema validation** (same as `/api/agent/publish`)
  backed by a real in-memory `IEventSchemaStore`, plus the real
  `DeterministicContactClassifier`. It asserts the enriched payload is
  schema-valid, the captured consumer received the classifier's output, and the
  original handoff was settled `complete`.
  Run: `dotnet test tests/EnrichmentAgent.Tests/`.

- **Live emulator e2e (Playwright):**
  `samples/CrmErpDemo/e2e/tests/07-agent-enrichment.spec.ts` creates a CRM contact
  via `crm-api` and polls `nimbus-ops` until `crm.contact.enriched.v1` is
  `Completed` on `DataPlatformEndpoint` and the original `CrmContactCreated` is
  `Completed` on `AgentZoneEndpoint`. Requires the AppHost running in emulator mode
  with `ANTHROPIC_API_KEY` unset (for deterministic classification).
  Run: from `samples/CrmErpDemo/e2e/`, `npx playwright test 07-agent-enrichment`.
