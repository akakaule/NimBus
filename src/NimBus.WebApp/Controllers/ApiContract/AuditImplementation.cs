using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.WebApp.Controllers.ApiContract
{
    public class AuditImplementation : IAuditApiController
    {
        private readonly INimBusMessageStore _cosmosClient;
        private readonly ILogger<AuditImplementation> _logger;

        public AuditImplementation(INimBusMessageStore cosmosClient, ILogger<AuditImplementation> logger)
        {
            _cosmosClient = cosmosClient;
            _logger = logger;
        }

        public async Task<ActionResult<AuditSearchResponse>> PostAuditsSearchAsync(AuditSearchRequest body)
        {
            var filter = MapFilter(body.Filter);
            // Clamp page size to [1, 200] with a default of 50. The upper bound prevents
            // unbounded scans against Cosmos / SQL when an external caller forgets a sensible value.
            var maxItems = body.MaxItemCount <= 0 ? 50 : Math.Min(body.MaxItemCount, 200);

            var result = await _cosmosClient.SearchAudits(filter, body.ContinuationToken, maxItems);

            return new AuditSearchResponse
            {
                Audits = result.Audits.Select(a => new AuditEntry
                {
                    EventId = a.EventId,
                    EndpointId = a.EndpointId,
                    EventTypeId = a.EventTypeId,
                    AuditorName = a.Audit.AuditorName,
                    AuditTimestamp = a.Audit.AuditTimestamp,
                    AuditType = Enum.Parse<AuditEntryAuditType>(a.Audit.AuditType.ToString()),
                    Comment = a.Audit.Comment,
                    CreatedAt = a.CreatedAt
                }).ToList(),
                ContinuationToken = result.ContinuationToken
            };
        }

        private static AuditFilter MapFilter(AuditSearchFilter apiFilter)
        {
            if (apiFilter == null)
                return new AuditFilter();

            return new AuditFilter
            {
                EventId = apiFilter.EventId,
                EndpointId = apiFilter.EndpointId,
                AuditorName = apiFilter.AuditorName,
                EventTypeId = apiFilter.EventTypeId,
                AuditType = apiFilter.AuditType != null
                    ? Enum.TryParse<MessageAuditType>(apiFilter.AuditType.ToString(), true, out var at) ? at : null
                    : null,
                CreatedAtFrom = apiFilter.CreatedAtFrom,
                CreatedAtTo = apiFilter.CreatedAtTo
            };
        }
    }
}
