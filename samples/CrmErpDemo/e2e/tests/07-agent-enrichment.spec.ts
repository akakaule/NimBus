import { test, expect } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { NimBusApiClient, type NimBusEvent } from "../helpers/nimbus-api-client.js";
import { Timeouts } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

/**
 * AI agent bus participation — end-to-end enrichment loop (spec 022, Phase 3).
 *
 * REQUIRES the live CrmErpDemo AppHost running in emulator mode
 * (`UseEmulator=true` / `NIMBUS_SB_EMULATOR=true`). Leave ANTHROPIC_API_KEY
 * UNSET so the EnrichmentAgent uses its DeterministicContactClassifier — the
 * classification is then stable and this spec stays deterministic in CI.
 *
 * Flow under test:
 *   1. CRM creates a contact → publishes CrmContactCreated (SessionKey=ContactId).
 *   2. The `agent-zone` park host subscribes to AgentZoneEndpoint and parks the
 *      CrmContactCreated as Pending+Handoff (awaiting agent pickup).
 *   3. The `enrichment-agent` receives it over the REST agent API on nimbus-ops,
 *      classifies it, first-run defines `crm.contact.enriched.v1`, publishes the
 *      enriched event (schema-validated), then settles the original handoff.
 *   4. The Agent Zone dynamic forward rule routes `crm.contact.enriched.v1` to
 *      DataPlatformEndpoint, where EnrichedContactHandler consumes it (Task D).
 *
 * Verification: the enriched event surfaces Completed on DataPlatformEndpoint for
 * the contact's session, AND the original CrmContactCreated audit on
 * AgentZoneEndpoint flips to Completed once the agent settles the handoff.
 */
test.describe("Agent enrichment: CRM contact → park → agent enrich → DataPlatform consumes → original completes", () => {
  const AgentZoneEndpoint = "AgentZoneEndpoint";
  const DataPlatformEndpoint = "DataPlatformEndpoint";
  const EnrichedEventTypeId = "crm.contact.enriched.v1";

  let crm: CrmApiClient;
  let nimbus: NimBusApiClient;

  test.beforeAll(async () => {
    crm = await CrmApiClient.create();
    nimbus = await NimBusApiClient.create();
  });

  test.afterAll(async () => {
    await crm.dispose();
    await nimbus.dispose();
  });

  test("create contact → enriched event consumed on DataPlatform + original CrmContactCreated completes", async () => {
    // ── 1. Create a CRM contact. The "tech" domain steers the deterministic
    //       classifier to "Technology", but we only assert the enriched event
    //       flows through — the exact industry is the agent's concern.
    const stamp = Date.now();
    const contact = await crm.createContact({
      firstName: "Ada",
      lastName: `Agent-${stamp}`,
      email: `ada.${stamp}@techcorp.com`,
      phone: "+1-555-0142",
    });
    expect(contact.id, "created contact id").toBeTruthy();

    // The CrmContactCreated event's session key is the ContactId, so both the
    // parked original and the published enriched event share session = contact.id.
    const sessionId = contact.id;

    // ── 2. The enriched event should land Completed on DataPlatformEndpoint
    //       (parked → agent classifies/publishes → forward rule → consumer settles).
    const enriched: NimBusEvent = await waitFor(
      async () => {
        const events = await nimbus.searchEvents(DataPlatformEndpoint, {
          sessionId,
          eventTypeId: [EnrichedEventTypeId],
          resolutionStatus: ["Completed"],
        });
        return events[0] ?? null;
      },
      {
        timeoutMs: Timeouts.propagationMs,
        description: `${EnrichedEventTypeId} Completed on ${DataPlatformEndpoint} for session ${sessionId}`,
      },
    );
    expect(enriched.eventTypeId).toBe(EnrichedEventTypeId);

    // ── 3. The original CrmContactCreated on AgentZoneEndpoint should flip from
    //       Pending+Handoff to Completed once the agent settles the handoff.
    await waitFor(
      async () => {
        const events = await nimbus.searchEvents(AgentZoneEndpoint, {
          sessionId,
          eventTypeId: ["CrmContactCreated"],
          resolutionStatus: ["Completed"],
        });
        return events.length > 0 ? events : null;
      },
      {
        timeoutMs: Timeouts.propagationMs,
        description: `CrmContactCreated Completed on ${AgentZoneEndpoint} for session ${sessionId}`,
      },
    );

    // ── 4. No lingering non-terminal audit rows for this session on the Agent Zone
    //       (the handoff settled cleanly, nothing stuck Pending/Failed/Deferred).
    const stuck = (
      await nimbus.searchEvents(AgentZoneEndpoint, {
        sessionId,
        resolutionStatus: ["Failed", "Deferred", "DeadLettered", "Pending"],
      })
    ).filter((e) => e.sessionId === sessionId);
    expect.soft(stuck.length, "no lingering non-terminal Agent Zone audit rows").toBe(0);
  });
});
