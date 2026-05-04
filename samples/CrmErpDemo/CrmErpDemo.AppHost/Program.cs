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

// NimBus transport toggle. CLI flag (--Transport rabbitmq) wins; NIMBUS_TRANSPORT
// env-var is a fallback. Default 'servicebus' keeps the existing CrmErpDemo wiring;
// Phase 6.3 sub-issue switches the default to 'rabbitmq' once the provider lands.
var transportProvider = (
        builder.Configuration["Transport"]
        ?? Environment.GetEnvironmentVariable("NIMBUS_TRANSPORT")
        ?? "servicebus")
    .ToLowerInvariant();

if (transportProvider is not ("servicebus" or "rabbitmq" or "inmemory"))
{
    throw new InvalidOperationException(
        $"Unknown Transport '{transportProvider}'. Use 'servicebus' (default), 'rabbitmq', or 'inmemory'.");
}

var servicebusConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["ConnectionStrings:servicebus"]);
var rabbitmqConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["ConnectionStrings:rabbitmq"])
    || !string.IsNullOrWhiteSpace(builder.Configuration["RabbitMq:Host"]);
if (transportProvider == "servicebus" && !servicebusConfigured && rabbitmqConfigured)
{
    throw new InvalidOperationException(
        "Transport=servicebus but ConnectionStrings:servicebus is not set; only RabbitMQ config is present. " +
        "Pass --Transport rabbitmq (or set NIMBUS_TRANSPORT=rabbitmq) or supply a Service Bus connection string.");
}
if (transportProvider == "rabbitmq" && !rabbitmqConfigured && servicebusConfigured)
{
    throw new InvalidOperationException(
        "Transport=rabbitmq but no RabbitMq:Host / ConnectionStrings:rabbitmq is set; only Service Bus config is present. " +
        "Pass --Transport servicebus (or set NIMBUS_TRANSPORT=servicebus) or supply RabbitMQ connection settings.");
}

Console.WriteLine($"[CrmErpDemo.AppHost] StorageProvider={storageProvider} Transport={transportProvider}");

// External resources — Service Bus is the only always-non-local dependency.
var servicebus = builder.AddConnectionString("servicebus");

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

// Provision topics/subscriptions for CrmEndpoint + ErpEndpoint.
var provisioner = builder.AddProject<Projects.CrmErpDemo_Provisioner>("provisioner")
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
    .WithEnvironment("AzureWebJobsServiceBus", builder.Configuration["ConnectionStrings:servicebus"]!)
    .WithEnvironment("NimBus__Transport", transportProvider)
    .WaitFor(provisioner);

// Point nimbus-ops at the CRM/ERP platform catalog instead of the default
// Storefront/Billing/Warehouse one, so Endpoints/EventTypes show Crm & Erp.
var crmErpContractsPath = typeof(CrmErpDemo.Contracts.CrmErpPlatformConfiguration).Assembly.Location;
var nimbusOps = builder.AddProject<Projects.NimBus_WebApp>("nimbus-ops")
    .WithReference(servicebus)
    .WithEnvironment("NimBus__PlatformType", typeof(CrmErpDemo.Contracts.CrmErpPlatformConfiguration).FullName!)
    .WithEnvironment("NimBus__PlatformAssembly", crmErpContractsPath)
    .WithEnvironment("NimBus__Transport", transportProvider)
    .WithEndpoint("http", e => e.Port = 28376)
    .WithEndpoint("https", e => e.Port = 28375)
    .WithExternalHttpEndpoints()
    .WaitFor(provisioner);

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
    .WaitFor(crmDb)
    .WaitFor(provisioner);

builder.AddProject<Projects.Crm_Adapter>("crm-adapter")
    .WithReference(servicebus)
    .WithReference(crmDb)
    .WithReference(crmApi)
    .WaitFor(crmApi)
    .WaitFor(provisioner);

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
    .WaitFor(erpDb)
    .WaitFor(provisioner);

builder.AddAzureFunctionsProject<Projects.Erp_Adapter_Functions>("erp-adapter")
    .WithReference(servicebus)
    .WithReference(erpApi)
    .WithEnvironment("AzureWebJobsServiceBus", builder.Configuration["ConnectionStrings:servicebus"]!)
    .WithEnvironment("TopicName", "ErpEndpoint")
    .WithEnvironment("SubscriptionName", "ErpEndpoint")
    .WaitFor(erpApi)
    .WaitFor(provisioner);

builder.AddViteApp("erp-web", "../Erp.Web")
    .WithReference(erpApi)
    .WithExternalHttpEndpoints()
    .WaitFor(erpApi);

builder.Build().Run();
