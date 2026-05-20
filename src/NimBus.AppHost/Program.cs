using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// Storage provider selection. NIMBUS_STORAGE_PROVIDER=sqlserver (or
// --NIMBUS_STORAGE_PROVIDER sqlserver) spins up an Aspire-managed SQL Server
// container instead of expecting a Cosmos connection string. Default 'cosmos'
// preserves the existing local-dev experience.
var storageProvider = (Environment.GetEnvironmentVariable("NIMBUS_STORAGE_PROVIDER")
    ?? builder.Configuration["NIMBUS_STORAGE_PROVIDER"]
    ?? "cosmos").ToLowerInvariant();

// Optional: enable NimBus.Extensions.Identity (username/password sign-in) for the
// management WebApp. Off by default — set NIMBUS_IDENTITY=true (or pass
// --NIMBUS_IDENTITY true) to opt in. Identity needs SQL, so flipping the switch
// also provisions the SQL container even when storage is Cosmos.
var identityEnabled = string.Equals(
    Environment.GetEnvironmentVariable("NIMBUS_IDENTITY") ?? builder.Configuration["NIMBUS_IDENTITY"],
    "true",
    StringComparison.OrdinalIgnoreCase);

// Real Azure Service Bus (connection string from configuration/user secrets)
var servicebus = builder.AddConnectionString("servicebus");

// Aspire-managed SQL Server container — provisioned when storage is sqlserver
// OR when Identity is enabled (Identity always needs SQL, even if messages are
// stored in Cosmos). Persistent container + data volume keep the database across
// AppHost restarts so users don't have to re-bootstrap admins every run.
IResourceBuilder<SqlServerDatabaseResource>? nimbusDb = null;
if (storageProvider == "sqlserver" || identityEnabled)
{
    var sql = builder.AddSqlServer("sqlserver")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume();
    nimbusDb = sql.AddDatabase("nimbusdb");

    // DbGate — web SQL browser for the Aspire-managed SQL Server. Wired manually
    // because the CommunityToolkit DbGate package doesn't ship a WithDbGate()
    // extension for SqlServerServerResource (only Postgres/Mongo/MySQL/Redis).
    // Reads the resource's password parameter so the auto-generated sa password
    // is wired into the container without leaking into config files.
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
}

// Topology provisioner — runs once then exits
var provisioner = builder.AddProject<Projects.AspirePubSub_Provisioner>("provisioner")
    .WithReference(servicebus);

// Resolver Function App
var resolver = builder.AddProject<Projects.NimBus_Resolver>("resolver")
    .WithReference(servicebus)
    .WithEnvironment("ResolverId", "Resolver")
    .WithEnvironment("AzureWebJobsServiceBus", builder.Configuration["ConnectionStrings:servicebus"]!)
    .WaitFor(provisioner);

// WebApp (Management UI)
var webapp = builder.AddProject<Projects.NimBus_WebApp>("webapp")
    .WithReference(servicebus)
    .WithExternalHttpEndpoints()
    .WaitFor(provisioner);

// Bind the active storage provider to both runtime services. Each provider package
// resolves its own connection string at runtime.
if (storageProvider == "sqlserver")
{
    // The SQL Server provider in NimBus.MessageStore.SqlServer reads
    // ConnectionStrings:sqlserver / SqlConnection / SqlServerConnection.
    // Bridge nimbusDb's ConnectionStringExpression onto those keys so the
    // runtime picks up the Aspire-managed container without further config.
    resolver.WithReference(nimbusDb!)
            .WithEnvironment("ConnectionStrings__sqlserver", nimbusDb!.Resource.ConnectionStringExpression)
            .WaitFor(nimbusDb);
    webapp.WithReference(nimbusDb!)
          .WithEnvironment("ConnectionStrings__sqlserver", nimbusDb!.Resource.ConnectionStringExpression)
          .WaitFor(nimbusDb);
}
else
{
    var cosmos = builder.AddConnectionString("cosmos");
    resolver.WithReference(cosmos);
    webapp.WithReference(cosmos);
}

if (identityEnabled)
{
    var adminEmail = Environment.GetEnvironmentVariable("NIMBUS_IDENTITY_ADMIN_EMAIL")
        ?? builder.Configuration["NIMBUS_IDENTITY_ADMIN_EMAIL"]
        ?? "admin@local";
    var adminPassword = Environment.GetEnvironmentVariable("NIMBUS_IDENTITY_ADMIN_PASSWORD")
        ?? builder.Configuration["NIMBUS_IDENTITY_ADMIN_PASSWORD"]
        ?? "Local!Admin123";

    webapp
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

// Sample Publisher (HTTP API for publishing events)
var publisher = builder.AddProject<Projects.AspirePubSub_Publisher>("publisher")
    .WithReference(servicebus)
    .WithExternalHttpEndpoints()
    .WaitFor(provisioner);

// Sample Subscriber (handles events + separated DeferredProcessor)
var subscriber = builder.AddProject<Projects.AspirePubSub_Subscriber>("subscriber")
    .WithReference(servicebus)
    .WaitFor(provisioner);

builder.Build().Run();
