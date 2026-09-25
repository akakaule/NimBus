using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using System.Net;

namespace NimBus.WebApp.Controllers.ApiContract;

public partial class EventImplementation
{
    public async Task<ActionResult<SearchResponse>> PostApiEventEndpointIdGetByFilterAsync(SearchRequest body, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        // Canonical casing keeps the audit rows and the exact-match
        // enrichment lookups (resubmit counts, report flags) consistent
        // regardless of the casing the request arrived with.
        endpointId = CanonicalEndpointId(endpointId);

        // Spec 008 FR-032: pass the search filter (serialized) as Data so
        // operators answering "what searches has user X run?" see the
        // query parameters, not just the bare action.
        var searchDataJson = JsonConvert.SerializeObject(body);

        if (!await authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
        {
            await auditLogService.LogAuditAsync(MessageAuditType.SearchEvents, httpContextAccessor.HttpContext,
                accessDenied: true, data: searchDataJson, endpointId: endpointId);
            return new ForbidResult();
        }

        var canReadPii = await authorizationService.CanReadPiiAsync();

        // A raw predicate can reveal sensitive values through hit/miss results.
        // Ordinary Readers may search only verified non-sensitive receiving contracts.
        string[] nonSensitiveTypes = null;
        if (!canReadPii && !string.IsNullOrWhiteSpace(body.EventFilter?.Payload))
        {
            nonSensitiveTypes = PayloadSearchPolicy.GetNonSensitiveReceivedTypes(
                platform.Endpoints.Single(e => e.Id == endpointId));
        }
        if (!canReadPii && !string.IsNullOrWhiteSpace(body.EventFilter?.Payload)
            && (nonSensitiveTypes == null || (body.EventFilter.EventTypeId?.Any(
                id => !nonSensitiveTypes.Contains(id, StringComparer.Ordinal)) ?? false)))
        {
            await auditLogService.LogAuditAsync(MessageAuditType.SearchEvents, httpContextAccessor.HttpContext,
                accessDenied: true, data: searchDataJson, endpointId: endpointId);
            return new ObjectResult(
                "Payload search requires PiiReader when receiving event contracts contain sensitive fields or cannot be classified. A site Owner can grant it on the Access Control page.")
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }

        try
        {
            var filter = Mapper.MapFilter(body.EventFilter);
            filter.EndPointId = endpointId;  // Use validated URL parameter instead of body value
            // Exclude historical/unregistered types rather than letting a catalog check
            // authorize an unrestricted query against every document in the container.
            if (nonSensitiveTypes != null && (filter.EventTypeId == null || filter.EventTypeId.Count == 0))
                filter.EventTypeId = nonSensitiveTypes.ToList();
            var reponse = await messageStore.GetEventsByFilter(filter, body.ContinuationToken, body.MaxSearchItemsCount);
            await auditLogService.LogAuditAsync(MessageAuditType.SearchEvents, httpContextAccessor.HttpContext,
                data: searchDataJson, endpointId: endpointId);
            var events = reponse.Events
                .Select(Mapper.EventFromMessageStoreEvent)
                .ToList();
            await AttachResubmitCounts(endpointId, events);
            await AttachReportFlags(endpointId, events);

            if (!canReadPii)
                payloadRedaction.Redact(events);

            return new SearchResponse
            {
                Events = events,
                ContinuationToken = reponse.ContinuationToken
            };
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
        }
        catch (EndpointNotFoundException)
        {
            return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
        }
    }
    // Fills each event's ResubmitCount from the audit log in a single batched
    // query, so the event list can show how many times an event was
    // resubmitted without a per-row round-trip. Fail-soft: the count is a
    // display nicety — an enrichment failure must not break search.
    private async Task AttachResubmitCounts(string endpointId, List<Event> events)
    {
        var eventIds = events
            .Select(e => e.EventId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();
        if (eventIds.Count == 0) return;

        try
        {
            var counts = await messageStore.GetResubmitCounts(endpointId, eventIds);
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

    // Fills each event's "reported" marker (flag + who/when + ticket) from
    // the report store in a single batched lookup. Fail-soft like
    // AttachResubmitCounts.
    private async Task AttachReportFlags(string endpointId, List<Event> events)
    {
        var eventIds = events
            .Select(e => e.EventId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();
        if (eventIds.Count == 0) return;

        try
        {
            var reports = await messageStore.GetEventReports(endpointId, eventIds);
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
}
