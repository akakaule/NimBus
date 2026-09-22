using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NimBus.WebApp.Services.IntegrationIntelligence;

/// <summary>Loads the immutable Admin override before Startup registers optional controllers.</summary>
public static class IntelligenceSettingsBootstrap
{
    public const string RevisionKey = "NimBus:IntelligenceAdmin:ActiveRevision";
    public const string FailureKey = "NimBus:IntelligenceAdmin:LoadFailed";

    /// <summary>Where the active instance's provider key came from: "saved", "deployment" or "none".</summary>
    public const string ApiKeySourceKey = "NimBus:IntelligenceAdmin:ApiKeySource";

    private const string NestedApiKey = "NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey";
    private const string FlatApiKey = "NimBus:IntegrationIntelligence:FailureClassification:ApiKey";

    /// <summary>
    /// Runs an isolated, disposable configuration bootstrap, not a second application host.
    /// Reuses storage registration/credential precedence; no hosted services are started.
    /// Data Protection is registered with the same application name as Startup so a key
    /// sealed by the running WebApp can be unsealed here.
    /// </summary>
    public static IConfiguration Load(IConfiguration configuration)
    {
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(configuration);
            services.AddDataProtection().SetApplicationName(IntelligenceSecretProtector.ApplicationName);
            var provider = Startup.AddStorage(services, configuration);
            using var bootstrap = services.BuildServiceProvider();
            var storage = new WebAppIntegrationIntelligenceStorageSettings(provider, bootstrap);
            var store = IntelligenceSettingsStore.Create(storage);
            var protector = new DataProtectionSecretProtector(bootstrap.GetRequiredService<IDataProtectionProvider>());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            return LoadAsync(configuration, store, protector, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Configuration exceptions can contain credentials; never print their text.
            Console.Error.WriteLine("Failure intelligence settings could not be loaded; classification is disabled until a successful restart.");
            return FailClosed(configuration);
        }
    }

    /// <summary>Loads saved settings without a protector: a saved provider key is ignored and deployment configuration supplies it.</summary>
    public static Task<IConfiguration> LoadAsync(IConfiguration configuration, IIntelligenceSettingsStore store, CancellationToken cancellationToken)
        => LoadAsync(configuration, store, protector: null, cancellationToken);

    /// <summary>
    /// Applies the saved revision over deployment configuration. A saved provider key that unseals
    /// overrides the deployment key; an unreadable one is ignored so a rotated key ring can never
    /// disable classification by itself. The key value is never logged.
    /// </summary>
    public static async Task<IConfiguration> LoadAsync(IConfiguration configuration, IIntelligenceSettingsStore store,
        IIntelligenceSecretProtector? protector, CancellationToken cancellationToken)
    {
        var saved = await store.ReadAsync(cancellationToken).WaitAsync(cancellationToken);
        if (saved is not null && (saved.Settings.Validate().Count != 0 || !Guid.TryParse(saved.Revision, out _)))
            throw new InvalidOperationException("Stored intelligence settings are invalid.");
        var apiKey = saved?.ProtectedApiKey is null ? null : protector?.TryUnprotect(saved.ProtectedApiKey);
        var effective = saved is null ? configuration : saved.Settings.ApplyTo(configuration, apiKey);
        var source = apiKey is not null ? "saved" : HasDeploymentCredential(configuration) ? "deployment" : "none";
        return new ConfigurationBuilder().AddConfiguration(effective).AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RevisionKey] = saved?.Revision ?? "none", [FailureKey] = "false", [ApiKeySourceKey] = source,
        }).Build();
    }

    public static IConfiguration FailClosed(IConfiguration configuration)
        => new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NimBus:IntegrationIntelligence:Enabled"] = "false", [FailureKey] = "true", [RevisionKey] = "unavailable",
        }).Build();

    /// <summary>True when configuration (deployment or an applied saved revision) carries a provider key.</summary>
    public static bool HasDeploymentCredential(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration[NestedApiKey]) || !string.IsNullOrWhiteSpace(configuration[FlatApiKey]);
}
