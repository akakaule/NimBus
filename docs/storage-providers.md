# Storage Providers

NimBus persists message tracking, audit, resolver state, subscription, endpoint
metadata, and metrics data behind a set of provider-neutral contracts. Today there
are two implementations:

| Provider | Package | When to use |
|---|---|---|
| Cosmos DB | `NimBus.MessageStore.CosmosDb` | Greenfield Azure deployments where Cosmos is approved |
| SQL Server | `NimBus.MessageStore.SqlServer` | Organizations whose approved infrastructure does not include Cosmos DB |

The Cosmos provider used to ship as `NimBus.MessageStore`. Update your
package reference to `NimBus.MessageStore.CosmosDb` and your registration
call to `AddCosmosDbMessageStore(...)`. Type namespaces are unchanged.

Exactly one provider must be registered per running application instance. The
NimBus builder validates this at `Build()` time and fails fast otherwise.

## Cosmos DB

Add the package and register:

```csharp
services.AddNimBus(nimbus =>
{
    nimbus.AddCosmosDbMessageStore();
});
```

Reads connection from configuration in this order:
1. `CosmosAccountEndpoint` — endpoint URL (uses `DefaultAzureCredential` for AAD)
2. Connection string named `cosmos`
3. `CosmosConnection` configuration value

For an explicit `CosmosClient` (tests, advanced scenarios):

```csharp
nimbus.AddCosmosDbMessageStore(myCosmosClient);
```

## SQL Server

Add the package and register:

```csharp
services.AddNimBus(nimbus =>
{
    nimbus.AddSqlServerMessageStore(options =>
    {
        // Optional. Defaults below.
        options.Schema = "nimbus";
        options.ProvisioningMode = SchemaProvisioningMode.AutoApply;
    });
});
```

Reads connection from configuration in this order:
1. `SqlConnection` configuration value
2. Connection string named `sqlserver`
3. `SqlServerConnection` configuration value

The provider registers an `IHostedService` that runs DbUp on startup. The schema
scripts are embedded in the package and applied idempotently to the configured
schema (default `nimbus`).

### Provisioning modes

| Mode | Behavior |
|---|---|
| `AutoApply` (default) | Apply pending DbUp scripts on startup. Best for development and managed environments. |
| `VerifyOnly` | Read the DbUp journal table and fail fast if any embedded script is unapplied. Best for production environments where DDL is performed by the deployment pipeline. |

When using `VerifyOnly`, run DbUp from the deployment pipeline against an
arbitrary connection string before the application starts.

### Schema layout

Single table per concern with `EndpointId` as a discriminator (no per-endpoint
table). Composite indexes target the dominant queries: per-endpoint status
counts, recent-events lists, per-event lookups. See
`src/NimBus.MessageStore.SqlServer/Schema/` for the canonical scripts.

## SQL-only deployment

To deploy NimBus without Cosmos DB at all (no Cosmos resources provisioned, no
Cosmos secrets required, no Cosmos packages referenced):

```bash
nb infra apply \
  --solution-id mybus \
  --environment prod \
  --resource-group rg-mybus-prod \
  --storage-provider sqlserver \
  --sql-mode provision \
  --sql-admin-login dbadmin \
  --sql-admin-password '<strong-password>'
```

Or to use an externally-provisioned SQL Server:

```bash
nb infra apply \
  --solution-id mybus \
  --environment prod \
  --resource-group rg-mybus-prod \
  --storage-provider sqlserver \
  --sql-mode external \
  --sql-connection-string 'Server=tcp:...;Initial Catalog=MessageDatabase;...'
```

The Bicep templates skip Cosmos provisioning entirely when `storageProvider == 'sqlserver'`.

## Local development with Aspire

The AppHost reads `NIMBUS_STORAGE_PROVIDER`:

```bash
# Cosmos (default)
dotnet run --project src/NimBus.AppHost

# SQL Server (requires ConnectionStrings:sqlserver in user-secrets or env)
NIMBUS_STORAGE_PROVIDER=sqlserver dotnet run --project src/NimBus.AppHost
```

## Operator tools that are Cosmos-only in v1

Two operator workflows currently work only with the Cosmos provider:

- **Copy Endpoint Data** (WebApp Advanced Operations + `nb` CLI) — uses Cosmos
  cross-account container copy. SQL deployments should use SQL-native
  backup/restore tools instead. The WebApp surfaces a clear error when the
  active provider does not support it.
- **Storage hook webhook** — Cosmos Change Feed → Event Grid → SignalR. SQL
  deployments receive the same SignalR push events from the Resolver
  write-path (provider-neutral), so realtime UI updates work the same way for
  the operator.

## Adding a new provider

A provider package implements `INimBusMessageStore` (which aggregates the four
storage contracts: `IMessageTrackingStore`, `ISubscriptionStore`,
`IEndpointMetadataStore`, `IMetricsStore`) and registers an
`IStorageProviderRegistration`. Run the shared
`MessageTrackingStoreConformanceTests` (in `NimBus.Testing.Conformance`) against
your implementation.

See `NimBus.MessageStore.SqlServer` for a complete reference implementation.
