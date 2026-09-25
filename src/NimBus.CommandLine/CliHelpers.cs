using McMaster.Extensions.CommandLineUtils;
using NimBus.Core;
using Spectre.Console;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;

namespace NimBus.CommandLine;

/// <summary>Parsers and platform helpers shared by the command modules.</summary>
internal static class CliHelpers
{
    internal static StorageProviderChoice ParseStorageProvider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return StorageProviderChoice.Cosmos;
        return value.ToLowerInvariant() switch
        {
            "cosmos" => StorageProviderChoice.Cosmos,
            "sqlserver" or "sql" or "sql-server" => StorageProviderChoice.SqlServer,
            _ => throw new InvalidOperationException($"Unknown --storage-provider value '{value}'. Expected 'cosmos' or 'sqlserver'."),
        };
    }

    internal static SqlProvisioningMode ParseSqlMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return SqlProvisioningMode.Provision;
        return value.ToLowerInvariant() switch
        {
            "provision" => SqlProvisioningMode.Provision,
            "external" => SqlProvisioningMode.External,
            _ => throw new InvalidOperationException($"Unknown --sql-mode value '{value}'. Expected 'provision' or 'external'."),
        };
    }

    // Parses the -f|--format option. Returns false (after printing an error) on an unknown value;
    // leaves 'format' null when the option is absent so the caller can infer from the output path.
    internal static readonly HttpClient PlatformHttpClient = new();

    /// <summary>
    /// Resolves the catalog to provision from, in precedence order: a NuGet package, a
    /// local assembly, or the platform compiled into this CLI. The package path is the one
    /// a customer pipeline uses — it needs neither a NimBus clone nor a build of their own
    /// solution (ADR-015).
    /// </summary>
    internal static async Task<Func<IPlatform>> ResolvePlatformFactoryAsync(
        string? assemblyPath,
        string? packageReference,
        string? feed,
        string? platformTypeName,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(packageReference))
        {
            if (!string.IsNullOrWhiteSpace(assemblyPath))
                throw new CommandException("Use either --assembly or --platform-package, not both.");

            var package = await PlatformPackage.ResolveAsync(
                PlatformHttpClient, packageReference, feed, platformTypeName, cancellationToken).ConfigureAwait(false);
            return package.CreateFactory();
        }

        return PlatformLoader.CreateFactory(assemblyPath, platformTypeName);
    }

    internal static bool TryParseFormat(CommandOption formatOption, out CoreAsyncApiFormat? format)
    {
        format = null;
        if (!formatOption.HasValue())
        {
            return true;
        }

        switch (formatOption.Value()!.ToLowerInvariant())
        {
            case "yaml":
            case "yml":
                format = CoreAsyncApiFormat.Yaml;
                return true;
            case "json":
                format = CoreAsyncApiFormat.Json;
                return true;
            default:
                AnsiConsole.MarkupLine($"[red]Unknown format '{formatOption.Value()}'. Use 'yaml' or 'json'.[/]");
                return false;
        }
    }
}
