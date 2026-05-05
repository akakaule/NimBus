using NimBus.Core.Extensions;
using NimBus.MessageStore.SqlServer;
using NimBus.SDK.Extensions;
using NimBus.Transport.Abstractions;
using NimBus.Transport.RabbitMQ.Extensions;
using RabbitMqOnPrem.Contracts.Events;
using RabbitMqOnPrem.Subscriber.Handlers;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

const string EndpointName = "DemoEndpoint";

var rabbitConnectionString = builder.Configuration.GetConnectionString("rabbitmq")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:rabbitmq is required. The AppHost wires it from the RabbitMQ container.");

builder.Services.AddNimBus(nimbus =>
{
    nimbus.AddSqlServerMessageStore();
    nimbus.AddRabbitMqTransport(opt => opt.Uri = rabbitConnectionString);
});

builder.Services.AddNimBusSubscriber(EndpointName, sub =>
{
    sub.AddHandler<CustomerCreated, CustomerCreatedHandler>();
});

// NOTE: the broker-side consumer hosted service (RabbitMqReceiverHostedService)
// lands in slice 1D of issue #14. Until that lands, this Worker hosts the
// subscriber pipeline (handler dispatch + retries + park-on-block) but no
// AMQP consumer loop is registered, so messages enqueued on the broker are
// not yet drained. Builds cleanly today; running the sample end-to-end waits
// on 1D + 1G (Testcontainers conformance pass).

var host = builder.Build();

// Best-effort topology guard: the publisher does the same on startup so this
// ensures the subscriber works even if it spins up before the publisher.
using (var scope = host.Services.CreateScope())
{
    var management = scope.ServiceProvider.GetRequiredService<ITransportManagement>();
    await management.DeclareEndpointAsync(
        new EndpointConfig(EndpointName, RequiresOrderedDelivery: true, MaxConcurrency: null),
        CancellationToken.None);
}

await host.RunAsync();
