using Serilog;
using System.Threading.Tasks;

namespace NimBus.Management.ServiceBus;
public class EndpointManagement
{
    private readonly IServiceBusManagement _serviceBusManagement;
    private readonly ILogger _logger;

    public EndpointManagement(IServiceBusManagement serviceBusManagement, ILogger logger = null)
    {
        _serviceBusManagement = serviceBusManagement;
        _logger = logger;
    }

    public async Task ClearEndpoint(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        await _serviceBusManagement.DeleteSubscription(topicName, subscriptionName);

        await _serviceBusManagement.CreateSubscription(topicName, subscriptionName);

        await _serviceBusManagement.DeleteRule(topicName, subscriptionName, "$Default");

        await _serviceBusManagement.CreateRule(topicName, subscriptionName, $"to-{subscriptionName}");

        _logger?.Information("Cleared endpoint succesfully");
    }

    public async Task DisableEndpoint(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        await _serviceBusManagement.DisableSubscription(topicName, subscriptionName);

        _logger?.Information("Disabled endpoint succesfully");
    }

    public async Task EnableEndpoint(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        await _serviceBusManagement.EnableSubscription(topicName, subscriptionName);

        _logger?.Information("Enabled endpoint succesfully");
    }

    public async Task<bool> IsEndpointActive(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        return await _serviceBusManagement.IsSubscriptionActive(topicName, subscriptionName);
    }

    public async Task<SubscriptionState> GetEndpointSubscriptionState(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        return await _serviceBusManagement.GetSubscriptionState(topicName, subscriptionName);
    }
}
