using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NimBus.OpenTelemetry;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Aspire service defaults for NimBus hosts and adapters: OpenTelemetry with NimBus instrumentation,
/// health checks and service discovery.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>
    /// Adds OpenTelemetry (with NimBus instrumentation), the default health checks and service
    /// discovery, and makes every <c>IHttpClientFactory</c> client resolve service-discovery names.
    /// </summary>
    /// <remarks>
    /// The standard HTTP resilience handler is not added unless
    /// <see cref="NimBusServiceDefaultsOptions.UseStandardResilienceHandler"/> is set. An adapter's
    /// external-system client should usually leave it off, so that NimBus retry rules are the only
    /// retry layer and each delivery attempt stays within the message lock.
    /// </remarks>
    /// <param name="builder">The host builder.</param>
    /// <param name="configure">Optional configuration of the defaults.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddServiceDefaults(
        this IHostApplicationBuilder builder,
        Action<NimBusServiceDefaultsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new NimBusServiceDefaultsOptions();
        configure?.Invoke(options);

        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            if (options.UseStandardResilienceHandler)
            {
                http.AddStandardResilienceHandler();
            }

            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>
    /// Adds OpenTelemetry logging, metrics and tracing with NimBus instrumentation, exporting over OTLP
    /// when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set and to Azure Monitor when
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is set.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddNimBusInstrumentation();
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddNimBusInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource("Azure.Cosmos.Operation")
                    .AddSource("Azure.Messaging.ServiceBus")
                    .AddNimBusInstrumentation();
            });

        AddOpenTelemetryExporters(builder);

        return builder;
    }

    private static void AddOpenTelemetryExporters(IHostApplicationBuilder builder)
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // When running in Azure, also export to Azure Monitor
        if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            builder.Services.AddOpenTelemetry().UseAzureMonitor();
        }
    }

    /// <summary>
    /// Adds a <c>self</c> health check tagged <c>live</c>.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps <c>/health</c> (every check), <c>/alive</c> (checks tagged <c>live</c>) and <c>/ready</c>
    /// (checks tagged <c>ready</c>).
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health");

        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        app.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready")
        });

        return app;
    }
}
