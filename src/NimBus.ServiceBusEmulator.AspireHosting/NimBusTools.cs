using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace NimBus.ServiceBusEmulator.AspireHosting;

/// <summary>
/// Runs a NimBus dotnet tool package (<c>Akaule.NimBus.*</c>) as an Aspire executable through
/// <c>dotnet tool exec</c>, so an AppHost needs no NimBus source checkout.
/// </summary>
/// <remarks>
/// The tool version defaults to this hosting package's own version, so the tools match the
/// packages the AppHost references. <c>NIMBUS_TOOL_VERSION</c> in the AppHost's configuration
/// overrides it. Packages restore through the AppHost directory's NuGet configuration.
/// </remarks>
internal static class NimBusTools
{
    /// <summary>The AppHost configuration key that overrides the tool version.</summary>
    internal const string VersionOverrideKey = "NIMBUS_TOOL_VERSION";

    internal static IResourceBuilder<ExecutableResource> AddTool(
        IDistributedApplicationBuilder builder,
        string name,
        string packageId,
        string? version,
        params string[] toolArgs)
    {
        var resolved = ResolveVersion(version, builder.Configuration[VersionOverrideKey], HostingPackageVersion());
        var package = resolved is null ? packageId : $"{packageId}@{resolved}";
        string[] args = ["tool", "exec", package, "--", .. toolArgs];
        return builder.AddExecutable(name, "dotnet", builder.AppHostDirectory, args);
    }

    /// <summary>
    /// The explicit version wins, then the configured override, then the hosting package's own
    /// version; a development build (0.0.0) yields <c>null</c>, which lets NuGet pick the latest.
    /// </summary>
    internal static string? ResolveVersion(string? explicitVersion, string? configuredVersion, string? hostingVersion)
    {
        if (!string.IsNullOrWhiteSpace(explicitVersion)) return explicitVersion.Trim();
        if (!string.IsNullOrWhiteSpace(configuredVersion)) return configuredVersion.Trim();
        if (string.IsNullOrWhiteSpace(hostingVersion)) return null;

        var plus = hostingVersion.IndexOf('+', StringComparison.Ordinal);
        var version = plus >= 0 ? hostingVersion[..plus] : hostingVersion;
        return version.StartsWith("0.0.0", StringComparison.Ordinal) ? null : version;
    }

    private static string? HostingPackageVersion() =>
        typeof(NimBusTools).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
}
