using System.Text.Json.Serialization;
using D365Sales.Api.Data;
using D365Sales.Api.Domain;
using D365Sales.Api.Endpoints;
using D365Sales.Api.Integration;
using Microsoft.EntityFrameworkCore;
using NimBus.SDK;
using NimBus.SDK.Extensions;

// Dynamics 365 Sales simulator: the Dataverse data the demo needs (leads, accounts, contacts,
// opportunities, product groups), the Sales Hub look-alike's JSON surface, a Dataverse-shaped API for the
// integration, and the pilot-office burst. Seller actions publish to NimBus directly; in production
// they would leave Dataverse through a Service Endpoint and the NimBus Dataverse adapter.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults(options => options.UseStandardResilienceHandler = true);

var hasServiceBus = !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("servicebus"));
if (hasServiceBus)
{
    builder.AddAzureServiceBusClient("servicebus");
}

var d365ConnectionString = builder.Configuration.GetConnectionString("d365")
    ?? throw new InvalidOperationException("ConnectionStrings:d365 is required.");

builder.Services.AddDbContext<D365DbContext>(options => options.UseSqlServer(d365ConnectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<SalesService>();
builder.Services.AddScoped<CrmChangePublisher>();
builder.Services.AddSingleton<LastPublishedChange>();

if (hasServiceBus)
{
    builder.Services.AddNimBusPublisher("D365SalesEndpoint");
}
else
{
    // Keeps the CRUD surface usable in DB-only smoke tests; nothing is published.
    builder.Services.AddSingleton<IPublisherClient, NoopPublisherClient>();
}

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();
app.MapDefaultEndpoints();

await D365DatabaseInitializer.InitializeAsync(
    app.Services,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup"));

app.MapAppEndpoints();
app.MapDataverseApiEndpoints();
app.MapDemoEndpoints();

app.Run();
