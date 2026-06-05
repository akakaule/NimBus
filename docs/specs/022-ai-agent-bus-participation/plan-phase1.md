# Spec 022 — Phase 1 Implementation Plan: Schema Registry + Agent REST API

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the capability core of spec 022 — a JSON-Schema registry (`IEventSchemaStore`) and the agent-ready REST surface (`/api/agent/{catalog,event-types,subscribe,receive,publish,settle}`) — so an external agent can discover, define an event type, subscribe, receive, publish, and settle, all over HTTP with no NimBus C# written.

**Architecture:** The REST API is the single capability core (the future `NimBus.Mcp` server and `.NET` SDK are thin clients of it). Dynamically-typed events ride the **already-proven** `EventTypeId` property routing (Phase 0). `/receive` + `/settle` are built on NimBus's existing **pending-handoff** mechanism, not a held Service Bus lock: a coded Agent Zone subscriber parks each routed event as `Pending+Handoff`, `/receive` reads parked rows from the message store (stateless long-poll), and `/settle` publishes a handoff completion/failure via `IHandoffClient`. The schema registry is a new small store on the `INimBusMessageStore` facade, implemented for InMemory/Cosmos/SQL and covered by the shared conformance suite.

**Tech Stack:** .NET 10, ASP.NET Core, NSwag (api-spec.yaml → C# contract + TS client at build), Newtonsoft.Json, NJsonSchema (payload validation), Cosmos SDK 3.45, Dapper + DbUp (SQL), MSTest.

---

## Design decisions & spec reconciliation

These were settled from the Phase 0 result and Phase 1 research. Read before starting.

1. **`/receive` + `/settle` reuse pending-handoff (not a held SB lock).** NimBus receives via a long-lived `ServiceBusSessionProcessor`; a peek-lock lives only inside one processor callback and cannot survive two stateless HTTP requests (`NimBusReceiverHostedService.cs:127`, `MessageContext.cs:183-241`). So the Agent Zone subscriber's handler calls `context.MarkPendingHandoff(...)` (`IEventHandlerContext.cs:43`), which — via `StrictMessageHandler.cs:69-76` — parks the event `Pending+Handoff`, blocks the session, and completes the SB message. State now lives in the Resolver store. `/receive` reads it; `/settle` calls `IHandoffClient.CompleteAsync/FailAsync` (`IHandoffClient.cs:53,66`), exactly as the WebApp operator handoff endpoints already do (`EventImplementation.cs:213-249`).

2. **Spec error-handling row must be reconciled (do this in Task 14).** Spec lines ~271/286 promise un-settled agent messages "stay under peek-lock … reappear for redelivery … dead-letter after N attempts." That is the *push-model* guarantee and **does not hold** here: a parked message is off the bus, so an agent that crashes leaves a `Pending+Handoff` row with the session blocked. **v1 recovery = operator Resubmit/Skip from the WebApp (already wired) + surfacing the `ExpectedBy` deadline.** An automatic timeout sweeper is explicitly deferred (call it out; don't assume SB gives it for free).

3. **`subscribe` is a logical filter, not topology mutation.** Runtime SB topology mutation by agents is out of scope (spec "Out of scope"). `POST /api/agent/subscribe` records `(agentId, eventTypeId)` interest in the schema store; `/receive` returns only parked events whose `EventTypeId` the agent has subscribed to (or, if the agent passes no filter, any parked event for the Agent Zone). For the v1 demo the Agent Zone's subscription to `CrmContactCreated` is pre-provisioned in topology — `subscribe` does not create SB entities.

4. **Classless publish.** Add a `Publish(Message)` overload to `IPublisherClient`/`PublisherClient` (gets OpenTelemetry instrumentation via `CreateAsync`), rather than building `Sender` by hand in the controller. `/api/agent/publish` loads the schema, validates the payload with **NJsonSchema**, builds the classless `Message` (the Phase 0 shape), and publishes onto the Agent Zone topic.

5. **JSON Schema lib = NJsonSchema 11.5.2** (Newtonsoft-based, already transitive via NSwag). Add an explicit package reference so an NSwag bump can't drop it.

6. **`IEventSchemaStore` joins the `INimBusMessageStore` facade**, so all three providers + the InMemory reference implement it and the WebApp injects it directly with no extra registration beyond the one-line-per-provider builder edit.

---

## File structure map

**Part 1A — Schema registry (storage, no WebApp):**
- Create `src/NimBus.MessageStore.Abstractions/IEventSchemaStore.cs` — interface.
- Create `src/NimBus.MessageStore.Abstractions/States/EventSchema.cs` — DTO (`[JsonProperty("id")]` on the id field).
- Create `src/NimBus.MessageStore.Abstractions/Exceptions/SchemaConflictException.cs` — 409 signal (or add to existing `Exceptions.cs`).
- Modify `src/NimBus.MessageStore.Abstractions/INimBusMessageStore.cs` — add `IEventSchemaStore` to the facade.
- Modify `src/NimBus.Testing/Conformance/InMemoryMessageStore.cs` — implement `IEventSchemaStore` (`_schemas` dictionary).
- Create `src/NimBus.Testing/Conformance/EventSchemaStoreConformanceTests.cs` — abstract base tests.
- Modify `src/NimBus.MessageStore.CosmosDb/CosmosDbClient.cs` (+ `MessageDocuments.cs`) — `eventschemas` container, `IEventSchemaStore` impl.
- Create `src/NimBus.MessageStore.SqlServer/Schema/0013_EventSchemas.sql`; modify `SqlServerMessageStore.cs`, `SqlServerSchemaInitializer.cs`.
- Modify both `*MessageStoreBuilderExtensions.cs` — register `IEventSchemaStore`.
- Modify the three `*OtherStoreConformanceTests.cs` test files + `SqlServerStoreTestHarness.cs` (TRUNCATE list).

**Part 1B — Agent REST API:**
- Modify `src/NimBus.SDK/IPublisherClient.cs` + `PublisherClient.cs` — `Publish(Message)` overload.
- Create the Agent Zone endpoint + park-handler in `samples/CrmErpDemo/CrmErpDemo.Contracts/` (endpoint) and a small `AgentParkHandler` (lives where the agent-zone subscriber is hosted; for Phase 1 tests it is exercised on the in-memory harness).
- Modify `src/NimBus.WebApp/api-spec.yaml` — `/api/agent/*` paths + DTOs + `Agent` tag.
- Create `src/NimBus.WebApp/Controllers/ApiContract/AgentImplementation.cs` — implements generated `IAgentApiController`.
- Modify `src/NimBus.WebApp/Startup.cs` — register `AgentImplementation`, `IHandoffClient` for the Agent Zone, API-key auth filter.
- Modify `src/NimBus.WebApp/NimBus.WebApp.csproj` — explicit `NJsonSchema` reference.

---

# Part 1A — Schema registry

### Task 1: `EventSchema` record, `IEventSchemaStore`, facade, conflict exception

**Files:**
- Create: `src/NimBus.MessageStore.Abstractions/States/EventSchema.cs`
- Create: `src/NimBus.MessageStore.Abstractions/IEventSchemaStore.cs`
- Create: `src/NimBus.MessageStore.Abstractions/Exceptions/SchemaConflictException.cs`
- Modify: `src/NimBus.MessageStore.Abstractions/INimBusMessageStore.cs`

- [ ] **Step 1: Create the `EventSchema` DTO.** Mirror `States/EndpointMetadata.cs` (Newtonsoft, `[JsonProperty("id")]` on the partition-key field).

```csharp
using Newtonsoft.Json;
using System;

namespace NimBus.MessageStore.States
{
    /// <summary>One agent-defined event-type contract (spec 022). Immutable after creation.</summary>
    public class EventSchema
    {
        /// <summary>Namespaced, globally-unique id, e.g. "crm.contact.enriched.v1". Cosmos partition key.</summary>
        [JsonProperty(PropertyName = "id")]
        public string EventTypeId { get; set; }

        public string Name { get; set; }

        /// <summary>JSON Schema (draft 2020-12 or earlier) describing the payload, as a JSON string.</summary>
        public string JsonSchema { get; set; }

        public string Description { get; set; }

        /// <summary>Optional JSONPath selecting the session key for ordering.</summary>
        public string SessionKeyPath { get; set; }

        public int Version { get; set; } = 1;

        /// <summary>The agent that created it (from the API key).</summary>
        public string AgentId { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedUtc { get; set; }
    }
}
```

- [ ] **Step 2: Create the conflict exception** (signals HTTP 409 — re-register with a different schema).

```csharp
using System;

namespace NimBus.MessageStore.Abstractions.Exceptions
{
    /// <summary>Thrown when an event type is re-registered with a schema different from the stored one (immutable in v1).</summary>
    public sealed class SchemaConflictException : Exception
    {
        public SchemaConflictException(string eventTypeId)
            : base($"Event type '{eventTypeId}' already exists with a different schema; agent-defined schemas are immutable in v1.")
        {
            EventTypeId = eventTypeId;
        }

        public string EventTypeId { get; }
    }
}
```

> Check the existing `src/NimBus.MessageStore.Abstractions/Exceptions.cs` first; if exceptions live there, add `SchemaConflictException` to that file instead of a new one and match its namespace.

- [ ] **Step 3: Create the `IEventSchemaStore` interface.** Mirror `IEndpointMetadataStore.cs`.

```csharp
using NimBus.MessageStore.States;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NimBus.MessageStore.Abstractions
{
    /// <summary>Registry of agent-defined event-type schemas (spec 022). Schemas are immutable in v1.</summary>
    public interface IEventSchemaStore
    {
        /// <summary>Returns the schema for an event type, or null if unknown.</summary>
        Task<EventSchema> GetSchema(string eventTypeId);

        /// <summary>Returns all registered schemas (for discovery / catalog).</summary>
        Task<IReadOnlyList<EventSchema>> GetSchemas();

        /// <summary>
        /// Registers an event type. Idempotent when the stored schema is byte-identical
        /// (returns the existing record); throws <see cref="Exceptions.SchemaConflictException"/>
        /// when an event type already exists with a different schema.
        /// </summary>
        Task<EventSchema> DefineEventType(EventSchema schema);
    }
}
```

- [ ] **Step 4: Add `IEventSchemaStore` to the facade.** Edit `INimBusMessageStore.cs:13`:

```csharp
public interface INimBusMessageStore
    : IMessageTrackingStore, ISubscriptionStore, IEndpointMetadataStore, IMetricsStore, IEventSchemaStore
{ }
```

- [ ] **Step 5: Build the abstractions project to verify it compiles.**

Run: `dotnet build src/NimBus.MessageStore.Abstractions/NimBus.MessageStore.Abstractions.csproj`
Expected: SUCCESS. (Other store projects will now fail to compile until they implement the new interface — that is expected and handled in Tasks 2–4.)

- [ ] **Step 6: Commit.**

```bash
git add src/NimBus.MessageStore.Abstractions/
git commit -m "feat(store): IEventSchemaStore abstraction + EventSchema record (spec 022)"
```

---

### Task 2: InMemory implementation + conformance suite (TDD anchor)

The InMemory store is the always-green reference impl and the conformance harness. Write the conformance tests first; they define the contract for all three providers.

**Files:**
- Create: `src/NimBus.Testing/Conformance/EventSchemaStoreConformanceTests.cs`
- Modify: `src/NimBus.Testing/Conformance/InMemoryMessageStore.cs`
- Modify: `tests/NimBus.MessageStore.InMemory.Tests/InMemoryOtherStoreConformanceTests.cs`

- [ ] **Step 1: Write the abstract conformance tests.** Mirror `SubscriptionStoreConformanceTests.cs`.

```csharp
#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.Abstractions.Exceptions;
using NimBus.MessageStore.States;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.Testing.Conformance
{
    [TestClass]
    public abstract class EventSchemaStoreConformanceTests
    {
        protected abstract IEventSchemaStore CreateStore();

        private static EventSchema Sample(string id = "crm.contact.enriched.v1", string schema = "{\"type\":\"object\"}")
            => new EventSchema
            {
                EventTypeId = id,
                Name = "Contact Enriched",
                JsonSchema = schema,
                Description = "test",
                Version = 1,
                AgentId = "agent-1",
                CreatedBy = "agent-1",
                CreatedUtc = new DateTime(2026, 06, 05, 0, 0, 0, DateTimeKind.Utc),
            };

        [TestMethod]
        public async Task DefineEventType_then_GetSchema_round_trips()
        {
            var store = CreateStore();
            var id = $"ct.{Guid.NewGuid():N}.v1";
            await store.DefineEventType(Sample(id));

            var got = await store.GetSchema(id);

            Assert.IsNotNull(got);
            Assert.AreEqual(id, got.EventTypeId);
            Assert.AreEqual("{\"type\":\"object\"}", got.JsonSchema);
        }

        [TestMethod]
        public async Task GetSchema_unknown_returns_null()
        {
            var store = CreateStore();
            Assert.IsNull(await store.GetSchema($"ct.{Guid.NewGuid():N}.v1"));
        }

        [TestMethod]
        public async Task DefineEventType_identical_is_idempotent()
        {
            var store = CreateStore();
            var id = $"ct.{Guid.NewGuid():N}.v1";
            await store.DefineEventType(Sample(id));

            // identical re-register must not throw and must not duplicate
            var second = await store.DefineEventType(Sample(id));

            Assert.AreEqual(id, second.EventTypeId);
            Assert.AreEqual(1, (await store.GetSchemas()).Count(s => s.EventTypeId == id));
        }

        [TestMethod]
        public async Task DefineEventType_changed_schema_throws_conflict()
        {
            var store = CreateStore();
            var id = $"ct.{Guid.NewGuid():N}.v1";
            await store.DefineEventType(Sample(id, "{\"type\":\"object\"}"));

            await Assert.ThrowsExceptionAsync<SchemaConflictException>(
                () => store.DefineEventType(Sample(id, "{\"type\":\"object\",\"required\":[\"x\"]}")));
        }

        [TestMethod]
        public async Task GetSchemas_returns_registered()
        {
            var store = CreateStore();
            var id = $"ct.{Guid.NewGuid():N}.v1";
            await store.DefineEventType(Sample(id));

            Assert.IsTrue((await store.GetSchemas()).Any(s => s.EventTypeId == id));
        }
    }
}
```

- [ ] **Step 2: Add the InMemory subclass** in `tests/NimBus.MessageStore.InMemory.Tests/InMemoryOtherStoreConformanceTests.cs` (append):

```csharp
[TestClass]
public sealed class InMemoryEventSchemaStoreConformanceTests : EventSchemaStoreConformanceTests
{
    protected override IEventSchemaStore CreateStore() => new InMemoryMessageStore();
}
```

- [ ] **Step 3: Run the conformance tests to verify they FAIL** (InMemory does not implement the interface yet — compile error / not implemented).

Run: `dotnet test tests/NimBus.MessageStore.InMemory.Tests/ --filter "FullyQualifiedName~EventSchemaStore"`
Expected: FAIL (build error: `InMemoryMessageStore` does not implement `IEventSchemaStore`).

- [ ] **Step 4: Implement `IEventSchemaStore` on `InMemoryMessageStore`.** Add a backing dictionary and the three methods (normalize schema JSON for the idempotent/conflict comparison).

```csharp
// field, alongside _subscriptions / _metadata
private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EventSchema> _schemas = new();

public Task<EventSchema> GetSchema(string eventTypeId)
    => Task.FromResult(_schemas.TryGetValue(eventTypeId, out var s) ? s : null);

public Task<IReadOnlyList<EventSchema>> GetSchemas()
    => Task.FromResult<IReadOnlyList<EventSchema>>(_schemas.Values.ToList());

public Task<EventSchema> DefineEventType(EventSchema schema)
{
    var existing = _schemas.GetOrAdd(schema.EventTypeId, schema);
    if (!ReferenceEquals(existing, schema)
        && !SchemaJson.Equal(existing.JsonSchema, schema.JsonSchema))
    {
        throw new SchemaConflictException(schema.EventTypeId);
    }
    return Task.FromResult(existing);
}
```

Add a tiny shared normalizer so all providers compare schemas identically. Create `src/NimBus.MessageStore.Abstractions/SchemaJson.cs`:

```csharp
using Newtonsoft.Json.Linq;

namespace NimBus.MessageStore.Abstractions
{
    /// <summary>Structural equality for JSON Schema strings, ignoring whitespace/key order.</summary>
    public static class SchemaJson
    {
        public static bool Equal(string a, string b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            return JToken.DeepEquals(JToken.Parse(a), JToken.Parse(b));
        }
    }
}
```

> Add `using NimBus.MessageStore.Abstractions;`, `using NimBus.MessageStore.Abstractions.Exceptions;`, and `using NimBus.MessageStore.States;` to `InMemoryMessageStore.cs` if not present.

- [ ] **Step 5: Run the conformance tests to verify they PASS.**

Run: `dotnet test tests/NimBus.MessageStore.InMemory.Tests/ --filter "FullyQualifiedName~EventSchemaStore"`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit.**

```bash
git add src/NimBus.Testing/ src/NimBus.MessageStore.Abstractions/SchemaJson.cs tests/NimBus.MessageStore.InMemory.Tests/
git commit -m "feat(store): EventSchemaStore conformance suite + InMemory impl (spec 022)"
```

---

### Task 3: Cosmos implementation

**Files:**
- Modify: `src/NimBus.MessageStore.CosmosDb/CosmosDbClient.cs`
- Modify: `tests/NimBus.MessageStore.CosmosDb.Tests/CosmosDbOtherStoreConformanceTests.cs`

- [ ] **Step 1: Add the Cosmos subclass** (append to `CosmosDbOtherStoreConformanceTests.cs`):

```csharp
[TestClass]
public sealed class CosmosDbEventSchemaStoreConformanceTests : EventSchemaStoreConformanceTests
{
    protected override IEventSchemaStore CreateStore() => CosmosDbStoreTestHarness.CreateStore();
}
```

- [ ] **Step 2: Add the container constant + accessor** in `CosmosDbClient.cs` (mirror the `subscriptions` container, partition `/id`):

```csharp
private const string EventSchemasContainerName = "eventschemas";

private async Task<Container> GetEventSchemasContainer()
{
    var db = await GetDatabase();
    await db.CreateContainerIfNotExistsAsync(EventSchemasContainerName, "/id");
    return db.GetContainer(EventSchemasContainerName);
}
```

> Match the exact `GetDatabase()`/container-creation idiom already used by `GetSubscriptionsContainer` (around `CosmosDbClient.cs:912,935`).

- [ ] **Step 3: Implement the three methods** on `CosmosDbClient` (point read/write are single-partition because `id == eventTypeId`):

```csharp
public async Task<EventSchema> GetSchema(string eventTypeId)
{
    var container = await GetEventSchemasContainer();
    try
    {
        var resp = await container.ReadItemAsync<EventSchema>(eventTypeId, new PartitionKey(eventTypeId));
        return resp.Resource;
    }
    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return null;
    }
}

public async Task<IReadOnlyList<EventSchema>> GetSchemas()
{
    var container = await GetEventSchemasContainer();
    var results = new List<EventSchema>();
    using var iterator = container.GetItemQueryIterator<EventSchema>("SELECT * FROM c");
    while (iterator.HasMoreResults)
        results.AddRange(await iterator.ReadNextAsync());
    return results;
}

public async Task<EventSchema> DefineEventType(EventSchema schema)
{
    var existing = await GetSchema(schema.EventTypeId);
    if (existing != null)
    {
        if (!SchemaJson.Equal(existing.JsonSchema, schema.JsonSchema))
            throw new SchemaConflictException(schema.EventTypeId);
        return existing;
    }

    var container = await GetEventSchemasContainer();
    try
    {
        var resp = await container.CreateItemAsync(schema, new PartitionKey(schema.EventTypeId));
        return resp.Resource;
    }
    catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
    {
        // lost a create race — re-read and apply the same idempotent/conflict rule
        var raced = await GetSchema(schema.EventTypeId);
        if (raced != null && !SchemaJson.Equal(raced.JsonSchema, schema.JsonSchema))
            throw new SchemaConflictException(schema.EventTypeId);
        return raced;
    }
}
```

> Use the same `using` namespaces already imported in `CosmosDbClient.cs` (`Microsoft.Azure.Cosmos`, `NimBus.MessageStore.States`). Add `NimBus.MessageStore.Abstractions(.Exceptions)` if missing. If the project already has a `CosmosExceptionTranslation` helper for `RequestLimitException`, wrap calls consistently with the surrounding code.

- [ ] **Step 4: Run the Cosmos conformance tests** (requires the Cosmos emulator env the other Cosmos tests use; they self-skip via `Assert.Inconclusive` if unset).

Run: `dotnet test tests/NimBus.MessageStore.CosmosDb.Tests/ --filter "FullyQualifiedName~EventSchemaStore"`
Expected: PASS (or Inconclusive if the emulator isn't configured locally — CI runs it).

- [ ] **Step 5: Commit.**

```bash
git add src/NimBus.MessageStore.CosmosDb/ tests/NimBus.MessageStore.CosmosDb.Tests/
git commit -m "feat(store): Cosmos EventSchemaStore impl (spec 022)"
```

---

### Task 4: SQL Server implementation

**Files:**
- Create: `src/NimBus.MessageStore.SqlServer/Schema/0013_EventSchemas.sql`
- Modify: `src/NimBus.MessageStore.SqlServer/SqlServerMessageStore.cs`
- Modify: `src/NimBus.MessageStore.SqlServer/SqlServerSchemaInitializer.cs`
- Modify: `tests/NimBus.MessageStore.SqlServer.Tests/SqlServerOtherStoreConformanceTests.cs`
- Modify: `tests/NimBus.MessageStore.SqlServer.Tests/SqlServerStoreTestHarness.cs`

- [ ] **Step 1: Add the DbUp migration** `Schema/0013_EventSchemas.sql` (embedded automatically via `<EmbeddedResource Include="Schema\*.sql" />`). Mirror `0005_Subscriptions.sql`.

```sql
IF OBJECT_ID('[$schema$].[EventSchemas]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[EventSchemas] (
        [EventTypeId]   NVARCHAR(200) NOT NULL PRIMARY KEY,
        [Name]          NVARCHAR(400) NULL,
        [JsonSchema]    NVARCHAR(MAX) NOT NULL,
        [Description]   NVARCHAR(MAX) NULL,
        [SessionKeyPath] NVARCHAR(400) NULL,
        [Version]       INT NOT NULL DEFAULT(1),
        [AgentId]       NVARCHAR(200) NULL,
        [CreatedBy]     NVARCHAR(200) NULL,
        [CreatedUtc]    DATETIME2 NOT NULL
    );
END
GO
```

- [ ] **Step 2: Register the table for `VerifyOnly`** — add `"EventSchemas"` to `RequiredTables` in `SqlServerSchemaInitializer.cs:31`.

- [ ] **Step 3: Implement the three methods** on `SqlServerMessageStore` (Dapper; use the `T("EventSchemas")` schema-qualify helper at `SqlServerMessageStore.cs:55`):

```csharp
public async Task<EventSchema> GetSchema(string eventTypeId)
{
    using var conn = Open();
    return await conn.QuerySingleOrDefaultAsync<EventSchema>(
        $"SELECT * FROM {T("EventSchemas")} WHERE [EventTypeId] = @eventTypeId",
        new { eventTypeId });
}

public async Task<IReadOnlyList<EventSchema>> GetSchemas()
{
    using var conn = Open();
    var rows = await conn.QueryAsync<EventSchema>($"SELECT * FROM {T("EventSchemas")}");
    return rows.ToList();
}

public async Task<EventSchema> DefineEventType(EventSchema schema)
{
    var existing = await GetSchema(schema.EventTypeId);
    if (existing != null)
    {
        if (!SchemaJson.Equal(existing.JsonSchema, schema.JsonSchema))
            throw new SchemaConflictException(schema.EventTypeId);
        return existing;
    }

    using var conn = Open();
    try
    {
        await conn.ExecuteAsync(
            $@"INSERT INTO {T("EventSchemas")}
               ([EventTypeId],[Name],[JsonSchema],[Description],[SessionKeyPath],[Version],[AgentId],[CreatedBy],[CreatedUtc])
               VALUES (@EventTypeId,@Name,@JsonSchema,@Description,@SessionKeyPath,@Version,@AgentId,@CreatedBy,@CreatedUtc)",
            schema);
        return schema;
    }
    catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 2627 || ex.Number == 2601) // PK / unique violation race
    {
        var raced = await GetSchema(schema.EventTypeId);
        if (raced != null && !SchemaJson.Equal(raced.JsonSchema, schema.JsonSchema))
            throw new SchemaConflictException(schema.EventTypeId);
        return raced;
    }
}
```

> `EventSchema.EventTypeId` maps to the `[EventTypeId]` column by name — but note the DTO field is `[JsonProperty("id")]` for Cosmos; Dapper maps by **property name** (`EventTypeId`), so the column must be `EventTypeId` (as above), independent of the JSON attribute. Add `using NimBus.MessageStore.States;`/`Abstractions(.Exceptions)` if missing.

- [ ] **Step 4: Add the SQL subclass** (append to `SqlServerOtherStoreConformanceTests.cs`, matching the `[ClassInitialize]`/`[TestInitialize]` pattern of the sibling classes):

```csharp
[TestClass]
public sealed class SqlServerEventSchemaStoreConformanceTests : EventSchemaStoreConformanceTests
{
    [ClassInitialize] public static Task Init(TestContext ctx) => SqlServerStoreTestHarness.EnsureSchemaAsync();
    [TestInitialize] public Task Reset() => SqlServerStoreTestHarness.ResetAsync();
    protected override IEventSchemaStore CreateStore() => SqlServerStoreTestHarness.CreateStore();
}
```

> Copy the exact `[ClassInitialize]`/`[TestInitialize]` signatures used by the existing `SqlServer*ConformanceTests` in that file — they may differ slightly from the above.

- [ ] **Step 5: Add `EventSchemas` to the harness TRUNCATE list** in `SqlServerStoreTestHarness.cs:41-46` (so each test starts clean).

- [ ] **Step 6: Run the SQL conformance tests** (needs the SQL container CI uses; self-skips locally if unset).

Run: `dotnet test tests/NimBus.MessageStore.SqlServer.Tests/ --filter "FullyQualifiedName~EventSchemaStore"`
Expected: PASS (or skip locally; CI runs the service container).

- [ ] **Step 7: Commit.**

```bash
git add src/NimBus.MessageStore.SqlServer/ tests/NimBus.MessageStore.SqlServer.Tests/
git commit -m "feat(store): SQL Server EventSchemaStore impl + 0013 migration (spec 022)"
```

---

### Task 5: Register `IEventSchemaStore` in both providers' DI

**Files:**
- Modify: `src/NimBus.MessageStore.CosmosDb/CosmosDbMessageStoreBuilderExtensions.cs:61`
- Modify: `src/NimBus.MessageStore.SqlServer/SqlServerMessageStoreBuilderExtensions.cs:57`

- [ ] **Step 1: Add the facade exposure line** in each `RegisterContracts` block (after the `IMetricsStore` line):

```csharp
services.AddSingleton<IEventSchemaStore>(sp => sp.GetRequiredService<INimBusMessageStore>());
```

- [ ] **Step 2: Build the solution to confirm everything compiles.**

Run: `dotnet build src/NimBus.sln`
Expected: SUCCESS.

- [ ] **Step 3: Commit.**

```bash
git add src/NimBus.MessageStore.CosmosDb/ src/NimBus.MessageStore.SqlServer/
git commit -m "feat(store): register IEventSchemaStore in Cosmos + SQL providers (spec 022)"
```

---

# Part 1B — Agent REST API

### Task 6: Classless publish overload on `IPublisherClient`/`PublisherClient`

**Files:**
- Modify: `src/NimBus.SDK/IPublisherClient.cs`
- Modify: `src/NimBus.SDK/PublisherClient.cs`
- Test: `tests/NimBus.SDK.Tests/` (new test file `ClasslessPublishTests.cs`) — verify the message hits the sender unchanged.

- [ ] **Step 1: Write the failing test.** Use a fake `ISender` to capture the sent `Message`.

```csharp
#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.SDK;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.Tests;

[TestClass]
public class ClasslessPublishTests
{
    private sealed class CapturingSender : ISender
    {
        public readonly List<IMessage> Sent = new();
        public Task Send(IMessage m, int d = 0, CancellationToken ct = default) { Sent.Add(m); return Task.CompletedTask; }
        public Task Send(IEnumerable<IMessage> ms, int d = 0, CancellationToken ct = default) { Sent.AddRange(ms); return Task.CompletedTask; }
        public Task<long> ScheduleMessage(IMessage m, System.DateTimeOffset t, CancellationToken ct = default) => Task.FromResult(0L);
        public Task CancelScheduledMessage(long s, CancellationToken ct = default) => Task.CompletedTask;
    }

    [TestMethod]
    public async Task Publish_classless_message_sends_it_unchanged()
    {
        var sender = new CapturingSender();
        var publisher = new PublisherClient(sender);
        var msg = new Message
        {
            To = "crm.contact.enriched.v1",
            EventTypeId = "crm.contact.enriched.v1",
            SessionId = "s1",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "crm.contact.enriched.v1", EventJson = "{\"x\":1}" } },
        };

        await publisher.Publish(msg);

        Assert.AreEqual(1, sender.Sent.Count);
        Assert.AreEqual("crm.contact.enriched.v1", sender.Sent[0].EventTypeId);
    }
}
```

- [ ] **Step 2: Run it to verify it fails** (no `Publish(Message)` overload).

Run: `dotnet test tests/NimBus.SDK.Tests/ --filter "FullyQualifiedName~ClasslessPublish"`
Expected: FAIL (compile error: no overload taking `Message`/`IMessage`).

- [ ] **Step 3: Add the overload.** In `IPublisherClient.cs`:

```csharp
/// <summary>
/// Publishes a pre-built, dynamically-typed message (no compiled IEvent). The caller is responsible
/// for setting EventTypeId + MessageContent.EventContent. Used by the agent REST API (spec 022).
/// </summary>
Task Publish(NimBus.Core.Messages.IMessage message, CancellationToken cancellationToken = default);
```

In `PublisherClient.cs` (mirror how the existing `Publish(IEvent)` calls `_sender.Send`, including any instrumentation wrapper):

```csharp
public Task Publish(IMessage message, CancellationToken cancellationToken = default)
{
    return _sender.Send(message, 0, cancellationToken);
}
```

> If `Publish(IEvent)` wraps sends in an OpenTelemetry activity (see `CreateAsync`/instrumented sender at `PublisherClient.cs:59-63`), the classless overload inherits it automatically because instrumentation lives in the wrapped `_sender`. Confirm `_sender` is the instrumented one.

- [ ] **Step 4: Run the test to verify it passes.**

Run: `dotnet test tests/NimBus.SDK.Tests/ --filter "FullyQualifiedName~ClasslessPublish"`
Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/NimBus.SDK/ tests/NimBus.SDK.Tests/
git commit -m "feat(sdk): classless Publish(IMessage) overload for agent publish (spec 022)"
```

---

### Task 7: Agent Zone endpoint + park-as-pending-handoff subscriber

This is the glue that makes `/receive` + `/settle` work: the Agent Zone has a coded subscriber whose handler parks routed events as `Pending+Handoff`. For Phase 1 we prove it on the in-memory harness; Phase 3 wires it into the demo AppHost.

**Files:**
- Create: `samples/CrmErpDemo/CrmErpDemo.Contracts/Endpoints/AgentZoneEndpoint.cs`
- Modify: `samples/CrmErpDemo/CrmErpDemo.Contracts/CrmErpPlatformConfiguration.cs`
- Create: `src/NimBus.SDK/EventHandlers/MarkPendingHandoffJsonHandler.cs` (a reusable raw-JSON handler that parks)
- Test: `tests/NimBus.EndToEnd.Tests/AgentParkAndSettleTests.cs`

- [ ] **Step 1: Add the Agent Zone endpoint** (mirror `DataPlatformEndpoint.cs`). Subscribe it to `CrmContactCreated` so the demo event is forwarded into the Agent Zone:

```csharp
using NimBus.Abstractions.Endpoints;
using CrmErpDemo.Contracts.Events;

namespace CrmErpDemo.Contracts.Endpoints;

/// <summary>Spec 022 Agent Zone: pre-provisioned endpoint carrying dynamically-typed agent events.</summary>
public sealed class AgentZoneEndpoint : Endpoint
{
    public AgentZoneEndpoint()
    {
        Consumes<CrmContactCreated>();
    }
}
```

> Use the exact base class + `Consumes<>` idiom from a sibling endpoint (`DataPlatformEndpoint.cs`); confirm the event namespace.

- [ ] **Step 2: Register it** in `CrmErpPlatformConfiguration` — add `AddEndpoint(new AgentZoneEndpoint());` next to the others.

- [ ] **Step 3: Create the reusable park handler.** A `DelegateEventJsonHandler` variant that marks pending-handoff (this is the consumer-side counterpart to Phase 0's `DelegateEventJsonHandler`):

```csharp
using NimBus.Core.Messages;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.EventHandlers
{
    /// <summary>
    /// Parks any received event as Pending+Handoff so an external agent can pull it via the
    /// REST /api/agent/receive endpoint and settle it later (spec 022). Holds no Service Bus lock.
    /// </summary>
    public sealed class MarkPendingHandoffJsonHandler : IEventJsonHandler
    {
        private readonly string _reason;
        public MarkPendingHandoffJsonHandler(string reason = "Awaiting agent pickup") => _reason = reason;

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            // EventHandlerContext.MarkPendingHandoff sets HandlerOutcome.PendingHandoff;
            // StrictMessageHandler then parks the row, blocks the session, completes the SB msg.
            if (context is IEventHandlerContext handoffCtx)
                handoffCtx.MarkPendingHandoff(_reason);
            return Task.CompletedTask;
        }
    }
}
```

> Verify the exact mechanism: the park signal is on `IEventHandlerContext.MarkPendingHandoff` (`IEventHandlerContext.cs:43`). In the real dispatch the handler receives an `EventHandlerContext` (built in `EventJsonHandler<T>`); for a raw-JSON handler you must construct/obtain an `IEventHandlerContext` around the `IMessageContext`. Mirror how `EventJsonHandler<T>` builds `EventHandlerContext` (`EventJsonHandler.cs:23-29`) and call `MarkPendingHandoff` on it, then ensure `StrictMessageHandler` observes the outcome. **This is the one integration subtlety — pin it down against `StrictMessageHandler.cs:69-76` before coding.**

- [ ] **Step 4: Write the end-to-end park test** (in-memory): publish a classless event → Agent Zone park handler → assert the message is completed and a `PendingHandoffResponse` was emitted (the parked-row signal), and the session is blocked.

```csharp
#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;
using NimBus.SDK.EventHandlers;

namespace NimBus.EndToEnd.Tests;

[TestClass]
public class AgentParkAndSettleTests
{
    [TestMethod]
    public async Task ParkedDynamicEvent_EmitsPendingHandoff_AndCompletes()
    {
        var fixture = new EndToEndFixture();
        fixture.RegisterDynamicHandler("crm.contact.enriched.v1", () => new MarkPendingHandoffJsonHandler());

        await fixture.PublishBus.Send(new Message
        {
            To = "crm.contact.enriched.v1",
            EventTypeId = "crm.contact.enriched.v1",
            SessionId = "s1",
            MessageId = System.Guid.NewGuid().ToString(),
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "crm.contact.enriched.v1", EventJson = "{}" } },
        });
        await fixture.DeliverAll();

        Assert.IsTrue(fixture.ResponseBus.SentMessages.Any(m => m.MessageType == MessageType.PendingHandoffResponse),
            "Parked event must emit a PendingHandoffResponse.");
    }
}
```

- [ ] **Step 5: Run it; fix the park-handler wiring until it passes.**

Run: `dotnet test tests/NimBus.EndToEnd.Tests/ --filter "FullyQualifiedName~AgentParkAndSettle"`
Expected: PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/NimBus.SDK/ samples/CrmErpDemo/ tests/NimBus.EndToEnd.Tests/
git commit -m "feat(agent): Agent Zone endpoint + park-as-pending-handoff handler (spec 022)"
```

