# Spec 022 v1.1 — Spec-Closure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the gap between the shipped spec-022 v1 ("demo-grade, implemented") and what the specification text actually promises — `sessionKeyPath` enforcement, a payload size cap, production-provisioner routing parity, and a verified MCP-client loop — so the spec's own success criteria are all demonstrably met.

**Architecture:** Spec 022 is already built as "MCP-over-REST": a single capability core (`/api/agent/*` in `NimBus.WebApp`) with a thin MCP adapter (`NimBus.Mcp`) and a scripted demo agent (`EnrichmentAgent`). This plan adds the missing behaviours *inside that existing core* (no new architecture) and one routing fix in the CLI provisioner. Each task is self-contained and independently testable; tasks 1–2 are pure TDD against the in-memory store, task 3 is a CLI/topology change verified by the SB emulator, task 4 is verification/runbook.

**Tech Stack:** .NET 10 (`net10.0`), MSTest (`[TestClass]`/`[TestMethod]`), Newtonsoft.Json (`JToken`/JSONPath), NJsonSchema (payload validation), Azure.Messaging.ServiceBus.Administration (provisioner), ModelContextProtocol 1.4.0 (MCP), Aspire AppHost (demo orchestration).

---

## Coverage analysis — how much of the spec is implemented

Verified against the codebase on 2026-06-07. Full solution builds clean; **253 spec-022 tests pass** (MCP 36, WebApp 84, EndToEnd 66, EnrichmentAgent 12, InMemory conformance 55).

