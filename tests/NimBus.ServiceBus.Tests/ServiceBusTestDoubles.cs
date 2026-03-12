using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using NimBus.Core.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

internal static class ServiceBusTestDoubles
{
    public static ServiceBusMessageActions CreateMessageActions() =>
        (ServiceBusMessageActions)RuntimeHelpers.GetUninitializedObject(typeof(ServiceBusMessageActions));

    public static ServiceBusSessionMessageActions CreateSessionActions() =>
        (ServiceBusSessionMessageActions)RuntimeHelpers.GetUninitializedObject(typeof(ServiceBusSessionMessageActions));
}

internal sealed class RecordingServiceBusClient : ServiceBusClient
{
    public RecordingServiceBusSessionReceiver SessionReceiver { get; } = new();
    public RecordingServiceBusSender Sender { get; } = new();
    public Exception? CreateSenderException { get; set; }
    public Exception? AcceptSessionException { get; set; }

    public string? LastSenderEntityPath { get; private set; }
    public string? LastQueueName { get; private set; }
    public string? LastTopicName { get; private set; }
    public string? LastSubscriptionName { get; private set; }
    public string? LastSessionId { get; private set; }

    public override ServiceBusSender CreateSender(string queueOrTopicName)
    {
        LastSenderEntityPath = queueOrTopicName;
        if (CreateSenderException != null)
        {
            throw CreateSenderException;
        }

        return Sender;
    }

    public override ServiceBusSender CreateSender(string queueOrTopicName, ServiceBusSenderOptions options)
    {
        LastSenderEntityPath = queueOrTopicName;
        if (CreateSenderException != null)
        {
            throw CreateSenderException;
        }

        return Sender;
    }

    public override Task<ServiceBusSessionReceiver> AcceptSessionAsync(string queueName, string sessionId, ServiceBusSessionReceiverOptions options, CancellationToken cancellationToken)
    {
        LastQueueName = queueName;
        LastSessionId = sessionId;
        LastTopicName = null;
        LastSubscriptionName = null;
        if (AcceptSessionException != null)
            throw AcceptSessionException;
        return Task.FromResult<ServiceBusSessionReceiver>(SessionReceiver);
    }

    public override Task<ServiceBusSessionReceiver> AcceptSessionAsync(string topicName, string subscriptionName, string sessionId, ServiceBusSessionReceiverOptions options, CancellationToken cancellationToken)
    {
        LastTopicName = topicName;
        LastSubscriptionName = subscriptionName;
        LastSessionId = sessionId;
        LastQueueName = null;
        if (AcceptSessionException != null)
            throw AcceptSessionException;
        return Task.FromResult<ServiceBusSessionReceiver>(SessionReceiver);
    }
}

internal sealed class CreateSenderProbeException : Exception
{
    public CreateSenderProbeException(string message) : base(message) { }
}

[SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
internal sealed class RecordingServiceBusSender : ServiceBusSender
{
    public List<Azure.Messaging.ServiceBus.ServiceBusMessage> SentMessages { get; } = new();
    public List<(Azure.Messaging.ServiceBus.ServiceBusMessage Message, DateTimeOffset ScheduledEnqueueTime)> ScheduledMessages { get; } = new();

    public override Task SendMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(message);
        return Task.CompletedTask;
    }

    public override Task SendMessagesAsync(IEnumerable<Azure.Messaging.ServiceBus.ServiceBusMessage> messages, CancellationToken cancellationToken = default)
    {
        SentMessages.AddRange(messages);
        return Task.CompletedTask;
    }

    public override Task<long> ScheduleMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        ScheduledMessages.Add((message, scheduledEnqueueTime));
        return Task.FromResult(1L);
    }

    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
internal sealed class RecordingServiceBusSessionReceiver : ServiceBusSessionReceiver
{
    public IReadOnlyList<ServiceBusReceivedMessage> DeferredMessagesToReturn { get; set; } = Array.Empty<ServiceBusReceivedMessage>();
    public IReadOnlyList<long> LastDeferredSequenceNumbers { get; private set; } = Array.Empty<long>();

    // Batch-receive support for DeferredMessageProcessor tests
    public List<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveBatches { get; set; } = new();
    private int _receiveBatchIndex;
    public List<ServiceBusReceivedMessage> CompletedMessages { get; } = new();
    public Exception? ReceiveMessagesException { get; set; }

    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveDeferredMessagesAsync(IEnumerable<long> sequenceNumbers, CancellationToken cancellationToken = default)
    {
        LastDeferredSequenceNumbers = sequenceNumbers.ToArray();
        return Task.FromResult(DeferredMessagesToReturn);
    }

    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(int maxMessages, TimeSpan? maxWaitTime, CancellationToken cancellationToken = default)
    {
        if (_receiveBatchIndex >= ReceiveBatches.Count && ReceiveMessagesException != null)
            throw ReceiveMessagesException;
        if (_receiveBatchIndex < ReceiveBatches.Count)
            return Task.FromResult(ReceiveBatches[_receiveBatchIndex++]);
        return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(Array.Empty<ServiceBusReceivedMessage>());
    }

    public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
    {
        CompletedMessages.Add(message);
        return Task.CompletedTask;
    }

    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestEvent : Event
{
    public string SessionIdValue { get; set; } = "session-1";
    public string Payload { get; set; } = "payload";

    public override string GetSessionId() => SessionIdValue;
}
