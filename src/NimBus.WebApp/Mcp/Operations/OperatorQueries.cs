using Microsoft.AspNetCore.Mvc;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>
/// Read operations for the operator tools, served in-process by the same implementations as
/// the REST API. Endpoint Reader checks, PII redaction and audit rows therefore behave exactly
/// as they do for the Web UI. This class is the seam: tools depend on it rather than on the
/// controllers, so shared logic can later move behind it without changing tool contracts.
/// </summary>
public sealed class OperatorQueries
{
    private readonly IEndpointApiController _endpoints;
    private readonly IEventApiController _events;
    private readonly IMonitorApiController _monitor;

    /// <summary>Creates the queries for one request.</summary>
    public OperatorQueries(IEndpointApiController endpoints, IEventApiController events, IMonitorApiController monitor)
    {
        _endpoints = endpoints;
        _events = events;
        _monitor = monitor;
    }

    /// <summary>Status counts for every endpoint the caller can read.</summary>
    public async Task<IReadOnlyList<EndpointStatusCount>> GetStatusCountsAsync()
        => Unwrap(await _endpoints.GetEndpointStatusCountAllAsync().ConfigureAwait(false),
            () => OperatorToolErrors.SourceUnavailable("Endpoint status counts are unavailable.")).ToList();

    /// <summary>Status counts for one endpoint.</summary>
    public async Task<EndpointStatusCount> GetStatusCountAsync(string endpointId)
        => Unwrap(await _endpoints.GetEndpointStatusCountIdAsync(endpointId).ConfigureAwait(false),
            () => OperatorToolErrors.EndpointNotFound(endpointId));

    /// <summary>Active Monitor acknowledgements on endpoints the caller can read.</summary>
    public async Task<IReadOnlyList<MonitorAcknowledgement>> GetAcknowledgementsAsync()
        => Unwrap(await _monitor.GetMonitorAcknowledgementsAsync().ConfigureAwait(false),
            () => OperatorToolErrors.SourceUnavailable("Monitor acknowledgements are unavailable.")).ToList();

    /// <summary>One page of tracked messages on an endpoint matching <paramref name="filter"/>.</summary>
    /// <param name="endpointId">Canonical endpoint id.</param>
    /// <param name="filter">Typed filter.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="continuationToken">Store continuation token, or null for the first page.</param>
    /// <param name="notFound">Error for a refused search; defaults to <see cref="OperatorToolErrors.EndpointNotFound"/>.</param>
    public async Task<SearchResponse> SearchAsync(string endpointId, EventFilter filter, int limit, string? continuationToken, Func<Exception>? notFound = null)
    {
        var request = new SearchRequest { EventFilter = filter, MaxSearchItemsCount = limit, ContinuationToken = continuationToken };
        return Unwrap(await _events.PostApiEventEndpointIdGetByFilterAsync(request, endpointId).ConfigureAwait(false),
            notFound ?? (() => OperatorToolErrors.EndpointNotFound(endpointId)));
    }

    /// <summary>The failed and originating messages of one event.</summary>
    public async Task<EventDetails> GetEventDetailsAsync(string endpointId, string eventId)
        => Unwrap(await _events.GetEventDetailsIdAsync(eventId, endpointId).ConfigureAwait(false),
            () => OperatorToolErrors.MessageNotFound(endpointId, eventId));

    /// <summary>Processing attempts of one event on an endpoint, newest first.</summary>
    public async Task<IReadOnlyList<Message>> GetEventHistoryAsync(string endpointId, string eventId)
        => Unwrap(await _events.GetEventDetailsHistoryIdAsync(eventId, endpointId).ConfigureAwait(false),
            () => OperatorToolErrors.MessageNotFound(endpointId, eventId)).ToList();

    /// <summary>Processing log entries of one event on an endpoint.</summary>
    public async Task<IReadOnlyList<EventLogEntry>> GetEventLogsAsync(string endpointId, string eventId)
        => Unwrap(await _events.GetEventDetailsLogsIdAsync(eventId, endpointId).ConfigureAwait(false),
            () => OperatorToolErrors.MessageNotFound(endpointId, eventId)).ToList();

    /// <summary>Pending and deferred events of one session on an endpoint.</summary>
    public async Task<SessionStatus> GetSessionAsync(string endpointId, string sessionId)
        => Unwrap(await _endpoints.GetEndpointSessionIdAsync(endpointId, sessionId).ConfigureAwait(false),
            () => OperatorToolErrors.MessageNotFound(endpointId, sessionId));

    // Success returns the value. A 400 is surfaced as an invalid argument; every other
    // failure, including 403, becomes the caller's not-found error, so a tool never reveals
    // whether a resource the caller cannot read exists.
    private static T Unwrap<T>(ActionResult<T> result, Func<Exception> notFound)
    {
        if (result.Value is not null)
            return result.Value;

        return result.Result switch
        {
            ObjectResult { StatusCode: null or >= 200 and < 300, Value: T value } => value,
            BadRequestObjectResult bad => throw OperatorToolErrors.InvalidArgument(bad.Value?.ToString() ?? "The request was rejected."),
            BadRequestResult => throw OperatorToolErrors.InvalidArgument("The request was rejected."),
            _ => throw notFound(),
        };
    }
}
