var builder = DistributedApplication.CreateBuilder(args);

// External resources — connection strings supplied via user secrets / env.
var servicebus = builder.AddConnectionString("servicebus");
var cosmos = builder.AddConnectionString("cosmos");

// SQL Server — Aspire spins up a container; CRM and ERP get their own databases.
var sql = builder.AddSqlServer("sql");
var crmDb = sql.AddDatabase("crm");
var erpDb = sql.AddDatabase("erp");

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
var resolver = builder.AddAzureFunctionsProject<Projects.NimBus_Resolver>("resolver")
    .WithReference(cosmos)
    .WithReference(servicebus)
    .WithEnvironment("ResolverId", "Resolver")
    .WithEnvironment("AzureWebJobsServiceBus", builder.Configuration["ConnectionStrings:servicebus"]!)
    .WaitFor(provisioner);

// Point nimbus-ops at the CRM/ERP platform catalog instead of the default
// Storefront/Billing/Warehouse one, so Endpoints/EventTypes show Crm & Erp.
var crmErpContractsPath = typeof(CrmErpDemo.Contracts.CrmErpPlatformConfiguration).Assembly.Location;
builder.AddProject<Projects.NimBus_WebApp>("nimbus-ops")
    .WithReference(cosmos)
    .WithReference(servicebus)
    .WithEnvironment("NimBus__PlatformType", typeof(CrmErpDemo.Contracts.CrmErpPlatformConfiguration).FullName!)
    .WithEnvironment("NimBus__PlatformAssembly", crmErpContractsPath)
    .WithExternalHttpEndpoints()
    .WaitFor(provisioner);

// CRM side.
var crmApi = builder.AddProject<Projects.Crm_Api>("crm-api")
    .WithReference(servicebus)
    .WithReference(crmDb)
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
