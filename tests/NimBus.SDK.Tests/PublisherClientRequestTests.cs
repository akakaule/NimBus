#pragma warning disable CA1707, CA2007

using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.Extensions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.Tests;

[TestClass]
public class PublisherClientRequestTests
{
    private sealed class PingRequest : Event
    {
        public string Text { get; set; }
    }

    private sealed class PongResponse
    {
        public string Echo { get; set; }
    }

    private sealed class PingHandler : IRequestHandler<PingRequest, PongResponse>
    {
        public Task<PongResponse> Handle(PingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new PongResponse { Echo = request.Text });
    }

    private sealed class CapturingSender : ISender
    {
        public List<IMessage> Sent { get; } = new();

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
            => Task.FromResult(1L);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // Accepting the reply session throws a marker so the test can assert on the
    // SENT message without standing up a functioning reply pipeline.
    private sealed class MarkerException : Exception
    {
    }

    private sealed class MarkerServiceBusClient : ServiceBusClient
    {
        public string AcceptedTopic { get; private set; }
        public string AcceptedSubscription { get; private set; }
        public string AcceptedSessionId { get; private set; }

        public override Task<ServiceBusSessionReceiver> AcceptSessionAsync(
            string topicName, string subscriptionName, string sessionId,
            ServiceBusSessionReceiverOptions options = default, CancellationToken cancellationToken = default)
        {
            AcceptedTopic = topicName;
            AcceptedSubscription = subscriptionName;
            AcceptedSessionId = sessionId;
            throw new MarkerException();
        }
    }

    [TestMethod]
    public async Task Request_StampsReplyToWithPublisherEndpoint_AndAcceptsEndpointReplySubscription()
    {
        var sender = new CapturingSender();
        var client = new MarkerServiceBusClient();
        var publisher = new PublisherClient(sender, "CrmEndpoint") { ReplyServiceBusClient = client };

        await Assert.ThrowsExactlyAsync<MarkerException>(() =>
            publisher.Request<PingRequest, PongResponse>(new PingRequest { Text = "hi" }, TimeSpan.FromSeconds(5)));

        Assert.AreEqual(1, sender.Sent.Count);
        var sent = sender.Sent[0];
        Assert.AreEqual("CrmEndpoint", sent.ReplyTo, "ReplyTo must be the publisher's endpoint, not the event-type id");
        Assert.AreEqual("PingRequest", sent.To, "To must stay the event-type id for routing");
        Assert.IsFalse(string.IsNullOrEmpty(sent.ReplyToSessionId));

        Assert.AreEqual("CrmEndpoint", client.AcceptedTopic);
        Assert.AreEqual("CrmEndpoint-reply", client.AcceptedSubscription);
        Assert.AreEqual(sent.ReplyToSessionId, client.AcceptedSessionId);
    }

    [TestMethod]
    public async Task Request_WithoutServiceBusClient_ThrowsWithGuidance()
    {
        var publisher = new PublisherClient(new CapturingSender(), "CrmEndpoint");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            publisher.Request<PingRequest, PongResponse>(new PingRequest(), TimeSpan.FromSeconds(1)));

        StringAssert.Contains(ex.Message, "AddNimBusPublisher");
    }

    [TestMethod]
    public async Task Request_WithoutPublisherEndpoint_ThrowsWithGuidance()
    {
        var publisher = new PublisherClient(new CapturingSender())
        {
            ReplyServiceBusClient = new MarkerServiceBusClient(),
        };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            publisher.Request<PingRequest, PongResponse>(new PingRequest(), TimeSpan.FromSeconds(1)));

        StringAssert.Contains(ex.Message, "-reply");
    }

    [TestMethod]
    public void AddRequestHandler_RegistersHandlerAndRejectsDuplicates()
    {
        var services = new ServiceCollection();
        var builder = new NimBusSubscriberBuilder(services);

        builder.AddRequestHandler<PingRequest, PongResponse, PingHandler>();

        Assert.AreEqual(1, builder.HandlerRegistrations.Count);
        Assert.AreEqual("PingRequest", builder.HandlerRegistrations[0].EventTypeId);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => builder.AddRequestHandler<PingRequest, PongResponse, PingHandler>());
        StringAssert.Contains(ex.Message, "PingRequest");
    }

    [TestMethod]
    public void AddRequestHandler_ConflictsWithTypedHandlerForSameEventType()
    {
        var services = new ServiceCollection();
        var builder = new NimBusSubscriberBuilder(services);
        builder.AddHandler<PingRequest, PingEventHandler>();

        Assert.ThrowsExactly<InvalidOperationException>(
            () => builder.AddRequestHandler<PingRequest, PongResponse, PingHandler>());
    }

    private sealed class PingEventHandler : NimBus.SDK.EventHandlers.IEventHandler<PingRequest>
    {
        public Task Handle(PingRequest message, NimBus.SDK.EventHandlers.IEventHandlerContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
