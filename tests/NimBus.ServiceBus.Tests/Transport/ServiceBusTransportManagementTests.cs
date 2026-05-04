#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Management.ServiceBus;
using NimBus.ServiceBus.Transport;
using NimBus.Transport.Abstractions;

namespace NimBus.ServiceBus.Tests.Transport;

[TestClass]
public class ServiceBusTransportManagementTests
{
    [TestMethod]
    public async Task Constructor_NullManagement_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new ServiceBusTransportManagement(null!));
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task DeclareEndpointAsync_CreatesTopicAndSubscription()
    {
        var fake = new FakeServiceBusManagement();
        var management = new ServiceBusTransportManagement(fake);

        await management.DeclareEndpointAsync(
            new EndpointConfig("orders", RequiresOrderedDelivery: true, MaxConcurrency: 4),
            CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "orders" }, fake.CreatedTopics);
        Assert.AreEqual(1, fake.CreatedSubscriptions.Count);
        Assert.AreEqual(("orders", "orders"), fake.CreatedSubscriptions[0]);
    }

    [TestMethod]
    public async Task DeclareEndpointAsync_NullConfig_Throws()
    {
        var management = new ServiceBusTransportManagement(new FakeServiceBusManagement());

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            management.DeclareEndpointAsync(null!, CancellationToken.None));
    }

    [TestMethod]
    public async Task DeclareEndpointAsync_CancelledToken_Throws()
    {
        var management = new ServiceBusTransportManagement(new FakeServiceBusManagement());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            management.DeclareEndpointAsync(
                new EndpointConfig("orders", false, null),
                cts.Token));
    }

    [TestMethod]
    public async Task RemoveEndpointAsync_DeletesSelfNamedSubscription()
    {
        var fake = new FakeServiceBusManagement();
        var management = new ServiceBusTransportManagement(fake);

        await management.RemoveEndpointAsync("orders", CancellationToken.None);

        Assert.AreEqual(1, fake.DeletedSubscriptions.Count);
        Assert.AreEqual(("orders", "orders"), fake.DeletedSubscriptions[0]);
    }

    [TestMethod]
    public async Task RemoveEndpointAsync_NullName_Throws()
    {
        var management = new ServiceBusTransportManagement(new FakeServiceBusManagement());

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            management.RemoveEndpointAsync(null!, CancellationToken.None));
    }

    [TestMethod]
    public async Task ListEndpointsAsync_ThrowsNotSupported()
    {
        var management = new ServiceBusTransportManagement(new FakeServiceBusManagement());

        await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            management.ListEndpointsAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task PurgeEndpointAsync_ThrowsNotSupported()
    {
        var management = new ServiceBusTransportManagement(new FakeServiceBusManagement());

        await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            management.PurgeEndpointAsync("orders", CancellationToken.None));
    }

    private sealed class FakeServiceBusManagement : IServiceBusManagement
    {
        public List<string> CreatedTopics { get; } = new();
        public List<(string Topic, string Subscription)> CreatedSubscriptions { get; } = new();
        public List<(string Topic, string Subscription)> DeletedSubscriptions { get; } = new();

        public Task CreateTopic(string topicName)
        {
            CreatedTopics.Add(topicName);
            return Task.CompletedTask;
        }

        public Task CreateSubscription(string topicName, string subscriptionName)
        {
            CreatedSubscriptions.Add((topicName, subscriptionName));
            return Task.CompletedTask;
        }

        public Task DeleteSubscription(string topicName, string subscriptionName)
        {
            DeletedSubscriptions.Add((topicName, subscriptionName));
            return Task.CompletedTask;
        }

        public Task CreateRule(string topicName, string subscriptionName, string ruleName) => Task.CompletedTask;
        public Task CreateEventTypeRule(string topicName, string subscriptionName, string ruleName, string eventtype) => Task.CompletedTask;
        public Task CreateCustomRule(string topicName, string subscriptionName, string ruleName, string filter, string action) => Task.CompletedTask;
        public Task CreateForwardSubscription(string topicName, string subscriptionName, string forwardTo) => Task.CompletedTask;
        public Task DeleteRule(string topicName, string subscriptionName, string ruleName) => Task.CompletedTask;
        public Task DisableSubscription(string topicName, string subscriptionName) => Task.CompletedTask;
        public Task EnableSubscription(string topicName, string subscriptionName) => Task.CompletedTask;
        public Task<bool> IsSubscriptionActive(string topicName, string subscriptionName) => Task.FromResult(true);
        public Task<SubscriptionState> GetSubscriptionState(string topicName, string subscriptionName) => Task.FromResult(SubscriptionState.Active);
        public Task UpdateForwardTo(string topicName, string subscriptionName, string forwardTo) => Task.CompletedTask;
        public Task CreateDeferredSubscription(string topicName) => Task.CompletedTask;
        public Task CreateDeferredProcessorSubscription(string topicName) => Task.CompletedTask;
    }
}
