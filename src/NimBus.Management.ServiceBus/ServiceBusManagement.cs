using Azure.Messaging.ServiceBus.Administration;
using Serilog;
using System;
using System.Threading.Tasks;

namespace NimBus.Management.ServiceBus;

public enum SubscriptionState
{
    Active,
    Disabled,
    NotFound,
}

public enum TopicSendState
{
    Enabled,
    SendDisabled,
    NotFound,
}
/// <summary>
/// Runtime management operations against an existing Service Bus topology
/// (status toggles, forward-to updates, and the subscription/rule rebuild used
/// by ClearEndpoint). Topology <em>provisioning</em> lives exclusively in
/// <c>NimBus.ServiceBus.Provisioning.ServiceBusTopologyProvisioner</c> — do not
/// add entity-creation methods here that duplicate it.
/// </summary>
public interface IServiceBusManagement
{
    Task CreateCustomRule(string topicName, string subscriptionName, string ruleName, string filter, string action);
    Task CreateSubscription(string topicName, string subscriptionName);
    Task DeleteRule(string topicName, string subscriptionName, string ruleName);
    Task DeleteSubscription(string topicName, string subscriptionName);
    Task DisableSubscription(string topicName, string subscriptionName);
    Task EnableSubscription(string topicName, string subscriptionName);
    Task<bool> IsSubscriptionActive(string topicName, string subscriptionName);
    Task<SubscriptionState> GetSubscriptionState(string topicName, string subscriptionName);
    Task DisableTopicSend(string topicName);
    Task EnableTopicSend(string topicName);
    Task<TopicSendState> GetTopicSendState(string topicName);
    Task UpdateForwardTo(string topicName, string subscriptionName, string forwardTo);
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
        ServiceBusFilterValidator.ValidateName(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
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

    public async Task DeleteSubscription(string topicName, string subscriptionName)
    {
        ServiceBusFilterValidator.ValidateName(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
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

    public async Task CreateCustomRule(string topicName, string subscriptionName, string ruleName, string filter, string action)
    {
        // Names are validated here; `filter` and `action` are SQL templates whose
        // interpolated values must already have been validated upstream. We don't
        // second-guess the SQL syntax of an explicitly-supplied custom rule.
        ServiceBusFilterValidator.ValidateName(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        ServiceBusFilterValidator.ValidateName(ruleName, nameof(ruleName));
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

    public async Task DeleteRule(string topicName, string subscriptionName, string ruleName)
    {
        ServiceBusFilterValidator.ValidateName(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        ServiceBusFilterValidator.ValidateName(ruleName, nameof(ruleName));
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
        ServiceBusFilterValidator.ValidateName(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
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
        ServiceBusFilterValidator.ValidateName(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
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

    public async Task DisableTopicSend(string topicName)
    {
        ServiceBusFilterValidator.ValidateName(topicName, nameof(topicName));
        try
        {
            var topic = await client.GetTopicAsync(topicName);
            if (topic != null)
            {
                _logger?.Verbose("Updating send status for topic...");

                topic.Value.Status = EntityStatus.SendDisabled;
                await client.UpdateTopicAsync(topic);

                _logger?.Verbose("Send status updated on topic successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not update send status for topic {e.Message}");
            throw;
        }
    }

    public async Task EnableTopicSend(string topicName)
    {
        ServiceBusFilterValidator.ValidateName(topicName, nameof(topicName));
        try
        {
            var topic = await client.GetTopicAsync(topicName);
            if (topic != null)
            {
                _logger?.Verbose("Updating send status for topic...");

                topic.Value.Status = EntityStatus.Active;
                await client.UpdateTopicAsync(topic);

                _logger?.Verbose("Send status updated on topic successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.Error(e, $"Could not update send status for topic {e.Message}");
            throw;
        }
    }

    public async Task<TopicSendState> GetTopicSendState(string topicName)
    {
        ServiceBusFilterValidator.ValidateName(topicName, nameof(topicName));
        try
        {
            var topic = await client.GetTopicAsync(topicName);
            // Any non-Active status (SendDisabled, or fully Disabled) means producers
            // can't publish, so it collapses to SendDisabled from the send perspective.
            return topic?.Value?.Status == EntityStatus.Active
                ? TopicSendState.Enabled
                : TopicSendState.SendDisabled;
        }
        catch (Azure.Messaging.ServiceBus.ServiceBusException ex)
            when (ex.Reason == Azure.Messaging.ServiceBus.ServiceBusFailureReason.MessagingEntityNotFound)
        {
            _logger?.Information("Topic '{TopicName}' was not found.", topicName);
            return TopicSendState.NotFound;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger?.Information("Topic '{TopicName}' was not found.", topicName);
            return TopicSendState.NotFound;
        }
    }

    public async Task UpdateForwardTo(string topicName, string subscriptionName, string forwardTo)
    {
        ServiceBusFilterValidator.ValidateName(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        ServiceBusFilterValidator.ValidateName(forwardTo, nameof(forwardTo));
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
        return await GetSubscriptionState(topicName, subscriptionName) == SubscriptionState.Active;
    }

    public async Task<SubscriptionState> GetSubscriptionState(string topicName, string subscriptionName)
    {
        ServiceBusFilterValidator.ValidateName(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        try
        {
            var subscription = await client.GetSubscriptionAsync(topicName, subscriptionName);
            return subscription?.Value?.Status == EntityStatus.Active
                ? SubscriptionState.Active
                : SubscriptionState.Disabled;
        }
        catch (Azure.Messaging.ServiceBus.ServiceBusException ex)
            when (ex.Reason == Azure.Messaging.ServiceBus.ServiceBusFailureReason.MessagingEntityNotFound)
        {
            _logger?.Information("Subscription '{SubscriptionName}' on topic '{TopicName}' was not found.", subscriptionName, topicName);
            return SubscriptionState.NotFound;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger?.Information("Subscription '{SubscriptionName}' on topic '{TopicName}' was not found.", subscriptionName, topicName);
            return SubscriptionState.NotFound;
        }
    }

}
