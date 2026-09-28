using DynamicsBcDemo.Contracts;

var builder = DistributedApplication.CreateBuilder(args);

// Service Bus source. The in-process NimBus Service Bus emulator is the default, so the demo needs
// neither Azure nor another container. For client-facing runs a real namespace is the more robust
// choice: set NIMBUS_SB_EMULATOR=false (or --UseEmulator false) and put ConnectionStrings:servicebus
// in this AppHost's user secrets. The emulator multiplexes AMQP and the admin API on one port, so
// the regular provisioner works against it unchanged.
var useEmulator = string.Equals(
    Environment.GetEnvironmentVariable("NIMBUS_SB_EMULATOR")
        ?? builder.Configuration["UseEmulator"]
        ?? "true",
    "true",
    StringComparison.OrdinalIgnoreCase);

IResourceBuilder<IResourceWithConnectionString> servicebus;
IResourceBuilder<ProjectResource>? serviceBusEmulatorProject = null;
if (useEmulator)
{
    var emulator = builder.AddNimBusServiceBusEmulator<Projects.NimBus_ServiceBusEmulator>("servicebus");
    servicebus = emulator.ConnectionString;
    serviceBusEmulatorProject = emulator.Project;
}
else
{
    servicebus = builder.AddConnectionString("servicebus");
}

// SQL Server. Deliberately NOT persistent and without a data volume: every run starts from the
// seed data with an empty NimBus store, which is what a rehearsed demo needs. Broker session state
// (blocked sessions, scheduled retries) lives in the in-memory emulator and resets the same way —
// so "reset the demo" means "restart the AppHost".
var sql = builder.AddSqlServer("sql");
var d365Db = sql.AddDatabase("d365");
var bcDb = sql.AddDatabase("bc");
// The integration platform's own database: the NimBus message store (audit trail, Resolver state)
// and the Business Central adapter's inbox. Never BC's database — BC is SaaS.
var nimbusDb = sql.AddDatabase("nimbus");

// Topics and subscriptions for D365SalesEndpoint and BusinessCentralEndpoint.
var provisioner = builder.AddProject<Projects.DynamicsBcDemo_Provisioner>("provisioner")
    .WithReference(servicebus);
if (serviceBusEmulatorProject is not null)
{
    provisioner.WaitFor(serviceBusEmulatorProject);
}

// NimBus Resolver: records every message's outcome (the audit trail nimbus-ops shows).
var resolver = builder.AddAzureFunctionsProject<Projects.NimBus_Resolver>("resolver")
    .WithReference(servicebus)
    .WithEnvironment("ResolverId", "Resolver")
    // Functions reads AzureWebJobsServiceBus, not ConnectionStrings:servicebus.
    .WithEnvironment("AzureWebJobsServiceBus", servicebus.Resource.ConnectionStringExpression)
    .WithReference(nimbusDb)
    .WithEnvironment("NimBus__StorageProvider", "sqlserver")
    .WithEnvironment("ConnectionStrings__sqlserver", nimbusDb.Resource.ConnectionStringExpression)
    .WaitFor(nimbusDb)
    .WaitForCompletion(provisioner);

// nimbus-ops: the NimBus operator WebApp, pointed at this demo's catalog. Ports are pinned (and
// differ from CrmErpDemo's 18080/18443) so both demos can run side by side and the look-alike
// apps can deep-link into it.
var nimbusOps = builder.AddProject<Projects.NimBus_WebApp>("nimbus-ops")
    .WithReference(servicebus)
    .WithReference(nimbusDb)
    .WithEnvironment("NimBus__PlatformType", typeof(DynamicsBcPlatformConfiguration).FullName!)
    .WithEnvironment("NimBus__PlatformAssembly", typeof(DynamicsBcPlatformConfiguration).Assembly.Location)
    .WithEnvironment("NimBus__StorageProvider", "sqlserver")
    .WithEnvironment("ConnectionStrings__sqlserver", nimbusDb.Resource.ConnectionStringExpression)
    // Local-dev sign-in bypass (Development only), so nobody needs an Entra app registration.
    .WithEnvironment("EnableLocalDevAuthentication", "true")
    // The read-only operator MCP endpoint (/mcp) for the optional "ask an AI about failures" scene.
    .WithEnvironment("NimBus__Mcp__EnableForLocalDevelopment", "true")
    .WithEndpoint("http", e => e.Port = 18180)
    .WithEndpoint("https", e => e.Port = 18543)
    .WithExternalHttpEndpoints()
    .WaitFor(nimbusDb);

// Live Flow page: SQL storage has no change feed, so the Resolver notifies the WebApp directly.
resolver.WithEnvironment("NimBus__Flow__WebAppUrl", nimbusOps.GetEndpoint("http"));

// ---- Dynamics 365 Sales (simulated) ----------------------------------------------------------
// API ports are pinned (and differ from CrmErpDemo's 5080/5090) so both demos can run side by side.
var d365Api = builder.AddProject<Projects.D365Sales_Api>("d365-api")
    .WithReference(servicebus)
    .WithReference(d365Db)
    .WithEndpoint("http", e => e.Port = 5280)
    .WithExternalHttpEndpoints()
    .WaitFor(d365Db)
    .WaitForCompletion(provisioner);

builder.AddProject<Projects.D365Sales_Adapter>("d365-adapter")
    .WithReference(servicebus)
    .WithReference(d365Api)
    .WaitFor(d365Api)
    .WaitForCompletion(provisioner);

// ---- Business Central (simulated) ------------------------------------------------------------
var bcApi = builder.AddProject<Projects.BusinessCentral_Api>("bc-api")
    .WithReference(servicebus)
    .WithReference(bcDb)
    .WithEndpoint("http", e => e.Port = 5290)
    .WithExternalHttpEndpoints()
    .WaitFor(bcDb)
    .WaitForCompletion(provisioner);

builder.AddProject<Projects.BusinessCentral_Adapter>("bc-adapter")
    .WithReference(servicebus)
    .WithReference(bcApi)
    // The adapter's inbox (deduplication store) lives in the integration platform's database.
    .WithReference(nimbusDb)
    .WaitFor(bcApi)
    .WaitFor(nimbusDb)
    .WaitForCompletion(provisioner);

// ---- The two look-alike web clients ----------------------------------------------------------
// Vite dev servers (Aspire installs the npm packages on first run). Their ports are pinned so the
// apps can deep-link into each other and into nimbus-ops; the URLs reach the browser code as
// VITE_* variables. Each proxies /api to its own simulator; the BC client also proxies /d365-api
// for the presenter's /demo cockpit (the pilot-office burst).
var d365Web = builder.AddViteApp("d365-web", "../D365Sales.Web")
    .WithReference(d365Api)
    .WithEndpoint("http", e => e.Port = 5283)
    .WithExternalHttpEndpoints()
    .WaitFor(d365Api);

var bcWeb = builder.AddViteApp("bc-web", "../BusinessCentral.Web")
    .WithReference(bcApi)
    .WithReference(d365Api)
    .WithEndpoint("http", e => e.Port = 5293)
    .WithExternalHttpEndpoints()
    .WaitFor(bcApi);

d365Web
    .WithEnvironment("VITE_BC_WEB_URL", bcWeb.GetEndpoint("http"))
    .WithEnvironment("VITE_NIMBUS_OPS_URL", nimbusOps.GetEndpoint("https"));
bcWeb
    .WithEnvironment("VITE_D365_WEB_URL", d365Web.GetEndpoint("http"))
    .WithEnvironment("VITE_NIMBUS_OPS_URL", nimbusOps.GetEndpoint("https"));

builder.Build().Run();
