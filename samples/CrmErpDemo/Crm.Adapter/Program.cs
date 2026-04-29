using Azure.Messaging.ServiceBus;
using Crm.Adapter;
using Crm.Adapter.Clients;
using Crm.Adapter.Handlers;
using CrmErpDemo.Contracts.Events;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Pipeline;
using NimBus.Outbox.SqlServer;
using NimBus.SDK.Extensions;
using NimBus.SDK.Hosting;
using NimBus.ServiceBus;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

var crmConnectionString = builder.Configuration.GetConnectionString("crm")
    ?? throw new InvalidOperationException("ConnectionStrings:crm is required.");

var crmApiBaseUrl = builder.Configuration["services:crm-api:https:0"]
    ?? builder.Configuration["services:crm-api:http:0"]
    ?? builder.Configuration["Crm:ApiBaseUrl"]
    ?? throw new InvalidOperationException("Crm API base URL is required (service discovery or Crm:ApiBaseUrl).");

builder.Services.AddHttpClient<ICrmApiClient, CrmApiClient>(c => c.BaseAddress = new Uri(crmApiBaseUrl));

// Outbox + dispatcher: adapter owns dispatch so the API stays lean.
builder.Services.AddNimBusSqlServerOutbox(crmConnectionString);
builder.Services.AddSingleton<OutboxDispatcherSender>(sp =>
{
    var client = sp.GetRequiredService<ServiceBusClient>();
    return new OutboxDispatcherSender(client.CreateSender("CrmEndpoint"));
});
builder.Services.AddNimBusOutboxDispatcher(TimeSpan.FromSeconds(1));

// Middleware pipeline.
builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<MetricsMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
});

// Subscriber + receiver for CrmEndpoint. Also publishes acks/errors back through CrmEndpoint sender.
builder.Services.AddNimBusSubscriber("CrmEndpoint", sub =>
{
    sub.AddHandler<ErpCustomerCreated, ErpCustomerCreatedHandler>();
    sub.AddHandler<ErpCustomerUpdated, ErpCustomerUpdatedHandler>();
    sub.AddHandler<ErpCustomerDeleted, ErpCustomerDeletedHandler>();
    sub.AddHandler<ErpContactCreated, ErpContactCreatedHandler>();
    sub.AddHandler<ErpContactUpdated, ErpContactUpdatedHandler>();
    sub.AddHandler<ErpContactDeleted, ErpContactDeletedHandler>();
});
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "CrmEndpoint";
    opts.SubscriptionName = "CrmEndpoint";
});

// Deferred processor.
builder.Services.AddSingleton<IDeferredMessageProcessor>(sp =>
{
    var sbClient = sp.GetRequiredService<ServiceBusClient>();
    return new DeferredMessageProcessor(sbClient);
});
builder.Services.AddHostedService(sp =>
{
    var sbClient = sp.GetRequiredService<ServiceBusClient>();
    var processor = sp.GetRequiredService<IDeferredMessageProcessor>();
    var logger = sp.GetRequiredService<ILogger<DeferredProcessorService>>();
    return new DeferredProcessorService(sbClient, processor, logger, "CrmEndpoint");
});

var host = builder.Build();

// Ensure outbox table exists on startup so a fresh DB doesn't race the dispatcher.
var startupOutbox = (SqlServerOutbox)host.Services.GetRequiredService<NimBus.Core.Outbox.IOutbox>();
await startupOutbox.EnsureTableExistsAsync();

host.Run();
