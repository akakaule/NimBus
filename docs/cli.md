# NimBus CLI (`nb`)

Command-line tool for provisioning Azure infrastructure, managing Service Bus topology, deploying applications, and performing operational tasks on the NimBus platform.

## Installation

The CLI is distributed as a .NET tool (package `Akaule.NimBus.CommandLine` — the bare
`NimBus.*` prefix is reserved on nuget.org, assemblies and namespaces stay `NimBus.*`):

```bash
dotnet tool install --global Akaule.NimBus.CommandLine
```

Or run it npx-style without installing, via `dnx` (ships with the .NET 10 SDK). The `--`
separator is required so tool options aren't picked up by `dnx` itself:

```bash
dnx Akaule.NimBus.CommandLine -- <command> [options]
```

Or run directly from source:

```bash
dotnet run --project src/NimBus.CommandLine -- <command>
```

### One-command cloud install

From a repository clone (the CLI needs the bicep templates and app sources), a single
command provisions the infrastructure, applies the Service Bus topology, and deploys
the resolver + management WebApp:

```bash
git clone https://github.com/akakaule/NimBus && cd NimBus
dnx Akaule.NimBus.CommandLine -- setup --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

Prerequisites: .NET 10 SDK (provides `dnx`), Node.js 22 (the WebApp SPA builds during
`dotnet publish`), and Azure CLI ≥ 2.60.0 logged in via `az login` (≥ 2.70 recommended).
2.60.0 is the Microsoft-documented minimum for Flex Consumption: older versions push to
the legacy Kudu zipdeploy endpoint and fail with a misleading SSL/proxy error, so `nb`
refuses to deploy to a Flex Consumption plan with an older az.

## Global Options

| Option | Description |
|---|---|
| `-sbc`, `--sb-connection-string` | Azure Service Bus connection string, or a fully qualified namespace for Entra ID auth (overrides `AzureServiceBus_ConnectionString` env var) |
| `-dbc`, `--db-connection-string` | Cosmos DB connection string, or an account endpoint URI for Entra ID auth (overrides `CosmosDb_ConnectionString` env var) |
| `-h`, `--help` | Show help for any command |

Connection strings can be set via environment variables instead of passing them on every call:

```bash
export AzureServiceBus_ConnectionString="Endpoint=sb://..."
export CosmosDb_ConnectionString="AccountEndpoint=https://..."
```

### Entra ID / managed identity

Instead of a connection string, pass a fully qualified Service Bus namespace or a Cosmos DB
account endpoint. The CLI then authenticates with `DefaultAzureCredential` (`az login`,
managed identity, environment credentials, etc.) — the same heuristic the WebApp and
Resolver use, so no keys need to be distributed:

```bash
export AzureServiceBus_ConnectionString="mybus.servicebus.windows.net"
export CosmosDb_ConnectionString="https://myaccount.documents.azure.com/"
```

The signed-in identity needs `Azure Service Bus Data Owner` on the namespace and a Cosmos
DB data-plane role (e.g. `Cosmos DB Built-in Data Contributor`) on the account.

---

## Commands

### `nb infra apply`

Deploy Azure infrastructure using bicep templates.

```bash
nb infra apply --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

| Option | Required | Description |
|---|---|---|
| `--solution-id` | Yes | Solution identifier used in resource names |
| `--environment` | Yes | Environment name (dev, staging, prod) |
| `--resource-group` | Yes | Azure resource group name |
| `--repo-root` | No | Repository root (auto-detected) |
| `--location` | No | Azure region override |
| `--webapp-version` | No | Version string for web app settings |
| `--storage-provider` | No | Storage backend: `cosmos` (default) or `sqlserver` |
| `--sql-mode` | No | When `--storage-provider sqlserver`: `provision` (default, creates a new Azure SQL server + DB) or `external` (use an existing SQL Server) |
| `--sql-connection-string` | Conditional | Required when `--sql-mode external` |
| `--sql-admin-login` | Conditional | Required when `--sql-mode provision` |
| `--sql-admin-password` | Conditional | Required when `--sql-mode provision` |
| `--sql-server-name` | No | Override the SQL server name (default: `sql-{solution-id}-{environment}`). Useful when the default DNS name is held in Azure's global namespace from a recent delete (24–72h cooldown). |
| `--resolver-plan` | No | Resolver Function App hosting plan: `FlexConsumption` (default for new deployments; FC1, scale-to-zero Linux) or `ElasticPremium` (EP1 Windows). Existing deployments keep their current plan type unless this flag is passed. |
| `--management-plan-sku` | No | SKU for the management App Service Plan hosting the WebApp. Default for new deployments: `B1` for `dev`/`development`, `S1` otherwise. Existing deployments keep their current SKU unless this flag is passed. |