---

### Task 8: Add `/api/agent/*` to api-spec.yaml (codegen source)

**Files:**
- Modify: `src/NimBus.WebApp/api-spec.yaml`
- Modify: `src/NimBus.WebApp/NimBus.WebApp.csproj` (explicit NJsonSchema ref)

- [ ] **Step 1: Add the `Agent` tag** to the `tags:` list at the bottom of `api-spec.yaml`:

```yaml
  - name: Agent
```

- [ ] **Step 2: Add the six paths.** All tagged `Agent` (one controller). Full definitions:

```yaml
  /api/agent/catalog:
    get:
      summary: Discover endpoints, event types, and registered agent schemas
      operationId: get-agent-catalog
      responses:
        '200':
          description: OK
          content: { application/json: { schema: { $ref: '#/components/schemas/AgentCatalog' } } }
      tags: [Agent]
  /api/agent/event-types:
    post:
      summary: Define (register) an agent event type by JSON Schema
      operationId: post-agent-event-types
      requestBody:
        content: { application/json: { schema: { $ref: '#/components/schemas/DefineEventTypeRequest' } } }
      responses:
        '200': { description: Created or idempotent, content: { application/json: { schema: { $ref: '#/components/schemas/EventTypeInfo' } } } }
        '409': { description: Event type exists with a different schema }
      tags: [Agent]
  /api/agent/subscribe:
    post:
      summary: Record a pull-based logical subscription to an event type
      operationId: post-agent-subscribe
      requestBody:
        content: { application/json: { schema: { $ref: '#/components/schemas/AgentSubscribeRequest' } } }
      responses:
        '200': { description: OK }
      tags: [Agent]
  /api/agent/receive:
    get:
      summary: Long-poll pull of the next parked message for the agent
      operationId: get-agent-receive
      parameters:
        - { name: eventTypeId, in: query, required: false, schema: { type: string } }
        - { name: waitSeconds, in: query, required: false, schema: { type: integer } }
      responses:
        '200': { description: A message, or empty if none, content: { application/json: { schema: { $ref: '#/components/schemas/AgentReceivedMessage' } } }
        '204': { description: No message available within the wait window }
      tags: [Agent]
  /api/agent/publish:
    post:
      summary: Publish an agent event (payload validated against its registered schema)
      operationId: post-agent-publish
      requestBody:
        content: { application/json: { schema: { $ref: '#/components/schemas/AgentPublishRequest' } } }
      responses:
        '200': { description: Published }
        '400': { description: Payload failed schema validation }
        '404': { description: Unknown eventTypeId }
      tags: [Agent]
  /api/agent/settle:
    post:
      summary: Complete or fail a received message
      operationId: post-agent-settle
      requestBody:
        content: { application/json: { schema: { $ref: '#/components/schemas/AgentSettleRequest' } } }
      responses:
        '200': { description: Settled }
      tags: [Agent]
```

