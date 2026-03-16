var builder = DistributedApplication.CreateBuilder(args);

// Real Azure Cosmos DB (connection string from configuration/user secrets)
var cosmos = builder.AddConnectionString("cosmos");

// Real Azure Service Bus (connection string from configuration/user secrets)
var servicebus = builder.AddConnectionString("servicebus");

// Topology provisioner — runs once then exits
var provisioner = builder.AddProject<Projects.AspirePubSub_Provisioner>("provisioner")
    .WithReference(servicebus);

// Resolver Function App
var resolver = builder.AddProject<Projects.NimBus_Resolver>("resolver")
    .WithReference(cosmos)
    .WithReference(servicebus)
    .WithEnvironment("ResolverId", "Resolver")
    .WithEnvironment("AzureWebJobsServiceBus", builder.Configuration["ConnectionStrings:servicebus"]!)
    .WaitFor(provisioner);

// WebApp (Management UI)
var webapp = builder.AddProject<Projects.NimBus_WebApp>("webapp")
    .WithReference(cosmos)
    .WithReference(servicebus)
    .WithExternalHttpEndpoints()
    .WaitFor(provisioner);

builder.Build().Run();
