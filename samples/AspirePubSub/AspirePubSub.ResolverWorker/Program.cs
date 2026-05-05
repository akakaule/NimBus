using NimBus.Resolver;
using NimBus.ServiceBus.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Configuration["ResolverId"] = "Resolver";
builder.Services.AddResolver();

builder.Services.AddServiceBusReceiver(options =>
{
    options.TopicName = "Resolver";
    options.SubscriptionName = "Resolver";
    options.MaxConcurrentSessions = 8;
});

var host = builder.Build();
host.Run();