Deploys core infrastructure (Service Bus, App Insights, and either Cosmos DB or Azure SQL depending on `--storage-provider`) and the web app infrastructure via bicep. The provisioned SQL path uses AAD managed-identity auth (`Authentication=Active Directory Default`); the external path uses the supplied connection string verbatim. Automatically creates an Application Insights API key and resolves required resource endpoints/namespace settings.

**Existing-resource location pinning.** Before deploying, the CLI lists the resources already in the target resource group and pins each known NimBus resource (Service Bus, App Insights, Cosmos, SQL Server, function storage, app service plans, function app, web app) to its current location. This avoids the `InvalidResourceLocation` error Azure raises when a same-named resource already exists in another region. Net-new resources still use `--location` (or `westeurope` if unset). To actually move a resource between regions, delete it first.

**Existing-plan pinning.** The same applies to hosting plans: an existing core App Service Plan pins the resolver plan type (Azure cannot convert between Elastic Premium and Flex Consumption in place), and an existing management plan pins its SKU so re-runs never silently rescale it. Explicit `--resolver-plan` / `--management-plan-sku` flags win; a `--resolver-plan` that conflicts with the existing plan type fails with guidance (delete both the resolver Function App and the core plan first).

---

### `nb topology export`

Export the platform configuration to JSON.

```bash
nb topology export -o platform-config.json
```

| Option | Required | Description |
|---|---|---|
| `-o`, `--output` | No | Output file path (default: `platform-config.json`) |

Outputs a JSON file with all endpoints, event types, and Service Bus identifiers for use by deployment scripts.

---

### `nb topology apply`

Provision the Service Bus topology for all endpoints.

```bash
nb topology apply --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

| Option | Required | Description |
|---|---|---|
| `--solution-id` | Yes | Solution identifier |
| `--environment` | Yes | Environment name |
| `--resource-group` | Yes | Resource group with the Service Bus namespace |

Creates topics, subscriptions, and routing rules for each endpoint. Idempotent — only recreates entities if configuration has changed. Creates:
- Main subscription (session-enabled) per endpoint
- Resolver subscription (forwarding)
- Continuation and Retry subscriptions (forwarding back to self)
- Deferred subscription (session-enabled)
- DeferredProcessor subscription (sessions=OFF)
- Event-type forwarding subscriptions for cross-endpoint routing

---

### `nb deploy apps`

Build and deploy the resolver and web app to Azure.

```bash
nb deploy apps --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

| Option | Required | Description |
|---|---|---|
| `--solution-id` | Yes | Solution identifier |
| `--environment` | Yes | Environment name |
| `--resource-group` | Yes | Resource group with target apps |
| `--repo-root` | No | Repository root (auto-detected) |
| `--configuration` | No | Build configuration (default: `Release`) |

Publishes the resolver (Azure Function) and web app, packages as ZIP, and deploys via Azure CLI. On a Flex Consumption resolver the zip is deployed directly (the app must stay running — the Azure CLI verifies host health after publishing); on Elastic Premium the app is stopped for the deployment and restarted afterwards.

---

### `nb setup`

Run infrastructure, topology, and app deployment in sequence.

