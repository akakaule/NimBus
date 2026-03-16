var builder = DistributedApplication.CreateBuilder(args);

var servicebus = builder.AddConnectionString("servicebus");
var cosmos = builder.AddConnectionString("cosmos");

var provisioner = builder.AddProject<Projects.AspirePubSub_Provisioner>("provisioner")
    .WithReference(servicebus);

var resolver = builder.AddProject<Projects.AspirePubSub_ResolverWorker>("resolver")
    .WithReference(servicebus).WithReference(cosmos)
    .WaitFor(provisioner);

builder.AddProject<Projects.NimBus_WebApp>("webapp")
    .WithReference(servicebus).WithReference(cosmos)
    .WaitFor(provisioner).WithExternalHttpEndpoints();

builder.AddProject<Projects.AspirePubSub_Publisher>("publisher")
    .WithReference(servicebus).WaitFor(provisioner)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.AspirePubSub_Subscriber>("subscriber")
    .WithReference(servicebus).WaitFor(provisioner);

builder.Build().Run();
