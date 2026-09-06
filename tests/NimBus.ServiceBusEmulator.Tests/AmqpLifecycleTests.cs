#pragma warning disable CA1707, CA2007

using System.Net;
using System.Net.Sockets;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using NimBus.ServiceBusEmulator.Broker;
using NimBus.ServiceBusEmulator.Protocol;

namespace NimBus.ServiceBusEmulator.Tests;

[TestClass]
public sealed class AmqpLifecycleTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task Closing_a_pending_session_attach_keeps_other_links_on_the_connection_usable()
    {
        var broker = CreateBroker();
        using var failures = new FailureLogger();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(failures));
        var port = GetPort();
        using var frontend = new AmqpFrontend(port, broker, loggerFactory: loggerFactory);
        frontend.Start();
        var factory = new Amqp.ConnectionFactory();
        factory.SASL.Profile = new MssbcbsSaslProfile();
        var connection = await factory.CreateAsync(new Amqp.Address($"amqp://127.0.0.1:{port}"));
        try
        {
            var session = new Amqp.Session(connection);
            var sender = new Amqp.SenderLink(session, "sender", "events");
            var pending = new Amqp.ReceiverLink(session, "pending", new Amqp.Framing.Source
            {
                Address = "events/Subscriptions/consumer",
                FilterSet = new Amqp.Types.Map { [new Amqp.Types.Symbol("com.microsoft:session-filter")] = null },
            }, null);
            // No session is available. Detach before the asynchronous attach completes.
            pending.Close(TimeSpan.Zero);
            var message = new Amqp.Message
            {
                BodySection = new Amqp.Framing.Data { Binary = "connection remains usable"u8.ToArray() },
                Properties = new Amqp.Framing.Properties { GroupId = "S" },
            };
            await sender.SendAsync(message).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNull(connection.Error);
            Assert.IsFalse(failures.Failure.Task.IsCompleted, "Cancelling a pending attach is normal link cleanup.");
        }
        finally
        {
            await connection.CloseAsync(TimeSpan.FromSeconds(2));
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Deleting_subscription_while_receiving_closes_the_link_promptly()
    {
        var broker = CreateBroker();
        var port = GetPort();
        using var failures = new FailureLogger();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(failures));
        using var frontend = new AmqpFrontend(port, broker, loggerFactory: loggerFactory);
        frontend.Start();
        await using var client = CreateClient(port);
        await using var receiver = await client.AcceptSessionAsync("events", "consumer", "S");
        // Complete a delivery first, proving the receive pump is attached.
        await using var sender = client.CreateSender("events");
        await sender.SendMessageAsync(new ServiceBusMessage("first") { SessionId = "S" });
        var first = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(first);
        await receiver.CompleteMessageAsync(first);

        var receive = receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        broker.DeleteSubscription("events", "consumer");
        // The SDK completes an outstanding receive with no message on remote detach.
        Assert.IsNull(await receive.WaitAsync(TimeSpan.FromSeconds(4)));
        var exception = await Assert.ThrowsAsync<ServiceBusException>(async () =>
            await receiver.RenewSessionLockAsync());
        Assert.AreEqual(ServiceBusFailureReason.SessionLockLost, exception.Reason);
        Assert.IsInstanceOfType<KeyNotFoundException>(await failures.Failure.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Empty_session_accept_returns_service_timeout_and_keeps_connection_usable()
    {
        var broker = CreateBroker();
        var port = GetPort();
        using var frontend = new AmqpFrontend(port, broker);
        frontend.Start();
        await using var client = CreateClient(port);

        var exception = await Assert.ThrowsAsync<ServiceBusException>(async () =>
            await client.AcceptNextSessionAsync("events", "consumer"));
        Assert.AreEqual(ServiceBusFailureReason.ServiceTimeout, exception.Reason);

        await using var sender = client.CreateSender("events");
        await sender.SendMessageAsync(new ServiceBusMessage("after timeout") { SessionId = "S" });
        await using var receiver = await client.AcceptNextSessionAsync("events", "consumer");
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(message);
        await receiver.CompleteMessageAsync(message);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Empty_receives_drain_promptly_and_session_can_renew_and_receive_again()
    {
        var broker = CreateBroker();
        var port = GetPort();
        using var frontend = new AmqpFrontend(port, broker);
        frontend.Start();
        await using var client = CreateClient(port);
        await using var receiver = await client.AcceptSessionAsync("events", "consumer", "S");

        for (var iteration = 0; iteration < 3; iteration++)
        {
            Assert.IsNull(await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(3)));
        }

        await receiver.RenewSessionLockAsync();
        await using var sender = client.CreateSender("events");
        await sender.SendMessageAsync(new ServiceBusMessage("after drain") { SessionId = "S" });
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(message);
        Assert.AreEqual("after drain", message.Body.ToString());
        await receiver.CompleteMessageAsync(message);
    }

    private static BrokerNamespace CreateBroker()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription("events", new SubscriptionDefinition("consumer") { RequiresSession = true });
        return broker;
    }

    private static ServiceBusClient CreateClient(int port) => new(
        $"Endpoint=sb://127.0.0.1:{port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=local;UseDevelopmentEmulator=true;",
        new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions { MaxRetries = 0, TryTimeout = TimeSpan.FromSeconds(2) },
        });

    private static int GetPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FailureLogger : ILoggerProvider, ILogger
    {
        public TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => this;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error && exception is not null)
            {
                Failure.TrySetResult(exception);
            }
        }

        public void Dispose()
        {
        }
    }
}
