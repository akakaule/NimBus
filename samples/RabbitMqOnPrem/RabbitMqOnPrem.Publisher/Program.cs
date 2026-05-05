using NimBus.Core.Extensions;
using NimBus.MessageStore.SqlServer;
using NimBus.SDK;
using NimBus.SDK.Extensions;
using NimBus.Transport.Abstractions;
using NimBus.Transport.RabbitMQ.Extensions;
using NimBus.Transport.RabbitMQ.Topology;
using RabbitMqOnPrem.Contracts.Events;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

const string EndpointName = "DemoEndpoint";

// On-premise NimBus wiring: SQL Server message store + RabbitMQ transport.
// No Azure SDK packages are referenced — `dotnet list package --include-transitive`
// against this project should never show Azure.Messaging.ServiceBus or Cosmos.
var rabbitConnectionString = builder.Configuration.GetConnectionString("rabbitmq")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:rabbitmq is required. The AppHost wires it from the RabbitMQ container.");

builder.Services.AddNimBus(nimbus =>
{
    nimbus.AddSqlServerMessageStore();
    nimbus.AddRabbitMqTransport(opt => opt.Uri = rabbitConnectionString);
});

builder.Services.AddNimBusPublisher(EndpointName);

var app = builder.Build();

app.MapDefaultEndpoints();

// Ensure the endpoint topology exists before we accept publishes. Idempotent —
// re-running on an already-provisioned broker is a no-op.
using (var scope = app.Services.CreateScope())
{
    var management = scope.ServiceProvider.GetRequiredService<ITransportManagement>();
    await management.DeclareEndpointAsync(
        new EndpointConfig(EndpointName, RequiresOrderedDelivery: true, MaxConcurrency: null),
        CancellationToken.None);
}

app.MapPost("/publish/customer", async (IPublisherClient publisher) =>
{
    var customer = new CustomerCreated
    {
        CustomerId = Guid.NewGuid(),
        Name = "Acme Corp.",
        Email = "ops@acme.example.com",
    };

    await publisher.Publish(customer);
    return Results.Ok(new { customer.CustomerId, Status = "Published" });
});

app.MapPost("/publish/customer-failed", async (IPublisherClient publisher) =>
{
    var customer = new CustomerCreated
    {
        CustomerId = Guid.NewGuid(),
        Name = "Will Fail Inc.",
        Email = "ops@fail.example.com",
        SimulateFailure = true,
    };

    await publisher.Publish(customer);
    return Results.Ok(new { customer.CustomerId, Status = "Published (will fail)" });
});

app.Run();
