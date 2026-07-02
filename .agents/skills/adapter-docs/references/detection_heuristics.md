# Detection heuristics — where to look in a NimBus adapter repo

Reference material for the `adapter-docs` skill. Load on demand when running in **generate-from-code** or **update-from-diff** mode. These heuristics reflect the conventions used by NimBus and demonstrated in `samples/CrmErpDemo` (`Crm.Adapter`, `Erp.Adapter.Functions`, `CrmErpDemo.Contracts`, `CrmErpDemo.Provisioner`).

## Runtime and hosting

| Signal | Conclusion |
|---|---|
| `*.csproj` with `<TargetFramework>net{N}.0</TargetFramework>` | .NET N. C# is the language. NimBus targets `net10.0`. |
| `Program.cs` calls `Host.CreateApplicationBuilder(args)` + `builder.AddServiceDefaults()` | Generic .NET host worker, typically deployed as Container App via Aspire. |
| `Program.cs` calls `FunctionsApplication.CreateBuilder(args)` and `builder.ConfigureFunctionsWebApplication()` | Azure Functions (isolated worker). `host.json` + `local.settings.json` confirm. |
| `Microsoft.NET.Sdk.Web` SDK + `WebApplication.CreateBuilder` | ASP.NET Core (Web API surface — webhook receiver, plugin endpoint). |
| `Dockerfile` in the repo root | Built as a container image. |
| Aspire AppHost project (`*.AppHost.csproj` referencing `Aspire.Hosting.AppHost`) | Local orchestration via .NET Aspire; resources are declared in the AppHost's `Program.cs`. |
| `bicep/`, `terraform/`, `azure.yaml`, `infra/` | Infra-as-code is also present; cross-reference with Aspire (Aspire may publish manifests that the IaC consumes). |

Record runtime + hosting model in TDD §2 metadata and in the title block.

## NimBus event contract

Default pattern: a `*.Contracts` project (sibling project or shared NuGet) containing one file per adapter under `**/Endpoints/*Endpoint.cs`, plus event classes under `**/Events/*.cs`.

Example: `samples/CrmErpDemo/CrmErpDemo.Contracts/Endpoints/CrmEndpoint.cs`:

```csharp
public class CrmEndpoint : Endpoint
{
    public CrmEndpoint()
    {
        Produces<AccountCreated>();
        Produces<AccountUpdated>();
        Consumes<CustomerCreated>();
        Consumes<ContactUpdated>();
    }

    public override ISystem System => new CrmSystem();
    public override string Description => "...";
}
```

Extract:

- Class name → TDD §3 "Source of truth". The class name doubles as the topic / endpoint name (NimBus uses `Id => GetType().Name`).
- `ISystem.SystemId` from the `System` override → adapter source-system identifier for §1 / §2.
- Every `Consumes<TEvent>()` → TDD §3.1 row + `events.md` §1.2 row.
- Every `Produces<TEvent>()` → TDD §3.2 row + `events.md` §1.1 row.
- The endpoint's `Description` → use as the §1.1 purpose hint.

Cross-references in adapter code:

- `services.AddNimBusPublisher("{EndpointName}")` and `services.AddNimBusSubscriber("{EndpointName}", sub => ...)` calls in `Program.cs` should match the endpoint class name.
- `services.AddNimBusReceiver(opts => { opts.TopicName = "..."; opts.SubscriptionName = "..."; })` should also reference the endpoint name. Flag mismatches between contract and wiring.

The `Platform` subclass (typically `*PlatformConfiguration.cs` in the contracts project) registers all endpoints that participate in the platform — useful to confirm the adapter's endpoint is actually included.

## Handlers

Default pattern: classes implementing `NimBus.SDK.EventHandlers.IEventHandler<T>`.

Signals:

