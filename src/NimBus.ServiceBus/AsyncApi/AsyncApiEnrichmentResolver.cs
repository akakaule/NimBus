using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using NimBus.Core.Events;

namespace NimBus.ServiceBus.AsyncApi;

/// <summary>
/// Merges <see cref="AsyncApiMessageAttribute"/> (attribute) and <see cref="AsyncApiMessageOptions"/>
/// (fluent, via <see cref="AsyncApiEnrichmentRegistry"/>) enrichment for one event contract, shared
/// by the AsyncAPI and EventCatalog exporters so both surface identical resolved values.
/// Merge order for scalars: fluent ?? attribute ?? derived default. Tags are unioned
/// (first-seen, de-duped Ordinal); deprecated is OR-ed.
/// </summary>
internal static class AsyncApiEnrichmentResolver
{
    internal static ResolvedAsyncApiEnrichment Resolve(
        IEventType evt,
        Type? clrType,
        AsyncApiEnrichmentRegistry? enrichment)
    {
        var attribute = clrType?.GetCustomAttribute<AsyncApiMessageAttribute>();
        var typeDescription = clrType?.GetCustomAttribute<DescriptionAttribute>()?.Description;

        AsyncApiMessageOptions? fluent = null;
        if (clrType != null) enrichment?.TryGet(clrType, out fluent);

        var tags = new List<string>();
        var seenTags = new HashSet<string>(StringComparer.Ordinal);
        void AddTags(IEnumerable<string>? source)
        {
            if (source is null) return;
            foreach (var tag in source)
            {
                if (!string.IsNullOrEmpty(tag) && seenTags.Add(tag)) tags.Add(tag);
            }
        }

        AddTags(attribute?.Tags);
        AddTags(fluent?.Tags);

        return new ResolvedAsyncApiEnrichment
        {
            Name = fluent?.Name ?? attribute?.Name,
            Title = fluent?.Title ?? attribute?.Title ?? evt.Name,
            Summary = fluent?.Summary ?? attribute?.Summary ?? typeDescription ?? $"{evt.Name} event.",
            Description = fluent?.Description ?? attribute?.Description,
            Owner = fluent?.Owner ?? attribute?.Owner,
            Team = fluent?.Team ?? attribute?.Team,
            BusinessCapability = fluent?.BusinessCapability ?? attribute?.BusinessCapability,
            Version = fluent?.Version ?? attribute?.Version,
            ExternalDocsUrl = fluent?.ExternalDocsUrl ?? attribute?.ExternalDocsUrl,
            ExternalDocsDescription = fluent?.ExternalDocsDescription ?? attribute?.ExternalDocsDescription,
            Deprecated = (attribute?.Deprecated ?? false) || (fluent?.Deprecated ?? false),
            Tags = tags,
            Examples = fluent?.Examples?.ToList() ?? new List<AsyncApiMessageExample>(),
        };
    }
}

/// <summary>The merged enrichment values for one event contract.</summary>
internal sealed class ResolvedAsyncApiEnrichment
{
    public string? Name { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Owner { get; init; }
    public string? Team { get; init; }
    public string? BusinessCapability { get; init; }
    public string? Version { get; init; }
    public string? ExternalDocsUrl { get; init; }
    public string? ExternalDocsDescription { get; init; }
    public bool Deprecated { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<AsyncApiMessageExample> Examples { get; init; } = Array.Empty<AsyncApiMessageExample>();
}
