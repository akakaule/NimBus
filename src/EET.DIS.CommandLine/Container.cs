using EET.DIS.MessageStore.States;
using EET.DIS.MessageStore;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Azure.Cosmos.Serialization.HybridRow;
using Azure.Messaging.ServiceBus;
using EET.DIS.ServiceBus;
using EET.DIS.Core.Messages;

namespace EET.DIS.CommandLine
{
    static class Container
    {
        public static async Task DeleteDocuments(CosmosDbClient dbClient, CommandArgument nameArg)
        {
            await RemoveMessagesFromCosmosDB(dbClient, nameArg.Value);
        }

        public static async Task DeleteDocument(CosmosDbClient dbClient, CommandArgument nameArg, CommandArgument eventIdArg)
        {
            await RemoveMessageFromCosmosDB(dbClient, nameArg.Value, eventIdArg.Value);
        }

        public static async Task ResubmitMessages(ServiceBusClient serviceBusClient, CosmosDbClient dbClient, CommandArgument nameArg)
        {
            await UpdateMessagesAndResubmit(serviceBusClient, dbClient, nameArg.Value, ResolutionStatus.Failed );
        }

        static async Task RemoveMessagesFromCosmosDB(CosmosDbClient dbClient, string endpoint)
        {
            Console.WriteLine($"Removing messages in CosmosDB...");

            string continuationToken = string.Empty;
            int maxSearchItemsCount = 20;
            SearchResponse searchResponse;

            do
            {
                Console.WriteLine($"Retrieving messages");
                searchResponse = await dbClient.GetEventsByFilter(new EventFilter() { EndPointId = endpoint, ResolutionStatus = new List<string> { ResolutionStatus.DeadLettered.ToString() } }, continuationToken, maxSearchItemsCount);
                continuationToken = searchResponse.ContinuationToken;
                Console.WriteLine($"Retrieved {searchResponse.Events.Count()} messages");

                foreach (var @event in searchResponse.Events)
                {
                    Console.WriteLine($"Removing message with EventId {@event.EventId} from DB");
                    bool messageRemovedFromDb = false;
                    messageRemovedFromDb = await dbClient.RemoveMessage(@event.EventId, @event.SessionId, endpoint);
                    if (messageRemovedFromDb)
                    {
                        Console.WriteLine($"Removed message from DB");
                    };
                }
            }
            while (continuationToken != null);
        }

        static async Task UpdateMessagesAsCompleted(CosmosDbClient dbClient, string endpoint, List<string> resolutionStatus)
        {
            Console.WriteLine($"Updating status for messages in CosmosDB...");

            string continuationToken = string.Empty;
            int maxSearchItemsCount = 20;
            SearchResponse searchResponse;

            do
            {
                Console.WriteLine($"Retrieving messages");
                searchResponse = await dbClient.GetEventsByFilter(new EventFilter() { EndPointId = endpoint, ResolutionStatus = resolutionStatus }, continuationToken, maxSearchItemsCount);
                continuationToken = searchResponse.ContinuationToken;
                Console.WriteLine($"Retrieved {searchResponse.Events.Count()} messages");

                foreach (var @event in searchResponse.Events)
                {
                    Console.WriteLine($"Updating message with EventId {@event.EventId} from DB");
                    bool messageUpdated = await dbClient.UploadCompletedMessage(@event.EventId, @event.SessionId, endpoint, @event);
                    if (messageUpdated)
                    {
                        Console.WriteLine($"Updated message from DB");
                    };
                }
            }
            while (continuationToken != null);
        }

        static async Task UpdateMessagesAndResubmit(ServiceBusClient serviceBusClient, CosmosDbClient dbClient, string endpoint, ResolutionStatus resolutionStatus)
        {
            var sender = new Sender(serviceBusClient.CreateSender(EET.DIS.Core.Messages.Constants.ManagerId));
            //var 

            Console.WriteLine($"Updating status for messages in CosmosDB...");

            string continuationToken = string.Empty;
            int maxSearchItemsCount = 20;
            SearchResponse searchResponse;

            do
            {
                Console.WriteLine($"Retrieving messages");
                searchResponse = await dbClient.GetEventsByFilter(new EventFilter() { EndPointId = endpoint, ResolutionStatus = new List<string> { resolutionStatus.ToString() } }, continuationToken, maxSearchItemsCount);
                continuationToken = searchResponse.ContinuationToken;
                Console.WriteLine($"Retrieved {searchResponse.Events.Count()} messages");

                foreach (var @event in searchResponse.Events)
                {
                    if (@event.UpdatedAt < DateTime.Now.AddMinutes(-10))
                    {
                        @event.ResolutionStatus = resolutionStatus;
                        @event.MessageType = Core.Messages.MessageType.EventRequest;
                        Console.WriteLine($"Updating message with EventId {@event.EventId} from DB");
                        bool messageUpdated = await dbClient.UploadFailedMessage(@event.EventId, @event.SessionId, endpoint, @event);
                        if (messageUpdated)
                        {                            
                            Console.WriteLine($"Updated message as Failed");
                            await Resubmit(sender, @event, @event.EndpointId, @event.EventTypeId, @event.MessageContent.EventContent.EventJson);
                        };
                    }
                    else
                    {
                        Console.WriteLine($"Message {@event.EventId} is not old enough");
                    }
                }
            }
            while (continuationToken != null);
        }

        static Task Resubmit(Sender sender, UnresolvedEvent errorResponse, string endpoint, string eventTypeId, string eventJson)
        {
            Console.WriteLine($"MANAGER RESUBMIT EVENT: EventId: {errorResponse.EventId} EventtypeId: {eventTypeId} EventJson: {eventJson} errorResponse: {errorResponse} ");
            return sender.Send(new Message
            {
                CorrelationId = errorResponse.CorrelationId,
                EventId = errorResponse.EventId,
                SessionId = errorResponse.SessionId,
                To = endpoint,
                OriginatingMessageId = errorResponse.OriginatingMessageId,
                ParentMessageId = errorResponse.LastMessageId,
                MessageType = MessageType.ResubmissionRequest,
                EventTypeId = eventTypeId,
                MessageContent = new MessageContent
                {
                    EventContent = new EventContent
                    {
                        EventTypeId = eventTypeId,
                        EventJson = eventJson
                    }
                },
            });
        }

        static async Task RemoveMessageFromCosmosDB(CosmosDbClient dbClient, string endpoint, string eventId)
        {
            Console.WriteLine($"Removing messages in CosmosDB...");

            string continuationToken = string.Empty;
            int maxSearchItemsCount = 20;
            SearchResponse searchResponse;

            do
            {
                Console.WriteLine($"Retrieving messages");
                searchResponse = await dbClient.GetEventsByFilter(new EventFilter() { EndPointId = endpoint, EventId = eventId }, continuationToken, maxSearchItemsCount);
                continuationToken = searchResponse.ContinuationToken;
                Console.WriteLine($"Retrieved {searchResponse.Events.Count()} messages");

                foreach (var @event in searchResponse.Events)
                {
                    Console.WriteLine($"Removing message with EventId {@event.EventId} from DB");
                    bool messageRemovedFromDb = await dbClient.RemoveMessage(@event.EventId, @event.SessionId, endpoint);
                    if (messageRemovedFromDb)
                    {
                        Console.WriteLine($"Removed message from DB");
                    };
                }
            }
            while (continuationToken != null);
        }
    }
}