using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Newtonsoft.Json;
using NimBus.Core.Events;
using NimBus.Core.Messages;

namespace NimBus.ServiceBus;

/// <summary>
/// Service Bus implementation of <see cref="IRequestSender"/>. Sends the
/// request through the registered <see cref="ISender"/> factory and waits for
/// the reply on a per-request session in the conventional
/// <c>{topic}-reply</c> subscription.
/// </summary>
public sealed class ServiceBusRequestSender : IRequestSender
{
    private readonly ServiceBusClient _client;
    private readonly Func<string, ISender> _senderFactory;

    public ServiceBusRequestSender(ServiceBusClient client, Func<string, ISender> senderFactory)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _senderFactory = senderFactory ?? throw new ArgumentNullException(nameof(senderFactory));
    }

    public async Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
        where TRequest : IEvent
        where TResponse : class
    {
        var replySessionId = Guid.NewGuid().ToString();
        var msg = EventMessageBuilder.Build(request);
        msg.ReplyTo = msg.To;
        msg.ReplyToSessionId = replySessionId;

        var sender = _senderFactory(msg.To);
        await sender.Send(msg, cancellationToken: cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        ServiceBusSessionReceiver? receiver = null;
        try
        {
            receiver = await _client.AcceptSessionAsync(
                msg.To, $"{msg.To}-reply", replySessionId, cancellationToken: cts.Token);

            var reply = await receiver.ReceiveMessageAsync(timeout, cts.Token);
            if (reply == null)
                throw new TimeoutException($"No response received within {timeout}");

            await receiver.CompleteMessageAsync(reply, cts.Token);

            var body = reply.Body.ToString();
            return JsonConvert.DeserializeObject<TResponse>(body)!;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response received within {timeout}");
        }
        finally
        {
            if (receiver != null)
                await receiver.DisposeAsync();
        }
    }
}
