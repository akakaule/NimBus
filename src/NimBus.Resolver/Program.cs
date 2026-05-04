using NimBus.Resolver;
using NimBus.Core.Extensions;
using NimBus.MessageStore;
using NimBus.MessageStore.SqlServer;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.AddServiceDefaults();

// Configure OpenTelemetry for distributed tracing and metrics
builder.Services
    .AddOpenTelemetry()
    .UseFunctionsWorkerDefaults();

// Configure Serilog for structured logging (console output)
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(LogEventLevel.Information)
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Services.AddLogging(loggingBuilder => loggingBuilder.AddSerilog(dispose: true));

// Provider selection mirrors the WebApp logic: explicit StorageProvider config wins,
// otherwise auto-detect from which connection settings are present, defaulting to
// Cosmos for backwards compatibility.
var storageProvider = builder.Configuration.GetValue<string>("NimBus:StorageProvider")
    ?? builder.Configuration.GetValue<string>("StorageProvider");
if (string.IsNullOrWhiteSpace(storageProvider))
{
    var hasSqlConfig = !string.IsNullOrWhiteSpace(builder.Configuration.GetValue<string>("SqlConnection"))
        || !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("sqlserver"))
        || !string.IsNullOrWhiteSpace(builder.Configuration.GetValue<string>("SqlServerConnection"));
    var hasCosmosConfig = !string.IsNullOrWhiteSpace(builder.Configuration.GetValue<string>("CosmosAccountEndpoint"))
        || !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("cosmos"))
        || !string.IsNullOrWhiteSpace(builder.Configuration.GetValue<string>("CosmosConnection"));
    storageProvider = (hasSqlConfig && !hasCosmosConfig) ? "sqlserver" : "cosmos";
}

// Transport selection mirrors the storage block: NimBus:Transport (or Transport)
// from configuration, falling back to 'servicebus'. Recognised values are
// 'servicebus' (default), 'rabbitmq', and 'inmemory'. The matching
// Add{Transport}Transport() extension methods land in follow-up tasks; until
// those exist, every value falls through to WithoutTransport() so the builder
// validation passes — but unknown values still error here for fast feedback.
var transportProvider = (
        builder.Configuration.GetValue<string>("NimBus:Transport")
        ?? builder.Configuration.GetValue<string>("Transport")
        ?? "servicebus")
    .ToLowerInvariant();
if (transportProvider is not ("servicebus" or "rabbitmq" or "inmemory"))
{
    throw new InvalidOperationException(
        $"Unknown NimBus:Transport '{transportProvider}'. Use 'servicebus' (default), 'rabbitmq', or 'inmemory'.");
}

builder.Services.AddNimBus(nimbus =>
{
    if (string.Equals(storageProvider, "sqlserver", StringComparison.OrdinalIgnoreCase))
    {
        nimbus.AddSqlServerMessageStore();
    }
    else
    {
        nimbus.AddCosmosDbMessageStore();
    }

    // Phase 6 transition: Add{Transport}Transport() extension methods ship with
    // tasks #18 (ServiceBus) and #24 (RabbitMQ). Until those land, every selection
    // falls through to WithoutTransport() and the legacy ServiceBus wiring in
    // NimBus.SDK keeps working. The switch block is the seam those tasks fill in.
    switch (transportProvider)
    {
        case "servicebus":
            // TODO(#18): replace with nimbus.AddServiceBusTransport();
            nimbus.WithoutTransport();
            break;
        case "rabbitmq":
            // TODO(#24): replace with nimbus.AddRabbitMqTransport(...);
            throw new InvalidOperationException(
                "NimBus:Transport=rabbitmq selected but the RabbitMQ provider has not landed yet (Phase 6.2 / task #24). " +
                "Use 'servicebus' until then.");
        case "inmemory":
            // TODO(#18 follow-up): replace with nimbus.AddInMemoryTransport();
            nimbus.WithoutTransport();
            break;
    }
});

builder.Services.AddResolver();

builder.Build().Run();
