var builder = DistributedApplication.CreateBuilder(args);

// NimBus storage provider toggle. Accepts the same flag names as the slim
// src/NimBus.AppHost so muscle memory carries between the two sample apphosts:
//   --NIMBUS_STORAGE_PROVIDER sqlserver  (env or arg)
//   --StorageProvider sqlserver          (legacy synonym, kept for backwards compat)
//   NIMBUS_STORAGE_PROVIDER env var
// Default is sqlserver, which runs entirely locally on the Aspire-managed SQL
// Server container.
var storageProvider = (
        Environment.GetEnvironmentVariable("NIMBUS_STORAGE_PROVIDER")
        ?? builder.Configuration["NIMBUS_STORAGE_PROVIDER"]
        ?? builder.Configuration["StorageProvider"]
        ?? "sqlserver")
    .ToLowerInvariant();

if (storageProvider is not ("sqlserver" or "cosmos"))
{
    throw new InvalidOperationException(
        $"Unknown StorageProvider '{storageProvider}'. Use 'sqlserver' (default) or 'cosmos'.");
}

// Optional: enable NimBus.Extensions.Identity (username/password sign-in) on the
// nimbus-ops WebApp. Off by default — the demo's e2e Playwright suite relies on
// the LocalDev auth bypass below. Same flag shape as the slim NimBus.AppHost.
var identityEnabled = string.Equals(
    Environment.GetEnvironmentVariable("NIMBUS_IDENTITY") ?? builder.Configuration["NIMBUS_IDENTITY"],
    "true",
    StringComparison.OrdinalIgnoreCase);

// Service Bus source. Default is a real Azure namespace via AddConnectionString
// (connection string supplied by the AppHost's user-secrets). Setting
// `UseEmulator=true` (CLI flag) or `NIMBUS_SB_EMULATOR=true` (env var) instead
// spins up Microsoft's official emulator container under Aspire — no Azure
// dependency required for local/CI demos.
//
// Emulator mode also pre-declares topology via a generated config.json (see
// EmulatorTopologyConfigBuilder), because the SDK's ServiceBusAdministrationClient
// can't reach the emulator's REST admin endpoint over the connection string
// alone — the emulator exposes admin on container port 5300, which the SDK's
// connection-string-driven URL synthesis doesn't know about. Pre-declaring
// the topology at boot makes the runtime provisioner unnecessary in this
// mode; production keeps using ServiceBusTopologyProvisioner unchanged.
var useEmulator = string.Equals(
    builder.Configuration["UseEmulator"]
        ?? Environment.GetEnvironmentVariable("NIMBUS_SB_EMULATOR")
        ?? "false",
    "true",
    StringComparison.OrdinalIgnoreCase);

IResourceBuilder<IResourceWithConnectionString> servicebus;
if (useEmulator)
{
    var emulatorConfigPath = Path.Combine(
        AppContext.BaseDirectory,
        "servicebus-emulator-config.generated.json");
    File.WriteAllText(
        emulatorConfigPath,
        CrmErpDemo.Contracts.EmulatorTopologyConfigBuilder.Build(
            new CrmErpDemo.Contracts.CrmErpPlatformConfiguration()));

    servicebus = builder
        .AddAzureServiceBus("servicebus")
        .RunAsEmulator(emulator => emulator
            .WithImageTag("2.0.0")
            .WithConfigurationFile(emulatorConfigPath));
}
else
{
    servicebus = builder.AddConnectionString("servicebus");
}

// SQL Server — Aspire spins up a container; CRM and ERP always get their own databases.
// NimBus's message store also lives on this server when sqlserver is selected.
var sql = builder.AddSqlServer("sql")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume();
var crmDb = sql.AddDatabase("crm");
var erpDb = sql.AddDatabase("erp");
// nimbusDb is provisioned when the message store needs it OR when Identity
// needs it. Identity always requires SQL even if the message store is Cosmos.
var nimbusDb = (storageProvider == "sqlserver" || identityEnabled) ? sql.AddDatabase("nimbus") : null;

