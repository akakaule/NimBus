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
        private readonly Services.IEndpointAuthorizationService _authorizationService;

        public AuditImplementation(
            INimBusMessageStore cosmosClient,
            ILogger<AuditImplementation> logger,
            Services.IEndpointAuthorizationService authorizationService)
        {
            _cosmosClient = cosmosClient;
            _logger = logger;
            _authorizationService = authorizationService;
        }

        public async Task<ActionResult<AuditSearchResponse>> PostAuditsSearchAsync(AuditSearchRequest body)
        {
            var filter = MapFilter(body.Filter);

            // Audit rows can carry sensitive Data payloads (search filters,
            // resubmit-with-changes bodies, report toggles), so the search is
            // never open-ended:
            // - Unscoped (no endpoint filter, the cross-endpoint Audit Log page)
            //   requires a platform administrator.
            // - Endpoint-scoped requires managing that endpoint, and the query
            //   is switched to EXACT endpoint matching in storage — the default
            //   contract treats EndpointId as a case-insensitive PREFIX, so
            //   authorizing "Orders" must not leak "OrdersArchive". Exact
            //   matching in storage (not post-filtering a fetched page) keeps
            //   pages full even when prefix-siblings dominate the data.
            var scopedEndpointId = filter.EndpointId;
            if (string.IsNullOrEmpty(scopedEndpointId))
            {
                if (!_authorizationService.IsPlatformAdministrator())
                {
                    return new ForbidResult();
                }
            }
            else
            {
                if (!_authorizationService.IsManagerOfEndpoint(scopedEndpointId))
                {
                    return new ForbidResult();
                }

                filter.EndpointIdExact = true;
            }
            // Clamp page size to [1, 200] with a default of 50. The upper bound prevents
            // unbounded scans against Cosmos / SQL when an external caller forgets a sensible value.
            var maxItems = body.MaxItemCount <= 0 ? 50 : Math.Min(body.MaxItemCount, 200);

            var result = await _cosmosClient.SearchAudits(filter, body.ContinuationToken, maxItems);

            // Fail-closed belt-and-braces: EndpointIdExact is an OPTIONAL store
            // capability — a provider that predates the flag silently applies
            // prefix semantics, which would leak prefix-siblings. First-party
            // providers filter exactly in storage (keeping pages full); this
            // final check only ever removes rows a non-conforming provider let
            // through.
            var audits = string.IsNullOrEmpty(scopedEndpointId)
                ? result.Audits
                : result.Audits.Where(a => string.Equals(a.EndpointId, scopedEndpointId, StringComparison.OrdinalIgnoreCase));

            return new AuditSearchResponse
            {
                Audits = audits.Select(a => new AuditEntry
                {
                    EventId = a.EventId,
                    EndpointId = a.EndpointId,
                    EventTypeId = a.EventTypeId,
                    AuditorName = a.Audit.AuditorName,
                    AuditTimestamp = a.Audit.AuditTimestamp,
                    AuditType = Enum.Parse<AuditEntryAuditType>(a.Audit.AuditType.ToString()),
                    Comment = a.Audit.Comment,
                    AccessDenied = a.Audit.AccessDenied,
                    Data = a.Audit.Data,
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
