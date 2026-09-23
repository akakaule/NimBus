# NimBus — Agent Instructions

NimBus is an Azure-native event-driven integration platform on Azure Service Bus with
Cosmos DB or SQL Server storage: session-ordered processing, a centralized Resolver with a
full audit trail, a management WebApp (resubmit/skip), and declarative topology provisioning.

## Build & test

```bash
dotnet build src/NimBus.sln                    # .NET 10
dotnet test src/NimBus.sln
dotnet build src/NimBus.sln -c Release         # what CI runs — do this before pushing
cd src/NimBus.WebApp/ClientApp && npm install && npm test && npm run build   # Node.js 22
dotnet run --project src/NimBus.AppHost        # local Aspire stack
```

- Release promotes **compiler (CS) warnings** to errors; analyzer warnings (CA/S/SA) stay
  non-fatal. **CS8767** (nullability mismatch on an interface implementation) fails Release
  while Debug stays green.
- Several src projects opt out of `EnforceCodeStyleInBuild`. Tightening them is a backlog
  item, not something to "fix" in passing.
- The Cosmos DB and SQL Server conformance suites are env-gated: they skip locally and run
  live in CI.

## Where things live

- `src/` — `NimBus.Core` (pipeline, retry, outbox), `NimBus.ServiceBus` (transport,
  provisioning), `NimBus.SDK` (DI registration), `NimBus.Resolver`,
  `NimBus.MessageStore.*` (storage contracts and providers), `NimBus.WebApp` (+ `ClientApp/`:
  React 19, TypeScript, Vite 8, Tailwind, Vitest), `NimBus.CommandLine` (`nb` CLI),
  `NimBus.Testing` (in-memory transport and storage conformance suite), `NimBus.Extensions.*`.
- `tests/` — one `*.Tests` project per library (MSTest). `samples/` — AspirePubSub,
  CloudEventsInterop, CrmErpDemo.
- `docs/` — guides, `docs/adr/` for design rationale, `docs/spec/` for specs and designs,
  `docs/plan/` for implementation plans and plan reviews. Do not create `docs/superpowers/`
  or `docs/specs/`.

## Conventions

- C# latest: file-scoped namespaces, nullable enabled, namespaces `NimBus[.Project][.Folder]`.
- MSTest (`[TestClass]`, `[TestMethod]`); test files start with
  `#pragma warning disable CA1707, CA2007`.
- Newtonsoft.Json for serialization, `Microsoft.Extensions.Logging` for logging (ADR-006),
  features register through `services.AddNimBus*()` extension methods.
- XML doc comments on public types and members.
- Never delete public API outright: mark it `[Obsolete]` with a backward-compatible bridge
  and remove it in the next major (`docs/versioning.md`).
- Conventional Commits (`feat(webapp): …`, `fix(core): …`).

## Gotchas

- The WebApp API is generated from `src/NimBus.WebApp/api-spec.yaml` by NSwag at build time.
  Edit the spec, not `Controllers/ApiContract.g.cs` or `ClientApp/src/api-client/index.ts`.
- A new storage interface member must be implemented in every provider (Cosmos DB, SQL
  Server, in-memory), covered by the conformance suite, and forwarded by
  `InstrumentingMessageTrackingStoreDecorator` (OpenTelemetry). An unforwarded default
  interface method silently bypasses the real store.
- `EventTypeId` is the unqualified class name and is global to the Service Bus namespace:
  two event classes with the same name collide.
- Service Bus topology is created only by `ServiceBusTopologyProvisioner`
  (`nb topology apply`).
- Releases follow `docs/versioning.md#cutting-a-release` exactly, including the
  release-notes pattern.

## What not to do

- Don't abstract transports prematurely; Azure Service Bus is NimBus's strength.
- Don't chase NServiceBus feature parity; focus on the Resolver, WebApp and sessions.
- Don't rewrite the WebApp; enhance it incrementally.
- Don't build event sourcing without a concrete use case.