```bash
nb setup --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

Combines `infra apply` → `topology apply` → `deploy apps` in a single command. Accepts all options from the individual commands, including `--storage-provider`, `--sql-mode`, `--sql-connection-string`, `--sql-admin-login`, `--sql-admin-password`, `--sql-server-name`, `--resolver-plan`, and `--management-plan-sku`.

---

### `nb endpoint session delete`

Delete all messages on a Service Bus session and clear its state.

```bash
nb endpoint session delete <endpoint-name> <session-id> -sbc "..."
```

| Argument | Required | Description |
|---|---|---|
| `endpoint-name` | Yes | Name of the endpoint |
| `session` | Yes | Session ID to delete |

Accepts the session, removes all active and deferred messages from Service Bus, removes corresponding events from Cosmos DB, and clears the session state.

---

### `nb endpoint topics removeDeprecated`

Remove deprecated subscriptions and rules from a Service Bus topic.

```bash
nb endpoint topics removeDeprecated <endpoint-name> -sbc "..."
```

Compares the expected topology (from `PlatformConfiguration`) against the actual Service Bus state. Displays a visual tree with deprecated items highlighted in red, prompts for confirmation, then deletes them with progress tracking.

---

### `nb endpoint purge`

Purge messages from a Service Bus subscription by state and/or enqueued time.

```bash
nb endpoint purge <endpoint-name> --state Active,Deferred --before 2026-03-01T00:00:00 -sbc "..."
```

| Option | Required | Description |
|---|---|---|
| `--subscription` | No | Subscription name (defaults to endpoint name) |
| `--state` | No | Comma-separated states: `Active`, `Deferred` (default: all) |
| `--before` | No | Only purge messages enqueued before this UTC datetime |

Scans all messages, filters by state and time, organizes by session, prompts for confirmation, then completes matching messages.

---

### `nb container event delete`

Delete a specific event from Cosmos DB.

```bash
nb container event delete <endpoint-name> <event-id> -dbc "..."
```

---

### `nb container message delete`

Delete messages from the messages container filtered by the "To" field.

```bash
nb container message delete <to-field-value> -dbc "..."
```

Example: `nb container message delete BillingEndpoint` deletes all messages addressed to BillingEndpoint.

---

### `nb container delete`

Delete events from Cosmos DB by resolution status.

```bash
nb container delete <endpoint-name> -s failed,deadlettered -dbc "..."
```

| Option | Required | Description |
|---|---|---|
| `-s`, `--status` | No | Comma-separated statuses (default: `DeadLettered`) |

Valid statuses: `Pending`, `Deferred`, `Failed`, `DeadLettered`, `Unsupported`, `Completed`, `Skipped`

---

### `nb container resubmit`

Resubmit failed messages older than 10 minutes via Service Bus.

```bash
nb container resubmit <endpoint-name> -sbc "..." -dbc "..."
```

Finds failed events in Cosmos DB, updates their status, and sends `ResubmissionRequest` messages to the Manager topic for re-processing.

---

### `nb container copy`

Copy endpoint data (events + messages) from one Cosmos DB to another.

```bash
nb container copy <endpoint-name> -dbc "source-conn-string" --target-dbc "target-conn-string"
```

| Option | Required | Description |
|---|---|---|
| `--target-dbc` | Yes | Target Cosmos DB connection string |
| `--from` | No | Only copy events from this UTC datetime |
| `--to` | No | Only copy events up to this UTC datetime |
| `-s`, `--status` | No | Comma-separated statuses to copy (default: all) |
| `-b`, `--batch-size` | No | Documents per batch (default: all) |

Creates target containers if they don't exist. Removes TTL from copied documents to prevent premature expiration. Only copies messages for events that were copied.

---

### `nb container skip`

Mark events as Skipped in Cosmos DB.

```bash
nb container skip <endpoint-name> -s failed,deadlettered --before 2026-03-01T00:00:00 -dbc "..."
```

| Option | Required | Description |
|---|---|---|
| `-s`, `--status` | Yes | Source statuses to skip (e.g., `failed,deadlettered`) |
| `--before` | No | Only skip events last updated before this UTC datetime |

Cannot skip events that are already `Completed` or `Skipped`.

---

### `nb catalog export`

Export platform topology to EventCatalog-compatible markdown structure.

```bash
nb catalog export -o ./eventcatalog
```

| Option | Required | Description |
|---|---|---|
| `-o`, `--output` | No | Output directory (default: `./eventcatalog`) |

Generates markdown files for domains, services, events, and channels from `PlatformConfiguration`. Output can be consumed directly by [EventCatalog](https://www.eventcatalog.dev/) for interactive architecture visualization.

Generated structure:
```
eventcatalog/
├── domains/{SystemId}/index.mdx
├── services/{EndpointId}/index.mdx
├── events/{EventTypeId}/index.mdx
└── channels/{EndpointId}.events/index.mdx
```

---

### `nb catalog asyncapi`

Export platform topology as an AsyncAPI 3.0 specification.

```bash
nb catalog asyncapi -o ./asyncapi.yaml
nb catalog asyncapi --format json -o ./asyncapi.json
```

| Option | Required | Description |
|---|---|---|
| `-o`, `--output` | No | Output file (default: `./asyncapi.yaml`, or `./asyncapi.json` for `--format json`) |
| `-f`, `--format` | No | `yaml` (default) or `json`. When omitted, an `.json` output path is auto-detected as JSON. |

Generates an AsyncAPI 3.0 specification with:
- **Servers** — Azure Service Bus namespace (AMQP 1.0), with an `x-nimbus-topology` extension describing the topic-per-endpoint pattern, SQL-rule routing, and auto-forwarding.
- **Channels** — one per endpoint **topic** (both producers and consumers, since a consumer's own topic carries the auto-forwarded copy), with `x-servicebus` topic bindings.
- **Operations** — a `send` per producer and a `receive` per consumer. Each `receive` carries an `x-servicebus-delivery` extension documenting the **physical delivery path**: the consumer's own session subscription (`user.To = '<endpoint>'`) plus the forward subscription(s) on each producer topic (filter `user.EventTypeId = 'X' AND user.From IS NULL`, the rewrite action, and `forwardTo`).
- **Messages** — event types with a shared `NimBusMessageHeaders` header schema (the `user.*` application properties), `x-servicebus` message settings (session requirement, dead-letter, `MessageId`/`CorrelationId` conventions), an example payload, and `[Description]`/`[AsyncApiMessage]` enrichment.
- **Schemas** — JSON Schema from C# types (formats, required from `[Required]`/non-nullable, `[Range]`, enums, collections, and nested objects). Dynamically-typed events (spec 022 `DynamicForward`) appear as messages flagged `x-nimbus-dynamic`.

> **Mapping note.** Because there is no official AsyncAPI Service Bus binding, the document keeps portable **logical** channels/operations and carries Service Bus specifics via `x-servicebus*` / `x-nimbus*` specification extensions. See [`docs/asyncapi-mapping.md`](asyncapi-mapping.md) for the full NimBus → AsyncAPI concept mapping.

The spec can be used with:
- [EventCatalog AsyncAPI plugin](https://www.eventcatalog.dev/integrations/asyncapi) for architecture visualization
- [AsyncAPI HTML template](https://github.com/asyncapi/html-template) for documentation
- Schema validation and contract testing tools

> **Note.** `nb catalog asyncapi` is a backward-compatible alias for `nb asyncapi export` (below) and produces identical output from the same code path.

---

### `nb asyncapi`

Generate, validate, and diff AsyncAPI 3.0 documents for CI/CD governance. Three subcommands:

#### `nb asyncapi export`

Export the platform topology as an AsyncAPI 3.0 specification — identical generation to `nb catalog asyncapi`.

```bash
nb asyncapi export -o ./asyncapi.yaml
nb asyncapi export --format json -o ./asyncapi.json
# include fluent Publish<T>(o => o.AsyncApi…) enrichment recorded by a host assembly:
nb asyncapi export --assembly ./bin/MyApp.dll -o ./asyncapi.yaml
```

| Option | Required | Description |
|---|---|---|
| `-o`, `--output` | No | Output file (default: `./asyncapi.yaml`, or `./asyncapi.json` for `--format json`) |
| `-f`, `--format` | No | `yaml` (default) or `json`. When omitted, an `.json` output path is auto-detected as JSON. |
| `-a`, `--assembly` | No | Path to a host assembly exposing an `IAsyncApiDocumentProvider` or `IAsyncApiDocumentProviderFactory`. Use to include **fluent** `Publish<T>(o => o.AsyncApi…)` enrichment, which lives in the host's DI container and cannot be observed from the static built-in platform. When omitted, the built-in `PlatformConfiguration` is exported (attribute enrichment only). |
| `-p`, `--provider` | No | Provider/factory type name (full or simple) to select when `--assembly` exposes more than one candidate. |

Because `AddNimBusAsyncApiDocument(platform, (p, f, r) => AsyncApiExporter.Serialize(p, f, r))` registers a **private, DI-backed** `IAsyncApiDocumentProvider` (it has constructor dependencies the standalone CLI cannot instantiate), a host bridges to it by exposing a public, parameterless `IAsyncApiDocumentProviderFactory` whose `Create()` builds its container and resolves the provider:

```csharp
public sealed class MyAsyncApiFactory : IAsyncApiDocumentProviderFactory
{
    public IAsyncApiDocumentProvider Create()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(connectionString));
        services.AddNimBusPublisher("Orders", b => b.Publish<OrderPlaced>(o => o.AsyncApi.Owner = "orders-team"));
        services.AddNimBusAsyncApiDocument(platform, (p, f, r) => AsyncApiExporter.Serialize(p, f, r));
        return services.BuildServiceProvider().GetRequiredService<IAsyncApiDocumentProvider>();
    }
}
```

The CLI loads the assembly (`Assembly.LoadFrom`), instantiates the factory (or a directly-exposed public parameterless `IAsyncApiDocumentProvider`), and writes `GetDocument(format)` — so fluent enrichment surfaces from the CLI export. Export exits non-zero with a message when the assembly, factory, or provider cannot be loaded.

#### `nb asyncapi validate <file>`

Structurally validate a generated or hand-authored AsyncAPI 3.0 document. Checks `asyncapi: 3.0.0`; the presence of `info`, `channels`, `operations`, `components`; and that every `$ref` resolves to the **correct** section for its context (operation → channel; operation message → channel-scoped message → component message, or directly to component message; channel message → component message; message `payload`/`headers` → a component **schema**). A payload `$ref` that points at a non-schema node is rejected.

```bash
nb asyncapi validate ./asyncapi.yaml
```

**Exit codes** (for CI gating): `0` valid, non-zero when invalid (errors are printed, one per line).

#### `nb asyncapi diff <old-file> <new-file>`

Compare two AsyncAPI documents, classify added / removed / changed channels, operations, messages, and schemas (including schema properties), and flag breaking changes.

```bash
nb asyncapi diff ./asyncapi.previous.yaml ./asyncapi.yaml
```

Treated as **breaking**: a removed channel / operation / message / schema; a removed property or a property that becomes newly required; a property **effective-shape** change (`type`/`format`/`$ref`/array `items`); a removed enum value (including on an array `items` schema); a **tightened** validation bound (a `[Range]`-derived `minimum` raised or `maximum` lowered, or one newly added); a message-association removed from an operation; a same-key channel message whose `$ref` is **retargeted** to a different component message; an operation `action` flip or `channel` retarget; a `payload`/`headers` `$ref`, `contentType`, or session-semantics (`x-servicebus.requiresSession`/`sessionKeyProperty`) change. Additive/informational changes are **non-breaking** and still reported: new channels/operations/messages/schemas/properties, added enum values, a **relaxed or removed** validation bound (`minimum` lowered / `maximum` raised), and metadata changes at both the property level (`description`) and the schema root (`title` / `description` / `deprecated`) — so a metadata-only schema delta is still reported, never swallowed as "No differences".

**Exit codes** (for build gating): `0` when the only differences are non-breaking, non-zero when a breaking change is detected.

---

## Examples

### Full environment setup

```bash
# Default — Cosmos DB backend
nb setup --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

