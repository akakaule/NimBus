#pragma warning disable CA1707, CA2007
using System;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.SDK.Extensions;

namespace NimBus.SDK.Tests;

/// <summary>
/// Coverage for <see cref="ServiceCollectionExtensions.AddNimBusSubscriber"/>'s
/// one-endpoint-per-process invariant. <see cref="ISubscriberClient"/> is a
/// non-keyed singleton; a second registration against a different endpoint
/// used to be silently dropped by TryAddSingleton, binding the second
/// endpoint's handlers to a no-op. The guard now throws so the misuse is
/// loud at startup.
/// </summary>
[TestClass]
public class SubscriberRegistrationTests
{
    private const string FakeConnection =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA=";

    [TestMethod]
    public void Second_AddNimBusSubscriber_with_different_endpoint_throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("EndpointA", _ => { });

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            services.AddNimBusSubscriber("EndpointB", _ => { }));

        StringAssert.Contains(ex.Message, "EndpointA");
        StringAssert.Contains(ex.Message, "EndpointB");
    }

    [TestMethod]
    public void Second_AddNimBusSubscriber_with_same_endpoint_is_benign()
    {
        // Idempotent registration is harmless — TryAdd would keep the first
        // ISubscriberClient anyway. The guard only fires on a *different*
        // endpoint, where the silent-drop bug was real.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("EndpointA", _ => { });
        services.AddNimBusSubscriber("EndpointA", _ => { });
    }
}
