#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.SDK;
using NimBus.SDK.Extensions;

namespace NimBus.SDK.Tests;

/// <summary>
/// Coverage for <see cref="IHandoffClientFactory"/> — the runtime per-endpoint
/// settlement-client factory used by processes that serve arbitrary endpoints
/// (e.g. the management WebApp), where the endpoint set is not known at
/// registration time and keyed singletons can't be pre-registered.
/// </summary>
[TestClass]
public class HandoffClientFactoryTests
{
    private static ServiceBusClient FakeClient() =>
        new("Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA=");

    [TestMethod]
    public void ForEndpoint_returns_cached_instance_for_same_endpoint()
    {
        var factory = new HandoffClientFactory(FakeClient());

        var first = factory.ForEndpoint("EndpointA");
        var second = factory.ForEndpoint("EndpointA");

        Assert.AreSame(first, second,
            "Same endpoint must reuse the cached client (and its long-lived sender).");
    }

    [TestMethod]
    public void ForEndpoint_returns_distinct_instances_per_endpoint()
    {
        var factory = new HandoffClientFactory(FakeClient());

        var a = factory.ForEndpoint("EndpointA");
        var b = factory.ForEndpoint("EndpointB");

        Assert.AreNotSame(a, b,
            "Each endpoint must get its own client bound to its own topic sender.");
    }

    [TestMethod]
    public void ForEndpoint_with_null_or_empty_endpoint_throws()
    {
        var factory = new HandoffClientFactory(FakeClient());

        Assert.ThrowsExactly<ArgumentException>(() => factory.ForEndpoint(""));
        Assert.ThrowsExactly<ArgumentException>(() => factory.ForEndpoint(null!));
    }

    [TestMethod]
    public void AddNimBusHandoffClientFactory_resolves_and_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddSingleton(FakeClient());
        services.AddNimBusHandoffClientFactory();
        services.AddNimBusHandoffClientFactory();

        Assert.AreEqual(1, services.Count(d => d.ServiceType == typeof(IHandoffClientFactory)),
            "Registration must TryAdd — calling it twice must not stack descriptors.");

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHandoffClientFactory>();

        Assert.IsNotNull(factory.ForEndpoint("EndpointA"));
    }
}
