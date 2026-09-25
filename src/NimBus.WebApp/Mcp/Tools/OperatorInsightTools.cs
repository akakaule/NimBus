using System.ComponentModel;
using ModelContextProtocol.Server;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Mcp.Operations;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Mcp.Tools;

/// <summary>
/// Read-only tools for site-wide search and metrics (which keep the REST site Reader floor)
/// and for stored AI failure classifications (advisory only; reading never starts an analysis).
/// </summary>
[McpServerToolType]
public sealed class OperatorInsightTools
{
    /// <summary>Longest example error text returned by the failure metrics.</summary>
    public const int MaxExampleErrorLength = 500;

    /// <summary>Most rows returned per metrics list.</summary>
    public const int MaxMetricRows = 200;

    private static readonly Dictionary<string, Period> Periods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1h"] = Period._1h, ["12h"] = Period._12h, ["1d"] = Period._1d,
        ["3d"] = Period._3d, ["7d"] = Period._7d, ["30d"] = Period._30d,
    };

    private static readonly string[] Views = ["throughput", "latency", "failures"];

    private readonly OperatorEndpointCatalog _catalog;
    private readonly OperatorQueries _queries;
    private readonly IOperatorClassificationSource _classifications;
    private readonly IEndpointAuthorizationService _authorization;

    /// <summary>Creates the tools for one request.</summary>
    public OperatorInsightTools(
        OperatorEndpointCatalog catalog,
        OperatorQueries queries,
        IOperatorClassificationSource classifications,
        IEndpointAuthorizationService authorization)
    {
        _catalog = catalog;
        _queries = queries;
        _classifications = classifications;
        _authorization = authorization;
    }

    /// <summary>Processing messages across all endpoints.</summary>
    [McpServerTool(Name = "nimbus_search_messages", Title = "Search NimBus messages", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Searches individual processing messages (requests, responses, retries) across all endpoints with typed filters, newest first. Requires a site-wide Reader role. Returns metadata and sanitized errors, never payloads. Pass nextCursor back as cursor, with the same filters, for the next page.")]
    public async Task<MessageSearchResult> SearchMessagesAsync(
        [Description("Only messages of this event id.")] string? eventId = null,
        [Description("Only the message with this message id.")] string? messageId = null,
        [Description("Only messages in this session.")] string? sessionId = null,
        [Description("Only messages of this event type id.")] string? eventTypeId = null,
        [Description("Only messages tracked by this endpoint.")] string? endpointId = null,
        [Description("Only messages sent by this endpoint.")] string? senderEndpoint = null,
        [Description("Only messages sent to this endpoint.")] string? receiverEndpoint = null,
        [Description("Only messages of this type, for example EventRequest, ErrorResponse, ResubmissionRequest or SkipRequest.")] string? messageType = null,
        [Description("Only messages enqueued at or after this UTC time (ISO 8601).")] DateTime? enqueuedFrom = null,
        [Description("Only messages enqueued at or before this UTC time (ISO 8601).")] DateTime? enqueuedTo = null,
        [Description("Page size, 1-200. Default 50.")] int? limit = null,
        [Description("nextCursor from the previous page of the same query.")] string? cursor = null)
    {
        MessageSearchFilterMessageType? type = null;
        if (!string.IsNullOrWhiteSpace(messageType))
        {
            if (!Enum.TryParse<MessageSearchFilterMessageType>(messageType, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                throw OperatorToolErrors.InvalidArgument($"Unknown messageType '{messageType}'. Use one of: {string.Join(", ", Enum.GetNames<MessageSearchFilterMessageType>())}.");
            type = parsed;
        }

        var pageSize = Math.Clamp(limit ?? OperatorMessageTools.DefaultLimit, 1, OperatorMessageTools.MaxLimit);
        var filter = new MessageSearchFilter
        {
            EventId = Trim(eventId),
            MessageId = Trim(messageId),
            SessionId = Trim(sessionId),
            EventTypeId = string.IsNullOrWhiteSpace(eventTypeId) ? [] : [eventTypeId.Trim()],
            EndpointId = Trim(endpointId),
            SenderEndpoint = Trim(senderEndpoint),
            ReceiverEndpoint = Trim(receiverEndpoint),
            MessageType = type,
            EnqueuedAtFrom = enqueuedFrom,
            EnqueuedAtTo = enqueuedTo,
        };

        var scope = string.Join('|', _authorization.GetCurrentUserName(), "search", filter.EventId, filter.MessageId, filter.SessionId,
            filter.EventTypeId.FirstOrDefault(), filter.EndpointId, filter.SenderEndpoint, filter.ReceiverEndpoint, type,
            enqueuedFrom?.ToString("O"), enqueuedTo?.ToString("O"), pageSize);
        var page = await _queries.SearchMessagesAsync(filter, pageSize, OperatorCursor.Decode(cursor, scope)).ConfigureAwait(false);

        var messages = (page.Messages ?? []).Select(message => new SearchedMessage(
            message.EventId,
            message.MessageId,
            message.EndpointId,
            message.MessageType.ToString(),
            message.EventTypeId,
            message.SessionId,
            message.From,
            message.To,
            OperatorProjection.Utc(message.EnqueuedTimeUtc),
            OperatorProjection.Error(message))).ToList();

        return new MessageSearchResult(_catalog.Environment, DateTimeOffset.UtcNow, pageSize, messages, OperatorCursor.Encode(page.ContinuationToken, scope));
    }

    /// <summary>Site-wide throughput, latency or failure metrics.</summary>
    [McpServerTool(Name = "nimbus_get_metrics", Title = "NimBus metrics", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Site-wide metrics for a recent period. view=throughput: published, handled and failed counts per endpoint and event type. view=latency: queue and processing times. view=failures: failures grouped by error pattern with an example. Requires a site-wide Reader role.")]
    public async Task<MetricsResult> GetMetricsAsync(
        [Description("throughput (default), latency or failures.")] string? view = null,
        [Description("1h, 12h, 1d (default), 3d, 7d or 30d.")] string? period = null)
    {
        var selectedView = string.IsNullOrWhiteSpace(view) ? "throughput" : view.Trim().ToLowerInvariant();
        if (!Views.Contains(selectedView))
            throw OperatorToolErrors.InvalidArgument($"Unknown view '{view}'. Use one of: {string.Join(", ", Views)}.");

        var periodName = string.IsNullOrWhiteSpace(period) ? "1d" : period.Trim();
        if (!Periods.TryGetValue(periodName, out var selectedPeriod))
            throw OperatorToolErrors.InvalidArgument($"Unknown period '{period}'. Use one of: {string.Join(", ", Periods.Keys)}.");

        ThroughputMetrics? throughput = null;
        IReadOnlyList<LatencyMetric>? latency = null;
        FailureMetrics? failures = null;

        switch (selectedView)
        {
            case "throughput":
                var overview = await _queries.GetThroughputAsync(selectedPeriod).ConfigureAwait(false);
                throughput = new ThroughputMetrics(Counts(overview.Published), Counts(overview.Handled), Counts(overview.Failed));
                break;
            case "latency":
                var latencies = await _queries.GetLatencyAsync(selectedPeriod).ConfigureAwait(false);
                latency = (latencies.Latencies ?? []).Take(MaxMetricRows)
                    .Select(l => new LatencyMetric(l.EndpointId, l.EventTypeId, Stats(l.Queue), Stats(l.Processing)))
                    .ToList();
                break;
            default:
                var insights = await _queries.GetFailureInsightsAsync(selectedPeriod).ConfigureAwait(false);
                failures = new FailureMetrics(
                    insights.TotalFailed,
                    (insights.Groups ?? []).Take(MaxMetricRows).Select(group =>
                    {
                        var (text, truncated) = OperatorProjection.Truncate(group.ExampleErrorText, MaxExampleErrorLength);
                        return new FailureGroup(group.ErrorCategory, group.Count, group.Endpoints ?? [], group.EventTypes ?? [],
                            OperatorProjection.Utc(group.LatestOccurrence), text, truncated);
                    }).ToList());
                break;
        }

        return new MetricsResult(_catalog.Environment, DateTimeOffset.UtcNow, selectedView, periodName, throughput, latency, failures);
    }

    /// <summary>The stored AI classification of one failed attempt.</summary>
    [McpServerTool(Name = "nimbus_get_classification", Title = "NimBus failure classification", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("The latest stored AI classification of a failed message: category, confidence, likelihoods and guidance, with provider, model and when it was made. Advisory only; it authorizes nothing. Defaults to the message's latest attempt. Reading never starts a new analysis.")]
    public async Task<ClassificationResult> GetClassificationAsync(
        [Description("Endpoint id, as returned by nimbus_list_endpoints.")] string endpointId,
        [Description("The message's event id.")] string eventId,
        [Description("The failed attempt's message id. Defaults to the latest attempt.")] string? messageId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_classifications.IsAvailable)
            throw OperatorToolErrors.FeatureUnavailable("AI failure classification is not enabled in this deployment.");

        var endpoint = await _catalog.RequireReadableAsync(endpointId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(eventId))
            throw OperatorToolErrors.InvalidArgument("eventId is required.");

        var id = eventId.Trim();
        var attempt = Trim(messageId);
        if (attempt is null)
        {
            var page = await _queries.SearchAsync(endpoint, new EventFilter { EndpointId = endpoint, EventId = id }, 1, null,
                () => OperatorToolErrors.MessageNotFound(endpoint, id)).ConfigureAwait(false);
            attempt = page.Events?.FirstOrDefault(e => string.Equals(e.EventId, id, StringComparison.Ordinal))?.LastMessageId
                ?? throw OperatorToolErrors.MessageNotFound(endpoint, id);
        }

        var classification = await _classifications.GetLatestAsync(id, attempt, cancellationToken).ConfigureAwait(false);
        if (classification is not null && !string.Equals(classification.EndpointId, endpoint, StringComparison.OrdinalIgnoreCase))
            throw OperatorToolErrors.MessageNotFound(endpoint, id);

        return new ClassificationResult(
            _catalog.Environment,
            DateTimeOffset.UtcNow,
            endpoint,
            id,
            attempt,
            classification is not null,
            true,
            classification is null ? null : new ClassificationInfo(
                classification.FailureMessageId,
                classification.Category,
                classification.CategoryConfidence,
                classification.RetryLikelihood,
                classification.ChangeRequiredLikelihood,
                classification.ExternalDependencyLikelihood,
                classification.Guidance.ToString(),
                classification.Provider,
                classification.Model,
                classification.Revision,
                classification.CreatedAtUtc,
                classification.EventPayloadIncluded));
    }

    private static List<MetricCount> Counts(ICollection<EndpointEventTypeMessageCount>? rows)
        => (rows ?? []).Take(MaxMetricRows).Select(r => new MetricCount(r.EndpointId, r.EventTypeId, r.Count)).ToList();

    private static TimingStats? Stats(LatencyStats? stats)
        => stats is null ? null : new TimingStats(stats.Count, stats.AvgMs, stats.MinMs, stats.MaxMs);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Result of <c>nimbus_search_messages</c>.</summary>
/// <param name="Environment">The configured NimBus environment name.</param>
/// <param name="AsOfUtc">When the result was produced.</param>
/// <param name="Limit">The page size applied.</param>
/// <param name="Messages">Matching messages, newest first.</param>
/// <param name="NextCursor">Cursor for the next page; null when there is none.</param>
public sealed record MessageSearchResult(string? Environment, DateTimeOffset AsOfUtc, int Limit, IReadOnlyList<SearchedMessage> Messages, string? NextCursor);

/// <summary>One processing message.</summary>
/// <param name="EventId">Event id.</param>
/// <param name="MessageId">Message id.</param>
/// <param name="EndpointId">Endpoint that tracks it.</param>
/// <param name="MessageType">Message type.</param>
/// <param name="EventTypeId">Event type id.</param>
/// <param name="SessionId">Session id.</param>
/// <param name="From">Sender.</param>
/// <param name="To">Receiver.</param>
/// <param name="EnqueuedTimeUtc">When it was enqueued.</param>
/// <param name="Error">Its error, if it is an error response.</param>
public sealed record SearchedMessage(
    string? EventId, string? MessageId, string? EndpointId, string MessageType, string? EventTypeId,
    string? SessionId, string? From, string? To, DateTime EnqueuedTimeUtc, MessageError? Error);

/// <summary>Result of <c>nimbus_get_metrics</c>; exactly one of the view properties is set.</summary>
/// <param name="Environment">The configured NimBus environment name.</param>
/// <param name="AsOfUtc">When the result was produced.</param>
/// <param name="View">throughput, latency or failures.</param>
/// <param name="Period">The period covered.</param>
/// <param name="Throughput">Counts, for the throughput view.</param>
/// <param name="Latency">Timings, for the latency view.</param>
/// <param name="Failures">Failure groups, for the failures view.</param>
public sealed record MetricsResult(
    string? Environment, DateTimeOffset AsOfUtc, string View, string Period,
    ThroughputMetrics? Throughput, IReadOnlyList<LatencyMetric>? Latency, FailureMetrics? Failures);

/// <summary>Message counts per endpoint and event type, at most 200 rows each.</summary>
/// <param name="Published">Published messages.</param>
/// <param name="Handled">Handled messages.</param>
/// <param name="Failed">Failed messages.</param>
public sealed record ThroughputMetrics(IReadOnlyList<MetricCount> Published, IReadOnlyList<MetricCount> Handled, IReadOnlyList<MetricCount> Failed);

/// <summary>A count for one endpoint and event type.</summary>
/// <param name="EndpointId">Endpoint id.</param>
/// <param name="EventTypeId">Event type id.</param>
/// <param name="Count">Message count.</param>
public sealed record MetricCount(string? EndpointId, string? EventTypeId, int Count);

/// <summary>Latency for one endpoint and event type.</summary>
/// <param name="EndpointId">Endpoint id.</param>
/// <param name="EventTypeId">Event type id.</param>
/// <param name="Queue">Time spent queued.</param>
/// <param name="Processing">Time spent processing.</param>
public sealed record LatencyMetric(string? EndpointId, string? EventTypeId, TimingStats? Queue, TimingStats? Processing);

/// <summary>Timing statistics in milliseconds.</summary>
/// <param name="Count">Samples.</param>
/// <param name="AvgMs">Average.</param>
/// <param name="MinMs">Minimum.</param>
/// <param name="MaxMs">Maximum.</param>
public sealed record TimingStats(int Count, double AvgMs, double MinMs, double MaxMs);

/// <summary>Failures grouped by error pattern.</summary>
/// <param name="TotalFailed">Failures in the period.</param>
/// <param name="Groups">Groups, at most 200.</param>
public sealed record FailureMetrics(int TotalFailed, IReadOnlyList<FailureGroup> Groups);

/// <summary>One error pattern.</summary>
/// <param name="ErrorCategory">Pattern category.</param>
/// <param name="Count">Failures in the group.</param>
/// <param name="Endpoints">Endpoints affected.</param>
/// <param name="EventTypes">Event types affected.</param>
/// <param name="LatestOccurrence">Most recent failure.</param>
/// <param name="ExampleErrorText">Example error text, untrusted, at most 500 characters.</param>
/// <param name="ExampleErrorTextTruncated">Whether the example was cut.</param>
public sealed record FailureGroup(
    string? ErrorCategory, int Count, ICollection<string> Endpoints, ICollection<string> EventTypes,
    DateTime LatestOccurrence, string? ExampleErrorText, bool ExampleErrorTextTruncated);

/// <summary>Result of <c>nimbus_get_classification</c>.</summary>
/// <param name="Environment">The configured NimBus environment name.</param>
/// <param name="AsOfUtc">When the result was produced.</param>
/// <param name="EndpointId">The endpoint.</param>
/// <param name="EventId">The message's event id.</param>
/// <param name="MessageId">The failed attempt looked up.</param>
/// <param name="Classified">Whether a stored classification exists.</param>
/// <param name="Advisory">Always true: a classification never authorizes an action.</param>
/// <param name="Classification">The classification, when one exists.</param>
public sealed record ClassificationResult(
    string? Environment, DateTimeOffset AsOfUtc, string EndpointId, string EventId, string MessageId,
    bool Classified, bool Advisory, ClassificationInfo? Classification);

/// <summary>A stored AI failure classification.</summary>
/// <param name="SourceMessageId">The failed attempt it classifies.</param>
/// <param name="Category">Failure category.</param>
/// <param name="CategoryConfidence">Confidence in the category, 0-1.</param>
/// <param name="RetryLikelihood">Likelihood a retry succeeds, 0-1.</param>
/// <param name="ChangeRequiredLikelihood">Likelihood a code or data change is needed, 0-1.</param>
/// <param name="ExternalDependencyLikelihood">Likelihood an external dependency is at fault, 0-1.</param>
/// <param name="Guidance">Uncertain, RetryMayHelp, ChangeLikelyRequired or Investigate.</param>
/// <param name="Provider">AI provider.</param>
/// <param name="Model">Model.</param>
/// <param name="Revision">Revision number.</param>
/// <param name="CreatedAtUtc">When it was made; it may be stale.</param>
/// <param name="EventPayloadIncluded">Whether the payload was sent to the provider.</param>
public sealed record ClassificationInfo(
    string SourceMessageId, string Category, double CategoryConfidence, double RetryLikelihood,
    double ChangeRequiredLikelihood, double ExternalDependencyLikelihood, string Guidance, string Provider,
    string Model, int Revision, DateTimeOffset CreatedAtUtc, bool EventPayloadIncluded);