// DbGate — web SQL UI for browsing the Aspire-managed SQL Server, including
// the ERP [nimbus].[OutboxMessages] table. Wired manually because the
// CommunityToolkit DbGate package does not ship a WithDbGate() extension for
// SqlServerServerResource (only Postgres/Mongo/MySQL/Redis).
builder.AddDbGate("dbgate")
    .WaitFor(sql)
    .WithEnvironment(ctx =>
    {
        ctx.EnvironmentVariables["CONNECTIONS"] = "sql";
        ctx.EnvironmentVariables["LABEL_sql"] = "Aspire SQL Server";
        ctx.EnvironmentVariables["ENGINE_sql"] = "mssql@dbgate-plugin-mssql";
        ctx.EnvironmentVariables["USER_sql"] = "sa";
        ctx.EnvironmentVariables["SERVER_sql"] = sql.Resource.PrimaryEndpoint.Property(EndpointProperty.Host);
        ctx.EnvironmentVariables["PORT_sql"] = sql.Resource.PrimaryEndpoint.Property(EndpointProperty.Port);
        ctx.EnvironmentVariables["PASSWORD_sql"] = sql.Resource.PasswordParameter;
    });

// Provision topics/subscriptions for CrmEndpoint + ErpEndpoint at runtime
// against a real Azure namespace. Skipped in emulator mode — topology is
// pre-declared via the emulator's UserConfig (see EmulatorTopologyConfigBuilder).
IResourceBuilder<ProjectResource>? provisioner = useEmulator
    ? null
    : builder.AddProject<Projects.CrmErpDemo_Provisioner>("provisioner")
        .WithReference(servicebus);

// Reused operator surface — the same Resolver + WebApp used in the main NimBus.AppHost.
//
// Endpoint ports for crm-api / erp-api / nimbus-ops are pinned to the values
// in each project's launchSettings.json so the e2e Playwright suite (under
// samples/CrmErpDemo/e2e/) can target deterministic URLs without scraping the
// Aspire dashboard for DCP-assigned ports.
var resolver = builder.AddAzureFunctionsProject<Projects.NimBus_Resolver>("resolver")
    .WithReference(servicebus)
    .WithEnvironment("ResolverId", "Resolver")
    // Functions uses AzureWebJobsServiceBus, not ConnectionStrings:servicebus.
    // Bind from the resource's runtime expression so both AddConnectionString
    // (string in user-secrets) and RunAsEmulator (string materialised by the
    // container at start) flow through the same path.
    .WithEnvironment("AzureWebJobsServiceBus", servicebus.Resource.ConnectionStringExpression);

// Point nimbus-ops at the CRM/ERP platform catalog instead of the default
// Storefront/Billing/Warehouse one, so Endpoints/EventTypes show Crm & Erp.
var crmErpContractsPath = typeof(CrmErpDemo.Contracts.CrmErpPlatformConfiguration).Assembly.Location;
var nimbusOps = builder.AddProject<Projects.NimBus_WebApp>("nimbus-ops")
    .WithReference(servicebus)
    .WithEnvironment("NimBus__PlatformType", typeof(CrmErpDemo.Contracts.CrmErpPlatformConfiguration).FullName!)
    .WithEnvironment("NimBus__PlatformAssembly", crmErpContractsPath)
    .WithEndpoint("http", e => e.Port = 28376)
    .WithEndpoint("https", e => e.Port = 28375)
    .WithExternalHttpEndpoints();

if (identityEnabled)
{
    var adminEmail = Environment.GetEnvironmentVariable("NIMBUS_IDENTITY_ADMIN_EMAIL")
        ?? builder.Configuration["NIMBUS_IDENTITY_ADMIN_EMAIL"]
        ?? "admin@local";
    var adminPassword = Environment.GetEnvironmentVariable("NIMBUS_IDENTITY_ADMIN_PASSWORD")
        ?? builder.Configuration["NIMBUS_IDENTITY_ADMIN_PASSWORD"]
        ?? "Local!Admin123";

    nimbusOps
        .WithReference(nimbusDb!)
        .WithEnvironment("NimBusIdentity__ConnectionString", nimbusDb!.Resource.ConnectionStringExpression)
        .WithEnvironment("NimBusIdentity__RequireEmailConfirmation", "false")
        .WithEnvironment("NimBusIdentity__Bootstrap__Email", adminEmail)
        .WithEnvironment("NimBusIdentity__Bootstrap__Password", adminPassword)
        .WaitFor(nimbusDb);

    Console.WriteLine(
        $"Local Identity: enabled. Sign in at /account/login as {adminEmail} " +
        "(override with NIMBUS_IDENTITY_ADMIN_EMAIL / NIMBUS_IDENTITY_ADMIN_PASSWORD).");
}
else
{
    // Local-dev auth bypass — required so the e2e Playwright suite (and a
    // human operator browsing the dashboard) can hit the API without an
    // Azure AD ClientId. Aspire defaults ASPNETCORE_ENVIRONMENT to Development
    // for child projects, which is the gate Startup.cs checks before honoring
    // this flag.
    nimbusOps.WithEnvironment("EnableLocalDevAuthentication", "true");
}

