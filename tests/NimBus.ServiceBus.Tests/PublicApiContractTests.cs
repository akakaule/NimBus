#pragma warning disable CA1707
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.ServiceBus;
using System.Linq;
using System.Threading;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class PublicApiContractTests
{
    [TestMethod]
    public void IMessage_ExposesFromProperty()
    {
        var property = typeof(IMessage).GetProperty(nameof(IMessage.From));

        Assert.IsNotNull(property);
        Assert.AreEqual(typeof(string), property.PropertyType);
    }

    [TestMethod]
    public void ISubscriberClient_ExtendsIMessageHandler()
    {
        // ISubscriberClient is transport-neutral; it inherits from
        // IMessageHandler so consumers receive messages through the pipeline-
        // terminus contract. The legacy IServiceBusAdapter inheritance has
        // been removed (slice 4 of #18).
        Assert.IsTrue(typeof(IMessageHandler).IsAssignableFrom(typeof(ISubscriberClient)));
        Assert.IsFalse(typeof(IServiceBusAdapter).IsAssignableFrom(typeof(ISubscriberClient)),
            "ISubscriberClient must NOT inherit IServiceBusAdapter — that's the transport leak we removed.");
    }

    [TestMethod]
    public void SubscriberClient_DoesNotImplementIServiceBusAdapter_ForSC005()
    {
        // SC-005: NimBus.SDK assemblies have zero compile-time references to
        // Azure.Messaging.ServiceBus. The previously-staged [Obsolete] ASB-typed
        // Handle overloads on the concrete SubscriberClient have been dropped;
        // consumers that need the ASB-shaped surface inject IServiceBusAdapter
        // (registered by AddServiceBusReceiver in NimBus.ServiceBus) directly.
        Assert.IsFalse(typeof(IServiceBusAdapter).IsAssignableFrom(typeof(SubscriberClient)),
            "SubscriberClient must NOT implement IServiceBusAdapter — SC-005 requires zero ASB compile-time references in NimBus.SDK.");
    }

    [TestMethod]
    public void IServiceBusAdapter_ExposesExpectedHandleOverloads()
    {
        var signatures = typeof(IServiceBusAdapter)
            .GetMethods()
            .Where(method => method.Name == "Handle")
            .Select(method => string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name)))
            .OrderBy(signature => signature)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                $"{nameof(ServiceBusReceivedMessage)}, {nameof(ServiceBusSessionMessageActions)}, {nameof(CancellationToken)}",
                $"{nameof(ServiceBusReceivedMessage)}, {nameof(ServiceBusMessageActions)}, {nameof(ServiceBusSessionMessageActions)}, {nameof(CancellationToken)}",
                $"{nameof(ServiceBusReceivedMessage)}, {nameof(ServiceBusSessionReceiver)}, {nameof(CancellationToken)}",
                $"{nameof(ProcessSessionMessageEventArgs)}, {nameof(CancellationToken)}",
            },
            signatures);
    }

    [TestMethod]
    public void ISubscriberClient_ExposesGenericRegisterHandlerMethod()
    {
        var method = typeof(ISubscriberClient).GetMethods().Single(candidate => candidate.Name == "RegisterHandler");

        Assert.IsTrue(method.IsGenericMethodDefinition);
        Assert.AreEqual(1, method.GetGenericArguments().Length);
    }
}
