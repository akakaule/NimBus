# NimBus CLI (`nb`)

Command-line tool for provisioning Azure infrastructure, managing Service Bus topology, deploying applications, and performing operational tasks on the NimBus platform.

## Installation

The CLI is distributed as a .NET tool:

```bash
dotnet tool install --global NimBus.CommandLine
```

Or run directly from source:

```bash
dotnet run --project src/NimBus.CommandLine -- <command>
```

## Global Options

| Option | Description |
|---|---|
| `-sbc`, `--sb-connection-string` | Azure Service Bus connection string (overrides `AzureServiceBus_ConnectionString` env var) |
| `-dbc`, `--db-connection-string` | Cosmos DB connection string (overrides `CosmosDb_ConnectionString` env var) |
| `-h`, `--help` | Show help for any command |

Connection strings can be set via environment variables instead of passing them on every call:

```bash
export AzureServiceBus_ConnectionString="Endpoint=sb://..."
export CosmosDb_ConnectionString="AccountEndpoint=https://..."
```

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

Deploys core infrastructure (Service Bus, Cosmos DB, App Insights) and web app infrastructure via bicep. Automatically creates an Application Insights API key and resolves required resource endpoints/namespace settings.

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

Publishes the resolver (Azure Function) and web app, packages as ZIP, and deploys via Azure CLI.

---

### `nb setup`

Run infrastructure, topology, and app deployment in sequence.

```bash
nb setup --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

Combines `infra apply` → `topology apply` → `deploy apps` in a single command. Accepts all options from the individual commands.

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
```

| Option | Required | Description |
|---|---|---|
| `-o`, `--output` | No | Output file (default: `./asyncapi.yaml`) |

Generates an AsyncAPI 3.0 YAML specification with:
- **Servers** — Azure Service Bus namespace (AMQP)
- **Channels** — Topics per producing endpoint
- **Operations** — Send/receive per endpoint with `$ref` links
- **Messages** — Event types with descriptions from `[Description]` attributes
- **Schemas** — JSON Schema from C# types (types, formats, required, ranges, descriptions)

The spec can be used with:
- [EventCatalog AsyncAPI plugin](https://www.eventcatalog.dev/integrations/asyncapi) for architecture visualization
- [AsyncAPI HTML template](https://github.com/asyncapi/html-template) for documentation
- Schema validation and contract testing tools

---

## Examples

### Full environment setup

```bash
nb setup --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

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
