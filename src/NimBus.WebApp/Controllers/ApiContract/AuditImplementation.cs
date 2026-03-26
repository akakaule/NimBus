using NimBus.MessageStore;
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
        private readonly ICosmosDbClient _cosmosClient;
        private readonly ILogger<AuditImplementation> _logger;

        public AuditImplementation(ICosmosDbClient cosmosClient, ILogger<AuditImplementation> logger)
        {
            _cosmosClient = cosmosClient;
            _logger = logger;
        }

        public async Task<ActionResult<AuditSearchResponse>> PostAuditsSearchAsync(AuditSearchRequest body)
        {
            var filter = MapFilter(body.Filter);
            var maxItems = body.MaxItemCount > 0 ? body.MaxItemCount : 50;

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