- `: IEventHandler<SomeEvent>` → handler.
- `public Task Handle(SomeEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)` → handler entry point. The `IEventHandlerContext` parameter exposes correlation data and the response surface.
- Registration via `sub.AddHandler<SomeEvent, SomeEventHandler>()` inside the `AddNimBusSubscriber` builder action in `Program.cs`.
- Constructor-injected dependencies (clients, repositories, `ILogger<T>`) — these drive the sequence diagram participants.

For each handler: record class name, consumed event type, repositories and services injected, and whether it throws or silent-skips on missing references.

## Publishers

Signals:

- `IPublisherClient.PublishAsync(event, ...)` calls — the canonical publish path.
- A webhook / HTTP endpoint (`/events`, `/webhook/...`, plugin receiver) that resolves an event type and forwards to `IPublisherClient`.
- Scheduled services: `IHostedService`, `BackgroundService`, or a Functions `[Timer]` trigger.
- Within a handler, calls to `context.RespondAsync(...)` (from `IEventHandlerContext`) — these are response messages back through the same endpoint.

Map each publish call-site to the event it emits. For round-trip flows (consume + respond), document both directions in the same handler row in §5.

## Outbox

Signals:

- `services.AddNimBusSqlServerOutbox(connectionString)` → SQL Server transactional outbox.
- `services.AddNimBusOutboxDispatcher(...)` → background dispatcher hosted service.
- `OutboxDispatcherSender` registered as a singleton.
- `OutboxSender` decorating `ISender` (set up automatically when `IOutbox` is registered).

If the outbox is wired up, document this in TDD §4 (the publish path is decoupled from the API request scope) and in §2.2 (state store appears as a Container in the C4 Level 2 diagram).

## Pipeline behaviours

Signals:

- `services.AddNimBus(n => { n.AddPipelineBehavior<X>(); })` calls.
- Built-in behaviours: `LoggingMiddleware`, `MetricsMiddleware`, `ValidationMiddleware` (in `NimBus.Core.Pipeline`).
- Custom `IMessagePipelineBehavior` implementations.

Record the order of registration — pipeline behaviours run in registration order, so the order is part of the contract.

## Retry and resilience

Signals:

- `IRetryPolicyProvider` / `DefaultRetryPolicyProvider` registered in DI.
- A `subscriber.RetryPolicies(provider => ...)` block (or equivalent) inside `AddNimBusSubscriber`.
- Per-event retry rules: `provider.Configure<TEvent>(p => p.WithMaxAttempts(N).WithBackoff(...))`.
- Polly directly (`IAsyncPolicy`, `Polly.Policy.WrapAsync`) — flag as an alignment risk: NimBus has its own retry pipeline and bypassing it loses Resolver visibility.
- Exception predicates checking message substrings like `deadlock`, `timeout`, `lock`, `429`, `503`.
- Service Bus delivery-count handling — terminal failure after N delivery attempts is also part of the retry story.

Record the exact set of matched fault signatures, the number of attempts, and the backoff formula.

## Permanent-failure classification

Signals:

- `IPermanentFailureClassifier` implementations.
- Handlers that throw a specific exception type to short-circuit retry (e.g. `InvalidDataException`, `ValidationException`).

Permanent failures bypass retry and route to the Resolver as `Failed`. Document the classifier rules in TDD §4.4.

## Missing-reference policy

Per handler:

- `throw new InvalidDataException(...)` / `throw new InvalidOperationException(...)` after a failed lookup → **Throw** stance (§4.4). The message goes terminal and surfaces in NimBus Resolver / WebApp.
- `logger.LogWarning(...); return;` after a failed lookup → **Silent-skip** stance. The handler completes successfully and the message is resolved.
- A lookup that returns `null` followed by a caller null-check → follow to caller to pick stance.

Both stances are valid; do not try to normalise them unless the user asks.

## Echo-loop prevention

Signals:

- `IsUnchanged(existing, updated)` (or similarly-named) comparison method on handlers — suppresses no-op writes.
- A correlation-flag field like `Origin`, `IntegrationSource`, `SourceSystem` on the event class, written on inbound writes and checked on outbound publishes.

