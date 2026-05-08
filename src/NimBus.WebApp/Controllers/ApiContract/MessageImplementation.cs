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
    public class MessageImplementation : IMessageApiController
    {
        private readonly INimBusMessageStore _cosmosClient;
        private readonly ILogger<MessageImplementation> _logger;

        public MessageImplementation(INimBusMessageStore cosmosClient, ILogger<MessageImplementation> logger)
        {
            _cosmosClient = cosmosClient;
            _logger = logger;
        }

        public async Task<ActionResult<MessageSearchResponse>> PostMessagesSearchAsync(MessageSearchRequest body)
        {
            var filter = MapFilter(body.Filter);
            // Clamp page size to [1, 200] with a default of 50. The upper bound prevents
            // unbounded scans against Cosmos / SQL when an external caller forgets a sensible value.
            var maxItems = body.MaxItemCount <= 0 ? 50 : Math.Min(body.MaxItemCount, 200);

            var result = await _cosmosClient.SearchMessages(filter, body.ContinuationToken, maxItems);

            return new MessageSearchResponse
            {
                Messages = result.Messages.Select(Mapper.MessageFromMessageEntity).ToList(),
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
