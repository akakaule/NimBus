using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NimBus.Extensions.IntegrationIntelligence.Controllers;
using NimBus.Extensions.IntegrationIntelligence.Evidence;
using NimBus.Extensions.IntegrationIntelligence.Providers;
using NimBus.Extensions.IntegrationIntelligence.Storage;
using System.Reflection;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Registers the optional integration-intelligence feature.</summary>
public static class IntegrationIntelligenceRegistration
{
    /// <summary>
    /// Reads and validates a contained configuration snapshot, prunes feature
    /// controllers when disabled or unready, and registers execution services only
    /// when the provider configuration is valid.
    /// </summary>
    public static IServiceCollection AddNimBusIntegrationIntelligence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IntegrationIntelligenceOptions? parent = null;
        FailureClassificationOptions options;
        var bindingErrors = new List<string>();
        try
        {
            var section = configuration.GetSection("NimBus:IntegrationIntelligence");
            if (!section.GetValue<bool>("Enabled") || !section.GetValue("FailureClassification:Enabled", true))
            {
                services.AddControllers().ConfigureApplicationPartManager(manager =>
                    manager.FeatureProviders.Add(new IntegrationIntelligenceFeatureProvider(false, false)));
                return services;
            }
            parent = section.Get<IntegrationIntelligenceOptions>();
            options = parent?.FailureClassification ?? new();
            if (parent is not null)
            {
                options.IntelligenceEnabled = parent.Enabled;
                options.Enabled = parent.Enabled && options.Enabled;
                options.ApplyNestedSettings();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            options = new FailureClassificationOptions();
            var parentEnabled = string.Equals(configuration["NimBus:IntegrationIntelligence:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
            options.Enabled = parentEnabled;
            options.IntelligenceEnabled = parentEnabled;
            bindingErrors.Add("Classification configuration could not be bound.");
        }
        var validationErrors = bindingErrors.Concat(options.Validate()).ToArray();
        var hasStorage = services.Any(d => d.ServiceType == typeof(IIntegrationIntelligenceStorageSettings) || d.ServiceType == typeof(IFailureClassificationStore));
        var ready = options.Enabled && validationErrors.Length == 0 && hasStorage;
        services.AddSingleton(options);
        services.AddSingleton(new IntegrationIntelligenceActivation(options.Enabled, ready, validationErrors));
        services.AddControllers().ConfigureApplicationPartManager(manager =>
            manager.FeatureProviders.Add(new IntegrationIntelligenceFeatureProvider(options.Enabled, ready)));

        if (!options.Enabled)
        {
            return services;
        }

        services.AddScoped<IntegrationIntelligenceStatusService>();
        if (!ready)
        {
            return services;
        }

        services.AddSingleton<IntelligenceDataRedactor>();
        services.AddScoped(sp => new FailureEvidenceBuilder(
            sp.GetRequiredService<NimBus.MessageStore.Abstractions.IMessageTrackingStore>(),
            sp.GetRequiredService<NimBus.Core.Messages.PII.IEventJsonMasker>(),
            sp.GetService<NimBus.Core.Messages.PII.IEventJsonRedactor>(),
            sp.GetRequiredService<IntelligenceDataRedactor>(), options));
        services.AddHttpClient("NimBus.TypeSafe", client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<IFailureIntelligenceProvider, TypeSafeFailureIntelligenceProvider>();
        services.TryAddSingleton<IFailureClassificationStore>(sp =>
        {
            var storage = sp.GetService<IIntegrationIntelligenceStorageSettings>();
            if (storage is null) return new UnavailableClassificationStore();
            if (string.Equals(storage.Provider, "sqlserver", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(storage.SqlConnectionString) ? new UnavailableClassificationStore() : new SqlClassificationStore(storage.SqlConnectionString);
            }
            if (string.Equals(storage.Provider, "cosmos", StringComparison.OrdinalIgnoreCase)
                && storage.CosmosClient is not null
                && !string.IsNullOrWhiteSpace(storage.CosmosDatabaseName))
            {
                return new CosmosClassificationStore(storage.CosmosClient, storage.CosmosDatabaseName);
            }
            return new UnavailableClassificationStore();
        });
        services.AddHostedService<SqlClassificationSchemaInitializer>();
        services.AddHostedService<ClassificationRetentionWorker>();
        services.AddScoped<FailureClassificationService>();
        return services;
    }

    private sealed class IntegrationIntelligenceFeatureProvider(bool enabled, bool ready) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            if (!enabled)
            {
                for (var index = feature.Controllers.Count - 1; index >= 0; index--)
                {
                    if (IsIntelligenceController(feature.Controllers[index])) feature.Controllers.RemoveAt(index);
                }
            }
            else if (!ready)
            {
                for (var index = feature.Controllers.Count - 1; index >= 0; index--)
                {
                    if (feature.Controllers[index].AsType() == typeof(IntegrationIntelligenceController)) feature.Controllers.RemoveAt(index);
                }
            }
        }

        private static bool IsIntelligenceController(TypeInfo type)
            => typeof(IntegrationIntelligenceStatusController).IsAssignableFrom(type.AsType())
                || typeof(IntegrationIntelligenceController).IsAssignableFrom(type.AsType());
    }
}

internal sealed class SqlClassificationSchemaInitializer : IHostedService
{
    private static readonly Action<ILogger, Exception?> SchemaInitializationLog =
        LoggerMessage.Define(LogLevel.Error, new EventId(1, "SchemaInitializationFailed"), "Integration Intelligence SQL schema could not be initialized; classification remains unavailable until storage is fixed.");
    private readonly IServiceProvider _services;
    private readonly ILogger<SqlClassificationSchemaInitializer> _logger;

    public SqlClassificationSchemaInitializer(
        IServiceProvider services,
        ILogger<SqlClassificationSchemaInitializer> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_services.GetRequiredService<IFailureClassificationStore>() is not SqlClassificationStore sqlStore) return;
            await sqlStore.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SchemaInitializationLog(_logger, exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Activation snapshot used by status and tests.</summary>
public sealed record IntegrationIntelligenceActivation(bool Enabled, bool Ready, IReadOnlyList<string> ValidationErrors);

/// <summary>Stable status response data.</summary>
public sealed record IntegrationIntelligenceStatus(
    string Status,
    string? Provider,
    string? Model,
    int ContractVersion,
    string EndpointId,
    bool CanAnalyze);

/// <summary>Returns feature status without resolving execution dependencies.</summary>
public sealed class IntegrationIntelligenceStatusService
{
    private readonly FailureClassificationOptions _options;
    private readonly IntegrationIntelligenceActivation _activation;
    private readonly IIntegrationIntelligenceHost _host;

    /// <summary>Creates the status service.</summary>
    public IntegrationIntelligenceStatusService(FailureClassificationOptions options, IntegrationIntelligenceActivation activation, IIntegrationIntelligenceHost host)
    {
        _options = options;
        _activation = activation;
        _host = host;
    }

    /// <summary>Gets status for a verified endpoint.</summary>
    public async Task<IntegrationIntelligenceStatus?> GetAsync(string endpointId, CancellationToken cancellationToken)
    {
        if (!await _host.EndpointExistsAsync(endpointId, cancellationToken).ConfigureAwait(false)) return null;
        if (!await _host.HasReaderAsync(endpointId, cancellationToken).ConfigureAwait(false))
            throw new ClassificationServiceException("Forbidden", 403);
        var allowed = _options.AllowedEndpoints.Length == 0
            || _options.AllowedEndpoints.Contains(endpointId, StringComparer.OrdinalIgnoreCase);
        var canAnalyze = _activation.Ready && allowed && await _host.HasContributorAsync(endpointId, cancellationToken).ConfigureAwait(false);
        return new IntegrationIntelligenceStatus(
            _activation.Ready ? "Ready" : "ProviderNotConfigured",
            _activation.Ready ? _options.Provider : null,
            _activation.Ready ? _options.Model : null,
            1,
            endpointId,
            canAnalyze);
    }
}
