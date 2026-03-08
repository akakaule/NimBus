using Azure.Messaging.ServiceBus;
using EET.DIS.Core.Messages;
using EET.DIS.MessageStore;
using EET.DIS.MessageStore.States;
using McMaster.Extensions.CommandLineUtils;

using Azure.Messaging.ServiceBus.Administration;
using EET.DIS.CommandLine.Models;
using Spectre.Console;

namespace EET.DIS.CommandLine
{
    static class Endpoint
    {
        public static async Task DeleteSession(ServiceBusClient serviceBusClient, CosmosDbClient dbClient, CommandArgument nameArg, CommandArgument sessionIdArg)
        {
            string sessionId = sessionIdArg.Value;
            string endpoint = nameArg.Value;

            Console.WriteLine($"Waiting to accept session {sessionId}");

            var reciever = await serviceBusClient.AcceptSessionAsync(endpoint, endpoint, sessionId);

            Console.WriteLine($"Get service bus session retriever");

            int numberOfMessages = await RemoveActiveMessagesFromServiceBus(reciever);

            numberOfMessages += await RemoveDeferredMessagesFromServiceBus(reciever);

            await RemoveMessagesFromCosmosDB(dbClient, sessionId, endpoint);

            Console.WriteLine($"Clear session state for session {sessionId}");
            await reciever.SetSessionStateAsync(null);

           

        }

        public static async Task RemoveDeprecated(ServiceBusAdministrationClient sbAdmin, CommandArgument nameArg)
        {
            string endpointName = nameArg.Value.ToLower();

            // Check endpoint exists
            var topic = (await sbAdmin.GetTopicAsync(endpointName)).Value;
            
            // Set up expected topics/subscription/rules
            var expectedTopic = GetExpectedTopic(endpointName);
            var expectedTree = TopicToTree(expectedTopic);
            AnsiConsole.Write("Expected topics/subscriptions/rules \n");
            AnsiConsole.Write(expectedTree);
            AnsiConsole.WriteLine();
            
            // Set up actual topics/subscription/rules
            var actualTopic = await GetActualTopic(sbAdmin, endpointName);
            
            var isDeprecatedTopic = GetIsDeprecatedTopic(expectedTopic, actualTopic);
            var isDeprecatedTree = TopicToTree(isDeprecatedTopic);
            AnsiConsole.Write("Actual topics/subscriptions/rules (red will be deleted) \n");
            AnsiConsole.Write(isDeprecatedTree);
            AnsiConsole.WriteLine();

            if (!AnsiConsole.Confirm("Do you want to remove the marked topics and rules?"))
                return;

            await DeleteDeprecated(sbAdmin, endpointName, isDeprecatedTopic);

        }

        static TopicDto GetIsDeprecatedTopic(TopicDto expectedTopic, TopicDto actualTopic)
        {
            var expectedRules  = expectedTopic.Subscriptions.SelectMany(x => x.Rules);
            
            foreach (var subscription in actualTopic.Subscriptions)
            {
                subscription.IsDeprecated = !expectedTopic.Subscriptions.Contains(subscription, comparer: new SubscriptionDto.SubscriptionDtoComparer());

                foreach (var rule in subscription.Rules)
                {
                    rule.IsDeprecated = !expectedRules.Contains(rule, comparer: new RuleDto.RuleDtoComparer());
                }
            }
            return actualTopic;
        }
        
