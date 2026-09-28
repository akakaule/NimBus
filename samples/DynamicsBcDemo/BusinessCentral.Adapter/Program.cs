using BusinessCentral.Adapter.Clients;
using BusinessCentral.Adapter.Handlers;
using BusinessCentral.Adapter.Observability;
using BusinessCentral.Adapter.Resilience;
using DynamicsBcDemo.Contracts.BusinessCentral;
using DynamicsBcDemo.Contracts.D365Sales;
using Microsoft.Extensions.Configuration;
using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
using NimBus.Core.Pipeline;
using NimBus.Extensions.Notifications;
using NimBus.Inbox.SqlServer;
using NimBus.SDK.Extensions;

// Business Central adapter: receives CRM's prospects, opportunities and credit checks on
// BusinessCentralEndpoint and calls the Business Central APIs. It is a worker (container) rather
// than an Azure Function because only a worker host can pause its receivers when the circuit
// breaker opens — which is exactly what a BC update window calls for.
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

var bcApiBaseUrl = builder.Configuration["services:bc-api:https:0"]
    ?? builder.Configuration["services:bc-api:http:0"]
    ?? builder.Configuration["BusinessCentral:ApiBaseUrl"]
    ?? throw new InvalidOperationException("The Business Central API base URL is required (service discovery or BusinessCentral:ApiBaseUrl).");

var resilience = builder.Configuration.GetSection("BusinessCentral:Resilience").Get<BcResilienceOptions>() ?? new BcResilienceOptions();

// The BC client gets its own HttpClient, outside IHttpClientFactory, so it has NO resilience
// handler: ServiceDefaults adds a standard one to every factory client, which would retry 429/503
// invisibly inside a single NimBus attempt and time out with exceptions no retry rule recognizes
// (the per-client opt-out, RemoveAllResilienceHandlers, is still experimental). Retries for BC
// calls belong to NimBus, where each one is audited. HttpClient tracing still flows to the dashboard.
builder.Services.AddSingleton<IBusinessCentralClient>(_ => new BusinessCentralClient(
    new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
        BaseAddress = new Uri(bcApiBaseUrl),
        Timeout = TimeSpan.FromSeconds(15),
    }));

builder.Services.AddHttpClient<CircuitStateReporterClient>(client =>
{
    client.BaseAddress = new Uri(bcApiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(2);
});

// Inbox deduplication lives in the integration platform's own database, never in BC's (BC is SaaS).
var nimbusDbConnectionString = builder.Configuration.GetConnectionString("nimbus")
    ?? throw new InvalidOperationException("ConnectionStrings:nimbus is required for the inbox deduplication store.");
builder.Services.AddNimBusSqlServerInbox(nimbusDbConnectionString);

builder.Services.AddNimBus(nimbus =>
{
    nimbus.AddPipelineBehavior<LoggingMiddleware>();
    nimbus.AddPipelineBehavior<ValidationMiddleware>();
    nimbus.AddLifecycleObserver<CircuitStateReporter>();
});

builder.Services.AddNimBusSubscriber(
    configure: options => options.Endpoint = "BusinessCentralEndpoint",
    configureBuilder: sub =>
    {
        sub.AddHandlersFromAssemblyContaining<D365ProspectUpdatedHandler>();
        sub.AddRequestHandler<D365CreditCheckRequested, BcCreditStatus, D365CreditCheckRequestedHandler>();

        // CRM gives every change a deterministic MessageId, so a change delivered twice is skipped as
        // DuplicateDetected. Operator resubmits get a new MessageId and run the handler; the BC upserts
        // are idempotent for exactly that case.
        sub.UseInbox(inbox =>
        {
            inbox.DeduplicationStore = InboxStore.SqlServer;
            inbox.RetentionPeriod = TimeSpan.FromDays(2);
            inbox.CleanupInterval = TimeSpan.FromMinutes(15);
        });

        sub.ConfigureRetryPolicies(policies => BcResilience.ConfigureRetries(policies, resilience));
        sub.WithCircuitBreaker(circuit => BcResilience.ConfigureCircuitBreaker(circuit, resilience));
    });

builder.Services.AddNimBusReceiver(options =>
{
    options.TopicName = "BusinessCentralEndpoint";
    options.SubscriptionName = "BusinessCentralEndpoint";
    // Enough to take a whole pilot-office burst at once; per-customer order still holds.
    options.MaxConcurrentSessions = 16;
    // Release idle sessions quickly so half-open probing isn't held up by the 30 s default.
    options.SessionIdleTimeout = TimeSpan.FromSeconds(3);
    // No prefetch: with the circuit breaker, prefetched messages would burn delivery attempts on
    // every open cycle.
    options.PrefetchCount = 0;
});

// Worker hosts own the deferred-replay loop (parked messages behind a blocked session).
builder.Services.AddNimBusDeferredProcessorHostedService("BusinessCentralEndpoint");

// Alerts to the "#integration-alerts" demo page, standing in for a Teams channel (production would
// use channels.AddTeams(...)). Failures are Error, an opening circuit is Critical, and a closing
// circuit is Information, so MinSeverity is Information. Session-block alerts stay off: each
// failure already raises one alert. A short dedup window keeps a rehearsal from hiding the live
// circuit alert, whose id is the same every time.
builder.Services.AddNimBusNotifications(channels =>
{
    channels.AddWebhook(webhook =>
    {
        webhook.Url = bcApiBaseUrl.TrimEnd('/') + "/api/demo/alerts";
        webhook.MinSeverity = NotificationSeverity.Information;
        webhook.Template =
            "{\"severity\":\"{Severity}\",\"title\":\"{Title}\",\"message\":\"{Message}\"," +
            "\"eventId\":\"{EventId}\",\"eventTypeId\":\"{EventTypeId}\",\"messageId\":\"{MessageId}\"," +
            "\"correlationId\":\"{CorrelationId}\",\"errorDetails\":\"{ErrorDetails}\"}";
    });
    channels.WithDedupWindow(TimeSpan.FromSeconds(10));
}, options =>
{
    options.NotifyOnFailure = true;
    options.NotifyOnDeadLetter = true;
    options.NotifyOnSessionBlock = false;
    options.NotifyOnCircuitOpen = true;
});

builder.Build().Run();