- [ ] **Step 3: Add the DTO schemas** under `components/schemas:` (set `title:` so the generated C# class names are exactly these). Define: `AgentCatalog` (endpoints: string[], eventTypes: `EventTypeInfo[]`), `EventTypeInfo` (eventTypeId, name, jsonSchema, description), `DefineEventTypeRequest` (eventTypeId, name, jsonSchema, description, sessionKeyPath?), `AgentSubscribeRequest` (eventTypeId), `AgentReceivedMessage` (eventTypeId, payload, coordinates: `HandoffCoordinates`), `HandoffCoordinates` (eventId, sessionId, messageId, eventTypeId, correlationId, originatingMessageId), `AgentPublishRequest` (eventTypeId, payload, sessionId?), `AgentSettleRequest` (coordinates: `HandoffCoordinates`, outcome: enum[complete,fail], result?, errorText?, errorType?). Mirror the YAML shape of an existing `components/schemas` object (e.g. `MessageSearchRequest`). Mark only genuinely-required fields under `required:` (nswag makes required props constructor-mandatory).

- [ ] **Step 4: Add the explicit NJsonSchema package reference** to `NimBus.WebApp.csproj`:

```xml
<PackageReference Include="NJsonSchema" Version="11.5.2" />
```

> If the repo centralizes versions in `Directory.Packages.props`, add the version there and reference without the version attribute, per the repo convention.

- [ ] **Step 5: Build to regenerate the contract + TS client.**

Run: `dotnet build src/NimBus.WebApp/NimBus.WebApp.csproj`
Expected: SUCCESS; `Controllers/ApiContract.g.cs` now contains `IAgentApiController` (6 `…Async` methods) + `AgentApiController`, and `ClientApp/src/api-client/index.ts` gains the agent methods + DTOs. The build will then FAIL only if an `AgentImplementation` is referenced but missing — it isn't yet, so the generated controller is registered lazily. (Do not hand-edit the generated files.)

- [ ] **Step 6: Commit.**

```bash
git add src/NimBus.WebApp/api-spec.yaml src/NimBus.WebApp/Controllers/ApiContract.g.cs src/NimBus.WebApp/ClientApp/src/api-client/index.ts src/NimBus.WebApp/NimBus.WebApp.csproj
git commit -m "feat(agent): /api/agent/* OpenAPI surface + regenerated contract/client (spec 022)"
```

---

### Task 9: `AgentImplementation` — catalog + define-event-type

**Files:**
- Create: `src/NimBus.WebApp/Controllers/ApiContract/AgentImplementation.cs`
- Modify: `src/NimBus.WebApp/Startup.cs`
- Test: `tests/NimBus.WebApp.Tests/AgentImplementationTests.cs`

- [ ] **Step 1: Scaffold `AgentImplementation`** implementing `IAgentApiController`, injecting `IEventSchemaStore`, `IPlatform`, `ILogger<AgentImplementation>` (and, in later tasks, `ServiceBusClient`/`IPublisherClient`, `IHandoffClient`, `INimBusMessageStore`). Implement `GetAgentCatalogAsync` (compose `IPlatform.Endpoints` + `IEventSchemaStore.GetSchemas()`) and `PostAgentEventTypesAsync` (build an `EventSchema` from the request, set `CreatedUtc`/`AgentId` from the API-key principal, call `DefineEventType`; map `SchemaConflictException` → `Conflict()`/409, return `EventTypeInfo` on success). Model the class shape on `MessageImplementation.cs`.

```csharp
// Sketch — fill in generated type names from ApiContract.g.cs after Task 8 build.
public class AgentImplementation : IAgentApiController
{
    private readonly IEventSchemaStore _schemas;
    private readonly IPlatform _platform;
    private readonly ILogger<AgentImplementation> _logger;
    // + ServiceBusClient, IHandoffClient, INimBusMessageStore in Tasks 10-11

    public AgentImplementation(IEventSchemaStore schemas, IPlatform platform, ILogger<AgentImplementation> logger)
    { _schemas = schemas; _platform = platform; _logger = logger; }

    public async Task<ActionResult<AgentCatalog>> GetAgentCatalogAsync()
    {
        var schemas = await _schemas.GetSchemas();
        var catalog = new AgentCatalog
        {
            Endpoints = _platform.Endpoints.Select(e => e.Id).ToList(),
            EventTypes = schemas.Select(s => new EventTypeInfo {
                EventTypeId = s.EventTypeId, Name = s.Name, JsonSchema = s.JsonSchema, Description = s.Description
            }).ToList(),
        };
        return new OkObjectResult(catalog);
    }

    public async Task<ActionResult<EventTypeInfo>> PostAgentEventTypesAsync(DefineEventTypeRequest body)
    {
        try
        {
            var saved = await _schemas.DefineEventType(new EventSchema {
                EventTypeId = body.EventTypeId, Name = body.Name, JsonSchema = body.JsonSchema,
                Description = body.Description, SessionKeyPath = body.SessionKeyPath, Version = 1,
                AgentId = CurrentAgentId(), CreatedBy = CurrentAgentId(), CreatedUtc = DateTime.UtcNow,
            });
            return new OkObjectResult(new EventTypeInfo {
                EventTypeId = saved.EventTypeId, Name = saved.Name, JsonSchema = saved.JsonSchema, Description = saved.Description
            });
        }
        catch (SchemaConflictException)
        {
            return new ConflictResult();
        }
    }

    private string CurrentAgentId() => /* from API-key principal; Task 11 wires auth */ "demo-agent";
}
```

- [ ] **Step 2: Register the implementation** in `Startup.cs` (~line 459): `services.AddTransient<IAgentApiController, AgentImplementation>();`

- [ ] **Step 3: Write tests** for catalog (returns endpoints + schemas) and define (idempotent + 409 on changed schema), using a stub `IEventSchemaStore` (or the InMemory store) and a stub `IPlatform`. Mirror existing WebApp tests in `tests/NimBus.WebApp.Tests/`.

- [ ] **Step 4: Run the tests.**

Run: `dotnet test tests/NimBus.WebApp.Tests/ --filter "FullyQualifiedName~AgentImplementation"`
Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/NimBus.WebApp/ tests/NimBus.WebApp.Tests/
git commit -m "feat(agent): /api/agent/catalog + /event-types (spec 022)"
```

---

### Task 10: `AgentImplementation` — publish (schema validation + classless publish)

**Files:**
- Modify: `src/NimBus.WebApp/Controllers/ApiContract/AgentImplementation.cs`
- Test: `tests/NimBus.WebApp.Tests/AgentImplementationTests.cs`

- [ ] **Step 1: Write the failing tests:** (a) publish with a payload that violates the registered schema → `BadRequest` (400) and nothing published; (b) unknown eventTypeId → `NotFound` (404); (c) valid payload → publishes a classless `Message` whose `EventTypeId` matches and `EventJson` is the payload (assert via a fake publisher).

- [ ] **Step 2: Implement `PostAgentPublishAsync`:**

```csharp
public async Task<ActionResult> PostAgentPublishAsync(AgentPublishRequest body)
{
    var schema = await _schemas.GetSchema(body.EventTypeId);
    if (schema == null) return new NotFoundObjectResult($"Unknown eventTypeId '{body.EventTypeId}'.");

    var jsonSchema = await NJsonSchema.JsonSchema.FromJsonAsync(schema.JsonSchema);
    var errors = jsonSchema.Validate(body.Payload);
    if (errors.Count > 0)
        return new BadRequestObjectResult(errors.Select(e => $"{e.Path}: {e.Kind}").ToList());

    var agentZoneId = ResolveAgentZoneEndpointId(); // from IPlatform; the topic to send on
    var message = new Message
    {
        To = body.EventTypeId,
        EventTypeId = body.EventTypeId,
        SessionId = body.SessionId ?? Guid.NewGuid().ToString(),
        CorrelationId = Guid.NewGuid().ToString(),
        MessageId = Guid.NewGuid().ToString(),
        RetryCount = 0,
        MessageType = MessageType.EventRequest,
        MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = body.EventTypeId, EventJson = body.Payload } },
    };

    var publisher = await PublisherClient.CreateAsync(_serviceBusClient, agentZoneId);
    await publisher.Publish(message); // classless overload from Task 6
    return new OkResult();
}
```

> `body.Payload` is the event JSON as a string. If the generated DTO models `payload` as an object, serialize it with `JsonConvert.SerializeObject` before validation/publish. `ResolveAgentZoneEndpointId()` reads the Agent Zone endpoint id from `IPlatform` (don't hard-code). Add `using NJsonSchema;` etc.

- [ ] **Step 3: Run the tests; iterate to green.**

Run: `dotnet test tests/NimBus.WebApp.Tests/ --filter "FullyQualifiedName~AgentImplementation"`
Expected: PASS.

- [ ] **Step 4: Commit.**

```bash
git add src/NimBus.WebApp/ tests/NimBus.WebApp.Tests/
git commit -m "feat(agent): /api/agent/publish with schema validation (spec 022)"
```

---

### Task 11: `AgentImplementation` — subscribe, receive (long-poll), settle

**Files:**
- Modify: `src/NimBus.WebApp/Controllers/ApiContract/AgentImplementation.cs`
- Modify: `src/NimBus.WebApp/Startup.cs` (register `IHandoffClient` for the Agent Zone + API-key auth)
- Test: `tests/NimBus.WebApp.Tests/AgentImplementationTests.cs`

- [ ] **Step 1: Implement `subscribe`** — record `(agentId, eventTypeId)` interest. v1: persist via a lightweight record (reuse `ISubscriptionStore` if its shape fits, or store on the schema/agent record). Return `200`. (Keep it minimal; it only filters `/receive`.)

- [ ] **Step 2: Implement `receive` (long-poll over the store).** Read the next `Pending+Handoff` row for the Agent Zone endpoint, filtered by the agent's subscribed `eventTypeId` (or the `eventTypeId` query param). Reuse the existing pending-query path the WebApp already uses (`GetPendingEventsOnSession` / `GetEventPendingIdAsync`, `EventImplementation.cs:636-660`), filtered to `PendingSubStatus == "Handoff"`. Loop with a short delay up to `waitSeconds`; return `204` if none.

```csharp
public async Task<ActionResult<AgentReceivedMessage>> GetAgentReceiveAsync(string eventTypeId, int? waitSeconds)
{
    var deadline = DateTime.UtcNow.AddSeconds(waitSeconds ?? 20);
    do
    {
        var parked = await NextParkedEvent(ResolveAgentZoneEndpointId(), eventTypeId, CurrentAgentId());
        if (parked != null)
        {
            return new OkObjectResult(new AgentReceivedMessage
            {
                EventTypeId = parked.EventTypeId,
                Payload = parked.MessageContent?.EventContent?.EventJson,
                Coordinates = new HandoffCoordinates
                {
                    EventId = parked.EventId, SessionId = parked.SessionId, MessageId = parked.LastMessageId,
                    EventTypeId = parked.EventTypeId, CorrelationId = parked.CorrelationId,
                    OriginatingMessageId = parked.OriginatingMessageId,
                },
            });
        }
        await Task.Delay(500);
    } while (DateTime.UtcNow < deadline);
    return new NoContentResult();
}
```

> Confirm the exact property names on the stored `UnresolvedEvent` (`EventId`, `SessionId`, `CorrelationId`, `OriginatingMessageId`, `EventTypeId`, and the last message id — `EventImplementation.SettlePendingHandoffAsync` at `EventImplementation.cs:297-306` and `ManagerClient.CoordsFor` at `ManagerClient.cs:141-146` show the precise mapping, including `ParentMessageId = entry.MessageId`). Reuse that mapping verbatim.

- [ ] **Step 3: Implement `settle`** via `IHandoffClient`:

```csharp
public async Task<ActionResult> PostAgentSettleAsync(AgentSettleRequest body)
{
    var coords = new HandoffSettlement(
        body.Coordinates.EventId, body.Coordinates.SessionId, body.Coordinates.MessageId,
        body.Coordinates.EventTypeId, body.Coordinates.CorrelationId, body.Coordinates.OriginatingMessageId);

    if (string.Equals(body.Outcome, "fail", StringComparison.OrdinalIgnoreCase))
        await _handoffClient.FailAsync(coords, body.ErrorText ?? "agent failure", body.ErrorType ?? "AgentFailure");
    else
        await _handoffClient.CompleteAsync(coords, body.Result);

    return new OkResult();
}
```

> Use the real `HandoffSettlement` ctor signature from `IHandoffClient.cs:19-25` and the real `CompleteAsync`/`FailAsync` signatures (`:53,66`). The WebApp operator path (`EventImplementation.cs:213-249` via `managerClient.CompleteHandoff/FailHandoff`) is the working reference; prefer the non-obsolete `IHandoffClient`.

- [ ] **Step 4: Register `IHandoffClient` for the Agent Zone + API-key auth** in `Startup.cs`. Find how `IHandoffClient` is constructed elsewhere (`AddNimBusHandoffClient("AgentZone")` or equivalent) and add it; add a minimal API-key authentication that maps a key → `agentId` (demo-grade per spec) and exposes it to `AgentImplementation` (via `IHttpContextAccessor`).

- [ ] **Step 5: Write tests** — subscribe records interest; receive returns a parked row mapped to coordinates (seed the store with a `Pending+Handoff` row); receive returns 204 when none; settle(complete) calls `IHandoffClient.CompleteAsync` with the right coords (fake `IHandoffClient`); settle(fail) calls `FailAsync`.

- [ ] **Step 6: Run the tests; iterate to green.**

Run: `dotnet test tests/NimBus.WebApp.Tests/ --filter "FullyQualifiedName~AgentImplementation"`
Expected: PASS.

- [ ] **Step 7: Commit.**

```bash
git add src/NimBus.WebApp/ tests/NimBus.WebApp.Tests/
git commit -m "feat(agent): /api/agent/{subscribe,receive,settle} via pending-handoff (spec 022)"
```

---

### Task 12: Integration test — full agent loop on the in-memory harness

**Files:**
- Test: `tests/NimBus.EndToEnd.Tests/AgentLoopIntegrationTests.cs`

- [ ] **Step 1: Write one integration test** exercising the capability core without the WebApp HTTP layer: define a schema (InMemory `IEventSchemaStore`), publish a `CrmContactCreated` → Agent Zone parks it → "receive" reads the parked row → publish `crm.contact.enriched.v1` (validated against the schema) → a coded handler consumes the enriched event → settle the original → Resolver audit shows the original completed. Reuse `EndToEndFixture`, the park handler (Task 7), the `IEventSchemaStore` InMemory impl, and a `ResolverService` (as in `ResolverServiceTests`). This is the Phase 1 analogue of the spec's "Integration (EndToEnd.Tests style)" test (spec line ~285).

- [ ] **Step 2: Run it to green.**

Run: `dotnet test tests/NimBus.EndToEnd.Tests/ --filter "FullyQualifiedName~AgentLoopIntegration"`
Expected: PASS.

- [ ] **Step 3: Commit.**

```bash
git add tests/NimBus.EndToEnd.Tests/
git commit -m "test(agent): full define→publish→park→receive→enrich→settle loop (spec 022)"
```

---

### Task 13: Full build + test sweep

- [ ] **Step 1: Build the whole solution.**

Run: `dotnet build src/NimBus.sln`
Expected: SUCCESS (Release config will enforce TreatWarningsAsErrors — fix any analyzer errors the new code introduces; the `#pragma warning disable CA1707, CA2007` header belongs only on test files).

