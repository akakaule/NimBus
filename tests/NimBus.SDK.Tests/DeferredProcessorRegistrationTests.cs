#pragma warning disable CA1707, CA2007
using System.Linq;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.SDK.Extensions;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Tests;

/// <summary>
/// Coverage for the deferred-processor hosted-service registration.
/// The host is *not* auto-registered by AddNimBusSubscriber — Worker hosts
/// opt in via AddNimBusDeferredProcessorHostedService; Functions hosts skip
/// this method entirely and add their own [ServiceBusTrigger] function class.
/// </summary>
[TestClass]
public class DeferredProcessorRegistrationTests
{
    private const string FakeConnection =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA=";

    [TestMethod]
    public void AddNimBusSubscriber_alone_does_not_register_the_deferred_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));

        services.AddNimBusSubscriber("EndpointA", _ => { });

        Assert.IsFalse(
            services.Any(d => d.ImplementationType == typeof(DeferredMessageProcessorHostedService)),
            "AddNimBusSubscriber must NOT auto-register the deferred BackgroundService — that was the opt-in flip. " +
            "Worker hosts now call AddNimBusDeferredProcessorHostedService explicitly.");
        Assert.IsFalse(
            services.Any(d => d.ServiceType == typeof(DeferredMessageProcessorHostedServiceOptions)),
            "Options singleton must also be skipped when the host is not requested.");
    }

    [TestMethod]
    public void Explicit_AddNimBusDeferredProcessorHostedService_registers_the_host_and_options()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("EndpointA", _ => { });

        services.AddNimBusDeferredProcessorHostedService("EndpointA");

        var hosted = services.Single(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(DeferredMessageProcessorHostedService));
        Assert.IsNotNull(hosted.ImplementationType,
            "Typed registration (not factory) is required so TryAddEnumerable can deduplicate.");

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<DeferredMessageProcessorHostedServiceOptions>();
        Assert.AreEqual("EndpointA", resolved.TopicName);
        Assert.AreEqual("deferredprocessor", resolved.SubscriptionName);
        Assert.AreEqual(1, resolved.MaxConcurrentCalls,
            "Default must stay 1 — the non-session trigger subscription has no other ordering mechanism.");
    }

    [TestMethod]
    public void Custom_max_concurrent_calls_flows_into_hosted_service_options()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("EndpointA", _ => { });

        services.AddNimBusDeferredProcessorHostedService("EndpointA", maxConcurrentCalls: 4);

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<DeferredMessageProcessorHostedServiceOptions>();
        Assert.AreEqual(4, resolved.MaxConcurrentCalls);
    }

    [TestMethod]
    public void Non_positive_max_concurrent_calls_throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));

        Assert.ThrowsException<System.ArgumentOutOfRangeException>(
            () => services.AddNimBusDeferredProcessorHostedService("EndpointA", maxConcurrentCalls: 0));
        Assert.ThrowsException<System.ArgumentOutOfRangeException>(
            () => services.AddNimBusDeferredProcessorHostedService("EndpointA", maxConcurrentCalls: -1));
    }

    [TestMethod]
    public void Custom_subscription_name_flows_into_hosted_service_options()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("EndpointA", _ => { });

        services.AddNimBusDeferredProcessorHostedService("EndpointA", subscriptionName: "custom-sub");

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<DeferredMessageProcessorHostedServiceOptions>();
        Assert.AreEqual("custom-sub", resolved.SubscriptionName);
    }

    [TestMethod]
    public void Repeat_AddNimBusDeferredProcessorHostedService_is_idempotent()
    {
        // TryAddEnumerable deduplicates on ImplementationType, so calling the
        // explicit opt-in twice must not stack a second BackgroundService.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("EndpointA", _ => { });

        services.AddNimBusDeferredProcessorHostedService("EndpointA");
        services.AddNimBusDeferredProcessorHostedService("EndpointA");

        var count = services.Count(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(DeferredMessageProcessorHostedService));
        Assert.AreEqual(1, count, "TryAddEnumerable must deduplicate the hosted-service registration.");
    }
}
