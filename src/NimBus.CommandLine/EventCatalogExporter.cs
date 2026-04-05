using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NimBus.CommandLine;

/// <summary>
/// Generates an EventCatalog-compatible directory structure from PlatformConfiguration.
/// Creates markdown files for domains, services, events, and channels that can be
/// consumed by EventCatalog (https://www.eventcatalog.dev/).
/// </summary>
public static class EventCatalogExporter
{
    public static async Task ExportAsync(string outputPath)
    {
        var platform = new PlatformConfiguration();

        Directory.CreateDirectory(outputPath);

        // Collect all event types across the platform
        var allEventTypes = platform.Endpoints
            .SelectMany(ep => ep.EventTypesProduced.Concat(ep.EventTypesConsumed))
            .DistinctBy(et => et.Id)
            .ToList();

        // Group endpoints by system for domains
        var domains = platform.Endpoints
            .GroupBy(ep => ep.System.SystemId)
            .ToList();

        // Write domains
        foreach (var domain in domains)
        {
            await WriteDomain(outputPath, domain.Key, domain.ToList(), platform);
        }

        // Write services (endpoints)
        foreach (var endpoint in platform.Endpoints)
        {
            await WriteService(outputPath, endpoint);
        }

        // Write events
        foreach (var eventType in allEventTypes)
        {
            var producers = platform.Endpoints
                .Where(ep => ep.EventTypesProduced.Any(et => et.Id == eventType.Id))
                .ToList();
            var consumers = platform.Endpoints
                .Where(ep => ep.EventTypesConsumed.Any(et => et.Id == eventType.Id))
                .ToList();

            await WriteEvent(outputPath, eventType, producers, consumers);
        }

        // Write channels (one per endpoint topic)
        foreach (var endpoint in platform.Endpoints)
        {
            if (endpoint.EventTypesProduced.Any())
            {
                await WriteChannel(outputPath, endpoint);
            }
        }

        Console.WriteLine($"EventCatalog exported to: {outputPath}");
        Console.WriteLine($"  {domains.Count} domains, {platform.Endpoints.Count()} services, {allEventTypes.Count} events");
    }

