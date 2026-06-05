using CrmErpDemo.Contracts.Handlers;
using NimBus.Core.Extensions;
using NimBus.Core.Pipeline;
using NimBus.SDK.Extensions;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

// Middleware pipeline.
builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
});

// Subscriber for AgentZoneEndpoint. Parks every inbound CrmContactCreated event
// as Pending+Handoff so an external agent can pull and settle it via REST (spec 022).
builder.Services.AddNimBusSubscriber("AgentZoneEndpoint", sub =>
{
    sub.AddHandler<CrmErpDemo.Contracts.Events.CrmContactCreated, AgentZoneParkHandler>();
});
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "AgentZoneEndpoint";
    opts.SubscriptionName = "AgentZoneEndpoint";
});

// Deferred-replay BackgroundService — allows sessions blocked by a Pending+Handoff
// to replay deferred retries once the agent settles the message.
builder.Services.AddNimBusDeferredProcessorHostedService("AgentZoneEndpoint");

builder.Build().Run();