// Live Flow / Monitor realtime push (spec 020). SQL storage has no Cosmos
// Change Feed, so point the Resolver's write-path notifier at the WebApp's
// storage-hook webhook; the WebApp downloads the counts and broadcasts the
// endpointupdate that the Flow page animates. Best-effort — failures are
// swallowed and the pages reconcile via polling.
resolver.WithEnvironment("NimBus__Flow__WebAppUrl", nimbusOps.GetEndpoint("http"));

// Provider-specific wiring. The WebApp/Resolver pick their backend off NimBus__StorageProvider;
// we set it explicitly so the AppHost CLI flag wins over the runtime auto-detect fallback.
if (storageProvider == "sqlserver")
{
    // Aspire's WithReference(<database>) exposes the connection under ConnectionStrings:<dbname>.
    // The SQL Server provider looks up SqlConnection / ConnectionStrings:sqlserver / SqlServerConnection,
    // so bridge nimbusDb's connection string into ConnectionStrings__sqlserver.
    resolver.WithReference(nimbusDb!)
            .WithEnvironment("NimBus__StorageProvider", "sqlserver")
            .WithEnvironment("ConnectionStrings__sqlserver", nimbusDb!.Resource.ConnectionStringExpression)
            .WaitFor(nimbusDb);
    nimbusOps.WithReference(nimbusDb!)
             .WithEnvironment("NimBus__StorageProvider", "sqlserver")
             .WithEnvironment("ConnectionStrings__sqlserver", nimbusDb!.Resource.ConnectionStringExpression)
             .WaitFor(nimbusDb);
}
else // cosmos — connection string is supplied via the AppHost user-secret ConnectionStrings:cosmos.
{
    var cosmos = builder.AddConnectionString("cosmos");
    resolver.WithReference(cosmos)
            .WithEnvironment("NimBus__StorageProvider", "cosmos");
    nimbusOps.WithReference(cosmos)
             .WithEnvironment("NimBus__StorageProvider", "cosmos");
}

// CRM side.
var crmApi = builder.AddProject<Projects.Crm_Api>("crm-api")
    .WithReference(servicebus)
    .WithReference(crmDb)
    .WithEndpoint("http", e => e.Port = 5080)
    .WithExternalHttpEndpoints()
    .WaitFor(crmDb);
if (provisioner is not null) crmApi = crmApi.WaitFor(provisioner);

var crmAdapter = builder.AddProject<Projects.Crm_Adapter>("crm-adapter")
    .WithReference(servicebus)
    .WithReference(crmApi)
    .WaitFor(crmApi);
if (provisioner is not null) crmAdapter.WaitFor(provisioner);

builder.AddViteApp("crm-web", "../Crm.Web")
    .WithReference(crmApi)
    .WithExternalHttpEndpoints()
    .WaitFor(crmApi);

// ERP side.
var erpApi = builder.AddProject<Projects.Erp_Api>("erp-api")
    .WithReference(servicebus)
    .WithReference(erpDb)
    .WithEndpoint("http", e => e.Port = 5090)
    .WithExternalHttpEndpoints()
    .WaitFor(erpDb);
if (provisioner is not null) erpApi = erpApi.WaitFor(provisioner);

var erpAdapter = builder.AddAzureFunctionsProject<Projects.Erp_Adapter_Functions>("erp-adapter")
    .WithReference(servicebus)
    .WithReference(erpApi)
    .WithEnvironment("AzureWebJobsServiceBus", servicebus.Resource.ConnectionStringExpression)
    .WithEnvironment("TopicName", "ErpEndpoint")
    .WithEnvironment("SubscriptionName", "ErpEndpoint")
    .WaitFor(erpApi);
if (provisioner is not null) erpAdapter.WaitFor(provisioner);

builder.AddViteApp("erp-web", "../Erp.Web")
    .WithReference(erpApi)
    .WithExternalHttpEndpoints()
    .WaitFor(erpApi);

