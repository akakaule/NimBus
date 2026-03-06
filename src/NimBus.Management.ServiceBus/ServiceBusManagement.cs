using Azure.Messaging.ServiceBus.Administration;
using NimBus.Core.Logging;
using System;
using System.Threading.Tasks;

namespace NimBus.Management.ServiceBus;
public interface IServiceBusManagement
{
    Task CreateRule(string topicName, string subscriptionName, string ruleName);
    Task CreateEventTypeRule(string topicName, string subscriptionName, string ruleName, string eventtype);
    Task CreateCustomRule(string topicName, string subscriptionName, string ruleName, string filter, string action);
    Task CreateSubscription(string topicName, string subscriptionName);
    Task CreateForwardSubscription(string topicName, string subscriptionName, string forwardTo);
    Task CreateTopic(string topicName);
    Task DeleteRule(string topicName, string subscriptionName, string ruleName);
    Task DeleteSubscription(string topicName, string subscriptionName);
    Task DisableSubscription(string topicName, string subscriptionName);
    Task EnableSubscription(string topicName, string subscriptionName);
    Task<bool> IsSubscriptionActive(string topicName, string subscriptionName);
    Task UpdateForwardTo(string topicName, string subscriptionName, string forwardTo);
    Task CreateDeferredSubscription(string topicName);
    Task CreateDeferredProcessorSubscription(string topicName);
}

public class ServiceBusManagement : IServiceBusManagement
{
    private readonly ServiceBusAdministrationClient client;
    private readonly ILogger _logger;

    public ServiceBusManagement(ServiceBusAdministrationClient client, ILogger logger = null)
    {
        this.client = client;
        _logger = logger;
    }

    public async Task CreateSubscription(string topicName, string subscriptionName)
    {
        try
        {
            var subscriptionProperties = new CreateSubscriptionOptions(topicName, subscriptionName)
            {
                MaxDeliveryCount = 10,
                LockDuration = TimeSpan.FromSeconds(30),
                EnableBatchedOperations = true,
                EnableDeadLetteringOnFilterEvaluationExceptions = true,
                RequiresSession = true
            };

            _logger?.Verbose("Creating subscription...");
            await client.CreateSubscriptionAsync(subscriptionProperties);
            _logger?.Verbose("Created subscription successfully.");
        }
        catch (Exception e)
        {
            _logger?.Error(e, "Could not create subscription");
            throw;
        }
    }

    public async Task CreateForwardSubscription(string topicName, string subscriptionName, string forwardTo)
    {
        try
        {
            var subscriptionProperties = new CreateSubscriptionOptions(topicName, subscriptionName)
            {
                MaxDeliveryCount = 10,
                LockDuration = TimeSpan.FromSeconds(30),
                ForwardTo = forwardTo,
                EnableBatchedOperations = true,
                EnableDeadLetteringOnFilterEvaluationExceptions = true
            };

            try
            {
                var existingSub = await client.GetSubscriptionAsync(topicName, subscriptionName);
                if (existingSub.Value.RequiresSession == true) await DeleteSubscription(topicName, subscriptionName);
            }
            catch (Azure.RequestFailedException e) when (e.Status == 404)
            {
                // Subscription doesn't exist, this is expected - continue with creation
                _logger?.Verbose($"Subscription '{subscriptionName}' does not exist on topic '{topicName}', will create new");
            }

            _logger?.Verbose("Creating subscription...");
            await client.CreateSubscriptionAsync(subscriptionProperties);
            _logger?.Verbose("Created subscription successfully.");
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not create subscription {e.Message}");
            throw;
        }
    }

    public async Task DeleteSubscription(string topicName, string subscriptionName)
    {
        try
        {
            _logger?.Verbose("Creating subscription...");
            var result = await client.DeleteSubscriptionAsync(topicName, subscriptionName);
            _logger?.Verbose("Created subscription successfully.");
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not delete subscription {e.Message}");
            throw;
        }
    }

