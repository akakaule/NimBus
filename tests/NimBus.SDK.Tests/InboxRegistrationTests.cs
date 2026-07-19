#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.Extensions;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Tests;

[TestClass]
public sealed class InboxRegistrationTests
{
    private const string FakeConnection =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA=";

    [TestMethod]
    public void UseInbox_requires_an_explicit_provider()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            builder.UseInbox(_ => { }));

        StringAssert.Contains(exception.Message, nameof(InboxOptions.DeduplicationStore));
    }

    [TestMethod]
    public void UseInbox_rejects_an_unknown_provider()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            builder.UseInbox(options => options.DeduplicationStore = (InboxStore)999));

        Assert.AreEqual(nameof(InboxOptions.DeduplicationStore), exception.ParamName);
    }

    [TestMethod]
    public void UseInbox_can_only_be_configured_once()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        builder.UseInbox(options => options.DeduplicationStore = InboxStore.InMemory);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            builder.UseInbox(options => options.DeduplicationStore = InboxStore.SqlServer));

        StringAssert.Contains(exception.Message, "already configured");
    }

    [TestMethod]
    public void Missing_selected_provider_fails_when_subscriber_is_started_with_guidance()
    {
        var services = CreateServices();
        services.AddNimBusSubscriber(
            "Billing",
            builder => builder.UseInbox(options =>
                options.DeduplicationStore = InboxStore.SqlServer));

        using var provider = services.BuildServiceProvider();
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            provider.GetRequiredService<ISubscriberClient>());

        StringAssert.Contains(exception.Message, "SqlServer");
        StringAssert.Contains(exception.Message, "AddNimBusSqlServerInbox");
        StringAssert.Contains(exception.Message, "keyed");
    }

    [TestMethod]
    public void UseInbox_resolves_selected_keyed_store_wraps_handler_and_registers_cleanup()
    {
        var inMemory = new NoopInboxStore();
        var sqlServer = new NoopInboxStore();
        var services = CreateServices();
        services.AddKeyedSingleton<IInboxStore>(InboxStore.InMemory, inMemory);
        services.AddKeyedSingleton<IInboxStore>(InboxStore.SqlServer, sqlServer);
        services.AddNimBusSubscriber(
            "Billing",
            builder => builder.UseInbox(options =>
            {
                options.DeduplicationStore = InboxStore.SqlServer;
                options.RetentionPeriod = TimeSpan.FromDays(3);
                options.CleanupInterval = TimeSpan.FromMinutes(20);
            }));

        using var provider = services.BuildServiceProvider();
        var subscriber = provider.GetRequiredService<ISubscriberClient>();
        var strictHandler = GetStrictHandler(subscriber);
        var contextHandler = GetPrivateField(strictHandler, "_eventContextHandler");

        Assert.IsInstanceOfType<InboxMiddleware>(contextHandler);
        Assert.AreSame(sqlServer, GetPrivateField(contextHandler, "_inboxStore"));
        Assert.IsNotNull(provider.GetService<MessageLifecycleNotifier>());
        Assert.AreEqual(
            1,
            provider.GetServices<IHostedService>().OfType<InboxPurgeHostedService>().Count());
    }

    [TestMethod]
    public void Subscriber_without_UseInbox_has_no_decorator_notifier_or_cleanup_host()
    {
        var services = CreateServices();
        services.AddNimBusSubscriber("Billing", _ => { });

        using var provider = services.BuildServiceProvider();
        var subscriber = provider.GetRequiredService<ISubscriberClient>();
        var strictHandler = GetStrictHandler(subscriber);
        var contextHandler = GetPrivateField(strictHandler, "_eventContextHandler");

        Assert.IsNotInstanceOfType<InboxMiddleware>(contextHandler);
        Assert.IsNull(provider.GetService<MessageLifecycleNotifier>());
        Assert.AreEqual(
            0,
            provider.GetServices<IHostedService>().OfType<InboxPurgeHostedService>().Count());
    }

    [TestMethod]
    public void Later_same_endpoint_registration_cannot_partially_enable_inbox()
    {
        var services = CreateServices();
        services.AddNimBusSubscriber("Billing", _ => { });
        services.AddNimBusSubscriber(
            "Billing",
            builder => builder.UseInbox(options =>
                options.DeduplicationStore = InboxStore.SqlServer));

        using var provider = services.BuildServiceProvider();
        var subscriber = provider.GetRequiredService<ISubscriberClient>();
        var strictHandler = GetStrictHandler(subscriber);

        Assert.IsNotInstanceOfType<InboxMiddleware>(
            GetPrivateField(strictHandler, "_eventContextHandler"));
        Assert.IsNull(provider.GetService<MessageLifecycleNotifier>());
        Assert.AreEqual(
            0,
            provider.GetServices<IHostedService>().OfType<InboxPurgeHostedService>().Count());
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        return services;
    }

    private static object GetStrictHandler(ISubscriberClient subscriber)
    {
        var adapter = GetPrivateField(subscriber, "_serviceBusAdapter");
        return GetPrivateField(adapter, "_messageHandler");
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"Expected private field '{fieldName}' on {target.GetType().Name}.");
        return field.GetValue(target)!;
    }

    private sealed class NoopInboxStore : IInboxStore
    {
        public Task<bool> HasProcessedAsync(
            string messageId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task RecordProcessedAsync(
            string messageId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> PurgeExpiredAsync(
            DateTimeOffset olderThan,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
