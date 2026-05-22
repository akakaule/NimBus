#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.SDK.Extensions;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Tests;

/// <summary>
/// Coverage for the deferred-processor hosted-service registration that
/// <c>AddNimBusSubscriber</c> performs by default. The risky bits are the
/// DI shape (typed singleton + TryAddEnumerable so repeated same-endpoint
/// calls don't stack duplicates) and the conflict-detection on a second
/// call with different deferred-host options.
/// </summary>
[TestClass]
public class DeferredProcessorRegistrationTests
{
    private const string FakeConnection =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA=";

    [TestMethod]
    public void Default_registration_adds_the_deferred_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));

        services.AddNimBusSubscriber("EndpointA", _ => { });

        var descriptor = services.Single(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(DeferredMessageProcessorHostedService));

        // Typed registration (not factory) — ImplementationType is non-null,
        // which is what makes the same-endpoint deduplication via
        // TryAddEnumerable work.
        Assert.IsNotNull(descriptor.ImplementationType);
    }

    [TestMethod]
    public void Disable_flag_suppresses_the_deferred_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));

        services.AddNimBusSubscriber(
            opts =>
            {
                opts.Endpoint = "EndpointA";
                opts.DisableDeferredProcessorHostedService = true;
            },
            _ => { });

        Assert.IsFalse(
            services.Any(d => d.ImplementationType == typeof(DeferredMessageProcessorHostedService)),
            "DisableDeferredProcessorHostedService = true must suppress the hosted-service registration.");
        Assert.IsFalse(
            services.Any(d => d.ServiceType == typeof(DeferredMessageProcessorHostedServiceOptions)),
            "Options singleton must also be skipped when the hosted service is disabled.");
    }

    [TestMethod]
    public void Subscription_name_option_flows_into_hosted_service_options()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));

        services.AddNimBusSubscriber(
            opts =>
            {
                opts.Endpoint = "EndpointA";
                opts.DeferredProcessorSubscriptionName = "custom-sub";
            },
            _ => { });

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<DeferredMessageProcessorHostedServiceOptions>();

        Assert.AreEqual("EndpointA", resolved.TopicName);
        Assert.AreEqual("custom-sub", resolved.SubscriptionName);
    }

    [TestMethod]
    public void Same_endpoint_does_not_register_duplicate_hosted_service()
    {
        // TryAddEnumerable deduplicates on ImplementationType, so a second
        // AddNimBusSubscriber call with the same endpoint must not add a
        // second copy of the BackgroundService.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));

        services.AddNimBusSubscriber("EndpointA", _ => { });
        services.AddNimBusSubscriber("EndpointA", _ => { });

        var count = services.Count(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(DeferredMessageProcessorHostedService));

        Assert.AreEqual(1, count, "TryAddEnumerable must deduplicate the hosted-service registration.");
    }

    [TestMethod]
    public void Second_call_with_conflicting_Disable_flag_throws()
    {
        // First-call-wins semantics on TryAddSingleton + TryAddEnumerable mean
        // a later Disable=true cannot remove the hosted service the first call
        // already registered. Throw to surface the misuse loudly.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("EndpointA", _ => { }); // default: Disable=false

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            services.AddNimBusSubscriber(
                opts =>
                {
                    opts.Endpoint = "EndpointA";
                    opts.DisableDeferredProcessorHostedService = true;
                },
                _ => { }));

        StringAssert.Contains(ex.Message, "DisableDeferredProcessorHostedService");
        StringAssert.Contains(ex.Message, "EndpointA");
    }

    [TestMethod]
    public void Second_call_with_conflicting_subscription_name_throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("EndpointA", _ => { }); // default sub name: deferredprocessor

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            services.AddNimBusSubscriber(
                opts =>
                {
                    opts.Endpoint = "EndpointA";
                    opts.DeferredProcessorSubscriptionName = "different-sub";
                },
                _ => { }));

        StringAssert.Contains(ex.Message, "DeferredProcessorSubscriptionName");
        StringAssert.Contains(ex.Message, "different-sub");
    }

    [TestMethod]
    public void Same_endpoint_with_matching_deferred_options_is_benign()
    {
        // The conflict check only fires on a mismatch. Calling twice with the
        // same opts (the common idempotency pattern) must not throw.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));

        services.AddNimBusSubscriber(
            opts =>
            {
                opts.Endpoint = "EndpointA";
                opts.DisableDeferredProcessorHostedService = true;
                opts.DeferredProcessorSubscriptionName = "custom-sub";
            },
            _ => { });

        services.AddNimBusSubscriber(
            opts =>
            {
                opts.Endpoint = "EndpointA";
                opts.DisableDeferredProcessorHostedService = true;
                opts.DeferredProcessorSubscriptionName = "custom-sub";
            },
            _ => { });
        // assertion: no throw
    }
}