        static async Task DeleteDeprecated(ServiceBusAdministrationClient sbAdmin, string topicName, TopicDto topic)
        {
            var deprecatedRules = topic.Subscriptions.SelectMany(x => 
                x.Rules).Where(x => x.IsDeprecated);
            
            var deprecatedSubscriptions = topic.Subscriptions
                .Where(x => x.IsDeprecated);

            await AnsiConsole.Progress()
                .AutoRefresh(true)
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new ProgressColumn[] 
                {
                    new TaskDescriptionColumn(),    // Task description
                    new ProgressBarColumn(),        // Progress bar
                    new PercentageColumn(),         // Percentage
                    new RemainingTimeColumn(),      // Remaining time
                    new SpinnerColumn(),            // Spinner
                })
                .StartAsync(async ctx =>
                {
                    var deleteRulesTask = ctx.AddTask("[green]Deleting rules[/]");
                    var deleteSubscriptionsTask = ctx.AddTask("[green]Deleting topics[/]");

                    while (!ctx.IsFinished)
                    {
                        // Delete rules
                        if (deprecatedRules.Any())
                        {
                            var ruleIncrements = (double)(100M / deprecatedRules.Count());
                            foreach (var rule in deprecatedRules)
                            {
                                await sbAdmin.DeleteRuleAsync(topicName, rule.SubscriptionName, rule.Name);
                                deleteRulesTask.Increment(ruleIncrements);
                            }
                        }
                        else
                        {
                            deleteRulesTask.Increment(100);
                        }

                        if (deprecatedSubscriptions.Any())
                        {
                            // Delete subscriptions
                            var subscriptionIncrements = (double)(100M / deprecatedSubscriptions.Count());
                            foreach (var sub in deprecatedSubscriptions)
                            {
                                await sbAdmin.DeleteSubscriptionAsync(topicName, sub.Name);
                                deleteSubscriptionsTask.Increment(subscriptionIncrements);
                            } 
                        }
                        else
                        {
                            deleteSubscriptionsTask.Increment(100);
                        }
                    }
                });
        }
        static Tree TopicToTree(TopicDto topic)
        {
            var root = new Tree(topic.Name);

            var subscriptionNodes = topic.Subscriptions.Select(x =>
            {
                var subscriptionText = x.IsDeprecated ? $"[red]{x.Name}[/]" : x.Name;
                var subscriptionNode = new TreeNode(new Markup(subscriptionText));

                var ruleNodes = x.Rules.Select(y =>
                {
                    var ruleText = y.IsDeprecated ? $"[red]{y.Name}[/]" : y.Name;
                    return new TreeNode(new Markup(ruleText));
                });
                
                subscriptionNode.AddNodes(ruleNodes);
                return subscriptionNode;
            });
            root.AddNodes(subscriptionNodes);

            return root;
        }
        static TopicDto GetExpectedTopic(string endpointName)
        {
            var DataPlatform = new PlatformConfiguration();
            var expectedEndpoint = DataPlatform.Endpoints.First(x => x.Name.ToLower() == endpointName);
            var expectedTopic = new TopicDto{ 
                Name = endpointName.ToLower(),
                Subscriptions = new List<SubscriptionDto>
                {
                    // Main subscription
                    new SubscriptionDto
                    {
                        Name = endpointName, 
                        TopicName = endpointName, 
                        Rules = new List<RuleDto>
                        {
                            new RuleDto
                            {
                                Name = $"to-{endpointName}",
                                SubscriptionName = endpointName
                            }
                        }
                    },
                    // Forward-to-resolver subscription
                    new SubscriptionDto
                    {
                        Name = "resolver",
                        TopicName = endpointName,
                        Rules = new List<RuleDto>
                        {
                            new RuleDto
                            {
                                Name = $"to-{endpointName}",
                                SubscriptionName = "resolver"
                            },
                            new RuleDto
                            {
                                Name = $"from-{endpointName}",
                                SubscriptionName = "resolver"
                            }
                        }
                    },
                    // Forward-to-broker subscription
                    new SubscriptionDto
                    {
                        Name = "broker",
                        TopicName = endpointName,
                        Rules = new List<RuleDto>
                        {
                            new RuleDto
                            {
                                Name = $"from-{endpointName}",
                                SubscriptionName = "broker"
                            }
                        }
                    },
                    // "Forward"-to-self subscription (continuation requests)
                    new SubscriptionDto
                    {
                        Name = "continuation",
                        TopicName = endpointName,
                        Rules = new List<RuleDto>
                        {
                            new RuleDto
                            {
                                Name = $"continuation",
                                SubscriptionName = "continuation"
                            }
                        }
                    },
                    //  "Forward"-to-self subscription (retry requests)
                    new SubscriptionDto
                    {
                        Name = "retry",
                        TopicName = endpointName,
                        Rules = new List<RuleDto>
                        {
                            new RuleDto
                            {
                                Name = $"retry",
                                SubscriptionName = "retry"
                            }
                        }
                    },
                    // Deferred subscription
                    new SubscriptionDto
                    {
                        Name = "deferred",
                        TopicName = endpointName,
                        Rules = new List<RuleDto>
                        {
                            new RuleDto
                            {
                                Name = "to-deferred",
                                SubscriptionName = "deferred"
                            }
                        }
                    },
                    // DeferredProcessor subscription
                    new SubscriptionDto
                    {
                        Name = "deferredprocessor",
                        TopicName = endpointName,
                        Rules = new List<RuleDto>
                        {
                            new RuleDto
                            {
                                Name = "to-deferredprocessor",
                                SubscriptionName = "deferredprocessor"
                            }
                        }
                    }
                }
            };

            // Forward-from-eventtype-to-endpoint subscriptions 
            var createdSubscriptions = new List<SubscriptionDto>();
            foreach (var eventType in expectedEndpoint.EventTypesProduced)
            {
                var consumingEndpoints = DataPlatform.Endpoints
                    .Where(x => x.EventTypesConsumed.Contains(eventType))
                    .ToList();
                
                
                // Create forward subscription and rule for each subscribing endpoint
                foreach (var consumingEndpoint in consumingEndpoints)
                {
                    var subscription = createdSubscriptions.FirstOrDefault(x => x.Name == consumingEndpoint.Name.ToLower());
                    if (subscription != null)
                    {
                        subscription.Rules.Add(new RuleDto
                        {
                            Name = eventType.Id.ToLower(),
                            SubscriptionName = subscription.Name.ToLower()
                        });
                    }
                    else
                    {
                        createdSubscriptions.Add(new SubscriptionDto
                        {
                            TopicName = endpointName.ToLower(),
                            Name = consumingEndpoint.Name.ToLower(),
                            Rules = new List<RuleDto>
                            {
                                new RuleDto
                                {
                                    Name = eventType.Id.ToLower(),
                                    SubscriptionName = consumingEndpoint.Name.ToLower()
                                }
                            }
                        });
                    }
                }
            }
            expectedTopic.Subscriptions.AddRange(createdSubscriptions);

            return expectedTopic;
        }
        static async Task<TopicDto> GetActualTopic(ServiceBusAdministrationClient sbAdmin, string endpointName)
        {
            var subscriptonsAsync =  sbAdmin.GetSubscriptionsAsync(endpointName);
            var topic = new TopicDto(){ Name = endpointName, Subscriptions = new List<SubscriptionDto>()};
            
            await foreach (var page in subscriptonsAsync.AsPages())
            {
                var subscriptions = page.Values
                    .Select(x =>
                        new SubscriptionDto
                        {
                            Name = x.SubscriptionName.ToLower(),
                            TopicName = x.TopicName.ToLower(),
                            Rules = new List<RuleDto>()
                            
                        })
                    .ToList();
                topic.Subscriptions.AddRange(subscriptions);
            }

            foreach (var subscription in topic.Subscriptions)
            {
                var rulesAsync = sbAdmin.GetRulesAsync(endpointName, subscription.Name);

                await foreach (var page in rulesAsync.AsPages())
                {
                    var rules = page.Values
                        .Select(x => new RuleDto{Name = x.Name.ToLower(), SubscriptionName = subscription.Name.ToLower()})
                        .ToArray();
                    subscription.Rules.AddRange(rules);
                }
            }
            return topic;
        }
        static async Task<int> RemoveDeferredMessagesFromServiceBus(ServiceBusSessionReceiver reciever)
        {
            Console.WriteLine($"Removing deferred messages...");

            int numberOfMessages;
            do
            {
                Console.WriteLine($"Peeking messages");

                var peekMessages = await reciever.PeekMessagesAsync(100);
                numberOfMessages = peekMessages.Count;
                Console.WriteLine($"Message count {peekMessages.Count}");
                foreach (var message in peekMessages)
                {
                    if (message.State == ServiceBusMessageState.Deferred)
                    {
                        Console.WriteLine($"Recieving deferred message {message.MessageId} with sequence number {message.SequenceNumber}");
                        var deferredMessage = await reciever.ReceiveDeferredMessageAsync(message.SequenceNumber);
                        if (deferredMessage != null)
                        {
                            var sbMessage = new ServiceBus.ServiceBusMessage(deferredMessage);
                            string eventId = sbMessage.GetUserProperty(UserPropertyName.EventId);

                            Console.WriteLine($"Recieved deferred message {deferredMessage.MessageId} EventId: {eventId}");
                            await reciever.CompleteMessageAsync(deferredMessage);
                            Console.WriteLine($"Completed message {deferredMessage.MessageId}");
                        }
                    }
                }

            } while (numberOfMessages > 0);
            return numberOfMessages;
        }

