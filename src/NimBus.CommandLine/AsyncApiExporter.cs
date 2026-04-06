using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace NimBus.CommandLine;

/// <summary>
/// Generates an AsyncAPI 3.0 specification from PlatformConfiguration.
/// Produces a YAML file documenting all channels (topics), operations (send/receive),
/// messages (event types with JSON Schema), and servers (Service Bus namespace).
/// </summary>
public static class AsyncApiExporter
{
    public static async Task ExportAsync(string outputPath)
    {
        var platform = new PlatformConfiguration();

        var allEventTypes = platform.Endpoints
            .SelectMany(ep => ep.EventTypesProduced.Concat(ep.EventTypesConsumed))
            .DistinctBy(et => et.Id)
            .ToList();

        var sb = new StringBuilder();

        // Header
        sb.AppendLine("asyncapi: 3.0.0");
        sb.AppendLine("info:");
        sb.AppendLine("  title: NimBus Platform");
        sb.AppendLine("  version: 1.0.0");
        sb.AppendLine("  description: |");
        sb.AppendLine("    Event-driven integration platform built on Azure Service Bus.");
        sb.AppendLine("    Auto-generated from NimBus PlatformConfiguration.");

        // Servers
        sb.AppendLine();
        sb.AppendLine("servers:");
        sb.AppendLine("  production:");
        sb.AppendLine("    host: '{namespace}.servicebus.windows.net'");
        sb.AppendLine("    protocol: amqp");
        sb.AppendLine("    description: Azure Service Bus namespace");

        // Channels (one per endpoint topic that produces events)
        sb.AppendLine();
        sb.AppendLine("channels:");
        foreach (var endpoint in platform.Endpoints.Where(ep => ep.EventTypesProduced.Any()))
        {
            sb.AppendLine($"  {endpoint.Id}:");
            sb.AppendLine($"    address: {endpoint.Id}");
            sb.AppendLine($"    description: Service Bus topic for {endpoint.Name}");
            sb.AppendLine("    messages:");
            foreach (var et in endpoint.EventTypesProduced)
            {
                sb.AppendLine($"      {et.Id}:");
                sb.AppendLine($"        $ref: '#/components/messages/{et.Id}'");
            }
        }

        // Operations
        sb.AppendLine();
        sb.AppendLine("operations:");

        // Send operations (producers)
        foreach (var endpoint in platform.Endpoints.Where(ep => ep.EventTypesProduced.Any()))
        {
            foreach (var et in endpoint.EventTypesProduced)
            {
                sb.AppendLine($"  {endpoint.Id}.send.{et.Id}:");
                sb.AppendLine("    action: send");
                sb.AppendLine($"    summary: {endpoint.Name} publishes {et.Name}");
                sb.AppendLine($"    channel:");
                sb.AppendLine($"      $ref: '#/channels/{endpoint.Id}'");
                sb.AppendLine("    messages:");
                sb.AppendLine($"      - $ref: '#/channels/{endpoint.Id}/messages/{et.Id}'");
            }
        }

        // Receive operations (consumers)
        foreach (var endpoint in platform.Endpoints.Where(ep => ep.EventTypesConsumed.Any()))
        {
            foreach (var et in endpoint.EventTypesConsumed)
            {
                // Find which endpoint's topic this event comes from
                var producer = platform.Endpoints
                    .FirstOrDefault(ep => ep.EventTypesProduced.Any(p => p.Id == et.Id));

                if (producer != null)
                {
                    sb.AppendLine($"  {endpoint.Id}.receive.{et.Id}:");
                    sb.AppendLine("    action: receive");
                    sb.AppendLine($"    summary: {endpoint.Name} subscribes to {et.Name}");
                    sb.AppendLine($"    channel:");
                    sb.AppendLine($"      $ref: '#/channels/{producer.Id}'");
                    sb.AppendLine("    messages:");
                    sb.AppendLine($"      - $ref: '#/channels/{producer.Id}/messages/{et.Id}'");
                }
            }
        }

        // Components
        sb.AppendLine();
        sb.AppendLine("components:");

        // Messages
        sb.AppendLine("  messages:");
        foreach (var et in allEventTypes)
        {
            var clrType = et.GetEventClassType();
            var desc = clrType?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? $"{et.Name} event";

            sb.AppendLine($"    {et.Id}:");
            sb.AppendLine($"      name: {et.Id}");
            sb.AppendLine($"      title: {et.Name}");
            sb.AppendLine($"      summary: {desc}");
            sb.AppendLine($"      contentType: application/json");
            sb.AppendLine($"      payload:");
            sb.AppendLine($"        $ref: '#/components/schemas/{et.Id}'");
        }

        // Schemas (JSON Schema from C# types)
        sb.AppendLine("  schemas:");
        foreach (var et in allEventTypes)
        {
            var clrType = et.GetEventClassType();
            if (clrType == null) continue;

            sb.AppendLine($"    {et.Id}:");
            sb.AppendLine("      type: object");
            sb.AppendLine($"      description: {et.Name} event payload");

            var properties = clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var requiredProps = properties
                .Where(p => p.GetCustomAttribute<RequiredAttribute>() != null)
                .Select(p => ToCamelCase(p.Name))
                .ToList();

            if (requiredProps.Any())
            {
                sb.AppendLine("      required:");
                foreach (var rp in requiredProps)
                    sb.AppendLine($"        - {rp}");
            }

            sb.AppendLine("      properties:");
            foreach (var prop in properties)
            {
                var propName = ToCamelCase(prop.Name);
                var propDesc = prop.GetCustomAttribute<DescriptionAttribute>()?.Description;
                var jsonType = MapClrTypeToJsonSchema(prop.PropertyType);

                sb.AppendLine($"        {propName}:");
                sb.AppendLine($"          type: {jsonType.type}");
                if (jsonType.format != null)
                    sb.AppendLine($"          format: {jsonType.format}");
                if (propDesc != null)
                    sb.AppendLine($"          description: {propDesc}");

                var rangeAttr = prop.GetCustomAttribute<RangeAttribute>();
                if (rangeAttr != null)
                {
                    sb.AppendLine($"          minimum: {rangeAttr.Minimum}");
                    sb.AppendLine($"          maximum: {rangeAttr.Maximum}");
                }
            }
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString());

        Console.WriteLine($"AsyncAPI spec exported to: {outputPath}");
        Console.WriteLine($"  {platform.Endpoints.Count()} services, {allEventTypes.Count} event types");
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static (string type, string format) MapClrTypeToJsonSchema(Type clrType)
    {
        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (underlying == typeof(string)) return ("string", null);
        if (underlying == typeof(Guid)) return ("string", "uuid");
        if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset)) return ("string", "date-time");
        if (underlying == typeof(bool)) return ("boolean", null);
        if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short)) return ("integer", null);
        if (underlying == typeof(decimal) || underlying == typeof(double) || underlying == typeof(float)) return ("number", null);

        return ("string", null);
    }
}