    public async Task CreateRule(string topicName, string subscriptionName, string ruleName)
    {
        try
        {
            var ruleOptions = new CreateRuleOptions
            {
                Filter = new SqlRuleFilter($"user.To='{subscriptionName}'")
            };

            _logger?.Verbose("Creating rule...");
            var result = await client.CreateRuleAsync(topicName, subscriptionName, ruleOptions);
            _logger?.Verbose("Created rule successfully.");
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not create rule {e.Message}");
            throw;
        }
    }

    public async Task CreateEventTypeRule(string topicName, string subscriptionName, string ruleName, string eventType)
    {
        try
        {
            var ruleOptions = new CreateRuleOptions
            {
                Filter = new SqlRuleFilter($"user.EventTypeId='{eventType}'"),
                Action = new SqlRuleAction($"SET user.From ='{topicName}'; SET user.EventId = newid(); SET user.To = '{subscriptionName}';")
            };

            _logger?.Verbose("Creating rule...");
            var result = await client.CreateRuleAsync(topicName, subscriptionName, ruleOptions);
            _logger?.Verbose("Created rule successfully.");
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not create rule {e.Message}");
            throw;
        }
    }

    public async Task CreateCustomRule(string topicName, string subscriptionName, string ruleName, string filter, string action)
    {
        try
        {
            var ruleOptions = new CreateRuleOptions
            {
                Filter = new SqlRuleFilter(filter),
                Name = ruleName
            };

            if (!String.IsNullOrEmpty(action))
            {
                ruleOptions.Action = new SqlRuleAction(action);
            }

            _logger?.Verbose("Creating rule...");
            var result = await client.CreateRuleAsync(topicName, subscriptionName, ruleOptions);
            _logger?.Verbose("Created rule successfully.");
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not create rule {e.Message}");
            throw;
        }
    }

    public async Task CreateTopic(string topicName)
    {
        try
        {
            var topicParams = new CreateTopicOptions(topicName)
            {
                SupportOrdering = true,
                DuplicateDetectionHistoryTimeWindow = new TimeSpan(0, 10, 0),
                EnableBatchedOperations = true,
                MaxSizeInMegabytes = 5120,
            };

            _logger?.Verbose("Creating topic...");
            await client.CreateTopicAsync(topicParams);
            _logger?.Verbose("Created topic successfully.");
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not create topic {e.Message}");
            throw;
        }
    }

