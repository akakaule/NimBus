using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
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

    public ServiceBusManagement(ServiceBusAdministrationClient client, ILogger<ServiceBusManagement> logger = null)
    {
        this.client = client;
        _logger = logger;
    }

    /// <summary>
    /// Serilog bridge constructor. NimBus standardizes on
    /// Microsoft.Extensions.Logging (ADR-006); this overload remains for
    /// callers that still pass a Serilog logger. The logger parameter is
    /// deliberately required so single-argument construction resolves
    /// unambiguously to the MEL constructor.
    /// </summary>
    [Obsolete("Use the Microsoft.Extensions.Logging constructor — NimBus standardizes on Microsoft.Extensions.Logging (ADR-006). This bridge remains for callers that still pass a Serilog logger.")]
    public ServiceBusManagement(ServiceBusAdministrationClient client, Serilog.ILogger logger)
    {
        this.client = client;
        _logger = logger is null ? null : new SerilogBridgeLogger(logger);
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

            _logger?.LogTrace("Creating subscription...");
            await client.CreateSubscriptionAsync(subscriptionProperties);
            _logger?.LogTrace("Created subscription successfully.");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not create subscription");
            throw;
        }
    }

    public async Task DeleteSubscription(string topicName, string subscriptionName)
    {
        ServiceBusFilterValidator.ValidateName(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        try
        {
            _logger?.LogTrace("Deleting subscription...");
            var result = await client.DeleteSubscriptionAsync(topicName, subscriptionName);
            _logger?.LogTrace("Deleted subscription successfully.");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not delete subscription");
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

            _logger?.LogTrace("Creating rule...");
            var result = await client.CreateRuleAsync(topicName, subscriptionName, ruleOptions);
            _logger?.LogTrace("Created rule successfully.");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not create rule");
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
            _logger?.LogTrace("Deleting rule...");
            var response = await client.DeleteRuleAsync(topicName, subscriptionName, ruleName);
            _logger?.LogTrace("Deleted rule successfully.");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not delete rule");
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
                _logger?.LogTrace("Updating status for subscription...");

                subscription.Value.Status = EntityStatus.ReceiveDisabled;
                await client.UpdateSubscriptionAsync(subscription);

                _logger?.LogTrace("Status updated on subscription successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not update status for subscription");
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
                _logger?.LogTrace("Updating status for subscription...");

                subscription.Value.Status = EntityStatus.Active;
                await client.UpdateSubscriptionAsync(subscription);

                _logger?.LogTrace("Status updated on subscription successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not update status for subscription");
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
                _logger?.LogTrace("Updating send status for topic...");

                topic.Value.Status = EntityStatus.SendDisabled;
                await client.UpdateTopicAsync(topic);

                _logger?.LogTrace("Send status updated on topic successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not update send status for topic");
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
                _logger?.LogTrace("Updating send status for topic...");

                topic.Value.Status = EntityStatus.Active;
                await client.UpdateTopicAsync(topic);

                _logger?.LogTrace("Send status updated on topic successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not update send status for topic");
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
            _logger?.LogInformation("Topic '{TopicName}' was not found.", topicName);
            return TopicSendState.NotFound;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger?.LogInformation("Topic '{TopicName}' was not found.", topicName);
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
                _logger?.LogTrace("Updating forward to for subscription...");

                subscription.Value.ForwardTo = forwardTo;
                await client.UpdateSubscriptionAsync(subscription);

                _logger?.LogTrace("Forward to updated on subscription successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not update forward to for subscription");
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
            _logger?.LogInformation("Subscription '{SubscriptionName}' on topic '{TopicName}' was not found.", subscriptionName, topicName);
            return SubscriptionState.NotFound;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger?.LogInformation("Subscription '{SubscriptionName}' on topic '{TopicName}' was not found.", subscriptionName, topicName);
            return SubscriptionState.NotFound;
        }
    }

}
