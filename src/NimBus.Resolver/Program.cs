using NimBus.Resolver;
using NimBus.Core.Extensions;
using NimBus.MessageStore;
using NimBus.MessageStore.SqlServer;
using NimBus.ServiceBus.Transport;
using NimBus.Transport.RabbitMQ.Extensions;
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

    // Transport selection: AddServiceBusTransport (#21 / #24 / #4) registers the
    // ServiceBusClient + ServiceBusAdministrationClient + IServiceBusManagement
    // surface that ResolverBuilderExtensions consumes. RabbitMQ ships in Phase 6.2;
    // in-memory is the testing transport (#22) and stays opt-out for now until
    // its DI surface lights up.
    switch (transportProvider)
    {
        case "servicebus":
            nimbus.AddServiceBusTransport();
            break;
        case "rabbitmq":
            nimbus.AddRabbitMqTransport(opt =>
            {
                // Aspire bridges the RabbitMQ container as ConnectionStrings:rabbitmq
                // (an AMQP URI). Discrete RabbitMq:* settings are honoured as a
                // fallback for non-Aspire deployments.
                var rabbitUri = builder.Configuration.GetConnectionString("rabbitmq");
                if (!string.IsNullOrWhiteSpace(rabbitUri))
                {
                    opt.Uri = rabbitUri;
                    return;
                }

                var rabbitSection = builder.Configuration.GetSection("RabbitMq");
                if (rabbitSection.Exists())
                {
                    opt.HostName = rabbitSection["HostName"] ?? opt.HostName;
                    if (int.TryParse(rabbitSection["Port"], out var rabbitPort)) opt.Port = rabbitPort;
                    opt.VirtualHost = rabbitSection["VirtualHost"] ?? opt.VirtualHost;
                    opt.UserName = rabbitSection["UserName"] ?? opt.UserName;
                    opt.Password = rabbitSection["Password"] ?? opt.Password;
                }
            });
            break;
        case "inmemory":
            // TODO(#22 follow-up): replace with nimbus.AddInMemoryTransport();
            nimbus.WithoutTransport();
            break;
    }
});

builder.Services.AddResolver();

builder.Build().Run();
