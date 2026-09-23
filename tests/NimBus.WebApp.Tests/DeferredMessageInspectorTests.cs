#pragma warning disable CA1707, CA2007
using Azure.Messaging.ServiceBus;
using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using MessageType = NimBus.Core.Messages.MessageType;
using ResolutionStatus = NimBus.MessageStore.ResolutionStatus;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class DeferredMessageInspectorTests
{
    [TestMethod]
    public async Task Empty_transfer_queues_can_be_verified_without_unsupported_emulator_peek()
    {
        var broker = new FakeClient { FailTransferPeek = true };
        var result = await new DeferredMessageInspector(broker, new FakeAdministrationClient(0)).InspectAsync(Row(), []);
        Assert.IsTrue(result.CanSkip);
        Assert.IsTrue(result.BrokerChecks.All(c => c.Status == DeferredBrokerCheckStatus.NotFound));
    }

    [TestMethod]
    public async Task Nonempty_or_unavailable_transfer_counters_never_imply_absence()
    {
        var broker = new FakeClient { FailTransferPeek = true };
        Assert.IsFalse((await new DeferredMessageInspector(broker, new FakeAdministrationClient(1)).InspectAsync(Row(), [])).CanSkip);
        Assert.IsFalse((await new DeferredMessageInspector(broker, new FakeAdministrationClient(null)).InspectAsync(Row(), [])).CanSkip);
    }

    private sealed class FakeAdministrationClient(long? count) : ServiceBusAdministrationClient
    {
        public override Task<Response<SubscriptionRuntimeProperties>> GetSubscriptionRuntimePropertiesAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
            count is null ? throw new RequestFailedException("unavailable")
            : Task.FromResult(Azure.Response.FromValue(ServiceBusModelFactory.SubscriptionRuntimeProperties(topicName, subscriptionName,
                transferDeadLetterMessageCount: count.Value), null!));
    }
    internal static UnresolvedEvent Row() => new()
    {
        EndpointId = "crm", EventId = "event", SessionId = "session", LastMessageId = "deferral",
        UpdatedAt = DateTime.UtcNow.AddHours(-1), EnqueuedTimeUtc = DateTime.UtcNow.AddHours(-2),
        ResolutionStatus = ResolutionStatus.Deferred,
    };

    private static MessageEntity Response(string endpoint = "crm", string session = "session") => new()
    {
        EventId = "event", SessionId = session, From = endpoint, To = "Resolver", MessageId = "completed",
        MessageType = MessageType.ResolutionResponse, EnqueuedTimeUtc = DateTime.UtcNow.AddMinutes(-30),
    };

    [TestMethod]
    public async Task Missing_broker_message_does_not_claim_success_without_history()
    {
        var client = new FakeClient();
        var result = await new DeferredMessageInspector(client).InspectAsync(Row(), []);
        Assert.AreEqual("Unknown", result.HistoryOutcome);
        Assert.IsTrue(result.CanSkip);
        Assert.AreEqual(6, result.BrokerChecks.Count);
        Assert.IsTrue(result.BrokerChecks.All(c => c.Status == DeferredBrokerCheckStatus.NotFound));
    }

    [TestMethod]
    public async Task Completion_evidence_is_scoped_to_endpoint_session_and_event()
    {
        var inspector = new DeferredMessageInspector(new FakeClient());
        var unrelatedEvent = Response();
        unrelatedEvent.EventId = "other";
        var result = await inspector.InspectAsync(Row(), [Response("erp"), Response(session: "other"), unrelatedEvent]);
        Assert.AreEqual("Unknown", result.HistoryOutcome);
        result = await inspector.InspectAsync(Row(), [Response()]);
        Assert.AreEqual("Completed", result.HistoryOutcome);
        Assert.AreEqual("completed", result.TerminalMessageId);
    }

    [TestMethod]
    public async Task Later_attempt_does_not_turn_historical_completion_into_current_success()
    {
        var result = await new DeferredMessageInspector(new FakeClient()).InspectAsync(Row(), [Response(), new MessageEntity
        {
            EventId = "event", SessionId = "session", To = "crm", MessageType = MessageType.ResubmissionRequest,
            EnqueuedTimeUtc = DateTime.UtcNow.AddMinutes(-5),
        }]);
        Assert.AreEqual("Completed", result.HistoryOutcome);
        Assert.IsTrue(result.HasLaterAttempt);
    }

    [TestMethod]
    public async Task Latest_error_or_deadletter_response_does_not_claim_completion()
    {
        var response = Response();
        response.DeadLetterReason = "expired";
        var inspector = new DeferredMessageInspector(new FakeClient());
        Assert.AreEqual("DeadLettered", (await inspector.InspectAsync(Row(), [response])).HistoryOutcome);
        response.DeadLetterReason = null!;
        response.MessageType = MessageType.ErrorResponse;
        Assert.AreEqual("Failed", (await inspector.InspectAsync(Row(), [response])).HistoryOutcome);
        response.MessageType = MessageType.SkipResponse;
        Assert.AreEqual("Skipped", (await inspector.InspectAsync(Row(), [response])).HistoryOutcome);
    }

    [TestMethod]
    public async Task Matching_broker_message_blocks_skip_but_other_sessions_do_not()
    {
        var client = new FakeClient { Messages = [BrokerMessage(1, "other"), BrokerMessage(2)] };
        var result = await new DeferredMessageInspector(client).InspectAsync(Row(), []);
        Assert.IsFalse(result.CanSkip);
        Assert.IsTrue(result.BrokerChecks.Any(c => c.Status == DeferredBrokerCheckStatus.Present));
        client.Messages = [BrokerMessage(1, "other")];
        Assert.IsTrue((await new DeferredMessageInspector(client).InspectAsync(Row(), [])).CanSkip);
    }

    [TestMethod]
    public async Task Full_scan_pages_past_short_batches_and_never_calls_receive()
    {
        var client = new FakeClient { Messages = [BrokerMessage(1, "other"), BrokerMessage(2)] , BatchSize = 1 };
        var result = await new DeferredMessageInspector(client).InspectAsync(Row(), []);
        Assert.IsFalse(result.CanSkip);
        Assert.AreEqual(2, result.BrokerChecks.First().Scanned);
    }

    [TestMethod]
    public async Task Truncation_and_broker_errors_are_unknown_and_block_skip()
    {
        var client = new FakeClient { Messages = Enumerable.Range(1, 2001).Select(i => BrokerMessage(i, "other")).ToArray() };
        var result = await new DeferredMessageInspector(client).InspectAsync(Row(), []);
        Assert.IsFalse(result.CanSkip);
        Assert.IsTrue(result.BrokerChecks.All(c => c.Status == DeferredBrokerCheckStatus.Unknown));
        client.Error = new ServiceBusException("unavailable", ServiceBusFailureReason.ServiceCommunicationProblem);
        result = await new DeferredMessageInspector(client).InspectAsync(Row(), []);
        Assert.IsFalse(result.CanSkip);
        Assert.IsTrue(result.BrokerChecks.All(c => c.Status == DeferredBrokerCheckStatus.Unknown));
    }

    [TestMethod]
    public async Task Recent_or_non_deferred_rows_cannot_be_skipped()
    {
        var row = Row();
        row.UpdatedAt = DateTime.UtcNow;
        Assert.IsFalse((await new DeferredMessageInspector(new FakeClient()).InspectAsync(row, [])).CanSkip);
        row.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        row.ResolutionStatus = ResolutionStatus.Completed;
        Assert.IsFalse((await new DeferredMessageInspector(new FakeClient()).InspectAsync(row, [])).CanSkip);
    }

    private static ServiceBusReceivedMessage BrokerMessage(long sequence, string session = "session") =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(sessionId: session, sequenceNumber: sequence,
            properties: new Dictionary<string, object> { ["EventId"] = "event" });

    internal sealed class FakeClient : ServiceBusClient
    {
        internal ServiceBusReceivedMessage[] Messages { get; set; } = [];
        internal Exception? Error { get; set; }
        internal int BatchSize { get; set; } = 100;
        internal Action? BeforePeek { get; set; }
        internal bool FailTransferPeek { get; set; }
        public override ServiceBusReceiver CreateReceiver(string topicName, string subscriptionName, ServiceBusReceiverOptions options) => new FakeReceiver(this, options.SubQueue);
    }

    private sealed class FakeReceiver(FakeClient client, SubQueue subQueue) : ServiceBusReceiver
    {
        public override Task<IReadOnlyList<ServiceBusReceivedMessage>> PeekMessagesAsync(int maxMessages, long? fromSequenceNumber = null, CancellationToken cancellationToken = default)
        {
            var beforePeek = client.BeforePeek;
            client.BeforePeek = null;
            beforePeek?.Invoke();
            if (client.FailTransferPeek && subQueue == SubQueue.TransferDeadLetter)
                throw new ServiceBusException("Unsupported queue", ServiceBusFailureReason.MessagingEntityNotFound);
            if (client.Error is not null) throw client.Error;
            return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(client.Messages
                .Where(m => m.SequenceNumber >= fromSequenceNumber.GetValueOrDefault()).Take(Math.Min(maxMessages, client.BatchSize)).ToArray());
        }
        // The SDK's protected mocking constructor has no transport to dispose.
#pragma warning disable CA2215
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
#pragma warning restore CA2215
    }
}