        static async Task<int> RemoveActiveMessagesFromServiceBus(ServiceBusSessionReceiver reciever)
        {
            Console.WriteLine($"Removing active messages...");

            int numberOfMessages;
            do
            {
                Console.WriteLine($"Recieving messages");
                var messages = await reciever.ReceiveMessagesAsync(100);
                numberOfMessages = messages.Count;

                Console.WriteLine($"Message count {messages.Count}");
                foreach (var message in messages)
                {
                    var sbMessage = new ServiceBus.ServiceBusMessage(message);
                    string eventId = sbMessage.GetUserProperty(UserPropertyName.EventId);

                    Console.WriteLine($"MessageId: {message.MessageId} EventId: {eventId}");

                    await reciever.CompleteMessageAsync(message);
                    Console.WriteLine($"Completed message {message.MessageId}");
                }
            } while (numberOfMessages > 0);
            return numberOfMessages;
        }

        static async Task RemoveMessagesFromCosmosDB(CosmosDbClient dbClient, string sessionId, string endpoint)
        {
            Console.WriteLine($"Removing messages in CosmosDB...");

            string continuationToken = string.Empty;
            int maxSearchItemsCount = 20;
            SearchResponse searchResponse;

            do
            {
                Console.WriteLine($"Retrieving messages");
                searchResponse = await dbClient.GetEventsByFilter(new EventFilter() { EndPointId = endpoint, SessionId = sessionId }, continuationToken, maxSearchItemsCount);
                continuationToken = searchResponse.ContinuationToken;
                Console.WriteLine($"Retrieved {searchResponse.Events.Count()} messages");

                foreach (var @event in searchResponse.Events)
                {
                    Console.WriteLine($"Removing message with EventId {@event.EventId} from DB");
                    bool messageRemovedFromDb = await dbClient.RemoveMessage(@event.EventId, sessionId, endpoint);
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