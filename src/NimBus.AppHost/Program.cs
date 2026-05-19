var builder = DistributedApplication.CreateBuilder(args);

// Storage provider selection. Set NIMBUS_STORAGE_PROVIDER=sqlserver to spin up an
// Aspire-managed SQL Server container instead of expecting a Cosmos connection
// string. Default 'cosmos' preserves the existing local-dev experience.
var storageProvider = (Environment.GetEnvironmentVariable("NIMBUS_STORAGE_PROVIDER") ?? "cosmos").ToLowerInvariant();

// Real Azure Service Bus (connection string from configuration/user secrets)
var servicebus = builder.AddConnectionString("servicebus");

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
    // Note: requires the Aspire.Hosting.SqlServer package. Documented in
    // docs/storage-providers.md. Falls back to a connection string if the package
    // isn't available, mirroring the Cosmos default.
    var sqlConnection = builder.Configuration["ConnectionStrings:sqlserver"];
    if (!string.IsNullOrEmpty(sqlConnection))
    {
        var sql = builder.AddConnectionString("sqlserver");
        resolver.WithReference(sql).WithEnvironment("SqlConnection", sqlConnection);
        webapp.WithReference(sql).WithEnvironment("SqlConnection", sqlConnection);
    }
}
else
{
    var cosmos = builder.AddConnectionString("cosmos");
    resolver.WithReference(cosmos);
    webapp.WithReference(cosmos);
}

// Optional: enable NimBus.Extensions.Identity (username/password sign-in) for
// the management WebApp. Off by default — set NIMBUS_IDENTITY=true (or pass
// --NIMBUS_IDENTITY true) to opt in. Requires a SQL connection string in
// ConnectionStrings:sqlserver (the same user-secret used by the SQL storage
// path); throws here if missing so the failure mode is obvious instead of a
// runtime crash in the WebApp.
var identityEnabled = string.Equals(
    Environment.GetEnvironmentVariable("NIMBUS_IDENTITY") ?? builder.Configuration["NIMBUS_IDENTITY"],
    "true",
    StringComparison.OrdinalIgnoreCase);
if (identityEnabled)
{
    var identitySqlConnection = builder.Configuration["ConnectionStrings:sqlserver"];
    if (string.IsNullOrWhiteSpace(identitySqlConnection))
    {
        throw new InvalidOperationException(
            "NIMBUS_IDENTITY=true requires a SQL connection string. Set it via " +
            "`dotnet user-secrets --project src/NimBus.AppHost set ConnectionStrings:sqlserver \"<conn>\"` " +
            "or the ConnectionStrings__sqlserver env var.");
    }

    var adminEmail = Environment.GetEnvironmentVariable("NIMBUS_IDENTITY_ADMIN_EMAIL")
        ?? builder.Configuration["NIMBUS_IDENTITY_ADMIN_EMAIL"]
        ?? "admin@local";
    var adminPassword = Environment.GetEnvironmentVariable("NIMBUS_IDENTITY_ADMIN_PASSWORD")
        ?? builder.Configuration["NIMBUS_IDENTITY_ADMIN_PASSWORD"]
        ?? "Local!Admin123";

    webapp
        .WithEnvironment("NimBusIdentity__ConnectionString", identitySqlConnection)
        .WithEnvironment("NimBusIdentity__RequireEmailConfirmation", "false")
        .WithEnvironment("NimBusIdentity__Bootstrap__Email", adminEmail)
        .WithEnvironment("NimBusIdentity__Bootstrap__Password", adminPassword);

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
