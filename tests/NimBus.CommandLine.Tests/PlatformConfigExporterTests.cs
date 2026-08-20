#pragma warning disable CA1707, CA2007
using System.Text.Json;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using Xunit;

namespace NimBus.CommandLine.Tests;

// PlatformConfigExporter backs `nb topology export`. With no factory it must keep
// exporting the built-in PlatformConfiguration; with an injected factory (the
// --assembly/--platform path) the JSON must describe the injected platform instead.
public sealed class PlatformConfigExporterTests
{
    [Fact]
    public async Task ExportAsync_Default_ExportsTheBuiltInPlatformConfiguration()
    {
        var outputPath = MakeOutputPath();
        try
        {
            await new PlatformConfigExporter().ExportAsync(outputPath, CancellationToken.None);

            var exportedIds = ReadEndpointIds(outputPath);
            var builtInIds = new PlatformConfiguration().Endpoints
                .Select(endpoint => endpoint.Id)
                .OrderBy(id => id, StringComparer.Ordinal);

            Assert.Equal(builtInIds, exportedIds);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ExportAsync_WithInjectedPlatformFactory_ExportsThatPlatform()
    {
        var outputPath = MakeOutputPath();
        try
        {
            var platform = new ExporterTestPlatform(new ExporterTestEndpoint("ExternalEndpoint"));
            await new PlatformConfigExporter(() => platform).ExportAsync(outputPath, CancellationToken.None);

            var exportedIds = ReadEndpointIds(outputPath);

            Assert.Equal(new[] { "ExternalEndpoint" }, exportedIds);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static string MakeOutputPath() =>
        Path.Combine(Path.GetTempPath(), $"platform-config-{Guid.NewGuid():N}.json");

    private static IReadOnlyList<string> ReadEndpointIds(string outputPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        return document.RootElement.GetProperty("endpoints")
            .EnumerateArray()
            .Select(endpoint => endpoint.GetProperty("id").GetString()!)
            .ToList();
    }

    private sealed class ExporterTestPlatform : Platform
    {
        public ExporterTestPlatform(params IEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                AddEndpoint(endpoint);
            }
        }
    }

    private sealed class ExporterTestEndpoint : IEndpoint
    {
        public ExporterTestEndpoint(string id)
        {
            Id = id;
            Name = id;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => Array.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesConsumed => Array.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();
    }
}
