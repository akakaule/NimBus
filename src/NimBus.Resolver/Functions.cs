using Azure.Messaging.ServiceBus;
using NimBus.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using System;
using System.Threading.Tasks;

namespace NimBus.Resolver
{
    public class Functions
    {
        private readonly IServiceBusAdapter serviceBusAdapter;

        public Functions(IServiceBusAdapter serviceBusAdapter)
        {
            this.serviceBusAdapter = serviceBusAdapter ?? throw new ArgumentNullException(nameof(serviceBusAdapter));
        }

        [Function("Resolver")]
        public async Task RunAsync([ServiceBusTrigger("%ResolverId%", "%ResolverId%", Connection = "AzureWebJobsServiceBus", IsSessionsEnabled = true)]
            ServiceBusReceivedMessage message,
            ServiceBusMessageActions messageActions,
            ServiceBusSessionMessageActions sessionActions)
        {
            await serviceBusAdapter.Handle(message, messageActions, sessionActions);
        }
    }
}
