using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using BusinessCentral.Api.Data;
using BusinessCentral.Api.Demo;
using BusinessCentral.Api.Domain;
using BusinessCentral.Api.Endpoints;
using BusinessCentral.Api.Integration;
using Microsoft.EntityFrameworkCore;
using NimBus.Core.Outbox;
using NimBus.Outbox.SqlServer;
using NimBus.SDK;
using NimBus.SDK.Extensions;
using NimBus.SDK.Hosting;

// Business Central simulator: the BC data the demo needs, an API shaped like BC online (standard
// API v2.0 + one custom "AL extension" API), the web client's JSON surface, and the demo controls.
// Every change is published to NimBus through the transactional outbox.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Without a Service Bus connection string the API still starts (events stay in the outbox), so a
// DB-only smoke test works and the SPA never sees connection refused.
var hasServiceBus = !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("servicebus"));
if (hasServiceBus)
{
    builder.AddAzureServiceBusClient("servicebus");
}

var bcConnectionString = builder.Configuration.GetConnectionString("bc")
    ?? throw new InvalidOperationException("ConnectionStrings:bc is required.");

builder.Services.AddDbContext<BcDbContext>(options => options.UseSqlServer(bcConnectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<INumberSeries, SqlNumberSeries>();
builder.Services.AddScoped<SalesService>();
builder.Services.AddScoped<BcUnitOfWork>();
builder.Services.AddSingleton<BcFaultState>();
builder.Services.AddSingleton<AlertsState>();
builder.Services.AddSingleton<CircuitStateStore>();

// Transactional outbox: BC changes and their events commit together; the dispatcher hosted here
// sends them. AddNimBusSqlServerOutbox must come before AddNimBusPublisher, which then switches
// to the outbox sender automatically.
builder.Services.AddNimBusSqlServerOutbox(bcConnectionString);
if (hasServiceBus)
{
    builder.Services.AddNimBusPublisher("BusinessCentralEndpoint");
    builder.Services.AddSingleton(sp =>
        new OutboxDispatcherSender(sp.GetRequiredService<ServiceBusClient>().CreateSender("BusinessCentralEndpoint")));
    builder.Services.AddNimBusOutboxDispatcher(TimeSpan.FromSeconds(1));
}
else
{
    builder.Services.AddSingleton<IPublisherClient>(sp =>
        new PublisherClient(new OutboxSender(sp.GetRequiredService<IOutbox>())));
}

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();
app.UseMiddleware<BcFaultInjectionMiddleware>();
app.MapDefaultEndpoints();

await BcDatabaseInitializer.InitializeAsync(
    app.Services,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup"));

app.MapIntegrationApiEndpoints();
app.MapAppEndpoints();
app.MapDemoEndpoints();

app.Run();
