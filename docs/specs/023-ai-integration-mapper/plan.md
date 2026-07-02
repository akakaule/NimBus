# AI Integration Mapper Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an AI agent author a declarative event-to-event mapping once, have a human approve it, and have a first-class NimBus component apply it deterministically to every matching message — translating one system's event contract into another's with no hand-written integration code.

**Architecture:** Cold path (authoring) = an external agent reads source+target JSON Schemas from the spec-022 catalog and emits a JSONata transform stored in a new **mapping registry**. Hot path (execution) = a **Mapping Executor** (NimBus.SDK worker) owns a pre-provisioned **Mapping Zone**, registers one **fallback handler**, and per message looks up the source type in the registry; for an **Active** mapping it transforms, validates against the target schema, and publishes — no LLM at runtime. Reuses spec 022 wholesale (schema registry, dynamic routing, MCP, pending-handoff recovery, audit).

**Tech Stack:** .NET 10 (`net10.0`), MSTest, Newtonsoft.Json, NJsonSchema (validation), a sandboxed JSONata engine (library selected in Phase 2), NSwag (api-spec.yaml → C# contract + TS client), React + Vite + Tailwind (WebApp), Aspire AppHost (demo).

This plan follows the spec at [`spec.md`](spec.md) and reuses the patterns shipped by spec 022.

---

## Prerequisite — Phase 0: spec-022 routing parity (D5)

The Executor only works if source events physically reach the Mapping Zone and target events reach their consumer. Both require **declared dynamic forwards** in the emulator builder *and* the production provisioner. That is exactly **Task 3 (D5)** of [`../022-ai-agent-bus-participation/plan-v1.1-spec-closure.md`](../022-ai-agent-bus-participation/plan-v1.1-spec-closure.md).

- [ ] **Step 0.1:** Complete spec-022 plan-v1.1 **Task 3 (D5)** — the shared `DynamicForward` declaration consumed by `EmulatorTopologyConfigBuilder` and `ServiceBusTopologyProvisioner`. **Do not start Phase 7 (demo) until this is done.** Phases 1–6 do not depend on it and can proceed in parallel.

---

## File structure

```
src/NimBus.MessageStore.Abstractions/
  States/EventMapping.cs           # NEW: EventMapping record + MappingState enum
  IEventMappingStore.cs            # NEW: registry contract (mirrors IEventSchemaStore.cs)
  INimBusMessageStore.cs           # MODIFY: add IEventMappingStore to the facade

src/NimBus.Testing/Conformance/
  EventMappingStoreConformanceTests.cs   # NEW: provider-agnostic suite (mirrors EventSchemaStoreConformanceTests.cs)
  InMemoryMessageStore.cs                # MODIFY: implement IEventMappingStore

src/NimBus.MessageStore.CosmosDb/CosmosDbClient.cs    # MODIFY: implement IEventMappingStore
src/NimBus.MessageStore.SqlServer/SqlServerMessageStore.cs (+ DbUp script)  # MODIFY: implement IEventMappingStore

src/NimBus.Core/Transform/
  IMappingTransformEngine.cs       # NEW: Transform(transform, inputJson) -> string
  JsonataTransformEngine.cs        # NEW: wraps the selected JSONata library

src/NimBus.WebApp/
  api-spec.yaml                    # MODIFY: add /api/agent/mappings/* (tag: AgentMappings)
  Controllers/ApiContract/MappingImplementation.cs   # NEW: implements IAgentMappingsApiController
  ClientApp/src/pages/mappings.tsx # NEW: review page (mirrors pages/audits-list.tsx)

src/NimBus.SDK/
  EventHandlers/EventContextHandler.cs   # MODIFY: fallback handler on GetHandler miss
  Extensions/NimBusSubscriberBuilder.cs  # MODIFY: AddDynamicFallbackHandler(...)

src/NimBus.MappingExecutor/             # NEW project: hosted worker
  MappingExecutorHandler.cs              # the fallback IEventJsonHandler (core logic)
  MappingExecutorRegistration.cs         # DI + subscriber wiring on the Mapping Zone

src/NimBus.Mcp/
  Tools/NimBusAgentTools.cs        # MODIFY: propose_mapping, list_mappings
  Http/INimBusAgentApi.cs (+ client) # MODIFY: mapping calls

samples/CrmErpDemo/
  Marketing.Api/                   # NEW: emits marketing.lead.created.v1
  MappingAgent/                    # NEW: scripted authoring agent
  CrmErpDemo.Contracts/            # MODIFY: register contracts + Mapping Zone + forwards
  CrmErpDemo.AppHost/Program.cs    # MODIFY: run Marketing.Api, MappingAgent, Mapping Executor

tests/
  NimBus.MessageStore.{InMemory,CosmosDb,SqlServer}.Tests/   # register EventMappingStoreConformanceTests
  NimBus.Core.Tests/JsonataTransformEngineTests.cs           # NEW
  NimBus.WebApp.Tests/MappingImplementationTests.cs          # NEW (mirrors AgentImplementationTests.cs)
  NimBus.SDK.Tests/FallbackHandlerTests.cs                   # NEW
  NimBus.MappingExecutor.Tests/MappingExecutorHandlerTests.cs # NEW
  NimBus.Mcp.Tests/ToolMappingTests.cs                       # MODIFY: mapping tools
  MappingAgent.Tests/ (+ demo smoke)                         # NEW
```

---

## Phase 1: Mapping registry

### Task 1: `EventMapping` record + `MappingState` enum

**Files:**
- Create: `src/NimBus.MessageStore.Abstractions/States/EventMapping.cs`

- [ ] **Step 1: Create the type** (no test — pure data; exercised by Task 2's conformance suite)

```csharp
using System;

namespace NimBus.MessageStore.States;

/// <summary>Lifecycle state of an agent-authored event mapping (spec 023).</summary>
public enum MappingState
{
    Draft,
    Active,
    Paused,
    Stale,
    Rejected,
}

/// <summary>
/// An agent-authored, declarative transform from a source event type to a target event
/// type. Authored once by an agent, approved by an operator, then applied deterministically
/// by the Mapping Executor. Mirrors <see cref="EventSchema"/>'s storage shape.
/// </summary>
public class EventMapping
{
    /// <summary>Stable id: "{sourceEventTypeId}->{targetEventTypeId}".</summary>
    public string Id { get; set; } = string.Empty;
    public string SourceEventTypeId { get; set; } = string.Empty;
    public string TargetEventTypeId { get; set; } = string.Empty;

    /// <summary>The reusable artifact: a JSONata expression mapping source JSON to target JSON.</summary>
    public string Transform { get; set; } = string.Empty;

    /// <summary>The LLM's short explanation, shown to the operator during review.</summary>
    public string? Rationale { get; set; }

    /// <summary>Serialized JSON array of { source, output } worked examples.</summary>
    public string? WorkedExamplesJson { get; set; }

    /// <summary>Fingerprint of the source schema at authoring time, for drift detection.</summary>
    public string SourceSchemaHash { get; set; } = string.Empty;

    public MappingState State { get; set; } = MappingState.Draft;
    public int Version { get; set; } = 1;

    public string? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedUtc { get; set; }
}
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build src/NimBus.MessageStore.Abstractions/NimBus.MessageStore.Abstractions.csproj -v minimal`
Expected: `0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add src/NimBus.MessageStore.Abstractions/States/EventMapping.cs
git commit -m "feat(store): EventMapping record + MappingState enum (spec 023)"
```

### Task 2: `IEventMappingStore` contract + conformance suite

**Files:**
- Create: `src/NimBus.MessageStore.Abstractions/IEventMappingStore.cs`
- Create: `src/NimBus.Testing/Conformance/EventMappingStoreConformanceTests.cs`

- [ ] **Step 1: Write the contract**

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Registry of agent-authored event mappings (spec 023). Mirrors <see cref="IEventSchemaStore"/>.
/// Unlike schemas, mappings are mutable across their lifecycle (Draft→Active→…→re-author).
/// </summary>
public interface IEventMappingStore
{
    /// <summary>Returns the mapping by id, or null if unknown.</summary>
    Task<EventMapping?> GetMapping(string id);

    /// <summary>The Executor's hot lookup: the single Active mapping for a source type, or null.</summary>
    Task<EventMapping?> GetActiveMappingForSource(string sourceEventTypeId);

    /// <summary>All mappings (for the review UI / list API).</summary>
    Task<IReadOnlyList<EventMapping>> GetMappings();

    /// <summary>Upsert by <see cref="EventMapping.Id"/>. Returns the stored record.</summary>
    Task<EventMapping> SaveMapping(EventMapping mapping);
}
```

- [ ] **Step 2: Write the conformance suite (the failing tests)**

```csharp
#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>Provider-agnostic conformance suite for <see cref="IEventMappingStore"/>.</summary>
[TestClass]
public abstract class EventMappingStoreConformanceTests
{
    protected abstract IEventMappingStore CreateStore();

    private static EventMapping Sample(string source, string target = "erp.customer.upsert.v1", MappingState state = MappingState.Draft)
        => new EventMapping
        {
            Id = $"{source}->{target}",
            SourceEventTypeId = source,
            TargetEventTypeId = target,
            Transform = "{ \"id\": id }",
            SourceSchemaHash = "hash-1",
            State = state,
            Version = 1,
            CreatedBy = "agent-1",
            CreatedUtc = new DateTime(2026, 06, 07, 0, 0, 0, DateTimeKind.Utc),
        };

    [TestMethod]
    public async Task SaveMapping_then_GetMapping_round_trips()
    {
        var store = CreateStore();
        var src = $"src.{Guid.NewGuid():N}.v1";
        var saved = await store.SaveMapping(Sample(src));
        var got = await store.GetMapping(saved.Id);
        Assert.IsNotNull(got);
        Assert.AreEqual(src, got!.SourceEventTypeId);
        Assert.AreEqual(MappingState.Draft, got.State);
    }

    [TestMethod]
    public async Task GetMapping_unknown_returns_null()
    {
        var store = CreateStore();
        Assert.IsNull(await store.GetMapping($"x.{Guid.NewGuid():N}->y"));
    }

    [TestMethod]
    public async Task SaveMapping_upserts_by_id()
    {
        var store = CreateStore();
        var src = $"src.{Guid.NewGuid():N}.v1";
        await store.SaveMapping(Sample(src));
        var updated = Sample(src, state: MappingState.Active);
        await store.SaveMapping(updated);
        var got = await store.GetMapping(updated.Id);
        Assert.AreEqual(MappingState.Active, got!.State);
        Assert.AreEqual(1, (await store.GetMappings()).Count(m => m.Id == updated.Id));
    }

    [TestMethod]
    public async Task GetActiveMappingForSource_returns_only_active()
    {
        var store = CreateStore();
        var src = $"src.{Guid.NewGuid():N}.v1";
        await store.SaveMapping(Sample(src, state: MappingState.Draft));
        Assert.IsNull(await store.GetActiveMappingForSource(src), "Draft must not be returned as Active");

        await store.SaveMapping(Sample(src, state: MappingState.Active));
        var active = await store.GetActiveMappingForSource(src);
        Assert.IsNotNull(active);
        Assert.AreEqual(MappingState.Active, active!.State);
    }

    [TestMethod]
    public async Task GetMappings_returns_saved()
    {
        var store = CreateStore();
        var src = $"src.{Guid.NewGuid():N}.v1";
        await store.SaveMapping(Sample(src));
        Assert.IsTrue((await store.GetMappings()).Any(m => m.SourceEventTypeId == src));
    }
}
```

- [ ] **Step 3: Implement in `InMemoryMessageStore`**

In `src/NimBus.Testing/Conformance/InMemoryMessageStore.cs`, add the field near `_schemas` (line ~31) and the methods near the schema methods (line ~345):

```csharp
    private readonly ConcurrentDictionary<string, EventMapping> _mappings = new();

    public Task<EventMapping?> GetMapping(string id)
        => Task.FromResult(_mappings.TryGetValue(id, out var m) ? m : (EventMapping?)null);

    public Task<EventMapping?> GetActiveMappingForSource(string sourceEventTypeId)
        => Task.FromResult(_mappings.Values.FirstOrDefault(
            m => m.SourceEventTypeId == sourceEventTypeId && m.State == MappingState.Active));

    public Task<IReadOnlyList<EventMapping>> GetMappings()
        => Task.FromResult<IReadOnlyList<EventMapping>>(_mappings.Values.ToList());

    public Task<EventMapping> SaveMapping(EventMapping mapping)
    {
        _mappings[mapping.Id] = mapping;
        return Task.FromResult(mapping);
    }
```

- [ ] **Step 4: Register the conformance run for the in-memory provider**

In `tests/NimBus.MessageStore.InMemory.Tests/InMemoryOtherStoreConformanceTests.cs`, append:

```csharp
[TestClass]
public sealed class InMemoryEventMappingStoreConformanceTests : EventMappingStoreConformanceTests
{
    protected override IEventMappingStore CreateStore() => new InMemoryMessageStore();
}
```

- [ ] **Step 5: Run the in-memory conformance run**

Run: `dotnet test tests/NimBus.MessageStore.InMemory.Tests/NimBus.MessageStore.InMemory.Tests.csproj --filter "EventMappingStore" -v minimal`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add src/NimBus.MessageStore.Abstractions/IEventMappingStore.cs src/NimBus.Testing/Conformance/EventMappingStoreConformanceTests.cs src/NimBus.Testing/Conformance/InMemoryMessageStore.cs tests/NimBus.MessageStore.InMemory.Tests/InMemoryOtherStoreConformanceTests.cs
git commit -m "feat(store): IEventMappingStore + conformance suite + in-memory impl (spec 023)"
```

### Task 3: Add `IEventMappingStore` to the facade + Cosmos & SQL implementations

**Files:**
- Modify: `src/NimBus.MessageStore.Abstractions/INimBusMessageStore.cs`
- Modify: `src/NimBus.MessageStore.CosmosDb/CosmosDbClient.cs`
- Modify: `src/NimBus.MessageStore.SqlServer/SqlServerMessageStore.cs` (+ a DbUp migration script)
- Modify: `tests/NimBus.MessageStore.CosmosDb.Tests/CosmosDbOtherStoreConformanceTests.cs`, `tests/NimBus.MessageStore.SqlServer.Tests/SqlServerOtherStoreConformanceTests.cs`

- [ ] **Step 1: Extend the facade**

In `INimBusMessageStore.cs`, add `IEventMappingStore` to the interface list (after `IEventSchemaStore`):

```csharp
public interface INimBusMessageStore
    : IMessageTrackingStore,
      ISubscriptionStore,
      IEndpointMetadataStore,
      IMetricsStore,
      IEventSchemaStore,
      IEventMappingStore
{
}
```

- [ ] **Step 2: Implement in Cosmos** — mirror the `EventSchema` implementation in `CosmosDbClient.cs` (search for `DefineEventType`/`GetSchema`). Mappings go in a dedicated container `event-mappings` keyed by `Id`; `GetActiveMappingForSource` queries `WHERE c.SourceEventTypeId = @s AND c.State = 'Active'`; `SaveMapping` is an upsert. Show the exact methods mirroring the schema ones already present in that file.

- [ ] **Step 3: Implement in SQL** — mirror the `EventSchema` SQL implementation in `SqlServerMessageStore.cs`. Add a DbUp script `Scripts/NNNN_EventMappings.sql` creating `EventMappings(Id PK, SourceEventTypeId, TargetEventTypeId, Transform, Rationale, WorkedExamplesJson, SourceSchemaHash, State, Version, CreatedBy, CreatedUtc, ApprovedBy, ApprovedUtc)` with an index on `(SourceEventTypeId, State)`. `SaveMapping` is a MERGE/upsert.

- [ ] **Step 4: Register conformance runs** — add to each provider's `*OtherStoreConformanceTests.cs`:

```csharp
[TestClass]
public sealed class <Provider>EventMappingStoreConformanceTests : EventMappingStoreConformanceTests
{
    protected override IEventMappingStore CreateStore() => /* same factory the file already uses for the other stores */;
}
```

- [ ] **Step 5: Build the whole solution**

Run: `dotnet build src/NimBus.sln -v minimal`
Expected: `0 Error(s)` (every `INimBusMessageStore` implementer now satisfies `IEventMappingStore`).

- [ ] **Step 6: Run in-memory conformance (Cosmos/SQL conformance run in CI service containers)**

Run: `dotnet test tests/NimBus.MessageStore.InMemory.Tests/NimBus.MessageStore.InMemory.Tests.csproj -v minimal`
Expected: PASS. (Cosmos/SQL conformance is exercised by CI; locally they skip without the emulator/container.)

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(store): add IEventMappingStore to facade + Cosmos/SQL implementations (spec 023)"
```

---

## Phase 2: Mapping transform engine

### Task 4: `IMappingTransformEngine` + JSONata implementation

**Files:**
- Create: `src/NimBus.Core/Transform/IMappingTransformEngine.cs`
- Create: `src/NimBus.Core/Transform/JsonataTransformEngine.cs`
- Test: `tests/NimBus.Core.Tests/JsonataTransformEngineTests.cs`

- [ ] **Step 1: Select & add the JSONata library** — evaluate a maintained .NET JSONata evaluator (spec open question #1; first candidate `Jsonata.Net.Native`). Confirm it exists on NuGet, its license is acceptable, and it supports a per-call evaluation timeout. Add it to `Directory.Packages.props` and reference it from `NimBus.Core`. If no suitable library exists, implement `JsonataTransformEngine` against a constrained transform DSL instead — the interface in Step 2 is unchanged either way.

- [ ] **Step 2: Define the interface**

```csharp
namespace NimBus.Core.Transform;

/// <summary>Deterministically applies a declarative transform to a JSON document (spec 023).</summary>
public interface IMappingTransformEngine
{
    /// <summary>
    /// Applies <paramref name="transform"/> to <paramref name="inputJson"/> and returns the
    /// resulting JSON. Throws <see cref="MappingTransformException"/> on a malformed transform
    /// or input. Must be deterministic and side-effect free.
    /// </summary>
    string Transform(string transform, string inputJson);
}

/// <summary>Thrown when a transform cannot be compiled or applied.</summary>
public sealed class MappingTransformException : System.Exception
{
    public MappingTransformException(string message, System.Exception? inner = null) : base(message, inner) { }
}
```

- [ ] **Step 3: Write the failing tests**

```csharp
#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Transform;

namespace NimBus.Core.Tests;

[TestClass]
public sealed class JsonataTransformEngineTests
{
    private static IMappingTransformEngine Engine() => new JsonataTransformEngine();

    [TestMethod]
    public void Transform_renames_and_derives_fields()
    {
        // Marketing lead -> ERP customer: rename + derive fullName.
        var transform = "{ \"customerId\": leadId, \"fullName\": firstName & ' ' & lastName }";
        var input = "{ \"leadId\": \"L-1\", \"firstName\": \"Ada\", \"lastName\": \"Lovelace\" }";

        var output = Engine().Transform(transform, input);

        StringAssert.Contains(output, "\"customerId\":\"L-1\"".Replace(" ", ""));
        StringAssert.Contains(output.Replace(" ", ""), "\"fullName\":\"AdaLovelace\"");
    }

    [TestMethod]
    public void Transform_is_deterministic()
    {
        var t = "{ \"id\": leadId }";
        var input = "{ \"leadId\": \"L-9\" }";
        Assert.AreEqual(Engine().Transform(t, input), Engine().Transform(t, input));
    }

    [TestMethod]
    public void Transform_malformed_input_throws_MappingTransformException()
    {
        Assert.ThrowsException<MappingTransformException>(
            () => Engine().Transform("{ \"id\": leadId }", "{ not json"));
    }
}
```

- [ ] **Step 4: Run to verify they fail**

Run: `dotnet test tests/NimBus.Core.Tests/NimBus.Core.Tests.csproj --filter "JsonataTransformEngine" -v minimal`
Expected: FAIL — `JsonataTransformEngine` not defined.

- [ ] **Step 5: Implement `JsonataTransformEngine`**

```csharp
using System;

namespace NimBus.Core.Transform;

/// <summary>
/// <see cref="IMappingTransformEngine"/> backed by a JSONata evaluator. Compiles the
/// transform and evaluates it against the input, translating any library error into a
/// <see cref="MappingTransformException"/> so callers have one exception type to handle.
/// </summary>
public sealed class JsonataTransformEngine : IMappingTransformEngine
{
    public string Transform(string transform, string inputJson)
    {
        if (string.IsNullOrWhiteSpace(transform))
            throw new MappingTransformException("Transform expression is empty.");
        try
        {
            // Adapt to the selected library's API (compile expression + evaluate against input).
            // e.g. with Jsonata.Net.Native:
            //   var query = Jsonata.Net.Native.JsonataQuery.Parse(transform);
            //   return query.Eval(inputJson);
            return JsonataAdapter.Evaluate(transform, inputJson);
        }
        catch (MappingTransformException) { throw; }
        catch (Exception ex)
        {
            throw new MappingTransformException($"Transform failed: {ex.Message}", ex);
        }
    }
}
```

(Replace `JsonataAdapter.Evaluate` with the chosen library's compile+eval calls, or the constrained-DSL evaluator if no library was selected in Step 1.)

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test tests/NimBus.Core.Tests/NimBus.Core.Tests.csproj --filter "JsonataTransformEngine" -v minimal`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props src/NimBus.Core/Transform/ tests/NimBus.Core.Tests/JsonataTransformEngineTests.cs
git commit -m "feat(core): IMappingTransformEngine + JSONata implementation (spec 023)"
```

---

## Phase 3: Mapping API

### Task 5: OpenAPI endpoints for mappings

**Files:**
- Modify: `src/NimBus.WebApp/api-spec.yaml`

- [ ] **Step 1: Add the paths** — under `paths:` (after the `/api/agent/settle` block, ~line 1849), add the mapping endpoints with a new tag `AgentMappings` so NSwag generates a separate `IAgentMappingsApiController`:

```yaml
  /api/agent/mappings:
    post:
      summary: Propose (author) a mapping
      operationId: post-agent-mappings
      tags: [AgentMappings]
      requestBody:
        content:
          application/json:
            schema: { $ref: '#/components/schemas/ProposeMappingRequest' }
      responses:
        '200': { description: OK, content: { application/json: { schema: { $ref: '#/components/schemas/MappingInfo' } } } }
        '404': { description: Unknown source or target event type }
    get:
      summary: List mappings
      operationId: get-agent-mappings
      tags: [AgentMappings]
      responses:
        '200': { description: OK, content: { application/json: { schema: { type: array, items: { $ref: '#/components/schemas/MappingInfo' } } } } }
  /api/agent/mappings/{id}/approve:
    post:
      summary: Approve a mapping (Draft -> Active)
      operationId: post-agent-mapping-approve
      tags: [AgentMappings]
      parameters: [ { name: id, in: path, required: true, schema: { type: string } } ]
      responses:
        '200': { description: OK }
        '404': { description: Unknown mapping }
        '409': { description: Source schema drifted; mapping marked Stale }
  /api/agent/mappings/{id}/reject:
    post:
      summary: Reject a mapping (Draft -> Rejected)
      operationId: post-agent-mapping-reject
      tags: [AgentMappings]
      parameters: [ { name: id, in: path, required: true, schema: { type: string } } ]
      responses: { '200': { description: OK }, '404': { description: Unknown mapping } }
  /api/agent/mappings/{id}/pause:
    post:
      summary: Pause a mapping (Active -> Paused)
      operationId: post-agent-mapping-pause
      tags: [AgentMappings]
      parameters: [ { name: id, in: path, required: true, schema: { type: string } } ]
      responses: { '200': { description: OK }, '404': { description: Unknown mapping } }
  /api/agent/mappings/{id}/resume:
    post:
      summary: Resume a mapping (Paused -> Active)
      operationId: post-agent-mapping-resume
      tags: [AgentMappings]
      parameters: [ { name: id, in: path, required: true, schema: { type: string } } ]
      responses: { '200': { description: OK }, '404': { description: Unknown mapping } }
```

- [ ] **Step 2: Add the component schemas** — under `components: schemas:` add `ProposeMappingRequest` (`sourceEventTypeId`, `targetEventTypeId`, `transform`, `rationale?`, `workedExamplesJson?`, `sourceSchemaHash`) and `MappingInfo` (the above plus `id`, `state`, `version`, `createdBy`, `approvedBy`). Mirror the `DefineEventTypeRequest`/`EventTypeInfo` definitions already in the file.

- [ ] **Step 3: Build to regenerate the contract + TS client**

Run: `dotnet build src/NimBus.WebApp/NimBus.WebApp.csproj -v minimal`
Expected: `0 Error(s)`; a new `IAgentMappingsApiController` appears in the generated `ApiContract.g.cs`, and `MappingInfo`/`ProposeMappingRequest` types appear in the TS client. (The build will now fail to find an implementer — that's Task 6.)

- [ ] **Step 4: Commit**

```bash
git add src/NimBus.WebApp/api-spec.yaml
git commit -m "feat(agent-api): OpenAPI for /api/agent/mappings (spec 023)"
```

### Task 6: `MappingImplementation` controller

**Files:**
- Create: `src/NimBus.WebApp/Controllers/ApiContract/MappingImplementation.cs`
- Test: `tests/NimBus.WebApp.Tests/MappingImplementationTests.cs`

This mirrors `AgentImplementation.cs` and is tested with the same in-memory harness style as `AgentImplementationTests.cs`.

- [ ] **Step 1: Write the failing tests** (mirror `AgentImplementationTests` builder/fakes — `InMemoryMessageStore`, `NullLogger`, no `IHttpContextAccessor`)

```csharp
#pragma warning disable CA1707, CA2007
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Tests;

[TestClass]
public class MappingImplementationTests
{
    private static (MappingImplementation Impl, InMemoryMessageStore Store) Build()
    {
        var store = new InMemoryMessageStore();
        var impl = new MappingImplementation(store, store, NullLogger<MappingImplementation>.Instance);
        return (impl, store);
    }

    private static async Task SeedSchema(InMemoryMessageStore store, string id)
        => await store.DefineEventType(new NimBus.MessageStore.States.EventSchema
        { EventTypeId = id, Name = id, JsonSchema = "{\"type\":\"object\"}", Version = 1, AgentId = "t", CreatedUtc = DateTime.UtcNow });

    private static ProposeMappingRequest Req(string src = "marketing.lead.created.v1", string tgt = "erp.customer.upsert.v1")
        => new ProposeMappingRequest
        {
            SourceEventTypeId = src,
            TargetEventTypeId = tgt,
            Transform = "{ \"customerId\": leadId }",
            SourceSchemaHash = "hash-1",
        };

    [TestMethod]
    public async Task Propose_unknown_source_or_target_returns_404()
    {
        var (impl, _) = Build(); // no schemas seeded
        var result = await impl.PostAgentMappingsAsync(Req());
        Assert.IsInstanceOfType(result.Result, typeof(NotFoundObjectResult));
    }

    [TestMethod]
    public async Task Propose_valid_returns_200_Draft()
    {
        var (impl, store) = Build();
        await SeedSchema(store, "marketing.lead.created.v1");
        await SeedSchema(store, "erp.customer.upsert.v1");

        var result = await impl.PostAgentMappingsAsync(Req());

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var info = ok!.Value as MappingInfo;
        Assert.AreEqual("marketing.lead.created.v1->erp.customer.upsert.v1", info!.Id);
        Assert.AreEqual(MappingInfoState.Draft, info.State);
    }

    [TestMethod]
    public async Task Approve_transitions_Draft_to_Active()
    {
        var (impl, store) = Build();
        await SeedSchema(store, "marketing.lead.created.v1");
        await SeedSchema(store, "erp.customer.upsert.v1");
        var info = ((await impl.PostAgentMappingsAsync(Req())).Result as OkObjectResult)!.Value as MappingInfo;

        var approve = await impl.PostAgentMappingApproveAsync(info!.Id);

        Assert.IsInstanceOfType(approve, typeof(OkResult));
        var active = await store.GetActiveMappingForSource("marketing.lead.created.v1");
        Assert.IsNotNull(active, "Approved mapping must become the Active mapping for its source");
    }

    [TestMethod]
    public async Task Approve_unknown_returns_404()
    {
        var (impl, _) = Build();
        Assert.IsInstanceOfType(await impl.PostAgentMappingApproveAsync("nope->nope"), typeof(NotFoundObjectResult));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/NimBus.WebApp.Tests/NimBus.WebApp.Tests.csproj --filter "MappingImplementation" -v minimal`
Expected: FAIL — `MappingImplementation` not defined.

- [ ] **Step 3: Implement the controller** (mirror `AgentImplementation.cs`; inject `IEventMappingStore` + `IEventSchemaStore` + logger; `MappingInfoState` is the NSwag-generated enum from the `state` field)

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Controllers.ApiContract;

public class MappingImplementation : IAgentMappingsApiController
{
    private readonly IEventMappingStore _mappings;
    private readonly IEventSchemaStore _schemas;
    private readonly ILogger<MappingImplementation> _logger;

    public MappingImplementation(IEventMappingStore mappings, IEventSchemaStore schemas, ILogger<MappingImplementation> logger)
    {
        _mappings = mappings;
        _schemas = schemas;
        _logger = logger;
    }

    public async Task<ActionResult<MappingInfo>> PostAgentMappingsAsync(ProposeMappingRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.SourceEventTypeId) || string.IsNullOrWhiteSpace(body.TargetEventTypeId))
            return new BadRequestObjectResult("sourceEventTypeId and targetEventTypeId are required.");
        if (string.IsNullOrWhiteSpace(body.Transform))
            return new BadRequestObjectResult("transform is required.");
        if (await _schemas.GetSchema(body.SourceEventTypeId) is null || await _schemas.GetSchema(body.TargetEventTypeId) is null)
            return new NotFoundObjectResult("Both source and target event types must be registered.");

        var id = $"{body.SourceEventTypeId}->{body.TargetEventTypeId}";
        var existing = await _mappings.GetMapping(id);
        var mapping = new EventMapping
        {
            Id = id,
            SourceEventTypeId = body.SourceEventTypeId,
            TargetEventTypeId = body.TargetEventTypeId,
            Transform = body.Transform,
            Rationale = body.Rationale,
            WorkedExamplesJson = body.WorkedExamplesJson,
            SourceSchemaHash = body.SourceSchemaHash,
            State = MappingState.Draft,                       // re-proposing returns to Draft for re-approval
            Version = (existing?.Version ?? 0) + 1,
            CreatedBy = "demo-agent",
            CreatedUtc = DateTime.UtcNow,
        };
        var saved = await _mappings.SaveMapping(mapping);
        return new OkObjectResult(ToInfo(saved));
    }

    public async Task<ActionResult<System.Collections.Generic.ICollection<MappingInfo>>> GetAgentMappingsAsync()
        => new OkObjectResult((await _mappings.GetMappings()).Select(ToInfo).ToList());

    public Task<IActionResult> PostAgentMappingApproveAsync(string id) => TransitionAsync(id, MappingState.Active, approve: true);
    public Task<IActionResult> PostAgentMappingRejectAsync(string id) => TransitionAsync(id, MappingState.Rejected, approve: false);
    public Task<IActionResult> PostAgentMappingPauseAsync(string id) => TransitionAsync(id, MappingState.Paused, approve: false);
    public Task<IActionResult> PostAgentMappingResumeAsync(string id) => TransitionAsync(id, MappingState.Active, approve: false);

    private async Task<IActionResult> TransitionAsync(string id, MappingState target, bool approve)
    {
        var mapping = await _mappings.GetMapping(id);
        if (mapping is null) return new NotFoundObjectResult("Unknown mapping.");

        if (approve)
        {
            // Re-check source-schema drift at approval time (spec error-handling).
            var schema = await _schemas.GetSchema(mapping.SourceEventTypeId);
            if (schema is null || SchemaHash.Of(schema.JsonSchema) != mapping.SourceSchemaHash)
            {
                mapping.State = MappingState.Stale;
                await _mappings.SaveMapping(mapping);
                return new ConflictObjectResult("Source schema drifted; mapping marked Stale. Re-author required.");
            }
            mapping.ApprovedBy = "operator";
            mapping.ApprovedUtc = DateTime.UtcNow;
        }
        mapping.State = target;
        await _mappings.SaveMapping(mapping);
        return new OkResult();
    }

    private static MappingInfo ToInfo(EventMapping m) => new MappingInfo
    {
        Id = m.Id,
        SourceEventTypeId = m.SourceEventTypeId,
        TargetEventTypeId = m.TargetEventTypeId,
        Transform = m.Transform,
        Rationale = m.Rationale,
        WorkedExamplesJson = m.WorkedExamplesJson,
        State = Enum.Parse<MappingInfoState>(m.State.ToString()),
        Version = m.Version,
        CreatedBy = m.CreatedBy,
        ApprovedBy = m.ApprovedBy,
    };
}
```

- [ ] **Step 4: Add the shared `SchemaHash` helper** (used by the controller now and the Executor in Phase 4 — DRY)

Create `src/NimBus.Core/Transform/SchemaHash.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace NimBus.MessageStore.States;

/// <summary>Stable fingerprint of a JSON-Schema string, for mapping drift detection (spec 023).</summary>
public static class SchemaHash
{
    public static string Of(string jsonSchema)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(jsonSchema ?? string.Empty));
        return System.Convert.ToHexString(bytes);
    }
}
```

- [ ] **Step 5: Run to verify the tests pass**

Run: `dotnet test tests/NimBus.WebApp.Tests/NimBus.WebApp.Tests.csproj --filter "MappingImplementation" -v minimal`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/NimBus.WebApp/Controllers/ApiContract/MappingImplementation.cs src/NimBus.Core/Transform/SchemaHash.cs tests/NimBus.WebApp.Tests/MappingImplementationTests.cs
git commit -m "feat(agent-api): MappingImplementation controller + SchemaHash drift helper (spec 023)"
```

---

## Phase 4: SDK fallback handler + Mapping Executor

### Task 7: SDK fallback dynamic handler

**Files:**
- Modify: `src/NimBus.SDK/EventHandlers/EventContextHandler.cs`
- Modify: `src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs`
- Test: `tests/NimBus.SDK.Tests/FallbackHandlerTests.cs` (create the test project if absent, mirroring another `tests/*.Tests` csproj)

- [ ] **Step 1: Write the failing test**

```csharp
#pragma warning disable CA1707, CA2007
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;

namespace NimBus.SDK.Tests;

[TestClass]
public class FallbackHandlerTests
{
    private sealed class RecordingHandler : IEventJsonHandler
    {
        public int Calls;
        public Task Handle(IMessageContext context, CancellationToken ct = default) { Calls++; return Task.CompletedTask; }
    }

    [TestMethod]
    public async Task Unregistered_eventTypeId_routes_to_fallback()
    {
        var provider = new EventHandlerProvider();
        var fallback = new RecordingHandler();
        provider.RegisterFallbackHandler(() => fallback);

        var ctx = MessageContextStub.ForEventType("some.unmapped.type.v1", "{}");
        await provider.Handle(ctx);

        Assert.AreEqual(1, fallback.Calls, "An EventTypeId with no specific handler must route to the fallback");
    }

    [TestMethod]
    public async Task Specific_handler_wins_over_fallback()
    {
        var provider = new EventHandlerProvider();
        var specific = new RecordingHandler();
        var fallback = new RecordingHandler();
        provider.RegisterHandler("known.type.v1", () => specific);
        provider.RegisterFallbackHandler(() => fallback);

        await provider.Handle(MessageContextStub.ForEventType("known.type.v1", "{}"));

        Assert.AreEqual(1, specific.Calls);
        Assert.AreEqual(0, fallback.Calls);
    }
}
```

(`MessageContextStub.ForEventType` is a tiny test helper that builds an `IMessageContext` whose `MessageContent.EventContent.EventTypeId`/`EventJson` are set — model it on how `AgentImplementationTests` constructs `MessageContent`/`EventContent`.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NimBus.SDK.Tests/NimBus.SDK.Tests.csproj --filter "FallbackHandler" -v minimal`
Expected: FAIL — `RegisterFallbackHandler` not defined.

- [ ] **Step 3: Add the fallback to `EventHandlerProvider`** — in `EventContextHandler.cs`, add a field + registration method and consult it on miss instead of throwing immediately:

```csharp
        private Func<IEventJsonHandler>? _fallbackBuilder;

        /// <summary>
        /// Registers a fallback handler invoked when no <c>EventTypeId</c>-specific handler is
        /// registered. Used by the Mapping Executor (spec 023) to handle every message arriving
        /// at the Mapping Zone and decide from the mapping registry per message.
        /// </summary>
        public void RegisterFallbackHandler(Func<IEventJsonHandler> fallbackFactory)
        {
            _fallbackBuilder = fallbackFactory ?? throw new ArgumentNullException(nameof(fallbackFactory));
        }
```

Change `GetHandler` to:

```csharp
        private IEventJsonHandler GetHandler(string eventTypeId)
        {
            if (_handlerBuilders.TryGetValue(eventTypeId, out var factory))
                return factory.Invoke();
            if (_fallbackBuilder != null)
                return _fallbackBuilder.Invoke();
            throw new EventHandlerNotFoundException($"Event handler not registered for Event type {eventTypeId}");
        }
```

- [ ] **Step 4: Expose it on the builder** — in `NimBusSubscriberBuilder.cs`, add (mirroring `AddDynamicHandler`):

```csharp
        /// <summary>
        /// Registers a single fallback handler invoked for any event type with no specific
        /// handler. The Executor uses this to consult the mapping registry per message (spec 023).
        /// </summary>
        public NimBusSubscriberBuilder AddDynamicFallbackHandler(Func<IServiceProvider, IEventJsonHandler> handlerFactory)
        {
            // Register, at subscriber startup, a fallback on the EventHandlerProvider resolved from DI.
            // Mirror the registration body of AddDynamicHandler, calling
            //   handlerProvider.RegisterFallbackHandler(() => handlerFactory(provider));
            return this;
        }
```

(Fill the body by mirroring the existing `AddDynamicHandler(string, Func<IServiceProvider, IEventJsonHandler>)` overload — same provider resolution, but call `RegisterFallbackHandler` instead of `RegisterHandler(eventTypeId, …)`.)

- [ ] **Step 5: Run to verify the tests pass**

Run: `dotnet test tests/NimBus.SDK.Tests/NimBus.SDK.Tests.csproj --filter "FallbackHandler" -v minimal`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/NimBus.SDK/EventHandlers/EventContextHandler.cs src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs tests/NimBus.SDK.Tests/
git commit -m "feat(sdk): fallback dynamic handler for unmatched event types (spec 023)"
```

### Task 8: `MappingExecutorHandler` (the core runtime logic)

**Files:**
- Create: `src/NimBus.MappingExecutor/MappingExecutorHandler.cs`
- Create: `src/NimBus.MappingExecutor/NimBus.MappingExecutor.csproj` (references SDK, Core, MessageStore.Abstractions)
- Test: `tests/NimBus.MappingExecutor.Tests/MappingExecutorHandlerTests.cs`

The handler is the fallback registered on the Mapping Zone. Per message it resolves the source `EventTypeId`, looks up the registry, and acts on state.

- [ ] **Step 1: Write the failing tests** (use `InMemoryMessageStore` for both schema + mapping; `JsonataTransformEngine` for transform; a capturing publisher and a capturing "park" sink)

```csharp
#pragma warning disable CA1707, CA2007
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Transform;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;

namespace NimBus.MappingExecutor.Tests;

[TestClass]
public class MappingExecutorHandlerTests
{
    private const string Src = "marketing.lead.created.v1";
    private const string Tgt = "erp.customer.upsert.v1";

    private static async Task<(MappingExecutorHandler Handler, CapturingPublisher Pub, CapturingPark Park)> Build(
        InMemoryMessageStore store, MappingState state, string transform, string targetSchema)
    {
        await store.DefineEventType(new EventSchema { EventTypeId = Src, Name = Src, JsonSchema = "{\"type\":\"object\"}", Version = 1, AgentId = "t", CreatedUtc = DateTime.UtcNow });
        await store.DefineEventType(new EventSchema { EventTypeId = Tgt, Name = Tgt, JsonSchema = targetSchema, Version = 1, AgentId = "t", CreatedUtc = DateTime.UtcNow });
        await store.SaveMapping(new EventMapping
        {
            Id = $"{Src}->{Tgt}", SourceEventTypeId = Src, TargetEventTypeId = Tgt,
            Transform = transform, SourceSchemaHash = SchemaHash.Of("{\"type\":\"object\"}"),
            State = state, Version = 1,
        });
        var pub = new CapturingPublisher();
        var park = new CapturingPark();
        var handler = new MappingExecutorHandler(store, store, new JsonataTransformEngine(), pub, park, NullLogger<MappingExecutorHandler>.Instance);
        return (handler, pub, park);
    }

    [TestMethod]
    public async Task Active_mapping_transforms_validates_and_publishes_target()
    {
        var store = new InMemoryMessageStore();
        var (handler, pub, park) = await Build(store, MappingState.Active,
            transform: "{ \"customerId\": leadId }",
            targetSchema: "{\"type\":\"object\",\"required\":[\"customerId\"],\"properties\":{\"customerId\":{\"type\":\"string\"}}}");

        await handler.Handle(MessageContextStub.ForEventType(Src, "{ \"leadId\": \"L-1\" }"));

        Assert.AreEqual(1, pub.Count);
        Assert.AreEqual(Tgt, pub.LastEventTypeId);
        Assert.AreEqual(0, park.Count, "A valid mapping must not park");
    }

    [TestMethod]
    public async Task Output_failing_target_schema_parks_and_does_not_publish()
    {
        var store = new InMemoryMessageStore();
        var (handler, pub, park) = await Build(store, MappingState.Active,
            transform: "{ \"wrong\": leadId }",   // produces no customerId
            targetSchema: "{\"type\":\"object\",\"required\":[\"customerId\"],\"properties\":{\"customerId\":{\"type\":\"string\"}}}");

        await handler.Handle(MessageContextStub.ForEventType(Src, "{ \"leadId\": \"L-1\" }"));

        Assert.AreEqual(0, pub.Count, "Invalid output must not publish");
        Assert.AreEqual(1, park.Count, "Invalid output must park for recovery");
    }

    [TestMethod]
    public async Task Paused_mapping_parks_and_does_not_publish()
    {
        var store = new InMemoryMessageStore();
        var (handler, pub, park) = await Build(store, MappingState.Paused,
            transform: "{ \"customerId\": leadId }", targetSchema: "{\"type\":\"object\"}");

        await handler.Handle(MessageContextStub.ForEventType(Src, "{ \"leadId\": \"L-1\" }"));

        Assert.AreEqual(0, pub.Count);
        Assert.AreEqual(1, park.Count, "Paused mapping must park, not transform");
    }

    [TestMethod]
    public async Task No_mapping_parks_as_misconfiguration()
    {
        var store = new InMemoryMessageStore();
        var pub = new CapturingPublisher();
        var park = new CapturingPark();
        var handler = new MappingExecutorHandler(store, store, new JsonataTransformEngine(), pub, park, NullLogger<MappingExecutorHandler>.Instance);

        await handler.Handle(MessageContextStub.ForEventType("unrouted.type.v1", "{}"));

        Assert.AreEqual(0, pub.Count);
        Assert.AreEqual(1, park.Count, "A message with no mapping at the zone must park, not silently complete");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/NimBus.MappingExecutor.Tests/NimBus.MappingExecutor.Tests.csproj --filter "MappingExecutorHandler" -v minimal`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement the handler** (define the small `IMappingParkSink` seam so failures reuse the existing pending-handoff parking without coupling the test to Service Bus)

```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.Core.Messages;
using NimBus.Core.Transform;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.SDK.EventHandlers;

namespace NimBus.MappingExecutor;

/// <summary>Parks a source message for operator recovery with a reason (spec 023).</summary>
public interface IMappingParkSink
{
    Task Park(IMessageContext context, string reason, CancellationToken ct);
}

/// <summary>
/// The single fallback handler registered on the Mapping Zone. Per message it resolves the
/// source EventTypeId, consults the mapping registry, and acts on the mapping's state. Never
/// calls an LLM.
/// </summary>
public sealed class MappingExecutorHandler : IEventJsonHandler
{
    private readonly IEventMappingStore _mappings;
    private readonly IEventSchemaStore _schemas;
    private readonly IMappingTransformEngine _engine;
    private readonly IAgentEventPublisher _publisher;   // reused from spec 022
    private readonly IMappingParkSink _park;
    private readonly ILogger<MappingExecutorHandler> _logger;

    public MappingExecutorHandler(
        IEventMappingStore mappings, IEventSchemaStore schemas, IMappingTransformEngine engine,
        IAgentEventPublisher publisher, IMappingParkSink park, ILogger<MappingExecutorHandler> logger)
    {
        _mappings = mappings; _schemas = schemas; _engine = engine;
        _publisher = publisher; _park = park; _logger = logger;
    }

    public async Task Handle(IMessageContext context, CancellationToken ct = default)
    {
        var source = context.MessageContent.EventContent.EventTypeId;
        var input = context.MessageContent.EventContent.EventJson;

        var active = await _mappings.GetActiveMappingForSource(source);
        if (active is null)
        {
            var any = (await _mappings.GetMappings()).Any(m => m.SourceEventTypeId == source);
            await _park.Park(context, any ? "mapping is Paused/Stale" : "no mapping for source type", ct);
            return;
        }

        // Drift guard: input schema fingerprint must still match.
        var schema = await _schemas.GetSchema(source);
        if (schema is null || SchemaHash.Of(schema.JsonSchema) != active.SourceSchemaHash)
        {
            active.State = MappingState.Stale;
            await _mappings.SaveMapping(active);
            await _park.Park(context, "source schema drifted; mapping marked Stale", ct);
            return;
        }

        string output;
        try { output = _engine.Transform(active.Transform, input); }
        catch (MappingTransformException ex) { await _park.Park(context, $"transform error: {ex.Message}", ct); return; }

        var targetSchema = await _schemas.GetSchema(active.TargetEventTypeId);
        var jsonSchema = await NJsonSchema.JsonSchema.FromJsonAsync(targetSchema!.JsonSchema, ct);
        if (jsonSchema.Validate(output).Count > 0)
        {
            await _park.Park(context, "transformed output failed target schema", ct);
            return;
        }

        await _publisher.PublishAsync(BuildTargetMessage(active.TargetEventTypeId, output, context), ct);
    }

    // Build a classless target Message the same way AgentImplementation.PostAgentPublishAsync does
    // (To/EventTypeId/SessionId/MessageType=EventRequest/EventContent). Extracted for reuse.
    private static NimBus.Core.Messages.Message BuildTargetMessage(string targetType, string payload, IMessageContext src)
        => /* mirror AgentImplementation.cs message construction; carry src.SessionId for ordering */ null!;
}
```

(Fill `BuildTargetMessage` by mirroring the `CoreMessage` construction in `AgentImplementation.PostAgentPublishAsync` — `To`/`EventTypeId` = target, `MessageType = EventRequest`, `EventContent.EventJson` = output, `SessionId` carried from the source context for ordering.)

- [ ] **Step 4: Add the test doubles** in the test project: `CapturingPublisher : IAgentEventPublisher` (records `Count`, `LastEventTypeId`), `CapturingPark : IMappingParkSink` (records `Count`), and reuse `MessageContextStub` from Task 7.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test tests/NimBus.MappingExecutor.Tests/NimBus.MappingExecutor.Tests.csproj --filter "MappingExecutorHandler" -v minimal`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/NimBus.MappingExecutor/ tests/NimBus.MappingExecutor.Tests/
git commit -m "feat(executor): MappingExecutorHandler core logic — transform/validate/publish/park (spec 023)"
```

### Task 9: Executor registration + real park sink

**Files:**
- Create: `src/NimBus.MappingExecutor/MappingExecutorRegistration.cs`
- Create: `src/NimBus.MappingExecutor/HandoffParkSink.cs`

- [ ] **Step 1: Implement `HandoffParkSink : IMappingParkSink`** — park the source message `Pending+Handoff` via the existing mechanism. Reuse spec-022's `MarkPendingHandoffJsonHandler` semantics (mark the event pending under the Mapping Zone with sub-status `Handoff` and the reason). No new test (covered by the integration test in Task 13); verified live in Phase 7.

- [ ] **Step 2: Implement `AddMappingExecutor(this IServiceCollection, …)`** — register `IMappingTransformEngine`→`JsonataTransformEngine`, `IMappingParkSink`→`HandoffParkSink`, `MappingExecutorHandler`, and wire a NimBus subscriber on the Mapping Zone endpoint that calls `AddDynamicFallbackHandler(sp => sp.GetRequiredService<MappingExecutorHandler>())`. Mirror how spec-022's Agent Zone host registers its subscriber.

- [ ] **Step 3: Build**

Run: `dotnet build src/NimBus.MappingExecutor/NimBus.MappingExecutor.csproj -v minimal`
Expected: `0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add src/NimBus.MappingExecutor/MappingExecutorRegistration.cs src/NimBus.MappingExecutor/HandoffParkSink.cs
git commit -m "feat(executor): DI registration + pending-handoff park sink (spec 023)"
```

---

## Phase 5: MCP tools

### Task 10: `propose_mapping` + `list_mappings`

**Files:**
- Modify: `src/NimBus.Mcp/Http/INimBusAgentApi.cs` (+ `NimBusAgentApiClient.cs`, `Contracts.cs`)
- Modify: `src/NimBus.Mcp/Tools/NimBusAgentTools.cs`
- Modify: `tests/NimBus.Mcp.Tests/ToolMappingTests.cs`

- [ ] **Step 1: Write the failing mapping test** (mirror existing `ToolMappingTests` — assert the tool calls the right REST path/args against a mock)

```csharp
        [TestMethod]
        public async Task propose_mapping_calls_post_mappings()
        {
            var (tools, api) = BuildWithMock();   // existing helper in ToolMappingTests
            await tools.ProposeMappingAsync("marketing.lead.created.v1", "erp.customer.upsert.v1", "{ \"customerId\": leadId }", "hash-1", rationale: null);
            api.Verify(a => a.ProposeMappingAsync(It.Is<ProposeMappingRequest>(r =>
                r.SourceEventTypeId == "marketing.lead.created.v1" && r.TargetEventTypeId == "erp.customer.upsert.v1"), It.IsAny<CancellationToken>()), Times.Once);
        }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NimBus.Mcp.Tests/NimBus.Mcp.Tests.csproj --filter "propose_mapping" -v minimal`
Expected: FAIL — `ProposeMappingAsync` not defined.

- [ ] **Step 3: Add the API methods + tools** — extend `INimBusAgentApi`/`NimBusAgentApiClient` with `ProposeMappingAsync`/`ListMappingsAsync` (POST `/api/agent/mappings`, GET `/api/agent/mappings`), and add the tools to `NimBusAgentTools.cs` mirroring the existing `[McpServerTool]` methods:

```csharp
    [McpServerTool(Name = "propose_mapping")]
    [Description("Proposes a declarative JSONata mapping from a registered source event type to a registered target event type. The mapping is saved as a Draft for human approval; it does not affect live traffic until approved. Returns the stored MappingInfo as JSON.")]
    public async Task<string> ProposeMappingAsync(
        [Description("Source event type id, e.g. 'marketing.lead.created.v1'.")] string sourceEventTypeId,
        [Description("Target event type id, e.g. 'erp.customer.upsert.v1'.")] string targetEventTypeId,
        [Description("The JSONata transform mapping source JSON to target JSON.")] string transform,
        [Description("Fingerprint of the source schema this transform was authored against.")] string sourceSchemaHash,
        [Description("Optional short rationale shown to the human approver.")] string? rationale = null,
        CancellationToken ct = default)
    {
        var info = await _api.ProposeMappingAsync(new ProposeMappingRequest(sourceEventTypeId, targetEventTypeId, transform, sourceSchemaHash, rationale), ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(info, s_json);
    }

    [McpServerTool(Name = "list_mappings")]
    [Description("Lists all mappings and their lifecycle state (Draft/Active/Paused/Stale/Rejected). Use to check whether a mapping already exists before proposing.")]
    public async Task<string> ListMappingsAsync(CancellationToken ct = default)
        => JsonSerializer.Serialize(await _api.ListMappingsAsync(ct).ConfigureAwait(false), s_json);
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/NimBus.Mcp.Tests/NimBus.Mcp.Tests.csproj -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NimBus.Mcp/ tests/NimBus.Mcp.Tests/
git commit -m "feat(mcp): propose_mapping + list_mappings tools (spec 023)"
```

---

## Phase 6: WebApp Mappings review page

### Task 11: Mappings page

**Files:**
- Create: `src/NimBus.WebApp/ClientApp/src/pages/mappings.tsx`
- Modify: `src/NimBus.WebApp/ClientApp/src/components/sidebar.tsx` (add a "Mappings" nav item) + the router in `main.tsx`/`app.tsx`
- Test: `src/NimBus.WebApp/ClientApp/src/pages/mappings.test.tsx`

This mirrors an existing list page (`pages/audits-list.tsx`) and uses the NSwag-generated TS client (now exposing `getAgentMappings`, `postAgentMappingApprove`, etc.).

- [ ] **Step 1: Write the failing vitest** — render the page with a mocked client returning one Draft mapping; assert the transform + a worked example render and that clicking "Approve" calls `postAgentMappingApprove(id)`. Model the test on `src/pages/event-details.test.tsx`.

- [ ] **Step 2: Run to verify it fails**

Run (from `src/NimBus.WebApp/ClientApp`): `npm test -- mappings`
Expected: FAIL — page not found.

- [ ] **Step 3: Implement the page** — a table of mappings (id, source→target, state badge) with a detail panel showing `transform`, `rationale`, and parsed `workedExamplesJson` (source → output side by side), plus Approve/Reject for Draft and Pause/Resume for Active/Paused. Reuse `components/data-table/data-table-new.tsx`, `components/ui/badge.tsx`, `components/ui/button.tsx`, `components/ui/code-block.tsx`. Drift alerts: show Stale rows with a warning badge. Mirror layout/data-loading from `pages/audits-list.tsx`.

- [ ] **Step 4: Add the route + nav** — register `/mappings` in the router and a sidebar entry next to the existing pages.

- [ ] **Step 5: Run to verify it passes + production build**

Run (from `ClientApp`): `npm test -- mappings` then `npm run build`
Expected: tests PASS; build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/NimBus.WebApp/ClientApp/src/pages/mappings.tsx src/NimBus.WebApp/ClientApp/src/pages/mappings.test.tsx src/NimBus.WebApp/ClientApp/src/components/sidebar.tsx src/NimBus.WebApp/ClientApp/src/main.tsx
git commit -m "feat(webapp): Mappings review page (spec 023)"
```

---

## Phase 7: Demo (depends on Phase 0)

### Task 12: Marketing source app + contracts + Mapping Zone forwards

**Files:**
- Create: `samples/CrmErpDemo/Marketing.Api/` (minimal publisher emitting `marketing.lead.created.v1`)
- Modify: `samples/CrmErpDemo/CrmErpDemo.Contracts/` — register `marketing.lead.created.v1` (source) and `erp.customer.upsert.v1` (target) schemas; add the **Mapping Zone** endpoint; declare the two `DynamicForward`s (`Marketing → MappingZone` for the source type, `MappingZone → DataPlatform` for the target type) using the Phase-0/D5 mechanism.

- [ ] **Step 1:** Add the Mapping Zone endpoint + the two dynamic forwards to the demo platform config (the same `DynamicForward` list the D5 task introduced).
- [ ] **Step 2:** Create `Marketing.Api` emitting `marketing.lead.created.v1` (mirror `Crm.Api`).
- [ ] **Step 3:** Build the demo solution. Run: `dotnet build samples/CrmErpDemo/... -v minimal` → `0 Error(s)`.
- [ ] **Step 4: Commit** `git commit -m "feat(demo): Marketing source + Mapping Zone forwards (spec 023)"`

### Task 13: `MappingAgent` + integration/smoke tests

**Files:**
- Create: `samples/CrmErpDemo/MappingAgent/` (mirror `EnrichmentAgent` + its `RestBusGateway`)
- Create: `tests/MappingAgent.Tests/` (in-memory smoke) + an EndToEnd integration test
- Modify: `samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs`

- [ ] **Step 1: Write the EndToEnd integration test** (in-memory transport): seed source+target schemas, save an **Active** mapping, publish a `marketing.lead.created.v1`, assert the Executor publishes a schema-valid `erp.customer.upsert.v1` and the audit shows source→mapper→target completed; then flip the mapping to Paused and assert the next message parks. Model on `tests/NimBus.EndToEnd.Tests/AgentParkAndSettleTests.cs`.
- [ ] **Step 2: Run → fail, implement `MappingAgent`** — authoring loop: read both schemas via `/api/agent/catalog`, pull samples via `/api/messages/search`, ask Claude for a JSONata transform + rationale (structured output), compute worked examples by running the transform locally, compute `sourceSchemaHash` with `SchemaHash.Of`, submit via `propose_mapping`. Provide a `DeterministicMappingAuthor` fallback (no `ANTHROPIC_API_KEY`) so CI is green — mirror `EnrichmentAgent`'s `DeterministicContactClassifier`.
- [ ] **Step 3: Wire AppHost** — add `marketing-api`, `mapping-agent`, and `mapping-executor` as Aspire resources (mirror `agent-zone`/`enrichment-agent` at `CrmErpDemo.AppHost/Program.cs:246-261`).
- [ ] **Step 4: Run the integration + smoke tests.** Run: `dotnet test tests/NimBus.EndToEnd.Tests/... --filter "Mapping" -v minimal` and `dotnet test tests/MappingAgent.Tests/... -v minimal`. Expected: PASS.
- [ ] **Step 5: Manual live pass (needs Docker + optional API key)** — follow the [Demo acceptance flow](spec.md#demo-acceptance-flow): start AppHost, author → approve in the WebApp, emit a lead, confirm the ERP customer + audit trail; then the negative (invalid output parks) and drift (source schema change → Stale) paths.
- [ ] **Step 6: Commit** `git commit -m "feat(demo): MappingAgent + integration/smoke tests + AppHost wiring (spec 023)"`

---

## Self-review

**1. Spec coverage** — every spec section maps to a task:
- Mapping registry (3 backends + conformance) → Tasks 1–3. Transform engine → Task 4. Mapping API + OpenAPI → Tasks 5–6. SDK fallback handler → Task 7. Mapping Executor (transform/validate/publish/park + per-message state decisions) → Tasks 8–9. MCP tools → Task 10. WebApp review page → Task 11. Demo (Marketing source, Mapping Zone forwards, MappingAgent, AppHost) → Tasks 12–13. Routing/D5 dependency → Phase 0. Drift detection → `SchemaHash` (Task 6) + Executor guard (Task 8) + approval re-check (Task 6). Sample sourcing via `/api/messages/search` → Task 13.
- Lifecycle states (Draft/Active/Paused/Stale/Rejected) → `MappingState` (Task 1), transitions (Task 6), runtime behavior (Task 8). Error-handling table rows (output-invalid, transform-throws, drift, paused/stale, no-mapping) → Task 8 tests.

**2. Placeholder scan** — Tasks 1, 2, 4, 6, 7, 8, 10 contain complete code + exact commands. Tasks 3, 9, 11, 12, 13 deliberately mirror cited existing files (Cosmos/SQL schema impls, the Agent Zone host, `pages/audits-list.tsx`, `EnrichmentAgent`) rather than reproduce large boilerplate verbatim — each names the exact reference and shows the new logic. The two genuinely unresolved choices (JSONata library; `BuildTargetMessage`/park-sink reuse) are isolated behind interfaces (`IMappingTransformEngine`, `IMappingParkSink`) with a stated fallback, so they don't block the TDD'd seams.

**3. Type consistency** — `EventMapping`/`MappingState` (Task 1) are used identically in the store (2), controller (6), and executor (8). `IEventMappingStore`'s four methods (`GetMapping`, `GetActiveMappingForSource`, `GetMappings`, `SaveMapping`) are used consistently. `SchemaHash.Of` (Task 6) is reused by the executor's drift guard (Task 8). `IMappingTransformEngine.Transform(transform, inputJson)` and `MappingTransformException` (Task 4) match their executor usage (Task 8). MCP `ProposeMappingRequest(source, target, transform, hash, rationale)` (Task 10) matches the OpenAPI request shape (Task 5).
