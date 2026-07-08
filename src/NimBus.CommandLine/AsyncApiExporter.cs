using System;
using System.Threading.Tasks;
using NimBus.Core;
using ServiceBusExporter = NimBus.ServiceBus.AsyncApi.AsyncApiExporter;
using ServiceBusFormat = NimBus.ServiceBus.AsyncApi.AsyncApiFormat;

namespace NimBus.CommandLine;

/// <summary>
/// Output format for <see cref="AsyncApiExporter"/>.
/// </summary>
/// <remarks>
/// Moved to <see cref="NimBus.ServiceBus.AsyncApi.AsyncApiFormat"/> so the AsyncAPI exporter can be
/// reused by the WebApp. Kept here as a backward-compatible bridge for existing references.
/// </remarks>
[Obsolete("Use NimBus.ServiceBus.AsyncApi.AsyncApiFormat instead. This bridge type is kept for backward compatibility.")]
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
    private static ServiceBusFormat Map(AsyncApiFormat format) =>
        format == AsyncApiFormat.Json ? ServiceBusFormat.Json : ServiceBusFormat.Yaml;

    private static AsyncApiFormat FormatFromPath(string path) =>
        path is not null && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? AsyncApiFormat.Json
            : AsyncApiFormat.Yaml;

    /// <summary>
    /// Back-compatible entry point used by <c>nb catalog asyncapi</c>: exports the built-in
    /// platform, inferring the format from the output extension (<c>.json</c> ⇒ JSON, else YAML).
    /// </summary>
    public static Task ExportAsync(string outputPath) =>
        ExportAsync(outputPath, FormatFromPath(outputPath));

    /// <summary>Exports the built-in platform in the requested format.</summary>
    public static Task ExportAsync(string outputPath, AsyncApiFormat format) =>
        ServiceBusExporter.ExportAsync(new PlatformConfiguration(), outputPath, Map(format));

    /// <summary>Exports an arbitrary platform (external integration repos, samples, tests).</summary>
    public static Task ExportAsync(IPlatform platform, string outputPath, AsyncApiFormat format) =>
        ServiceBusExporter.ExportAsync(platform, outputPath, Map(format));

    /// <summary>Builds the AsyncAPI document for <paramref name="platform"/> and serializes it.</summary>
    public static string Serialize(IPlatform platform, AsyncApiFormat format) =>
        ServiceBusExporter.Serialize(platform, Map(format));
}
