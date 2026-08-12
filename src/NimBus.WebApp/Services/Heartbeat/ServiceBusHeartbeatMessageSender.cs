using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using NimBus.ServiceBus;

namespace NimBus.WebApp.Services.Heartbeat;

/// <summary>
/// Sends heartbeat probes over the WebApp's shared <see cref="ServiceBusClient"/>.
/// </summary>
public sealed class ServiceBusHeartbeatMessageSender : IHeartbeatMessageSender
{
    private readonly ServiceBusClient _client;

    /// <summary>Creates a sender over the shared Service Bus client.</summary>
    /// <param name="client">The WebApp's singleton Service Bus client.</param>
    public ServiceBusHeartbeatMessageSender(ServiceBusClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public async Task SendAsync(string topicName, Message message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        ArgumentNullException.ThrowIfNull(message);

        await using var sender = _client.CreateSender(topicName);
        await sender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message), cancellationToken);
    }
}
