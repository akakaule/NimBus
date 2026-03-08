using System.Text.Json;
using System.Text.Json.Serialization;
using NimBus.Core.Messages;

namespace NimBus.CommandLine;

internal sealed class PlatformConfigExporter
{
    public async Task ExportAsync(string outputPath, CancellationToken cancellationToken)
    {
        var platform = new PlatformConfiguration();
        var config = new PlatformConfigJson
        {
            BrokerId = Constants.ManagerId,
            ResolverId = Constants.ResolverId,
            ManagerId = Constants.ManagerId,
            ContinuationId = Constants.ContinuationId,
            EventId = Constants.EventId,
            RetryId = Constants.RetryId,
            DeferredProcessorId = Constants.DeferredProcessorId,
            DeferredSubscriptionName = Constants.DeferredSubscriptionName,
            UserPropertyNames = new UserPropertyNamesJson
            {
                To = UserPropertyName.To.ToString(),
                From = UserPropertyName.From.ToString(),
                EventId = UserPropertyName.EventId.ToString(),
            },
            Endpoints = platform.Endpoints
                .Select(endpoint => new EndpointJson
                {
                    Id = endpoint.Id,
                    Name = endpoint.Name,
                    EventTypesConsumed = endpoint.EventTypesConsumed
                        .Select(eventType => new EventTypeJson { Id = eventType.Id, Name = eventType.Name })
                        .OrderBy(eventType => eventType.Id, StringComparer.Ordinal)
                        .ToList(),
                    EventTypesProduced = endpoint.EventTypesProduced
                        .Select(eventType => new EventTypeJson { Id = eventType.Id, Name = eventType.Name })
                        .OrderBy(eventType => eventType.Id, StringComparer.Ordinal)
                        .ToList(),
                })
                .OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal)
                .ToList(),
        };

        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, options);
        await File.WriteAllTextAsync(fullPath, json, cancellationToken).ConfigureAwait(false);
        CliOutput.WriteLine($"Platform configuration exported to '{fullPath}'.");
    }
}

internal sealed class PlatformConfigJson
{
    public string BrokerId { get; set; } = string.Empty;
    public string ResolverId { get; set; } = string.Empty;
    public string ManagerId { get; set; } = string.Empty;
    public string ContinuationId { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string RetryId { get; set; } = string.Empty;
    public string DeferredProcessorId { get; set; } = string.Empty;
    public string DeferredSubscriptionName { get; set; } = string.Empty;
    public UserPropertyNamesJson UserPropertyNames { get; set; } = new();
    public List<EndpointJson> Endpoints { get; set; } = new();
}

internal sealed class UserPropertyNamesJson
{
    public string To { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
}

internal sealed class EndpointJson
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<EventTypeJson> EventTypesConsumed { get; set; } = new();
    public List<EventTypeJson> EventTypesProduced { get; set; } = new();
}

internal sealed class EventTypeJson
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
