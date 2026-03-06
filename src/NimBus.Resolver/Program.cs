using NimBus.Resolver;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
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

builder.Services.AddResolver();

builder.Build().Run();
