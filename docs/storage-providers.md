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

### Retention of unresolved rows

Unresolved tracking rows — the ones in `Pending`, `Failed`, `Deferred`,
`DeadLettered` or `Unsupported` — used to be written with expiry disabled, so a
failed event kept its full payload forever. `UnresolvedRetentionDays` makes that
an explicit operator decision.

| | |
|---|---|
| Option | `CosmosDbMessageStoreOptions.UnresolvedRetentionDays` |
| Configuration key | `NimBus:Cosmos:UnresolvedRetentionDays` |
| Environment form | `NimBus__Cosmos__UnresolvedRetentionDays` |
| Valid values | `-1` (unlimited — the default) or `1`–`365` whole days |
| Recommended when a bound is required | `180` or `365` |

```csharp
nimbus.AddCosmosDbMessageStore(options => options.UnresolvedRetentionDays = 365);
```

```json
{
  "NimBus": {
    "Cosmos": {
      "UnresolvedRetentionDays": 365
    }
  }
}
```

Configuration binds first and the `configure` delegate runs after it, so code wins
over configuration. An invalid value fails host startup, naming the option and the
offending value — it does not wait for the first write.

The retention applies **only** to the five non-terminal statuses, and the window
slides forward on every rewrite of the row: an event that keeps retrying is
re-stamped on each write and does not expire mid-flight.

Everything else keeps its existing, hardcoded retention:

| Document | Retention |
|---|---|
| Unresolved rows (Pending / Failed / Deferred / DeadLettered / Unsupported) | configurable, unlimited by default |
| Terminal rows (Completed / Skipped) | 30 days |
| Soft-deleted rows | 60 seconds |
| Archived failed rows | 30 days |
| Per-message documents | 90 days |
| Audit documents | 1 year |

> **Expiry deletes the entire tracking document.** The audit metadata and the
> ability to resubmit that event are deleted with it — TTL is not a
> payload-stripping archive. Operators who must keep the metadata should stay on
> unlimited retention until the archive-style option exists (tracked separately as
> GH#94 fix option 2).

#### The setting applies to newly written documents only

Changing `UnresolvedRetentionDays` does not touch documents already in the store;
each keeps whatever `ttl` it was written with until it is rewritten. To backfill,
run this per endpoint container and patch each hit:

```sql
SELECT c.id FROM c
WHERE (c.ttl = -1 OR NOT IS_DEFINED(c.ttl))
  AND c.status IN ("Pending","Failed","Deferred","DeadLettered","Unsupported")
```

then `PatchOperation.Set("/ttl", <seconds>)` on each id. The `NOT IS_DEFINED`
branch matters: `nb container copy` and the WebApp copy both strip `ttl` before
upserting, so copied documents carry no `ttl` field at all. No tooling ships for
this; it is an operator action.

#### Container-level TTL on endpoint containers created before this change

Cosmos honours a document's `ttl` **only** when the container's
`DefaultTimeToLive` is set. Endpoint containers created before this change have it
unset, so every item TTL in them is inert — including the 30-day terminal and
60-second soft-delete values that have been written all along. Configuring
`UnresolvedRetentionDays` against such a container deletes nothing until an
operator enables it.

To enable it: Azure Portal → the Cosmos account → Data Explorer → the endpoint
container → **Settings → Time to Live → On (no default)**. Containers NimBus
creates from this change on need no action.

> **Flipping TTL on for an existing container activates the positive TTLs already
> stored in it.** Rows written earlier as terminal or archived (`ttl = 2592000`) or
> soft-deleted (`ttl = 60`) become deletion-eligible relative to their existing
> `_ts`, so anything already past that age is deleted shortly after the switch —
> soft-deleted rows effectively immediately. Export or back up the container
> **before** enabling TTL if you need those rows.

`DefaultTimeToLive = -1` means "TTL on, no container default": documents with no
`ttl`, and documents with `ttl = -1`, are still never expired.

#### Reserved endpoint ids

An endpoint may not be named `subscriptions`, `messages`, `audits`,
`eventschemas`, `eventreports`, `accesscontrol`, `Metadata` or `inbox` — those are
the store's own containers. Sharing one would mix endpoint tracking rows with
store data and make the container's TTL mode depend on call order, so the store
now rejects such an id instead. Comparison is case-sensitive, matching Cosmos:
`Messages` is a different container from `messages` and is allowed. An operator
who has renamed the inbox container via `CosmosInboxOptions.ContainerId` must
avoid that name too; only the default `inbox` is enforced in code.

#### Custom `ICosmosDatabaseAdapter` implementations

Endpoint-container creation now goes through
`CreateContainerIfNotExistsAsync(ContainerProperties, CancellationToken)`. An
adapter that does not implement it throws `NotSupportedException` rather than
silently creating a TTL-disabled container that accepts item TTLs and never
expires anything. The fix is a one-method override forwarding to
`Database.CreateContainerIfNotExistsAsync(containerProperties, …)`.

#### Provider scope

The SQL Server provider has no TTL mechanism and is unaffected by this setting;
retention there needs a delete job and is tracked separately.

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
