#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Tests;

/// <summary>
/// Coverage for <see cref="DeferredMessageDispatcher.ProcessAsync"/> — the
/// host-agnostic body that the Worker BackgroundService and the Azure
/// Functions trigger class both call. Centralising SessionId extraction +
/// SessionCannotBeLocked handling means these tests are the single source of
/// truth for that behaviour; the host shells just map the outcome.
/// </summary>
[TestClass]
public class DeferredMessageDispatcherTests
{
    [TestMethod]
    public async Task DeadLetter_when_session_id_missing_on_both_sources()
    {
        var processor = new RecordingProcessor();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(); // no SessionId, no application property

        var outcome = await DeferredMessageDispatcher.ProcessAsync(message, processor, "EndpointA");

        Assert.AreEqual(DeferredMessageDispatchAction.DeadLetter, outcome.Action);
        Assert.AreEqual("No SessionId", outcome.DeadLetterReason);
        Assert.AreEqual(0, processor.Calls.Count, "Processor must not be invoked when the trigger has no session id.");
    }

    [TestMethod]
    public async Task Prefers_broker_session_id_over_application_property()
    {
        // Normal NimBus wire shape: ServiceBusMessage.SessionId is populated;
        // there is no "SessionId" application property (MessageHelper.cs:92).
        var processor = new RecordingProcessor();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            sessionId: "broker-session",
            properties: new Dictionary<string, object> { ["SessionId"] = "app-prop-session" });

        var outcome = await DeferredMessageDispatcher.ProcessAsync(message, processor, "EndpointA");

        Assert.AreEqual(DeferredMessageDispatchAction.Complete, outcome.Action);
        Assert.AreEqual(1, processor.Calls.Count);
        Assert.AreEqual("broker-session", processor.Calls[0].SessionId,
            "When both sources carry a value the broker-level SessionId must win — the application-property branch is tolerance only.");
    }

    [TestMethod]
    public async Task Falls_back_to_application_property_when_broker_session_id_absent()
    {
        // Tolerance path: a hand-published trigger or third-party producer
        // might write the SessionId as an application property only.
        var processor = new RecordingProcessor();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            properties: new Dictionary<string, object> { ["SessionId"] = "app-prop-session" });

        var outcome = await DeferredMessageDispatcher.ProcessAsync(message, processor, "EndpointA");

        Assert.AreEqual(DeferredMessageDispatchAction.Complete, outcome.Action);
        Assert.AreEqual(1, processor.Calls.Count);
        Assert.AreEqual("app-prop-session", processor.Calls[0].SessionId);
    }

    [TestMethod]
    public async Task Complete_on_successful_processor_invocation()
    {
        var processor = new RecordingProcessor();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(sessionId: "session-1");

        var outcome = await DeferredMessageDispatcher.ProcessAsync(message, processor, "EndpointA");

        Assert.AreEqual(DeferredMessageDispatchAction.Complete, outcome.Action);
        Assert.IsNull(outcome.DeadLetterReason);
        Assert.AreEqual(1, processor.Calls.Count);
        Assert.AreEqual("session-1", processor.Calls[0].SessionId);
        Assert.AreEqual("EndpointA", processor.Calls[0].TopicName);
    }

    [TestMethod]
    public async Task Complete_on_SessionCannotBeLocked()
    {
        // No deferred messages parked for this session right now — graceful
        // no-op so we don't redeliver the trigger forever.
        var processor = new RecordingProcessor
        {
            Throw = () => new ServiceBusException("locked elsewhere", ServiceBusFailureReason.SessionCannotBeLocked),
        };
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(sessionId: "session-1");

        var outcome = await DeferredMessageDispatcher.ProcessAsync(message, processor, "EndpointA");

        Assert.AreEqual(DeferredMessageDispatchAction.Complete, outcome.Action);
        Assert.IsNull(outcome.DeadLetterReason);
    }

    [TestMethod]
    public async Task Surfaces_other_exceptions_to_the_caller()
    {
        var processor = new RecordingProcessor
        {
            Throw = () => new InvalidOperationException("boom"),
        };
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(sessionId: "session-1");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DeferredMessageDispatcher.ProcessAsync(message, processor, "EndpointA"));
    }

    /// <summary>
    /// Recording fake for <see cref="IDeferredMessageProcessor"/> — does not exercise
    /// the real Service Bus path; we're only asserting on dispatcher behaviour.
    /// </summary>
    private sealed class RecordingProcessor : IDeferredMessageProcessor
    {
        public List<(string SessionId, string TopicName)> Calls { get; } = new();
        public Func<Exception>? Throw { get; set; }

        public Task ProcessDeferredMessagesAsync(string sessionId, string topicName, CancellationToken cancellationToken = default)
        {
            Calls.Add((sessionId, topicName));
            if (Throw is not null) throw Throw();
            return Task.CompletedTask;
        }
    }
}