// DataPlatform sink. Pure terminal subscriber for ErpCustomerCreated —
// mirrors the Erp adapter's Functions hosting model. No Api / Web companion
// projects (the handler logs the inbound event; a real sink would write to
// a lake / warehouse here).
var dataPlatformAdapter = builder
    .AddAzureFunctionsProject<Projects.DataPlatform_Adapter_Functions>("dataplatform-adapter")
    .WithReference(servicebus)
    .WithEnvironment("AzureWebJobsServiceBus", servicebus.Resource.ConnectionStringExpression)
    .WithEnvironment("TopicName", "DataPlatformEndpoint")
    .WithEnvironment("SubscriptionName", "DataPlatformEndpoint");
if (provisioner is not null) dataPlatformAdapter.WaitFor(provisioner);

// Agent Zone (spec 022). The park host subscribes to AgentZoneEndpoint and parks
// every inbound CrmContactCreated as Pending+Handoff. Wired exactly like the
// Crm.Adapter worker subscriber (WithReference(servicebus) + gate on the
// provisioner): the AgentZoneEndpoint topology is created by the provisioner in
// real-Azure mode, or pre-declared in the emulator's UserConfig otherwise — the
// same topology gate the other subscribers rely on.
var agentZone = builder.AddProject<Projects.CrmErpDemo_AgentZone>("agent-zone")
    .WithReference(servicebus);
if (provisioner is not null) agentZone.WaitFor(provisioner);

// EnrichmentAgent (spec 022). Runs the receive->classify->define->publish->settle
// loop against the agent REST API on nimbus-ops. nimbus-ops is registered
// unconditionally above, so the agent binds to it directly — service discovery
// rewrites "https+http://nimbus-ops" to the resolved endpoint. ANTHROPIC_API_KEY
// is forwarded when present; absent, the agent uses its deterministic classifier.
builder.AddProject<Projects.EnrichmentAgent>("enrichment-agent")
    .WithReference(nimbusOps)
    .WaitFor(nimbusOps)
    .WithEnvironment("ANTHROPIC_API_KEY", builder.Configuration["ANTHROPIC_API_KEY"] ?? "");

// Marketing source (spec 023). Publishes marketing.lead.created.v1 as a classless
// dynamic event onto MarketingEndpoint. Also seeds marketing.lead.created.v1 and
// erp.customer.upsert.v1 schemas into the NimBus registry via the agent REST API
// so the AI Integration Mapper can reference both event types when authoring a mapping.
var marketingApi = builder.AddProject<Projects.Marketing_Api>("marketing-api")
    .WithReference(servicebus)
    .WithReference(nimbusOps)
    .WaitFor(nimbusOps)
    .WithEndpoint("http", e => e.Port = 5085)
    .WithExternalHttpEndpoints();
if (provisioner is not null) marketingApi.WaitFor(provisioner);

// Mapping Zone (spec 023). Subscribes to MappingZoneEndpoint and runs the Mapping Executor
// per message: consults the mapping registry, applies the JSONata transform (when Active),
// publishes to the target topic, or parks as Pending+Handoff (when Paused/Stale/no mapping).
// Mirrors the agent-zone resource above.
var mappingZone = builder.AddProject<Projects.CrmErpDemo_MappingZone>("mapping-zone")
    .WithReference(servicebus)
    .WithEnvironment("NimBus__StorageProvider", storageProvider);

if (storageProvider == "sqlserver")
{
    mappingZone
        .WithReference(nimbusDb!)
        .WithEnvironment("ConnectionStrings__sqlserver", nimbusDb!.Resource.ConnectionStringExpression)
        .WaitFor(nimbusDb);
}
else
{
    var cosmos = builder.AddConnectionString("cosmos");
    mappingZone.WithReference(cosmos);
}

if (provisioner is not null) mappingZone.WaitFor(provisioner);

// MappingAgent (spec 023). Reads both schemas from the catalog, samples source messages,
// authors a JSONata transform (Claude when key is present, deterministic otherwise), and
// submits a Draft mapping proposal via POST /api/agent/mappings. An operator then approves
// the Draft in the WebApp, which transitions it to Active and triggers the executor.
// Mirrors the enrichment-agent resource above.
builder.AddProject<Projects.MappingAgent>("mapping-agent")
    .WithReference(nimbusOps)
    .WaitFor(nimbusOps)
    .WithEnvironment("ANTHROPIC_API_KEY", builder.Configuration["ANTHROPIC_API_KEY"] ?? "");

builder.Build().Run();
