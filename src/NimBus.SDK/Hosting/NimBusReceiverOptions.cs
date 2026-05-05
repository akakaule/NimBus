using System;
using NimBus.ServiceBus.Hosting;

namespace NimBus.SDK.Hosting
{
    /// <summary>
    /// Configuration options for the NimBus receiver hosted service.
    /// </summary>
    [Obsolete("Use NimBus.ServiceBus.Hosting.ServiceBusReceiverOptions. " +
              "This type is a transport-leaking bridge kept for one major version " +
              "while NimBus.SDK is detached from Azure.Messaging.ServiceBus.", false)]
    public class NimBusReceiverOptions : ServiceBusReceiverOptions
    {
    }
}