    private static async Task WriteDomain(string outputPath, string systemId, List<IEndpoint> endpoints, IPlatform platform)
    {
        var domainDir = Path.Combine(outputPath, "domains", systemId);
        Directory.CreateDirectory(domainDir);

        var sends = endpoints
            .SelectMany(ep => ep.EventTypesProduced)
            .DistinctBy(et => et.Id)
            .ToList();

        var receives = endpoints
            .SelectMany(ep => ep.EventTypesConsumed)
            .DistinctBy(et => et.Id)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {systemId}");
        sb.AppendLine($"name: {systemId} Domain");
        sb.AppendLine("version: 1.0.0");
        sb.AppendLine("summary: |");
        sb.AppendLine($"  Domain for the {systemId} system");

        // Services
        sb.AppendLine("services:");
        foreach (var ep in endpoints)
        {
            sb.AppendLine($"  - id: {ep.Id}");
        }

        // Sends
        if (sends.Any())
        {
            sb.AppendLine("sends:");
            foreach (var et in sends)
            {
                sb.AppendLine($"  - id: {et.Id}");
                sb.AppendLine("    version: 1.0.0");
            }
        }

        // Receives
        if (receives.Any())
        {
            sb.AppendLine("receives:");
            foreach (var et in receives)
            {
                sb.AppendLine($"  - id: {et.Id}");
                sb.AppendLine("    version: 1.0.0");
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {systemId} Domain");
        sb.AppendLine();

        foreach (var ep in endpoints)
        {
            sb.AppendLine($"- **{ep.Name}**: {ep.Description}");
        }

        await File.WriteAllTextAsync(Path.Combine(domainDir, "index.mdx"), sb.ToString());
    }

    private static async Task WriteService(string outputPath, IEndpoint endpoint)
    {
        var serviceDir = Path.Combine(outputPath, "services", endpoint.Id);
        Directory.CreateDirectory(serviceDir);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {endpoint.Id}");
        sb.AppendLine($"name: {endpoint.Name}");
        sb.AppendLine("version: 1.0.0");
        sb.AppendLine("summary: |");
        sb.AppendLine($"  {endpoint.Description}");

        // Sends (produced event types)
        if (endpoint.EventTypesProduced.Any())
        {
            sb.AppendLine("sends:");
            foreach (var et in endpoint.EventTypesProduced)
            {
                sb.AppendLine($"  - id: {et.Id}");
                sb.AppendLine("    version: 1.0.0");
                sb.AppendLine("    to:");
                sb.AppendLine($"      - id: {endpoint.Id}.events");
            }
        }

        // Receives (consumed event types)
        if (endpoint.EventTypesConsumed.Any())
        {
            sb.AppendLine("receives:");
            foreach (var et in endpoint.EventTypesConsumed)
            {
                // Find which endpoint produces this event type to determine the channel
                sb.AppendLine($"  - id: {et.Id}");
                sb.AppendLine("    version: 1.0.0");
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {endpoint.Name}");
        sb.AppendLine();
        sb.AppendLine(endpoint.Description);
        sb.AppendLine();
        sb.AppendLine($"**System:** {endpoint.System.SystemId}");

        if (endpoint.EventTypesProduced.Any())
        {
            sb.AppendLine();
            sb.AppendLine("## Produces");
            foreach (var et in endpoint.EventTypesProduced)
                sb.AppendLine($"- `{et.Id}` ({et.Name})");
        }

        if (endpoint.EventTypesConsumed.Any())
        {
            sb.AppendLine();
            sb.AppendLine("## Consumes");
            foreach (var et in endpoint.EventTypesConsumed)
                sb.AppendLine($"- `{et.Id}` ({et.Name})");
        }

        await File.WriteAllTextAsync(Path.Combine(serviceDir, "index.mdx"), sb.ToString());
    }

    private static async Task WriteEvent(string outputPath, IEventType eventType, List<IEndpoint> producers, List<IEndpoint> consumers)
    {
        var eventDir = Path.Combine(outputPath, "events", eventType.Id);
        Directory.CreateDirectory(eventDir);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {eventType.Id}");
        sb.AppendLine($"name: {eventType.Name}");
        sb.AppendLine("version: 1.0.0");
        sb.AppendLine("summary: |");
        sb.AppendLine($"  {eventType.Name} event");

        if (producers.Any())
        {
            sb.AppendLine("producers:");
            foreach (var ep in producers)
            {
                sb.AppendLine($"  - id: {ep.Id}");
            }
        }

        if (consumers.Any())
        {
            sb.AppendLine("consumers:");
            foreach (var ep in consumers)
            {
                sb.AppendLine($"  - id: {ep.Id}");
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {eventType.Name}");
        sb.AppendLine();

        if (producers.Any())
        {
            sb.AppendLine($"Published by: {string.Join(", ", producers.Select(p => $"**{p.Name}**"))}");
            sb.AppendLine();
        }

        if (consumers.Any())
        {
            sb.AppendLine($"Consumed by: {string.Join(", ", consumers.Select(c => $"**{c.Name}**"))}");
            sb.AppendLine();
        }

        sb.AppendLine("## Message Flow");
        sb.AppendLine();
        sb.AppendLine("```");
        foreach (var producer in producers)
        {
            foreach (var consumer in consumers)
            {
                sb.AppendLine($"{producer.Name} --({eventType.Id})--> {consumer.Name}");
            }
        }
        sb.AppendLine("```");

        await File.WriteAllTextAsync(Path.Combine(eventDir, "index.mdx"), sb.ToString());
    }

    private static async Task WriteChannel(string outputPath, IEndpoint endpoint)
    {
        var channelId = $"{endpoint.Id}.events";
        var channelDir = Path.Combine(outputPath, "channels", channelId);
        Directory.CreateDirectory(channelDir);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {channelId}");
        sb.AppendLine($"name: {endpoint.Name} Events");
        sb.AppendLine("version: 1.0.0");
        sb.AppendLine("summary: |");
        sb.AppendLine($"  Azure Service Bus topic for {endpoint.Name} events");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {endpoint.Name} Events Channel");
        sb.AppendLine();
        sb.AppendLine($"Azure Service Bus topic `{endpoint.Id}` carrying events produced by {endpoint.Name}.");
        sb.AppendLine();
        sb.AppendLine("## Event Types");
        foreach (var et in endpoint.EventTypesProduced)
        {
            sb.AppendLine($"- `{et.Id}`");
        }

        await File.WriteAllTextAsync(Path.Combine(channelDir, "index.mdx"), sb.ToString());
    }
}
