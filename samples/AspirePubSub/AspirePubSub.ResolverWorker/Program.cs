using NimBus.Resolver;
using NimBus.SDK.Hosting;
using NimBus.ServiceBus;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Configuration["ResolverId"] = "Resolver";
builder.Services.AddResolver();

builder.Services.AddHostedService(sp =>
{
    var client = sp.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>();
    var adapter = sp.GetRequiredService<IServiceBusAdapter>();
    var logger = sp.GetRequiredService<ILogger<NimBusReceiverHostedService>>();

    var options = new NimBusReceiverOptions
    {
        TopicName = "Resolver",
        SubscriptionName = "Resolver",
        MaxConcurrentSessions = 8
    };

    return new NimBusReceiverHostedService(client, adapter, options, logger);
});

var host = builder.Build();
host.Run();