    public async Task DeleteRule(string topicName, string subscriptionName, string ruleName)
    {
        try
        {
            _logger?.Verbose("Deleting rule...");
            var response = await client.DeleteRuleAsync(topicName, subscriptionName, ruleName);
            _logger?.Verbose("Created rule successfully.");
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not create rule {e.Message}");
            throw;
        }
    }
    public async Task DisableSubscription(string topicName, string subscriptionName)
    {
        try
        {
            var subscription = await client.GetSubscriptionAsync(topicName, subscriptionName);
            if (subscription != null)
            {
                _logger?.Verbose("Updating status for subscription...");

                subscription.Value.Status = EntityStatus.ReceiveDisabled;
                await client.UpdateSubscriptionAsync(subscription);

                _logger?.Verbose("Status updated on subscription successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not update status for subscription {e.Message}");
            throw;
        }
    }

    public async Task EnableSubscription(string topicName, string subscriptionName)
    {

        try
        {
            var subscription = await client.GetSubscriptionAsync(topicName, subscriptionName);
            if (subscription != null)
            {
                _logger?.Verbose("Updating status for subscription...");

                subscription.Value.Status = EntityStatus.Active;
                await client.UpdateSubscriptionAsync(subscription);

                _logger?.Verbose("Status updated on subscription successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not update status for subscription {e.Message}");
            throw;
        }
    }

    public async Task UpdateForwardTo(string topicName, string subscriptionName, string forwardTo)
    {

        try
        {
            var subscription = await client.GetSubscriptionAsync(topicName, subscriptionName);
            if (subscription != null)
            {
                _logger?.Verbose("Updating forward to for subscription...");

                subscription.Value.ForwardTo = forwardTo;
                await client.UpdateSubscriptionAsync(subscription);

                _logger?.Verbose("Forward to updated on subscription successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not update forward to for subscription {e.Message}");
            throw;
        }
    }

    public async Task<bool> IsSubscriptionActive(string topicName, string subscriptionName)
    {
        var subscription = await client.GetSubscriptionAsync(topicName, subscriptionName);
        return subscription?.Value?.Status == EntityStatus.Active;
    }

    /// <summary>
    /// Creates the non-session Deferred subscription for storing deferred messages.
    /// This subscription receives messages with To='Deferred' and does NOT require sessions.
    /// </summary>
    public async Task CreateDeferredSubscription(string topicName)
    {
        const string subscriptionName = "Deferred";

        try
        {
            var subscriptionProperties = new CreateSubscriptionOptions(topicName, subscriptionName)
            {
                MaxDeliveryCount = 10,
                LockDuration = TimeSpan.FromSeconds(30),
                EnableBatchedOperations = true,
                EnableDeadLetteringOnFilterEvaluationExceptions = true,
                RequiresSession = false, // Non-session subscription
                DefaultMessageTimeToLive = TimeSpan.FromDays(14)
            };

            _logger?.Verbose("Creating Deferred subscription...");
            await client.CreateSubscriptionAsync(subscriptionProperties);
            _logger?.Verbose("Created Deferred subscription successfully.");

            // Create SQL filter rule for routing
            var ruleOptions = new CreateRuleOptions
            {
                Name = "DeferredFilter",
                Filter = new SqlRuleFilter("user.To = 'Deferred' AND user.OriginalSessionId IS NOT NULL")
            };

            // Delete default rule first
            try
            {
                await client.DeleteRuleAsync(topicName, subscriptionName, "$Default");
            }
            catch (Azure.RequestFailedException e) when (e.Status == 404)
            {
                // Default rule doesn't exist, this is expected
                _logger?.Verbose("Default rule does not exist, continuing");
            }

            await client.CreateRuleAsync(topicName, subscriptionName, ruleOptions);
            _logger?.Verbose("Created Deferred subscription rule successfully.");
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not create Deferred subscription: {e.Message}");
            throw;
        }
    }

    /// <summary>
    /// Creates the DeferredProcessor subscription for triggering deferred message processing.
    /// This subscription receives ProcessDeferredRequest messages and does NOT require sessions.
    /// </summary>
    public async Task CreateDeferredProcessorSubscription(string topicName)
    {
        const string subscriptionName = "DeferredProcessor";

        try
        {
            var subscriptionProperties = new CreateSubscriptionOptions(topicName, subscriptionName)
            {
                MaxDeliveryCount = 10,
                LockDuration = TimeSpan.FromSeconds(30),
                EnableBatchedOperations = true,
                EnableDeadLetteringOnFilterEvaluationExceptions = true,
                RequiresSession = false // Non-session subscription
            };

            _logger?.Verbose("Creating DeferredProcessor subscription...");
            await client.CreateSubscriptionAsync(subscriptionProperties);
            _logger?.Verbose("Created DeferredProcessor subscription successfully.");

            // Create SQL filter rule for routing
            var ruleOptions = new CreateRuleOptions
            {
                Name = "DeferredProcessorFilter",
                Filter = new SqlRuleFilter("user.To = 'DeferredProcessor'")
            };

            // Delete default rule first
            try
            {
                await client.DeleteRuleAsync(topicName, subscriptionName, "$Default");
            }
            catch (Azure.RequestFailedException e) when (e.Status == 404)
            {
                // Default rule doesn't exist, this is expected
                _logger?.Verbose("Default rule does not exist, continuing");
            }

            await client.CreateRuleAsync(topicName, subscriptionName, ruleOptions);
            _logger?.Verbose("Created DeferredProcessor subscription rule successfully.");
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not create DeferredProcessor subscription: {e.Message}");
            throw;
        }
    }
}
