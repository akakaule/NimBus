var builder = DistributedApplication.CreateBuilder(args);

// Storage provider selection. Set NIMBUS_STORAGE_PROVIDER=sqlserver to spin up an
// Aspire-managed SQL Server container instead of expecting a Cosmos connection
// string. Default 'cosmos' preserves the existing local-dev experience.
var storageProvider = (Environment.GetEnvironmentVariable("NIMBUS_STORAGE_PROVIDER") ?? "cosmos").ToLowerInvariant();

// Transport provider selection. Set NIMBUS_TRANSPORT=rabbitmq to swap Service Bus
// for the RabbitMQ provider (after Phase 6.2 lands); set NIMBUS_TRANSPORT=inmemory
// for unit-test scenarios. Default 'servicebus' preserves the existing behaviour.
var transportProvider = (Environment.GetEnvironmentVariable("NIMBUS_TRANSPORT") ?? "servicebus").ToLowerInvariant();
if (transportProvider is not ("servicebus" or "rabbitmq" or "inmemory"))
{
    throw new InvalidOperationException(
        $"Unknown NIMBUS_TRANSPORT '{transportProvider}'. Use 'servicebus' (default), 'rabbitmq', or 'inmemory'.");
}

var servicebusConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["ConnectionStrings:servicebus"]);
var rabbitmqConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["ConnectionStrings:rabbitmq"])
    || !string.IsNullOrWhiteSpace(builder.Configuration["RabbitMq:Host"]);
if (transportProvider == "servicebus" && !servicebusConfigured && rabbitmqConfigured)
{
    throw new InvalidOperationException(
        "NIMBUS_TRANSPORT=servicebus but ConnectionStrings:servicebus is not set; only RabbitMQ config is present. " +
        "Set NIMBUS_TRANSPORT=rabbitmq or supply a Service Bus connection string.");
}
if (transportProvider == "rabbitmq" && !rabbitmqConfigured && servicebusConfigured)
{
    throw new InvalidOperationException(
        "NIMBUS_TRANSPORT=rabbitmq but no RabbitMq:Host / ConnectionStrings:rabbitmq is set; only Service Bus config is present. " +
        "Set NIMBUS_TRANSPORT=servicebus or supply RabbitMQ connection settings.");
}

Console.WriteLine($"[NimBus.AppHost] StorageProvider={storageProvider} Transport={transportProvider}");

// Real Azure Service Bus (connection string from configuration/user secrets).
// Bridged into Resolver and WebApp regardless of the active transport so legacy
// AddServiceBus* registrations keep wiring; rabbitmq/inmemory paths simply
// override the active transport selection inside the runtime.
var servicebus = builder.AddConnectionString("servicebus");

// Topology provisioner — runs once then exits
var provisioner = builder.AddProject<Projects.AspirePubSub_Provisioner>("provisioner")
    .WithReference(servicebus);

// Resolver Function App
var resolver = builder.AddProject<Projects.NimBus_Resolver>("resolver")
    .WithReference(servicebus)
    .WithEnvironment("ResolverId", "Resolver")
    .WithEnvironment("AzureWebJobsServiceBus", builder.Configuration["ConnectionStrings:servicebus"]!)
    .WithEnvironment("NimBus__Transport", transportProvider)
    .WaitFor(provisioner);

// WebApp (Management UI)
var webapp = builder.AddProject<Projects.NimBus_WebApp>("webapp")
    .WithReference(servicebus)
    .WithEnvironment("NimBus__Transport", transportProvider)
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