The CrmErpDemo `CustomerCreated` event uses an `Origin` enum (`Erp` / `Crm`) for exactly this purpose:

```csharp
[SessionKey(nameof(AccountId))]
public class CustomerCreated : Event
{
    public CustomerOrigin Origin { get; set; }
    // ...
}
```

Document both signals if present; they typically work together.

## Session ordering

Signals:

- `[SessionKey(nameof(...))]` attribute on the event class — per-entity in-order delivery key.
- `override GetSessionId()` method on the event class — alternative to the attribute.

If neither is present, NimBus assigns a unique SessionId per message (no ordering guarantee). Record per event in `events.md` §2 and in TDD §1.5 (matrix column "Ordering key").

## Azure resources (IaC and Aspire)

Search paths, in order:

1. `samples/**/*.AppHost/Program.cs` (and any project named `*AppHost`) — Aspire orchestration.
2. `infra/`
3. `bicep/`
4. `terraform/`
5. `azure.yaml` / `azd` template
6. `deploy/`

From declarations, extract:

- Container Apps and Functions apps (count matters — one TDD may have multiple deployable units, e.g. `Crm.Adapter` worker + `Crm.Api` web).
- Azure Service Bus namespace + topics / subscriptions (NimBus uses topics named after endpoint classes).
- Cosmos DB account + per-endpoint containers (per ADR-008, NimBus uses one container per endpoint for the Message Store / Resolver).
- Key Vault, Managed Identity, Storage, SQL Server (for outbox).
- Role assignments — these populate the security table §2.5.
- Environment parameter patterns (`{env}`, `@env()`, `var.env`).

Aspire-specific:

- `builder.AddAzureServiceBus(...)` → Service Bus reference.
- `builder.AddAzureCosmosDB(...)` → Cosmos reference.
- `builder.AddProject<Projects.Crm_Adapter>("crm-adapter")` → deployable unit.
- `.WithReference(serviceBus)` → role binding.

## Authentication

Per client:

- `DefaultAzureCredential` / `ManagedIdentityCredential` → Managed Identity. Note the role.
- `builder.AddAzureServiceBusClient("servicebus")` (Aspire client integration) → Managed Identity by default in deployed environments.
- OAuth2 client credentials: look for `tokenEndpoint`, `clientId`, `clientSecret` secrets in Key Vault references.
- Basic auth: look for `BasicAuthenticationHandler`, `Authorization: Basic` headers — flag as a finding if the target is not internal-only.
- Shared-secret webhooks: look for signature validation (`X-Signature`, `HMAC`, `plugin-shared-secret`).

Fill TDD §2.5 from findings; if any auth is Basic or shared-secret over HTTPS only, flag in §8.

## Configuration

Signals:

- `appsettings.json` / `appsettings.{env}.json` — log levels, timeouts, retry parameters.
- `local.settings.json` (Functions) — local-only env vars; document but do not include secret values.
- `Environment.GetEnvironmentVariable(...)` calls — enumerate them.
- `IConfiguration.GetSection(...)` calls — enumerate bound POCOs.
- `builder.Configuration.GetConnectionString(...)` — connection-string keys (Aspire injects these via `WithReference`).
- Service-discovery keys like `services:{name}:https:0` — populated by Aspire at runtime.
- Key Vault references as `@Microsoft.KeyVault(SecretUri=...)` in Container App env vars.

Populate TDD §2.6.

## Tests

Signals:

- `tests/` folder (NimBus convention) or alongside the adapter project.
- `*.UnitTests.csproj` / `*.IntegrationTests.csproj` / `*.EndToEnd.Tests.csproj`.
- Frameworks (any of):
  - MSTest (`[TestClass]`, `[TestMethod]`) — NimBus's own test convention.
  - xUnit (`[Fact]`, `[Theory]`).
  - NUnit (`[Test]`).
