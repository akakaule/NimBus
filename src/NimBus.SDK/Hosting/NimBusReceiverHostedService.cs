using System;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.ServiceBus;
using NimBus.ServiceBus.Hosting;

namespace NimBus.SDK.Hosting
{
    /// <summary>
    /// Background service that receives messages from a Service Bus topic/subscription
    /// using a <see cref="ServiceBusSessionProcessor"/> and delegates to <see cref="IServiceBusAdapter"/>.
    /// </summary>
    [Obsolete("Use NimBus.ServiceBus.Hosting.ServiceBusReceiverHostedService. " +
              "This type is a transport-leaking bridge kept for one major version " +
              "while NimBus.SDK is detached from Azure.Messaging.ServiceBus.", false)]
    public class NimBusReceiverHostedService : ServiceBusReceiverHostedService
    {
        public NimBusReceiverHostedService(
            ServiceBusClient client,
            IServiceBusAdapter adapter,
            NimBusReceiverOptions options,
            ILogger<NimBusReceiverHostedService> logger)
            : base(client, adapter, options, logger as ILogger<ServiceBusReceiverHostedService> ?? NullLogger<ServiceBusReceiverHostedService>.Instance)
        {
        }
    }
}
