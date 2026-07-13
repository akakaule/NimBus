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
export NIMBUS_SQL_ADMIN_PASSWORD='<strong-password>'
nb infra apply \
  --solution-id mybus \
  --environment prod \
  --resource-group rg-mybus-prod \
  --storage-provider sqlserver \
  --sql-mode provision \
  --sql-admin-login dbadmin
unset NIMBUS_SQL_ADMIN_PASSWORD
```

Or to use an externally-provisioned SQL Server:

```bash
export NIMBUS_SQL_CONNECTION_STRING='Server=tcp:...;Initial Catalog=MessageDatabase;...'
nb infra apply \
  --solution-id mybus \
  --environment prod \
  --resource-group rg-mybus-prod \
  --storage-provider sqlserver \
  --sql-mode external
unset NIMBUS_SQL_CONNECTION_STRING
```

The Bicep templates skip Cosmos provisioning entirely when `storageProvider == 'sqlserver'`.

## Local development with Aspire

The AppHost reads `NIMBUS_STORAGE_PROVIDER`:

```bash
# Cosmos (default — requires ConnectionStrings:cosmos in user-secrets)
dotnet run --project src/NimBus.AppHost

# SQL Server — Aspire pulls the mssql container, creates the 'nimbusdb' database,
# and wires the connection string into the WebApp/Resolver automatically.
NIMBUS_STORAGE_PROVIDER=sqlserver dotnet run --project src/NimBus.AppHost
```

The SQL Server container is provisioned with a persistent data volume,
so tables and seeded users survive AppHost restarts. Docker Desktop must
be running.

### Local sign-in via NIMBUS_IDENTITY

Setting `NIMBUS_IDENTITY=true` when launching the AppHost wires the
`NimBus.Extensions.Identity` package into the management WebApp for the
duration of the Aspire run — the WebApp serves cookie-based
username/password sign-in at `/account/login` instead of the default
Entra ID flow. Off by default; the rest of the local-dev experience is
unchanged unless the env var is set.

Identity needs SQL, so flipping the switch also auto-provisions the
Aspire-managed SQL Server container even when the message store is
Cosmos. The container, the `nimbusdb` database, the `nimbus` schema,
the eight `AspNet*` tables, and the bootstrap admin are all created on
first run — no user-secrets setup required.

**Launch.**

```powershell
# PowerShell
$env:NIMBUS_IDENTITY = "true"
dotnet run --project src/NimBus.AppHost
```

```bash
# bash / zsh
NIMBUS_IDENTITY=true dotnet run --project src/NimBus.AppHost
```

Args form also works (`dotnet run --project src/NimBus.AppHost -- --NIMBUS_IDENTITY true`).

**First sign-in.** Open the WebApp URL from the Aspire dashboard.
Unauthenticated requests redirect to `/account/login`. Sign in as:

| Field | Default | Override env var |
|---|---|---|
| Email | `admin@local` | `NIMBUS_IDENTITY_ADMIN_EMAIL` |
| Password | `Local!Admin123` | `NIMBUS_IDENTITY_ADMIN_PASSWORD` |

The defaults are also printed to the AppHost console on start-up. A
successful sign-in sets a `NimBus.Identity` cookie and lands the SPA.

```powershell
$env:NIMBUS_IDENTITY = "true"
$env:NIMBUS_IDENTITY_ADMIN_EMAIL = "you@example.com"
$env:NIMBUS_IDENTITY_ADMIN_PASSWORD = "<your-pwd>"
dotnet run --project src/NimBus.AppHost
```

**Bootstrap is one-shot.** The admin is created only when the user
store is empty. After the first sign-in, change the password from the
UI; the override env vars become inert on subsequent runs. Drop the
`nimbus.AspNet*` tables to reseed.

**Security.** These defaults are for local dev only. For Azure deployment,
pass `--identity-admin-email` and set `NIMBUS_IDENTITY_ADMIN_PASSWORD` only
in the environment of the `nb setup` process (see *SQL-only deployment*
above). Remove it after the command completes. Never set
`NIMBUS_IDENTITY_ADMIN_PASSWORD=Local!Admin123` on a deployed slot.

Implementation reference: `src/NimBus.AppHost/Program.cs` (the env-var
resolution and validation block) and
`docs/sdk-api-reference.md` § Identity Extension (the underlying
`AddNimBusIdentity` surface).

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