### Deploy with SQL Server as the storage provider

Provision a fresh Azure SQL server + database (managed-identity auth from the WebApp / Resolver):

```bash
nb setup `
  --solution-id nimbus --environment dev --resource-group rg-nimbus-dev `
  --storage-provider sqlserver `
  --sql-mode provision `
  --sql-admin-login nimbusadmin `
  --sql-admin-password '<strong-password>'
```

Reuse an existing SQL Server:

```bash
nb setup `
  --solution-id nimbus --environment dev --resource-group rg-nimbus-dev `
  --storage-provider sqlserver `
  --sql-mode external `
  --sql-connection-string 'Server=tcp:my-existing.database.windows.net,1433;Initial Catalog=MessageDatabase;Authentication=Active Directory Default;Encrypt=true;'
```

The same flags work on `nb infra apply` if you prefer running infrastructure, topology, and app deployment as separate steps.

### Operational maintenance

```bash
# Purge old failed messages
nb container delete my-endpoint -s failed,deadlettered -dbc "..."

# Skip stuck deferred messages older than a week
nb container skip my-endpoint -s deferred --before 2026-03-20T00:00:00 -dbc "..."

# Clean up a blocked session
nb endpoint session delete my-endpoint session-123 -sbc "..." -dbc "..."

# Remove stale Service Bus subscriptions
nb endpoint topics removeDeprecated my-endpoint -sbc "..."
```

### Generate architecture documentation

```bash
# EventCatalog markdown
nb catalog export -o ./docs/eventcatalog

# AsyncAPI specification
nb catalog asyncapi -o ./docs/asyncapi.yaml
```

## Key Source Files

| File | Purpose |
|---|---|
| `src/NimBus.CommandLine/Program.cs` | Command definitions and CLI entry point |
| `src/NimBus.CommandLine/Endpoint.cs` | Session delete, topic cleanup, subscription purge |
| `src/NimBus.CommandLine/Container.cs` | Cosmos DB operations (delete, resubmit, copy, skip) |
| `src/NimBus.CommandLine/CommandRunner.cs` | Connection string handling and client factory |
| `src/NimBus.CommandLine/EventCatalogExporter.cs` | EventCatalog markdown generation |
| `src/NimBus.CommandLine/AsyncApiExporter.cs` | AsyncAPI 3.0 YAML generation |
| `src/NimBus.CommandLine/ServiceBusTopologyProvisioner.cs` | Service Bus topology provisioning |
| `src/NimBus.CommandLine/InfrastructureDeployer.cs` | Azure infrastructure deployment |
| `src/NimBus.CommandLine/AppDeploymentService.cs` | App build and deployment |
| `src/NimBus.CommandLine/ColoredHelpTextGenerator.cs` | Colored help output |