- [ ] **Step 2: Run the full test suite.**

Run: `dotnet test src/NimBus.sln`
Expected: all green (Cosmos/SQL conformance may be Inconclusive locally without their containers; they run in CI).

- [ ] **Step 3: Build the WebApp client** to confirm the regenerated TS compiles.

Run: `cd src/NimBus.WebApp/ClientApp && npm install && npm run build`
Expected: SUCCESS.

---

### Task 14: Reconcile the spec's error-handling section

**Files:**
- Modify: `docs/specs/022-ai-agent-bus-participation/spec.md`

- [ ] **Step 1: Update the error-handling row** for "Agent crashes / never settles." Replace the SB-peek-lock-redelivery wording with the park-and-recover reality: a parked agent task is recorded `Pending+Handoff` with the session blocked; recovery in v1 is operator **Resubmit/Skip** from the WebApp plus the visible `ExpectedBy` deadline; an automatic timeout sweeper is deferred to a later sub-project. Add a one-line note under the Architecture/receive-settle description that `/receive` + `/settle` are built on the pending-handoff mechanism (no held SB lock). Keep the rest of the table (schema-validation 400, unknown type 404, define 409, oversized 413) as-is.

- [ ] **Step 2: Commit.**

```bash
git add docs/specs/022-ai-agent-bus-participation/spec.md
git commit -m "docs(spec022): reconcile receive/settle error-handling with pending-handoff design"
```

