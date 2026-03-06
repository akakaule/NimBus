var builder = DistributedApplication.CreateBuilder(args);

// Real Azure Cosmos DB (connection string from configuration/user secrets)
var cosmos = builder.AddConnectionString("cosmos");

// Real Azure Service Bus (connection string from configuration/user secrets)
var servicebus = builder.AddConnectionString("servicebus");

// Resolver Function App
var resolver = builder.AddProject<Projects.NimBus_Resolver>("resolver")
    .WithReference(cosmos)
    .WithReference(servicebus)
    .WithEnvironment("ResolverId", "Resolver")
    .WithEnvironment("AzureWebJobsServiceBus", builder.Configuration["ConnectionStrings:servicebus"]!);

// WebApp (Management UI)
var webapp = builder.AddProject<Projects.NimBus_WebApp>("webapp")
    .WithReference(cosmos)
    .WithReference(servicebus)
    .WithExternalHttpEndpoints();

builder.Build().Run();
