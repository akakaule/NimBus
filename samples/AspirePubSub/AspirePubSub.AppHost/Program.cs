var builder = DistributedApplication.CreateBuilder(args);

var storageProvider = (Environment.GetEnvironmentVariable("NIMBUS_STORAGE_PROVIDER") ?? "cosmos").ToLowerInvariant();

var servicebus = builder.AddConnectionString("servicebus");

var provisioner = builder.AddProject<Projects.AspirePubSub_Provisioner>("provisioner")
    .WithReference(servicebus);

var resolver = builder.AddProject<Projects.AspirePubSub_ResolverWorker>("resolver")
    .WithReference(servicebus)
    .WaitFor(provisioner);

var webapp = builder.AddProject<Projects.NimBus_WebApp>("webapp")
    .WithReference(servicebus)
    .WaitFor(provisioner).WithExternalHttpEndpoints();

if (storageProvider == "sqlserver")
{
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

builder.AddProject<Projects.AspirePubSub_Publisher>("publisher")
    .WithReference(servicebus).WaitFor(provisioner)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.AspirePubSub_Subscriber>("subscriber")
    .WithReference(servicebus).WaitFor(provisioner);

builder.Build().Run();
