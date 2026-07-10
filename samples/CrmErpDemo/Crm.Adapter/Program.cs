using Crm.Adapter.Clients;
using Crm.Adapter.Handlers;
using NimBus.Core.CloudEvents;
using NimBus.Core.Extensions;
using NimBus.Core.Pipeline;
using NimBus.SDK.Extensions;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

var crmApiBaseUrl = builder.Configuration["services:crm-api:https:0"]
    ?? builder.Configuration["services:crm-api:http:0"]
    ?? builder.Configuration["Crm:ApiBaseUrl"]
    ?? throw new InvalidOperationException("Crm API base URL is required (service discovery or Crm:ApiBaseUrl).");

builder.Services.AddHttpClient<ICrmApiClient, CrmApiClient>(c => c.BaseAddress = new Uri(crmApiBaseUrl));

// Middleware pipeline. Crm.Adapter is a pure subscriber adapter: CRM-originated
// events are published directly by Crm.Api, while the ERP side is the sample
// that demonstrates the transactional outbox pattern.
builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
});

// Subscriber + receiver for CrmEndpoint. Also publishes acks/errors back through CrmEndpoint sender.
// AutoDetect handles both native NimBus messages and CloudEvents 1.0 messages on the same
// subscription: ERP events now arrive CloudEvents-binary (Erp.Api publishes with
// UseCloudEvents), and the external PartnerPortal submits raw CloudEvents leads.
builder.Services.AddNimBusSubscriber(
    configure: options =>
    {
        options.Endpoint = "CrmEndpoint";
        options.UseCloudEvents(ce => ce.Mode = CompatibilityMode.AutoDetect);
    },
    configureBuilder: sub =>
    {
        sub.AddHandlersFromAssemblyContaining<ErpCustomerCreatedHandler>();
    });
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "CrmEndpoint";
    opts.SubscriptionName = "CrmEndpoint";
    // SDK default is 1, which makes the whole CrmEndpoint serial across every
    // session and event type — far below the Erp.Adapter.Functions side which
    // allows 200 concurrent sessions (host.json). 32 is a demo-friendly middle
    // ground: 32× the current throughput with enough headroom that the shared
    // SQL Server isn't fighting itself. Per-session ordering still holds.
    opts.MaxConcurrentSessions = 32;
});

// Second receiver: partner ingress. The external PartnerPortal publishes raw
// CloudEvents (no NimBus user.* properties) to the plain PartnerInbound topic;
// its catch-all session subscription is drained here and AutoDetect routes the
// CloudEvent to PartnerLeadSubmittedHandler via its `type` attribute.
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "PartnerInbound";
    opts.SubscriptionName = "CrmEndpoint";
    opts.MaxConcurrentSessions = 8;
});

// Deferred-replay BackgroundService. Worker hosts that own the deferred-processor
// trigger themselves opt in here; Functions hosts skip this and add their own
// [ServiceBusTrigger] function class instead.
builder.Services.AddNimBusDeferredProcessorHostedService("CrmEndpoint");

builder.Build().Run();
