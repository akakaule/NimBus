using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus;

/// <summary>
/// Sends request/reply responses to the requesting endpoint's topic as raw session
/// messages matching the <see cref="ReplyConstants"/> wire contract. Senders are
/// cached per reply topic and disposed with the dispatcher.
/// </summary>
public sealed class ReplyDispatcher : IReplyDispatcher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.Ordinal);

    /// <summary>Creates the dispatcher on an existing <see cref="ServiceBusClient"/>.</summary>
    /// <param name="client">The Service Bus client; not disposed by this dispatcher.</param>
    public ReplyDispatcher(ServiceBusClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public async Task SendReplyAsync(ReplyMessage reply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var sender = _senders.GetOrAdd(reply.ReplyTo, topic => _client.CreateSender(topic));

        var message = new Azure.Messaging.ServiceBus.ServiceBusMessage(reply.PayloadJson ?? string.Empty)
        {
            SessionId = reply.ReplySessionId,
            CorrelationId = reply.CorrelationId,
            TimeToLive = ReplyConstants.ReplyTimeToLive,
        };

        // The reply's ONLY routable property: matches the reply subscription's SQL rule
        // (user.To = '{endpoint}-reply') and deliberately no other rule on the topic.
        message.ApplicationProperties["To"] = ReplyConstants.ReplySubscriptionName(reply.ReplyTo);
        message.ApplicationProperties[ReplyConstants.ReplyStatusProperty] =
            reply.IsError ? ReplyConstants.StatusError : ReplyConstants.StatusSuccess;
        if (reply.IsError)
        {
            message.ApplicationProperties[ReplyConstants.ErrorTypeProperty] = reply.ErrorType ?? string.Empty;
            message.ApplicationProperties[ReplyConstants.ErrorTextProperty] = reply.ErrorText ?? string.Empty;
        }

        await sender.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
        {
            await sender.DisposeAsync().ConfigureAwait(false);
        }

        _senders.Clear();
    }
}
