var builder = DistributedApplication.CreateBuilder(args);

// NimBus storage provider toggle. CLI flag wins (e.g. `aspire run -- --StorageProvider cosmos`
// or `dotnet run -- --StorageProvider cosmos`); NIMBUS_STORAGE_PROVIDER env-var is a fallback
// for parity with the AspirePubSub sample. Default is sqlserver, which runs entirely locally
// on the Aspire-managed SQL Server container.
var storageProvider = (
        builder.Configuration["StorageProvider"]
        ?? Environment.GetEnvironmentVariable("NIMBUS_STORAGE_PROVIDER")
        ?? "sqlserver")
    .ToLowerInvariant();

if (storageProvider is not ("sqlserver" or "cosmos"))
{
    throw new InvalidOperationException(
        $"Unknown StorageProvider '{storageProvider}'. Use 'sqlserver' (default) or 'cosmos'.");
}

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
        CrmErpDemo.AppHost.EmulatorTopologyConfigBuilder.Build(
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
var sql = builder.AddSqlServer("sql");
var crmDb = sql.AddDatabase("crm");
var erpDb = sql.AddDatabase("erp");
var nimbusDb = storageProvider == "sqlserver" ? sql.AddDatabase("nimbus") : null;

// DbGate — web SQL UI for browsing the Aspire-managed SQL Server (and the
// [nimbus].[OutboxMessages] table in particular). Wired manually because the
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
    // Local-dev auth bypass — required so the e2e Playwright suite (and a
    // human operator browsing the dashboard) can hit the API without an
    // Azure AD ClientId. Aspire defaults ASPNETCORE_ENVIRONMENT to Development
    // for child projects, which is the gate Startup.cs checks before honoring
    // this flag.
    .WithEnvironment("EnableLocalDevAuthentication", "true")
    .WithEndpoint("http", e => e.Port = 28376)
    .WithEndpoint("https", e => e.Port = 28375)
    .WithExternalHttpEndpoints();

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
    .WithReference(crmDb)
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

builder.Build().Run();
