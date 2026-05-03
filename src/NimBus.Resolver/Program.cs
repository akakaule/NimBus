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
});

builder.Services.AddResolver();

builder.Build().Run();
