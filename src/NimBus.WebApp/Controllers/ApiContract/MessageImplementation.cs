using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.WebApp.Controllers.ApiContract
{
    public class MessageImplementation : IMessageApiController
    {
        private readonly IMessageTrackingStore _messageStore;
        private readonly ILogger<MessageImplementation> _logger;
        private readonly IEndpointAuthorizationService _authorizationService;
        private readonly PayloadRedaction _payloadRedaction;

        public MessageImplementation(
            IMessageTrackingStore messageStore,
            ILogger<MessageImplementation> logger,
            IEndpointAuthorizationService authorizationService,
            PayloadRedaction payloadRedaction)
        {
            _messageStore = messageStore;
            _logger = logger;
            _authorizationService = authorizationService;
            _payloadRedaction = payloadRedaction;
        }

        public async Task<ActionResult<MessageSearchResponse>> PostMessagesSearchAsync(MessageSearchRequest body)
        {
            // Cross-endpoint search: the read floor is a site role (spec 026 phase D).
            if (!await _authorizationService.HasRoleAsync(AccessRole.Reader))
                return new ForbidResult();

            var filter = MapFilter(body.Filter);
            // Clamp page size to [1, 200] with a default of 50. The upper bound prevents
            // unbounded scans against Cosmos / SQL when an external caller forgets a sensible value.
            var maxItems = body.MaxItemCount <= 0 ? 50 : Math.Min(body.MaxItemCount, 200);

            var result = await _messageStore.SearchMessages(filter, body.ContinuationToken, maxItems);

            var messages = result.Messages.Select(Mapper.MessageFromMessageEntity).ToList();
            if (!await _authorizationService.CanReadPiiAsync())
                messages.ForEach(m => _payloadRedaction.Redact(m));

            return new MessageSearchResponse
            {
                Messages = messages,
                ContinuationToken = result.ContinuationToken
            };
        }

        private static MessageFilter MapFilter(MessageSearchFilter? apiFilter)
        {
            if (apiFilter == null)
                return new MessageFilter();

            return new MessageFilter
            {
                EndpointId = apiFilter.EndpointId,
                EventId = apiFilter.EventId,
                MessageId = apiFilter.MessageId,
                SessionId = apiFilter.SessionId,
                EventTypeId = apiFilter.EventTypeId?.ToList(),
                From = apiFilter.SenderEndpoint,
                To = apiFilter.ReceiverEndpoint,
                MessageType = apiFilter.MessageType != null
                    ? Enum.TryParse<Core.Messages.MessageType>(apiFilter.MessageType.ToString(), out var mt) ? mt : null
                    : null,
                EnqueuedAtFrom = apiFilter.EnqueuedAtFrom,
                EnqueuedAtTo = apiFilter.EnqueuedAtTo
            };
        }
    }
}
