using Microsoft.Extensions.Logging;
using NimBus.Core.Messages;
using System;
using System.Threading.Tasks;

namespace NimBus.Management.ServiceBus;
public class EndpointManagement
{
    private readonly IServiceBusManagement _serviceBusManagement;
    private readonly ILogger _logger;

    public EndpointManagement(IServiceBusManagement serviceBusManagement, ILogger<EndpointManagement> logger = null)
    {
        _serviceBusManagement = serviceBusManagement;
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
    public EndpointManagement(IServiceBusManagement serviceBusManagement, Serilog.ILogger logger)
    {
        _serviceBusManagement = serviceBusManagement;
        _logger = logger is null ? null : new SerilogBridgeLogger(logger);
    }

    public async Task ClearEndpoint(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        await _serviceBusManagement.DeleteSubscription(topicName, subscriptionName);

        await _serviceBusManagement.CreateSubscription(topicName, subscriptionName);

        await _serviceBusManagement.DeleteRule(topicName, subscriptionName, "$Default");

        // Recreate the same rule set ServiceBusTopologyProvisioner.EnsureEndpointTopologyAsync
        // puts on the endpoint's own subscription. Filter/action strings must stay
        // byte-identical to the provisioner's: its RuleMatches compares ordinally, so any
        // drift (even whitespace) makes the next `topology apply` delete and recreate rules.
        await _serviceBusManagement.CreateCustomRule(
            topicName, subscriptionName, $"to-{subscriptionName}",
            $"user.To = '{subscriptionName}'", action: null);
        await _serviceBusManagement.CreateCustomRule(
            topicName, subscriptionName, "continuation",
            $"user.To = '{Constants.ContinuationId}'",
            $"SET user.To = '{subscriptionName}'; SET user.From = '{Constants.ContinuationId}'");
        await _serviceBusManagement.CreateCustomRule(
            topicName, subscriptionName, "retry",
            $"user.To = '{Constants.RetryId}'",
            $"SET user.To = '{subscriptionName}'; SET user.From = '{Constants.RetryId}'");

        _logger?.LogInformation("Cleared endpoint successfully");
    }

    public async Task DisableEndpoint(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        await _serviceBusManagement.DisableSubscription(topicName, subscriptionName);

        _logger?.LogInformation("Disabled endpoint successfully");
    }

    public async Task EnableEndpoint(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        await _serviceBusManagement.EnableSubscription(topicName, subscriptionName);

        _logger?.LogInformation("Enabled endpoint successfully");
    }

    public async Task<bool> IsEndpointActive(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        return await _serviceBusManagement.IsSubscriptionActive(topicName, subscriptionName);
    }

    public async Task DisableEndpointSend(string endpointName)
    {
        string topicName = endpointName;

        await _serviceBusManagement.DisableTopicSend(topicName);

        _logger?.LogInformation("Disabled endpoint send successfully");
    }

    public async Task EnableEndpointSend(string endpointName)
    {
        string topicName = endpointName;

        await _serviceBusManagement.EnableTopicSend(topicName);

        _logger?.LogInformation("Enabled endpoint send successfully");
    }

    public async Task<TopicSendState> GetEndpointSendState(string endpointName)
    {
        string topicName = endpointName;

        return await _serviceBusManagement.GetTopicSendState(topicName);
    }

    public async Task<SubscriptionState> GetEndpointSubscriptionState(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        return await _serviceBusManagement.GetSubscriptionState(topicName, subscriptionName);
    }
}
