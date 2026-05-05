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
// env-var is a fallback. Default 'servicebus' keeps the Azure-shaped wiring; pass
// `--Transport rabbitmq` for a fully on-premise demo (RabbitMQ container instead
// of an Azure Service Bus connection string).
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
if (transportProvider == "servicebus" && !servicebusConfigured)
{
    throw new InvalidOperationException(
        "Transport=servicebus but ConnectionStrings:servicebus is not set. " +
        "Pass --Transport rabbitmq (or set NIMBUS_TRANSPORT=rabbitmq) for the on-prem path " +
        "or supply a Service Bus connection string via dotnet user-secrets.");
}

Console.WriteLine($"[CrmErpDemo.AppHost] StorageProvider={storageProvider} Transport={transportProvider}");

// Transport resources. Service Bus is an external connection string; RabbitMQ is
// an Aspire-managed container with both required NimBus plugins pre-loaded
// (rabbitmq_consistent_hash_exchange, rabbitmq_delayed_message_exchange).
IResourceBuilder<IResourceWithConnectionString>? servicebusResource = null;
IResourceBuilder<IResourceWithConnectionString>? rabbitmqResource = null;

if (transportProvider == "servicebus")
{
    servicebusResource = builder.AddConnectionString("servicebus");
}
else if (transportProvider == "rabbitmq")
{
    rabbitmqResource = builder.AddRabbitMQ("rabbitmq")
        .WithImage("rabbitmq", "4-management")
        .WithBindMount(
            source: Path.Combine(AppContext.BaseDirectory, "enabled_plugins"),
            target: "/etc/rabbitmq/enabled_plugins",
            isReadOnly: true)
        .WithManagementPlugin();
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

// Provision topics/subscriptions for CrmEndpoint + ErpEndpoint. Service Bus only —
// in rabbitmq mode the publisher/subscriber declare topology themselves on
// startup via ITransportManagement.DeclareEndpointAsync.
IResourceBuilder<Aspire.Hosting.ApplicationModel.IResource>? provisioner = null;
if (transportProvider == "servicebus")
{
    provisioner = builder.AddProject<Projects.CrmErpDemo_Provisioner>("provisioner")
        .WithReference(servicebusResource!);
}

// Reused operator surface — the same Resolver + WebApp used in the main NimBus.AppHost.
//
// Endpoint ports for crm-api / erp-api / nimbus-ops are pinned to the values
// in each project's launchSettings.json so the e2e Playwright suite (under
// samples/CrmErpDemo/e2e/) can target deterministic URLs without scraping the
// Aspire dashboard for DCP-assigned ports.
var resolver = builder.AddAzureFunctionsProject<Projects.NimBus_Resolver>("resolver")
    .WithEnvironment("ResolverId", "Resolver")
    .WithEnvironment("NimBus__Transport", transportProvider);
if (transportProvider == "servicebus")
{
    resolver = resolver
        .WithReference(servicebusResource!)
        .WithEnvironment("AzureWebJobsServiceBus", builder.Configuration["ConnectionStrings:servicebus"]!);
    if (provisioner is not null)
        resolver = resolver.WaitFor(provisioner);
}
else if (transportProvider == "rabbitmq")
{
    resolver = resolver.WithReference(rabbitmqResource!).WaitFor(rabbitmqResource!);
}

// nimbus-ops — the WebApp's AdminService surface has Service-Bus-specific session
// receivers / administration calls that don't yet route through ITransportSession
// Ops (Phase 6.2 task #25). It boots cleanly on rabbitmq but admin endpoints that
// touch sessions / topology will throw NotSupportedException-style DI errors. The
// audit-trail and message-store views work transport-agnostically. Track in #28.
var crmErpContractsPath = typeof(CrmErpDemo.Contracts.CrmErpPlatformConfiguration).Assembly.Location;
var nimbusOps = builder.AddProject<Projects.NimBus_WebApp>("nimbus-ops")
    .WithEnvironment("NimBus__PlatformType", typeof(CrmErpDemo.Contracts.CrmErpPlatformConfiguration).FullName!)
    .WithEnvironment("NimBus__PlatformAssembly", crmErpContractsPath)
    .WithEnvironment("NimBus__Transport", transportProvider)
    .WithEndpoint("http", e => e.Port = 28376)
    .WithEndpoint("https", e => e.Port = 28375)
    .WithExternalHttpEndpoints();
if (transportProvider == "servicebus")
{
    nimbusOps = nimbusOps.WithReference(servicebusResource!);
    if (provisioner is not null)
        nimbusOps = nimbusOps.WaitFor(provisioner);
}
else if (transportProvider == "rabbitmq")
{
    nimbusOps = nimbusOps.WithReference(rabbitmqResource!).WaitFor(rabbitmqResource!);
}

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

void ApplyTransport<T>(IResourceBuilder<T> rb)
    where T : IResourceWithEnvironment, IResourceWithWaitSupport
{
    if (transportProvider == "servicebus")
    {
        rb.WithReference(servicebusResource!);
        if (provisioner is not null)
            rb.WaitFor(provisioner);
    }
    else if (transportProvider == "rabbitmq")
    {
        rb.WithReference(rabbitmqResource!).WaitFor(rabbitmqResource!);
    }
    rb.WithEnvironment("NimBus__Transport", transportProvider);
}

// CRM side.
var crmApi = builder.AddProject<Projects.Crm_Api>("crm-api")
    .WithReference(crmDb)
    .WithEndpoint("http", e => e.Port = 5080)
    .WithExternalHttpEndpoints()
    .WaitFor(crmDb);
ApplyTransport(crmApi);

var crmAdapter = builder.AddProject<Projects.Crm_Adapter>("crm-adapter")
    .WithReference(crmDb)
    .WithReference(crmApi)
    .WaitFor(crmApi);
ApplyTransport(crmAdapter);

builder.AddViteApp("crm-web", "../Crm.Web")
    .WithReference(crmApi)
    .WithExternalHttpEndpoints()
    .WaitFor(crmApi);

// ERP side.
var erpApi = builder.AddProject<Projects.Erp_Api>("erp-api")
    .WithReference(erpDb)
    .WithEndpoint("http", e => e.Port = 5090)
    .WithExternalHttpEndpoints()
    .WaitFor(erpDb);
ApplyTransport(erpApi);

var erpAdapter = builder.AddAzureFunctionsProject<Projects.Erp_Adapter_Functions>("erp-adapter")
    .WithReference(erpApi)
    .WithEnvironment("TopicName", "ErpEndpoint")
    .WithEnvironment("SubscriptionName", "ErpEndpoint")
    .WaitFor(erpApi);
if (transportProvider == "servicebus")
{
    erpAdapter = erpAdapter
        .WithReference(servicebusResource!)
        .WithEnvironment("AzureWebJobsServiceBus", builder.Configuration["ConnectionStrings:servicebus"]!);
    if (provisioner is not null)
        erpAdapter = erpAdapter.WaitFor(provisioner);
}
else if (transportProvider == "rabbitmq")
{
    // Erp.Adapter.Functions uses Azure Functions ServiceBus triggers, which do
    // not have a RabbitMQ equivalent. Track ITransportSessionOps abstraction
    // (#25) for the proper fix; until then the Functions adapter only runs
    // under transport=servicebus.
    erpAdapter = erpAdapter.WithEnvironment("NimBus__Transport", transportProvider);
}

builder.AddViteApp("erp-web", "../Erp.Web")
    .WithReference(erpApi)
    .WithExternalHttpEndpoints()
    .WaitFor(erpApi);

builder.Build().Run();
