using NimBus.MessageStore;
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
        private readonly ICosmosDbClient _cosmosClient;
        private readonly ILogger<MessageImplementation> _logger;

        public MessageImplementation(ICosmosDbClient cosmosClient, ILogger<MessageImplementation> logger)
        {
            _cosmosClient = cosmosClient;
            _logger = logger;
        }

        public async Task<ActionResult<MessageSearchResponse>> PostMessagesSearchAsync(MessageSearchRequest body)
        {
            var filter = MapFilter(body.Filter);
            var maxItems = body.MaxItemCount > 0 ? body.MaxItemCount : 50;

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
                From = apiFilter.From,
                To = apiFilter.To,
                MessageType = apiFilter.MessageType != null
                    ? Enum.TryParse<Core.Messages.MessageType>(apiFilter.MessageType.ToString(), out var mt) ? mt : null
                    : null,
                EnqueuedAtFrom = apiFilter.EnqueuedAtFrom,
                EnqueuedAtTo = apiFilter.EnqueuedAtTo
            };
        }
    }
}