| Spec requirement | Status | Evidence |
|---|---|---|
| 6 REST `/api/agent/*` operations (catalog/event-types/subscribe/receive/publish/settle) | ✅ Done | `AgentImplementation.cs`; `api-spec.yaml:1745-1850` |
| OpenAPI surface for every agent capability | ✅ Done | `api-spec.yaml` (NSwag-generated C# contract + TS client) |
| Schema registry `IEventSchemaStore` across 3 backends | ✅ Done | `IEventSchemaStore.cs`; InMemory/Cosmos/SQL + `EventSchemaStoreConformanceTests` |
| Publish-time JSON-Schema validation | ✅ Done | `PostAgentPublishAsync` (NJsonSchema) |
| Schema immutability / 409 on changed schema | ✅ Done | `SchemaConflictException`; `PostAgentEventTypes_different_schema_returns_409` |
| Agent Zone + `EventTypeId` routing (emulator) | ✅ Done | `EmulatorTopologyConfigBuilder.cs` |
| MCP server, 7 tools (6 1:1 + `search_failures`) | ✅ Done | `NimBusAgentTools.cs`; `NimBus.Mcp.Tests` |
| EnrichmentAgent demo + AppHost wiring (`agent-zone`, `enrichment-agent`) | ✅ Done | `CrmErpDemo.AppHost/Program.cs:246-261` |
| Unit / contract / MCP-mapping / integration / smoke tests | ✅ Done | 253 tests green |
| Phase 0 dynamic-routing gate | ✅ Done | `DynamicEventRoutingTests`, `ResolverServiceTests` |
| Docs (spec + `docs/mcp-server.md` + walkthrough) | ✅ Done | present |
| **`sessionKeyPath` enforcement on publish** | ❌ **Gap (D2)** | stored on `EventSchema` but ignored by `PostAgentPublishAsync` → **Task 1** |
| **Oversized-payload `413` / size cap** | ❌ **Gap (D3)** | no size check in publish → **Task 2** |
| **Production provisioner dynamic-forward parity** | ❌ **Gap (D5)** | `ServiceBusTopologyProvisioner` only iterates compiled `EventTypesProduced` → **Task 3** |
| **MCP-client loop verified live (success criterion #1)** | ⚠️ **Partial (D4)** | only tool→mock mapping tested; demo uses REST (`RestBusGateway`) → **Task 4** |
| Agent identity = real API key / auth | ⛔ Deferred (D1) | self-asserted `X-Agent-Id`; spec scopes production auth out → Appendix |
| Automatic crash/timeout recovery | ⛔ Deferred (D6) | operator-only; spec defers the sweeper → Appendix |

**Bottom line:** the entire *in-scope feature set* of spec 022 is implemented and tested. The open items are four spec-promise gaps (Tasks 1–4); D1 and D6 are explicitly deferred by the spec itself.

---

## File structure

This plan touches four areas, each with one clear responsibility:

- `src/NimBus.WebApp/Controllers/ApiContract/AgentImplementation.cs` — the publish endpoint gains session-key derivation (Task 1) and a size guard (Task 2). Single file, single method (`PostAgentPublishAsync`).
- `tests/NimBus.WebApp.Tests/AgentImplementationTests.cs` — new test methods + a one-line extension to the `SeedSchema` helper.
- `src/NimBus.CommandLine/ServiceBusTopologyProvisioner.cs` + `samples/CrmErpDemo/CrmErpDemo.Contracts/EmulatorTopologyConfigBuilder.cs` — share one dynamic-forward declaration and emit matching rules from both the emulator builder and the production provisioner (Task 3).
- `docs/mcp-server.md` — add a reproducible "MCP live pass" runbook (Task 4).

---

## Task 1: Enforce `sessionKeyPath` on publish (D2)

The schema can declare `sessionKeyPath` (a JSONPath like `$.orderId`) to give agent events ordered processing. Today it's stored and ignored. Make `publish` derive the session id from the payload when the caller doesn't supply one.

**Files:**
- Modify: `src/NimBus.WebApp/Controllers/ApiContract/AgentImplementation.cs` (method `PostAgentPublishAsync`, ~lines 224-284)
- Test: `tests/NimBus.WebApp.Tests/AgentImplementationTests.cs`

- [ ] **Step 1: Extend the test helper to seed a `sessionKeyPath`**

In `AgentImplementationTests.cs`, replace the existing `SeedSchema` helper (currently ~lines 610-621) with one that accepts an optional path:

```csharp
        private static async Task SeedSchema(InMemoryMessageStore store, string eventTypeId, string jsonSchema, string? sessionKeyPath = null)
        {
            await store.DefineEventType(new NimBus.MessageStore.States.EventSchema
            {
                EventTypeId = eventTypeId,
                Name = eventTypeId,
                JsonSchema = jsonSchema,
                SessionKeyPath = sessionKeyPath,
                Version = 1,
                AgentId = "test",
                CreatedUtc = DateTime.UtcNow,
            });
        }
```

- [ ] **Step 2: Write the failing tests**

Add to the `PostAgentPublishAsync` region of `AgentImplementationTests.cs`:

```csharp
        [TestMethod]
        public async Task PostAgentPublish_derives_sessionId_from_sessionKeyPath_when_not_supplied()
        {
            var (impl, store, publisher) = BuildWithPublisher();
            await SeedSchema(store, "order.placed.v1",
                "{\"type\":\"object\",\"properties\":{\"orderId\":{\"type\":\"string\"}}}",
                sessionKeyPath: "$.orderId");

            var result = await impl.PostAgentPublishAsync(new AgentPublishRequest
            {
                EventTypeId = "order.placed.v1",
                Payload = "{\"orderId\":\"o-42\"}",
                // SessionId deliberately omitted.
            });

            Assert.IsInstanceOfType(result, typeof(OkResult));
            Assert.AreEqual("o-42", publisher.Published?.SessionId,
                "sessionId must be derived from the payload via sessionKeyPath");
        }

        [TestMethod]
        public async Task PostAgentPublish_explicit_sessionId_overrides_sessionKeyPath()
        {
            var (impl, store, publisher) = BuildWithPublisher();
            await SeedSchema(store, "order.placed.v1",
                "{\"type\":\"object\",\"properties\":{\"orderId\":{\"type\":\"string\"}}}",
                sessionKeyPath: "$.orderId");

            var result = await impl.PostAgentPublishAsync(new AgentPublishRequest
            {
                EventTypeId = "order.placed.v1",
                Payload = "{\"orderId\":\"o-42\"}",
                SessionId = "explicit-session",
            });

            Assert.IsInstanceOfType(result, typeof(OkResult));
            Assert.AreEqual("explicit-session", publisher.Published?.SessionId,
                "An explicit sessionId must win over sessionKeyPath derivation");
        }

        [TestMethod]
        public async Task PostAgentPublish_missing_sessionKey_value_returns_400()
        {
            var (impl, store, publisher) = BuildWithPublisher();
            // Schema declares a sessionKeyPath but does NOT require the field, so a payload
            // without it is schema-valid yet has no session key -> 400.
            await SeedSchema(store, "order.placed.v1",
                "{\"type\":\"object\",\"properties\":{\"orderId\":{\"type\":\"string\"}}}",
                sessionKeyPath: "$.orderId");

            var result = await impl.PostAgentPublishAsync(new AgentPublishRequest
            {
                EventTypeId = "order.placed.v1",
                Payload = "{\"note\":\"no order id here\"}",
            });

            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "A declared sessionKeyPath with no value in the payload must yield 400");
            Assert.AreEqual(0, publisher.CallCount, "Nothing must publish when the session key is missing");
        }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/NimBus.WebApp.Tests/NimBus.WebApp.Tests.csproj --filter "sessionKeyPath|sessionKey_value" -v minimal`
Expected: FAIL — `derives_sessionId...` asserts `o-42` but gets a random GUID; `missing_sessionKey_value...` expects 400 but gets 200.

- [ ] **Step 4: Implement session-key derivation**

In `AgentImplementation.cs`, add the using at the top (after the existing usings):

```csharp
using Newtonsoft.Json.Linq;
```

In `PostAgentPublishAsync`, immediately after the schema-validation block (right after `if (errors.Count > 0) return ...;` and before `var message = new CoreMessage`), insert:

```csharp
            // Resolve the session id for ordered processing. Precedence:
            //   1. An explicit sessionId on the request always wins.
            //   2. Else, if the schema declares a sessionKeyPath, derive the key from the
            //      (already schema-valid) payload via JSONPath. A declared path that
            //      resolves to no scalar is a client error (400): the agent asked for
            //      ordering but gave us no key.
            //   3. Else fall back to a fresh GUID (unordered).
            string sessionId;
            if (!string.IsNullOrWhiteSpace(body.SessionId))
            {
                sessionId = body.SessionId;
            }
            else if (!string.IsNullOrWhiteSpace(schema.SessionKeyPath))
            {
                JToken? token;
                try
                {
                    // body.Payload already parsed cleanly during schema validation above.
                    token = JToken.Parse(body.Payload).SelectToken(schema.SessionKeyPath);
                }
                catch (Newtonsoft.Json.JsonException ex)
                {
                    // A malformed sessionKeyPath expression in the registered schema.
                    _logger.LogError(ex, "Invalid sessionKeyPath '{Path}' on schema {EventTypeId}", schema.SessionKeyPath, body.EventTypeId);
                    return new BadRequestObjectResult($"Registered sessionKeyPath '{schema.SessionKeyPath}' is not a valid JSONPath: {ex.Message}");
                }

                var isScalar = token != null
                    && token.Type is not JTokenType.Null
                    && token.Type is not JTokenType.Object
                    && token.Type is not JTokenType.Array;
                if (!isScalar)
                    return new BadRequestObjectResult(
                        $"Payload has no scalar value at sessionKeyPath '{schema.SessionKeyPath}' (required by event type '{body.EventTypeId}').");
                sessionId = token!.ToString();
            }
            else
            {
                sessionId = Guid.NewGuid().ToString();
            }
```

Then change the message initializer's `SessionId` line from:

```csharp
                SessionId = string.IsNullOrWhiteSpace(body.SessionId) ? Guid.NewGuid().ToString() : body.SessionId,
```

to:

```csharp
                SessionId = sessionId,
```

- [ ] **Step 5: Run the tests to verify they pass (and nothing regressed)**

Run: `dotnet test tests/NimBus.WebApp.Tests/NimBus.WebApp.Tests.csproj -v minimal`
Expected: PASS — 87 tests (the prior 84 + 3 new). In particular `PostAgentPublish_valid_payload_without_sessionId_generates_one` still passes (no schema path → GUID fallback).

- [ ] **Step 6: Commit**

```bash
git add src/NimBus.WebApp/Controllers/ApiContract/AgentImplementation.cs tests/NimBus.WebApp.Tests/AgentImplementationTests.cs
git commit -m "feat(agent-api): derive sessionId from schema sessionKeyPath on publish (spec 022 D2)"
```

---

## Task 2: Reject oversized payloads with `413` (D3)

The error-handling table promises a size cap. Add a conservative byte cap to `publish`, returning `413` before any store or bus work.

**Files:**
- Modify: `src/NimBus.WebApp/Controllers/ApiContract/AgentImplementation.cs`
- Modify: `src/NimBus.WebApp/api-spec.yaml` (document the `413` response)
- Test: `tests/NimBus.WebApp.Tests/AgentImplementationTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `AgentImplementationTests.cs`. Add `using Microsoft.AspNetCore.Http;` to the file's usings (for `StatusCodes`), then:

```csharp
        [TestMethod]
        public async Task PostAgentPublish_oversized_payload_returns_413_and_does_not_publish()
        {
            var (impl, store, publisher) = BuildWithPublisher();
            // Permissive schema so size — not shape — is what trips the guard.
            await SeedSchema(store, "crm.lead.v1", "{\"type\":\"object\"}");

            // Valid JSON, comfortably over the 192 KB cap.
            var big = new string('x', 200 * 1024);
            var payload = "{\"blob\":\"" + big + "\"}";

            var result = await impl.PostAgentPublishAsync(new AgentPublishRequest
            {
                EventTypeId = "crm.lead.v1",
                Payload = payload,
            });

            var obj = result as ObjectResult;
            Assert.IsNotNull(obj, "Expected an ObjectResult carrying 413");
            Assert.AreEqual(StatusCodes.Status413PayloadTooLarge, obj!.StatusCode);
            Assert.AreEqual(0, publisher.CallCount, "Oversized payload must not publish");
        }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/NimBus.WebApp.Tests/NimBus.WebApp.Tests.csproj --filter "oversized_payload" -v minimal`
Expected: FAIL — currently the big-but-schema-valid payload returns `200 OkResult` (not an `ObjectResult` with 413), and `CallCount` is 1.

- [ ] **Step 3: Implement the size guard**

In `AgentImplementation.cs`, add a constant inside the class (next to the other `private const` fields near the top):

```csharp
        // Reject oversized payloads with 413 (spec 022 error handling). Conservative cap,
        // well under the Service Bus standard-tier 256 KB message limit, leaving headroom
        // for envelope/metadata. Large bodies should use the claim-check path (spec 013);
        // that integration is tracked separately.
        private const int MaxPayloadBytes = 192 * 1024;
```

In `PostAgentPublishAsync`, immediately after the two blank-input guards (after the `if (string.IsNullOrWhiteSpace(body.Payload)) ...` line) and before the schema lookup, insert:

```csharp
            if (System.Text.Encoding.UTF8.GetByteCount(body.Payload) > MaxPayloadBytes)
                return new ObjectResult($"Payload exceeds the {MaxPayloadBytes}-byte limit. Use the claim-check path for large bodies.")
                {
                    StatusCode = StatusCodes.Status413PayloadTooLarge,
                };
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/NimBus.WebApp.Tests/NimBus.WebApp.Tests.csproj --filter "oversized_payload" -v minimal`
Expected: PASS.

- [ ] **Step 5: Document the `413` in the OpenAPI spec**

In `src/NimBus.WebApp/api-spec.yaml`, under the `/api/agent/publish` `post:` `responses:` block (near line 1818), add a `'413'` entry alongside the existing `'200'`/`'400'`/`'404'`, matching the surrounding YAML indentation:

```yaml
        '413':
          description: Payload exceeds the maximum allowed size.
```

- [ ] **Step 6: Rebuild to confirm the NSwag contract still generates and compiles**

Run: `dotnet build src/NimBus.WebApp/NimBus.WebApp.csproj -v minimal`
Expected: `Build succeeded. 0 Error(s)` (the contract regenerates from `api-spec.yaml` during build).

- [ ] **Step 7: Commit**

```bash
git add src/NimBus.WebApp/Controllers/ApiContract/AgentImplementation.cs src/NimBus.WebApp/api-spec.yaml tests/NimBus.WebApp.Tests/AgentImplementationTests.cs
git commit -m "feat(agent-api): cap publish payload size with 413 (spec 022 D3)"
```

---

## Task 3: Production-provisioner dynamic-forward parity (D5)

Today the emulator builder hardcodes the `crm.contact.enriched.v1` forward (`EmulatorTopologyConfigBuilder.cs:44-47`), and the production `ServiceBusTopologyProvisioner` has the same gap — its forward loop only iterates compiled `endpoint.EventTypesProduced` (`ServiceBusTopologyProvisioner.cs:117-147`), so dynamically-typed events get no forward subscription on real Azure. Make both consume one declaration and have the production provisioner emit the matching rule.

> **Design decision to confirm before coding.** Dynamic forwards are demo/deployment data, declared code-first (consistent with ADR-007). Recommended home: a single `DynamicForwards` declaration in `CrmErpDemo.Contracts`, consumed by *both* builders. The one wiring question is how the production `ServiceBusTopologyProvisioner` (default `_platformFactory = () => new PlatformConfiguration()`, `ServiceBusTopologyProvisioner.cs:40`) obtains it for the demo platform. Resolve this first — read how the `nb` CLI / demo provisioner constructs the platform it passes in — then pick: **(a)** add `IReadOnlyList<DynamicForward> DynamicForwards => Array.Empty<DynamicForward>();` as a default-interface member on `NimBus.Core.IPlatform` and have the demo platform supply the list (cleanest, generic), or **(b)** pass the list as an explicit parameter to `ApplyCoreAsync` from the demo's provisioning entry point (smaller blast radius, demo-only). Steps below assume (a); if you choose (b), the rule-emission code in Step 4 is identical — only the source of `forwards` changes.

**Files:**
- Create: `src/NimBus.Core/Endpoints/DynamicForward.cs`
- Modify: `src/NimBus.Core/.../IPlatform.cs` (add the default-interface member)
- Modify: `samples/CrmErpDemo/CrmErpDemo.Contracts/EmulatorTopologyConfigBuilder.cs` (read shared list instead of the private `DynamicForwards`)
- Modify: the demo platform config in `CrmErpDemo.Contracts` (supply the list)
- Modify: `src/NimBus.CommandLine/ServiceBusTopologyProvisioner.cs` (emit forward sub + rule)
- Test: `tests/CrmErpDemo.Contracts.Tests/EmulatorTopologyConfigBuilderTests.cs` (or the existing topology test project for the demo)

- [ ] **Step 1: Define the shared declaration type**

Create `src/NimBus.Core/Endpoints/DynamicForward.cs`:

```csharp
namespace NimBus.Core.Endpoints;

/// <summary>
/// A code-first declaration that a dynamically-typed event (identified only by its
/// string <see cref="EventTypeId"/>, with no compiled <c>IEvent</c> class) published on
/// <see cref="SourceEndpoint"/> must be forwarded to <see cref="TargetEndpoint"/>.
/// Used by topology provisioning to create the forward subscription + EventTypeId rule
/// that the compiled-event forward loop cannot derive (spec 022).
/// </summary>
public sealed record DynamicForward(string SourceEndpoint, string EventTypeId, string TargetEndpoint);
```

- [ ] **Step 2: Expose forwards from the platform (default empty)**

In `NimBus.Core`'s `IPlatform` interface, add a default-interface member so existing implementers (and test fakes) are unaffected:

```csharp
    /// <summary>
    /// Dynamically-typed event forwards (spec 022). Empty for platforms with no agent
    /// dynamic events. Provisioning creates a forward subscription + EventTypeId rule for each.
    /// </summary>
    System.Collections.Generic.IReadOnlyList<NimBus.Core.Endpoints.DynamicForward> DynamicForwards
        => System.Array.Empty<NimBus.Core.Endpoints.DynamicForward>();
```

- [ ] **Step 3: Move the demo's list onto the demo platform and have the emulator builder read it**

In the demo's platform config (`CrmErpDemo.Contracts`), override `DynamicForwards`:

```csharp
    public IReadOnlyList<DynamicForward> DynamicForwards { get; } = new[]
    {
        new DynamicForward("AgentZoneEndpoint", "crm.contact.enriched.v1", "DataPlatformEndpoint"),
    };
```

In `EmulatorTopologyConfigBuilder.cs`, delete the private `DynamicForwards` field (lines 44-47) and read `platform.DynamicForwards` in `Build(IPlatform platform)` instead, so the single declaration drives the emulator JSON.

- [ ] **Step 4: Emit the forward sub + rule in the production provisioner**

In `ServiceBusTopologyProvisioner.cs`, at the end of `ApplyCoreAsync` (after the per-endpoint loop, ~line 94), add a dynamic-forward pass that reuses the existing helpers (`EnsureForwardSubscriptionAsync` and `EnsureRuleAsync` — same signatures used for compiled forwards at lines 125 and 138-145):

```csharp
        foreach (var fwd in platform.DynamicForwards.OrderBy(f => f.EventTypeId, StringComparer.Ordinal))
        {
            // Mirror the compiled-event forward rule (lines 117-146), but keyed on the
            // dynamic EventTypeId string instead of a compiled event type. The
            // "user.From IS NULL" guard prevents the same forward loop the compiled path
            // documents (only ORIGINAL publishes match; forwarded copies carry From).
            await EnsureForwardSubscriptionAsync(client, fwd.SourceEndpoint, fwd.TargetEndpoint, fwd.TargetEndpoint, cancellationToken).ConfigureAwait(false);
            await EnsureRuleAsync(
                client,
                fwd.SourceEndpoint,
                fwd.TargetEndpoint,
                $"dyn-{fwd.EventTypeId}",
                $"user.EventTypeId = '{fwd.EventTypeId}' AND user.From IS NULL",
                $"SET user.From = '{fwd.SourceEndpoint}'; SET user.EventId = newid(); SET user.To = '{fwd.TargetEndpoint}';",
                cancellationToken).ConfigureAwait(false);
        }
```

- [ ] **Step 5: Write a unit test for the emulator builder seam**

The provisioner itself talks to Azure and is verified by the live smoke (Step 7); the *builder* is pure and unit-testable. In the demo's topology test project, add:

```csharp
        [TestMethod]
        public void Build_emits_dynamic_forward_subscription_for_declared_forwards()
        {
            // A platform that declares one dynamic forward.
            var platform = new CrmErpDemoPlatformConfiguration(); // supplies DynamicForwards

            var json = EmulatorTopologyConfigBuilder.Build(platform);

            // The AgentZone topic must carry a forward subscription to DataPlatform keyed
            // on the dynamic EventTypeId, proving the single declaration drives the JSON.
            StringAssert.Contains(json, "crm.contact.enriched.v1",
                "Emulator config must include the declared dynamic forward's EventTypeId");
            StringAssert.Contains(json, "DataPlatformEndpoint",
                "Emulator config must forward the dynamic event to its declared target");
        }
```

(If a topology test project for the demo does not yet exist, create `tests/CrmErpDemo.Contracts.Tests/` with the standard MSTest csproj referencing `CrmErpDemo.Contracts`, and add `#pragma warning disable CA1707, CA2007` at the top of the test file.)

- [ ] **Step 6: Run the unit test + full demo build**

Run: `dotnet test tests/CrmErpDemo.Contracts.Tests/CrmErpDemo.Contracts.Tests.csproj -v minimal`
Expected: PASS. Then `dotnet build src/NimBus.sln -v minimal` → `0 Error(s)` (the `IPlatform` default member must not break any implementer).

- [ ] **Step 7: Verify routing on the live SB emulator (manual — needs Docker)**

The production rule cannot be unit-tested (it talks to `ServiceBusAdministrationClient`). Verify parity against the emulator:

```bash
dotnet run --project samples/CrmErpDemo/CrmErpDemo.AppHost
# In the CRM UI, create a contact; confirm in the WebApp dashboard that
# crm.contact.enriched.v1 is forwarded to DataPlatform (i.e. the enriched
# event lands in ERP/DataPlatform exactly as in the emulator path).
```

Expected: the enriched event reaches DataPlatform — the same outcome the hardcoded list produced, now driven by the shared declaration.

- [ ] **Step 8: Commit**

```bash
git add src/NimBus.Core/Endpoints/DynamicForward.cs src/NimBus.CommandLine/ServiceBusTopologyProvisioner.cs samples/CrmErpDemo/CrmErpDemo.Contracts tests/CrmErpDemo.Contracts.Tests
git commit -m "feat(provisioner): emit dynamic-event forward rules from a shared declaration (spec 022 D5)"
```

---

## Task 4: Verify the MCP-client loop end-to-end (D4)

Success criterion #1 ("an external MCP client completes the full loop") is only mapping-tested today; the demo drives REST directly via `RestBusGateway`, and `NimBus.Mcp` is not wired into the AppHost. Close the verification gap with a reproducible runbook driving the *real* MCP server against a running platform. (This is verification + docs, not new business logic — the MCP server already exists and its tools are mapping-tested.)

**Files:**
- Modify: `docs/mcp-server.md` (add a "Live MCP pass" section)

- [ ] **Step 1: Add the runbook to `docs/mcp-server.md`**

Append a section that an operator (or a CI job with Docker + an API key) can follow:

```markdown
## Live MCP pass (success criterion #1)

Drives the full discover → define → subscribe → receive → publish → settle loop
through the MCP server (not REST), against a running platform.

1. Start the platform so the agent REST core is reachable:
   `dotnet run --project samples/CrmErpDemo/CrmErpDemo.AppHost`
   Note the `nimbus-ops` HTTP endpoint from the Aspire dashboard.
2. Build the MCP server: `dotnet build src/NimBus.Mcp`.
3. Point an MCP client at it over stdio. For Claude Desktop / Claude Code, add to the
   MCP config (adjust the path and base URL):
   ```json
   {
     "mcpServers": {
       "nimbus": {
         "command": "dotnet",
         "args": ["run", "--project", "src/NimBus.Mcp"],
         "env": {
           "NimBus__AgentApi__BaseUrl": "http://localhost:<nimbus-ops-port>",
           "NimBus__AgentApi__AgentId": "claude-mcp"
         }
       }
     }
   }
   ```
4. From the MCP client, run the loop: `discover_topology` → `define_event_type`
   (`crm.contact.enriched.v1`) → `subscribe` → create a CRM contact in the UI →
   `receive_messages` → `publish_event` → `settle_message`.
5. Confirm in the WebApp dashboard that the chain is audited under agentId `claude-mcp`,
   exactly as the scripted REST runner produces.
```

(Use the real config keys from `src/NimBus.Mcp/Configuration/NimBusMcpOptions.cs` — confirm the exact `NimBus__AgentApi__*` names against that file before publishing the doc.)

- [ ] **Step 2: (Optional, if you want this in the demo) wire `NimBus.Mcp` into the AppHost**

`NimBus.Mcp` is a stdio server, so it is normally spawned by the MCP client, not hosted as an Aspire resource. Only add it to `CrmErpDemo.AppHost/Program.cs` if you first add an HTTP/SSE transport (the spec lists "HTTP/SSE optional"). Treat that transport as its own task — do not block this runbook on it.

- [ ] **Step 3: Run the live pass once and record the result**

Follow the runbook with a real MCP client (Docker emulator + `ANTHROPIC_API_KEY`). Capture that the audit trail shows the loop completing under the MCP agent id.

- [ ] **Step 4: Commit**

```bash
git add docs/mcp-server.md
git commit -m "docs(mcp): add reproducible live MCP-client pass runbook (spec 022 D4)"
```

---

## Appendix — deferred by the spec (not in this plan)

These are real gaps but the spec scopes them out of v1 (see §Future sub-projects). Listed so the breakdown is complete; each is its own future spec → plan cycle.

- **D1 — Production agent auth.** Replace the self-asserted `X-Agent-Id` header with scoped API keys / OAuth and per-agent authorization on `/api/agent/*`. (Spec: "Production auth" future sub-project.)
- **D6 — Automatic recovery sweeper.** A background service that fails or flags parked `Pending+Handoff` events older than a configurable timeout (today recovery is operator-only via Resubmit/Skip + `ExpectedBy`). (Spec: "An automatic timeout sweeper is deferred.")
- **Push delivery, NimBus.Agents SDK, ops/SRE agent, fully-dynamic topology** — unchanged from §Future sub-projects.

---

## Self-review

- **Spec coverage:** Every ❌/⚠️ row in the coverage matrix maps to a task — D2→Task 1, D3→Task 2, D5→Task 3, D4→Task 4. D1/D6 are explicitly deferred by the spec and listed in the appendix. All ✅ rows are already implemented and verified (253 tests).
- **Placeholder scan:** Tasks 1–2 contain complete, compile-ready code and exact commands. Task 3 contains complete rule-emission code using the provisioner's real helper signatures; the one genuine design decision (where `DynamicForwards` is sourced) is called out with two concrete options rather than left as "TBD", and the rule code is identical under either. Task 4 is verification/docs by nature (the MCP server already exists) — its "code" is a runbook, with a note to confirm the exact option keys against `NimBusMcpOptions.cs`.
- **Type consistency:** `EventSchema.SessionKeyPath` (`string?`, `EventSchema.cs:20`) matches Task 1; `IAgentEventPublisher.PublishAsync` captures `message.SessionId` (test harness `CapturingPublisher`) — the assertions match the field set in `PostAgentPublishAsync`. `StatusCodes.Status413PayloadTooLarge` and `ObjectResult` are already imported in `AgentImplementation.cs`. `DynamicForward(SourceEndpoint, EventTypeId, TargetEndpoint)` is used consistently across Steps 1, 3, and 4.
