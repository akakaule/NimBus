using Microsoft.Azure.Cosmos;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore;

internal sealed partial class CosmosDbMessageTrackingStore
{
    public async Task SetEventReport(string endpointId, string eventId, bool isReported, string? reportedBy, string? ticketId)
    {
        if (string.IsNullOrEmpty(endpointId)) throw new ArgumentNullException(nameof(endpointId));
        if (string.IsNullOrEmpty(eventId)) throw new ArgumentNullException(nameof(eventId));

        var report = new EventReport
        {
            Id = $"{endpointId}_{eventId}",
            EndpointId = endpointId,
            EventId = eventId,
            IsReported = isReported,
            ReportedBy = reportedBy,
            ReportedAtUtc = DateTime.UtcNow,
            // Only retain a ticket reference while the event is reported; clearing
            // the marker drops the ticket too.
            TicketId = isReported ? ticketId : null,
        };

        var container = await _getEventReportsContainer();
        await container.UpsertItemAsync(report, new PartitionKey(report.EndpointId));
    }

    public async Task<IReadOnlyDictionary<string, EventReport>> GetEventReports(string endpointId, IReadOnlyCollection<string> eventIds)
    {
        var ids = (eventIds ?? Array.Empty<string>())
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct()
            .ToList();

        var result = new Dictionary<string, EventReport>();
        if (string.IsNullOrEmpty(endpointId) || ids.Count == 0)
            return result;

        // Single-partition batched read (EndpointId is the partition key).
        var query = new QueryDefinition(
                $"SELECT * FROM c WHERE c.{nameof(EventReport.EndpointId)} = @endpointId " +
                $"AND ARRAY_CONTAINS(@eventIds, c.{nameof(EventReport.EventId)})")
            .WithParameter("@endpointId", endpointId)
            .WithParameter("@eventIds", ids);

        var container = await _getEventReportsContainer();
        var iterator = container.GetItemQueryIterator<EventReport>(query);
        while (iterator.HasMoreResults)
        {
            foreach (var report in await iterator.ReadNextAsync())
            {
                if (!string.IsNullOrEmpty(report.EventId))
                    result[report.EventId] = report;
            }
        }

        return result;
    }
}
