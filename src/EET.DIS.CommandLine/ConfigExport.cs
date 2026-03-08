using System.Text.Json;
using System.Text.Json.Serialization;
using EET.DIS.Core.Messages;
using McMaster.Extensions.CommandLineUtils;

namespace EET.DIS.CommandLine;

/// <summary>
/// Exports platform configuration to JSON for use by PowerShell deployment scripts.
/// This replaces the need to load .NET assemblies in PowerShell, enabling .NET 10 compatibility.
/// </summary>
static class ConfigExport
{
    public static async Task Export(CommandOption outputPath)
    {
        var platform = new PlatformConfiguration();

        var config = new PlatformConfigJson
        {
            ResolverId = Constants.ResolverId,
            ManagerId = Constants.ManagerId,
            ContinuationId = Constants.ContinuationId,
            EventId = Constants.EventId,
            RetryId = Constants.RetryId,
            UserPropertyNames = new UserPropertyNamesJson
            {
                To = Core.Messages.UserPropertyName.To.ToString(),
                From = Core.Messages.UserPropertyName.From.ToString(),
                EventId = Core.Messages.UserPropertyName.EventId.ToString()
            },
            Endpoints = platform.Endpoints.Select(e => new EndpointJson
            {
                Id = e.Id,
                Name = e.Name,
                EventTypesConsumed = e.EventTypesConsumed.Select(et => new EventTypeJson
                {
                    Id = et.Id,
                    Name = et.Name
                }).ToList(),
                EventTypesProduced = e.EventTypesProduced.Select(et => new EventTypeJson
                {
                    Id = et.Id,
                    Name = et.Name
                }).ToList(),
            }).ToList()
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(config, options);

        var path = outputPath.HasValue()
            ? outputPath.Value()!
            : "platform-config.json";

        await File.WriteAllTextAsync(path, json);

        Console.WriteLine($"Platform configuration exported to: {path}");
    }
}

#region JSON Models

public class PlatformConfigJson
{
    public string ResolverId { get; set; } = string.Empty;
    public string ManagerId { get; set; } = string.Empty;
    public string ContinuationId { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string RetryId { get; set; } = string.Empty;
    public UserPropertyNamesJson UserPropertyNames { get; set; } = new();
    public List<EndpointJson> Endpoints { get; set; } = new();
}

public class UserPropertyNamesJson
{
    public string To { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
}

public class EndpointJson
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<EventTypeJson> EventTypesConsumed { get; set; } = new();
    public List<EventTypeJson> EventTypesProduced { get; set; } = new();
}

public class EventTypeJson
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

#endregion
