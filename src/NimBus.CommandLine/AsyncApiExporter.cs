using System;
using System.Threading.Tasks;
using NimBus.Core;
using NimBus.Core.Events;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using ServiceBusExporter = NimBus.ServiceBus.AsyncApi.AsyncApiExporter;

namespace NimBus.CommandLine;

/// <summary>
/// Output format for <see cref="AsyncApiExporter"/>.
/// </summary>
/// <remarks>
/// Use <see cref="NimBus.Core.Events.AsyncApiFormat"/> for new code. This bridge remains so callers
/// that adopted the original command-line exporter API can migrate without losing source compatibility.
/// </remarks>
[Obsolete("Use NimBus.Core.Events.AsyncApiFormat instead. This bridge type is kept for backward compatibility.")]
public enum AsyncApiFormat
{
    /// <summary>AsyncAPI 3.0 as YAML (default).</summary>
    Yaml,

    /// <summary>AsyncAPI 3.0 as JSON.</summary>
    Json,
}

/// <summary>
/// Generates an AsyncAPI 3.0 document from an <see cref="IPlatform"/>.
/// </summary>
/// <remarks>
/// The implementation moved to <see cref="NimBus.ServiceBus.AsyncApi.AsyncApiExporter"/> so it can be
/// reused by the WebApp's admin export endpoint. This type is kept as a thin, backward-compatible
/// bridge that forwards to the new location; the produced document is byte-for-byte identical.
/// </remarks>
[Obsolete("Use NimBus.ServiceBus.AsyncApi.AsyncApiExporter instead. This bridge type is kept for backward compatibility.")]
public static class AsyncApiExporter
{
    private static CoreAsyncApiFormat Map(AsyncApiFormat format) =>
        format == AsyncApiFormat.Json ? CoreAsyncApiFormat.Json : CoreAsyncApiFormat.Yaml;

    private static CoreAsyncApiFormat FormatFromPath(string path) =>
        path is not null && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? CoreAsyncApiFormat.Json
            : CoreAsyncApiFormat.Yaml;

    /// <summary>
    /// Back-compatible entry point used by <c>nb catalog asyncapi</c>: exports the built-in
    /// platform, inferring the format from the output extension (<c>.json</c> =&gt; JSON, else YAML).
    /// </summary>
    public static Task ExportAsync(string outputPath) =>
        ServiceBusExporter.ExportAsync(new PlatformConfiguration(), outputPath, FormatFromPath(outputPath));

    /// <summary>Exports the built-in platform in the requested format.</summary>
    public static Task ExportAsync(string outputPath, CoreAsyncApiFormat format) =>
        ServiceBusExporter.ExportAsync(new PlatformConfiguration(), outputPath, format);

    /// <summary>Exports the built-in platform in the requested format.</summary>
    [Obsolete("Use the overload that accepts NimBus.Core.Events.AsyncApiFormat instead.")]
    public static Task ExportAsync(string outputPath, AsyncApiFormat format) =>
        ExportAsync(outputPath, Map(format));

    /// <summary>Exports an arbitrary platform (external integration repos, samples, tests).</summary>
    public static Task ExportAsync(
        IPlatform platform,
        string outputPath,
        CoreAsyncApiFormat format,
        AsyncApiEnrichmentRegistry? enrichment = null) =>
        ServiceBusExporter.ExportAsync(platform, outputPath, format, enrichment);

    /// <summary>Exports an arbitrary platform (external integration repos, samples, tests).</summary>
    [Obsolete("Use the overload that accepts NimBus.Core.Events.AsyncApiFormat instead.")]
    public static Task ExportAsync(IPlatform platform, string outputPath, AsyncApiFormat format) =>
        ExportAsync(platform, outputPath, Map(format));

    /// <summary>Builds the AsyncAPI document for <paramref name="platform"/> and serializes it.</summary>
    public static string Serialize(
        IPlatform platform,
        CoreAsyncApiFormat format,
        AsyncApiEnrichmentRegistry? enrichment = null) =>
        ServiceBusExporter.Serialize(platform, format, enrichment);

    /// <summary>Builds the AsyncAPI document for <paramref name="platform"/> and serializes it.</summary>
    [Obsolete("Use the overload that accepts NimBus.Core.Events.AsyncApiFormat instead.")]
    public static string Serialize(IPlatform platform, AsyncApiFormat format, AsyncApiEnrichmentRegistry? enrichment = null) =>
        Serialize(platform, Map(format), enrichment);
}
