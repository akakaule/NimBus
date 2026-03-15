var builder = DistributedApplication.CreateBuilder(args);

var servicebus = builder.AddConnectionString("servicebus");

builder.AddProject<Projects.AspirePubSub_Publisher>("publisher")
    .WithReference(servicebus)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.AspirePubSub_Subscriber>("subscriber")
    .WithReference(servicebus);

builder.Build().Run();
