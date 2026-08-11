using Amqp;
using Amqp.Listener;
using NimBus.ServiceBusEmulator.Broker;

namespace NimBus.ServiceBusEmulator.Protocol;

internal sealed class AmqpFrontend : IDisposable
{
    private readonly ContainerHost _host;
    private readonly HashSet<string> _managementNodes = new(StringComparer.OrdinalIgnoreCase);

    public AmqpFrontend(int port, BrokerNamespace broker, int maxMessageSize = 262_144)
    {
        var sessionLinks = new SessionLinkRegistry();
        _host = new ContainerHost(new Address($"amqp://127.0.0.1:{port}"));
        var listener = _host.Listeners[0];
        listener.SASL.EnableMechanism("MSSBCBS", new MssbcbsSaslProfile());
        listener.HandlerFactory = static _ => new GuidDeliveryTagHandler();
        _host.AddressResolver = static (_, attach) =>
        {
            var address = attach.Role
                ? (attach.Source as Amqp.Framing.Source)?.Address
                : (attach.Target as Amqp.Framing.Target)?.Address;
            return address?.TrimStart('/');
        };
        _host.RegisterRequestProcessor("$cbs", new CbsRequestProcessor());
        broker.TopicCreated += RegisterTopicManagement;
        broker.SubscriptionCreated += RegisterSubscriptionManagement;
        foreach (var topic in broker.GetTopics())
        {
            RegisterTopicManagement(topic.Name);
            foreach (var subscription in broker.GetSubscriptions(topic.Name))
            {
                RegisterSubscriptionManagement(topic.Name, subscription.Name);
            }
        }
        _host.RegisterLinkProcessor(new BrokerLinkProcessor(broker, sessionLinks, maxMessageSize));

        void RegisterTopicManagement(string topicName) => RegisterManagementNode(topicName);

        void RegisterSubscriptionManagement(string topicName, string subscriptionName) =>
            RegisterManagementNode($"{topicName}/Subscriptions/{subscriptionName}");

        void RegisterManagementNode(string entityPath)
        {
            var node = $"{entityPath}/$management";
            lock (_managementNodes)
            {
                if (_managementNodes.Add(node))
                {
                    _host.RegisterRequestProcessor(node, new ManagementRequestProcessor(broker, sessionLinks, entityPath));
                }
            }
        }
    }

    public void Start() => _host.Open();

    public void Dispose() => _host.Close();
}
