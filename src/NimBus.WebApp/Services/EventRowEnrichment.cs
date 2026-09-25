using Microsoft.Extensions.Logging;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services;

/// <summary>
/// Fills the display-only columns of event search rows (resubmit count, reported marker) in
/// one batched store lookup per endpoint. Fail-soft: an enrichment failure is logged and the
/// rows are returned without it, because search must not break over a display nicety.
/// </summary>
public static class EventRowEnrichment
{
    /// <summary>Attaches resubmit counts and report flags to <paramref name="events"/>, all on <paramref name="endpointId"/>.</summary>
    public static async Task AttachAsync(IMessageTrackingStore store, ILogger logger, string endpointId, List<Event> events)
    {
        await AttachResubmitCountsAsync(store, logger, endpointId, events);
        await AttachReportFlagsAsync(store, logger, endpointId, events);
    }

    /// <summary>
    /// Fills each event's ResubmitCount from the audit log, so the event list can show how
    /// many times an event was resubmitted without a per-row round-trip.
    /// </summary>
    public static async Task AttachResubmitCountsAsync(IMessageTrackingStore store, ILogger logger, string endpointId, List<Event> events)
    {
        var eventIds = DistinctEventIds(events);
        if (eventIds.Count == 0) return;

        try
        {
            var counts = await store.GetResubmitCounts(endpointId, eventIds);
            foreach (var ev in events)
            {
                if (ev.EventId != null && counts.TryGetValue(ev.EventId, out var count))
                    ev.ResubmitCount = count;
            }
        }
        catch (Exception e)
        {
            logger.LogWarning("AttachResubmitCounts failed for endpoint {EndpointId}: {Exception}", endpointId, e.Message);
        }
    }

    /// <summary>Fills each event's "reported" marker (flag + who/when + ticket) from the report store.</summary>
    public static async Task AttachReportFlagsAsync(IMessageTrackingStore store, ILogger logger, string endpointId, List<Event> events)
    {
        var eventIds = DistinctEventIds(events);
        if (eventIds.Count == 0) return;

        try
        {
            var reports = await store.GetEventReports(endpointId, eventIds);
            foreach (var ev in events)
            {
                if (ev.EventId != null && reports.TryGetValue(ev.EventId, out var report))
                {
                    ev.IsReported = report.IsReported;
                    ev.ReportedBy = report.ReportedBy;
                    ev.ReportedAtUtc = report.ReportedAtUtc;
                    ev.TicketId = report.TicketId;
                }
            }
        }
        catch (Exception e)
        {
            logger.LogWarning("AttachReportFlags failed for endpoint {EndpointId}: {Exception}", endpointId, e.Message);
        }
    }

    private static List<string> DistinctEventIds(List<Event> events) =>
        events
            .Select(e => e.EventId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList()!;
}