---

## Self-review notes

- **Spec coverage:** In-scope items mapped — agent REST API (Tasks 8–12), schema registry + Cosmos/SQL (Tasks 1–5), Agent Zone routing (Task 7, on Phase 0's proven property routing), MCP-tool→REST mapping is satisfied because every endpoint exists (the `NimBus.Mcp` server itself is **Phase 2**, not this plan), tests at unit/contract/integration levels (Tasks 2–4, 6, 9–12). **Out of this plan (later phases):** `NimBus.Mcp` server (Phase 2); `EnrichmentAgent` runner + AppHost wiring + demo smoke + live SB-emulator routing fidelity (Phase 3); ops/SRE agent and `NimBus.Agents` SDK (deferred sub-projects).
- **Open decision for the user (surface at handoff):** Recovery for a crashed agent — ship **v1-minimal** (operator Resubmit/Skip + `ExpectedBy` visibility, Task 14) vs add a **timeout sweeper** now. Plan assumes v1-minimal.
- **Type consistency:** `EventSchema.EventTypeId` (`[JsonProperty("id")]` for Cosmos; column `EventTypeId` for Dapper), `IEventSchemaStore.{GetSchema,GetSchemas,DefineEventType}`, `SchemaJson.Equal`, `SchemaConflictException`, `HandoffCoordinates`/`HandoffSettlement`, `Publish(IMessage)` — used consistently across tasks.
- **Codegen caveat:** Tasks 9–11 reference generated types (`IAgentApiController`, `AgentCatalog`, etc.) that only exist after the Task 8 build. Execute in order; rebuild after editing `api-spec.yaml`.
- **Verify-before-coding flags:** (1) the park-handler's `MarkPendingHandoff` wiring for a raw-JSON handler (Task 7 Step 3); (2) exact `UnresolvedEvent` coordinate property names for `/receive` (Task 11 Step 2); (3) `IHandoffClient` registration idiom for the Agent Zone (Task 11 Step 4).
