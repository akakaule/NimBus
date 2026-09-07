using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CrmErpDemo.Contracts.E2E;

/// <summary>Explicit opt-in for test-only behavior in the sample applications.</summary>
public static class E2eSettings
{
    /// <summary>The shared-secret request header used only by the E2E harness.</summary>
    public const string Header = "X-NimBus-E2E-Key";

    /// <summary>Production and missing/short keys always disable the controls.</summary>
    public static bool IsEnabled(string environment, bool enabled, string? key) =>
        environment == Environments.Development && enabled && key is { Length: >= 32 };

    /// <summary>Reads the application configuration and host environment.</summary>
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        IsEnabled(environment.EnvironmentName, configuration.GetValue<bool>("E2E:Enabled"), configuration["E2E:Key"]);
}