- Fakes: manual spies, `NSubstitute`, `Moq`, or NimBus's in-memory transport (`NimBus.Testing`).

Populate TDD §10 (test basis) with levels present, frameworks used, and test-project paths. If `NimBus.Testing` is used, note it — it is the canonical way to run handler integration tests without a real Service Bus.

## Review findings (optional)

If a file like `{Adapter}-review.md`, `code-review.md`, or `review-findings.md` exists alongside the TDD:

- Parse its prioritised defect list.
- Copy entries verbatim into TDD §8 with links back to the review.

Do not restate the review's full reasoning — §8 is an index.

## C4 diagram rendering rules

Use Mermaid `graph TB` with subgraphs and explicit `classDef` styles — not the native `C4Context` / `C4Container` syntax. The reason is render quality: Mermaid's C4 blocks lose edge-label contrast on dark backgrounds (GitHub, ADO Wiki, VS Code preview) and the theme block is honoured inconsistently across renderers. The `graph` approach preserves C4 semantics as long as every node carries a `[Person]` / `[Software system]` / `[Container]` annotation in its label.

Canonical style blocks to include at the bottom of every diagram:

```
classDef person    fill:#08427b,stroke:#0b3d91,color:#ffffff
classDef system    fill:#1168bd,stroke:#0b3d91,color:#ffffff
classDef container fill:#1168bd,stroke:#0b3d91,color:#ffffff
classDef store     fill:#1168bd,stroke:#0b3d91,color:#ffffff
classDef external  fill:#8a8a8a,stroke:#545454,color:#ffffff
classDef boundary  fill:none,stroke:#1168bd,stroke-dasharray:4 4,color:#1168bd
```

Colours follow the C4 convention (dark blue for actors, mid-blue for in-scope systems/containers, grey for external). White text reads on both light and dark backgrounds because the fills are saturated. Do not change these values without updating every diagram.

**Level 1 (System context, §2.1):**

- One node per primary actor (HR user, sales rep, integration scheduler) — use rounded-pill syntax `actor([...])` and the `person` class.
- One in-scope system node for the adapter, styled with the `system` class.
- One external-system node for every source system, target system, and NimBus itself — use the `external` class. NimBus appears as one external system from the adapter's point of view.
- Arrows labelled with the interaction verb ("sends data to", "publishes events to", "pulls data from").

**Level 2 (Container, §2.2):**

- `subgraph` wraps the adapter and uses the `boundary` class.
- One rectangle node per deployable unit — Container Apps, Functions apps, Web APIs — styled with `container`.
- Cylinder syntax `store[(...)]` for Azure Storage / SQL (outbox) / Cosmos / Key Vault, styled with `store`.
- External systems remain `external`. NimBus appears as `[Software system]<br/>Service Bus topic` annotated externally.
- Arrows labelled with the interaction verb *and* the protocol on a second line (`<br/>`), e.g. `publishes events<br/>Service Bus topic`.

Always include `[Person]` / `[Software system]` / `[Container: .NET Container App]` / `[Container: Azure Functions]` / `[Container: Azure Storage]` / `[Container: SQL Server]` as the second line of every node label. That is what keeps the diagram readable as C4.

Stop at Level 2 for TDDs. Level 3 (component) is usually overkill unless the adapter contains a non-trivial internal pipeline.

## Sequence diagram rendering rules

- Use `sequenceDiagram` with `autonumber`.
- One participant per external system touched + one for the adapter itself + one for NimBus (Service Bus topic) when publishes / subscribes are part of the flow.
- Represent branches with `alt` / `else` / `end`; represent background work with `par` / `and`.
- Cap each diagram at ~15 messages; if it grows beyond that, split by scenario (e.g. "create" vs "update" vs "delete").
- Reference the source code location in the diagram caption: *"Source: `HandlerFile.cs:42-120`"*.
