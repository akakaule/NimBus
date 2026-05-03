# NimBus - Claude Code Instructions

## Project Overview

NimBus is an Azure-native event-driven integration platform built on Azure Service Bus and Cosmos DB. Key differentiators: session-based ordered processing, centralized Resolver with full audit trail, management WebApp with resubmit/skip, and declarative topology provisioning.

## Build & Run

```bash
# Build entire solution
dotnet build src/NimBus.sln

# Run all tests
dotnet test src/NimBus.sln

# Build WebApp client (requires Node.js 22)
cd src/NimBus.WebApp/ClientApp && npm install && npm run build

# Run Aspire AppHost (local development)
dotnet run --project src/NimBus.AppHost
```

- Solution file: `src/NimBus.sln`
- Target framework: .NET 10 (`net10.0`)
- CI runs on: `dotnet restore` -> `dotnet build` -> `dotnet test` (see `.github/workflows/dotnet.yml`)

## Project Structure

```
src/
  NimBus.Abstractions/     # Core interfaces (IMessage, ISender, IEventHandler)
  NimBus.Core/             # Endpoint management, retry, outbox, pipeline, events
  NimBus.ServiceBus/       # Azure Service Bus transport, provisioning, health checks
  NimBus.SDK/              # Publisher/subscriber DI registration, hosted services
  NimBus.Resolver/         # Message outcome tracking and state management
  NimBus.MessageStore.Abstractions/ # Provider-neutral storage contracts (IMessageTrackingStore, ISubscriptionStore, IEndpointMetadataStore, IMetricsStore)
  NimBus.MessageStore.CosmosDb/     # Cosmos DB storage provider
  NimBus.MessageStore.SqlServer/    # SQL Server storage provider (DbUp-managed schema)
  NimBus.Manager/          # Management client abstractions
  NimBus.Management.ServiceBus/  # Service Bus management operations
  NimBus.Outbox.SqlServer/ # Transactional outbox (SQL Server)
  NimBus.Testing/          # In-memory transport + storage conformance suite
  NimBus.Extensions.Notifications/  # Failure/dead-letter notifications
  NimBus.CommandLine/      # `nb` CLI tool (Spectre.Console)
  NimBus.WebApp/           # ASP.NET Core management UI
    ClientApp/             # React + Vite + TypeScript + Tailwind frontend
  NimBus.AppHost/          # .NET Aspire orchestration
  NimBus.ServiceDefaults/  # Aspire service defaults
  NimBus/                  # Platform config and built-in endpoint definitions

tests/
  NimBus.Core.Tests/
  NimBus.ServiceBus.Tests/
  NimBus.CommandLine.Tests/
  NimBus.Resolver.Tests/
  NimBus.EndToEnd.Tests/   # 14+ integration tests
  NimBus.MessageStore.InMemory.Tests/   # In-memory store conformance run
  NimBus.MessageStore.CosmosDb.Tests/   # Cosmos provider conformance run
  NimBus.MessageStore.SqlServer.Tests/  # SQL provider conformance run (CI service container)

samples/
  NimBus.Aspire/           # Aspire Pub/Sub sample (Publisher, Subscriber, Provisioner, ResolverWorker)

docs/
  architecture.md          # System architecture
  roadmap.md               # Feature roadmap with phases
  backlog.md               # Actionable work items by priority
  adr/                     # 8 Architecture Decision Records
  sdk-api-reference.md     # SDK API guide
  getting-started.md       # Quick start
  pipeline-middleware.md   # Middleware pattern documentation
  message-flows.md         # 12 message flow diagrams
  error-handling.md        # Adapter error-handling reference (exception types, retry vs DLQ)
  deferred-messages.md     # Deferred message processing guide
  cli.md                   # CLI command reference
  extensions.md            # Extension framework
  azure-functions-hosting.md
```

## Code Conventions

- **C# version**: Latest (file-scoped namespaces, nullable reference types, implicit usings)
- **Namespaces**: `NimBus[.Project][.Subfolder]` (e.g., `NimBus.Core.Messages`, `NimBus.ServiceBus.HealthChecks`)
- **Test framework**: MSTest (`[TestClass]`, `[TestMethod]`)
- **Test files**: Use `#pragma warning disable CA1707, CA2007` at the top
- **Serialization**: Newtonsoft.Json (`JsonConvert`)
- **Logging**: `Microsoft.Extensions.Logging` (no custom logging abstraction)
- **DI**: Standard `IServiceCollection` extension methods (`AddNimBusPublisher`, `AddNimBusSubscriber`)
- **Public API**: XML doc comments on public types and members
- **Obsolete code**: Mark with `[Obsolete]` and implement as backward-compatible bridge; don't delete

## Analyzers & Quality

Global analyzers applied to all projects (via `Directory.Packages.props`):
- AsyncFixer, Meziantou.Analyzer, SecurityCodeScan, SonarAnalyzer, StyleCop

Build settings:
- `EnforceCodeStyleInBuild: true`
- `AnalysisLevel: latest-recommended`
- Release builds: `TreatWarningsAsErrors: true` (with nullable warning allowlist)
- Some projects (`NimBus.Core`, `NimBus.ServiceBus`) have relaxed analyzer settings for legacy compatibility

## Architecture Patterns

- **Decorator pattern**: `OutboxSender` decorates `ISender` to intercept sends transparently
- **Pipeline/middleware**: `IMessagePipelineBehavior` with delegate chain (built-in: Logging, Metrics, Validation)
- **Abstractions**: Storage and transport behind interfaces (`IOutbox`, `ISender`, `IMessageContext`)
- **DI extension methods**: Each feature area registers via `services.AddNimBus*()` methods
- **Hosted services**: Background work via `BackgroundService` (e.g., `OutboxDispatcherHostedService`)

## WebApp Frontend

- **Stack**: React 18 + TypeScript + Vite + Tailwind CSS
- **State**: React Router v6, TanStack Table, SignalR for real-time
- **Scripts**: `npm run dev` (dev server), `npm run build` (production), `npm test` (vitest)
- **Path**: `src/NimBus.WebApp/ClientApp/`

## Key Design Decisions

Refer to `docs/adr/` for rationale on:
- ADR-001: Session-based ordering
- ADR-002: Centralized Resolver
- ADR-003: Separated deferred processor
- ADR-004: Pipeline behavior pattern
- ADR-005: Transactional outbox (SQL Server)
- ADR-006: Standard logging (Microsoft.Extensions.Logging)
- ADR-007: Code-first catalog export
- ADR-008: Per-endpoint Cosmos containers

## What NOT to Do

From the roadmap's explicit guidance:
- Don't abstract transports prematurely -- Azure Service Bus is NimBus's strength
- Don't chase NServiceBus feature parity -- focus on Resolver, WebApp, sessions
- Don't rewrite the WebApp -- enhance incrementally
- Don't build event sourcing without a concrete use case
