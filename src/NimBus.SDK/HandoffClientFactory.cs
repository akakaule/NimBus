using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using NimBus.OpenTelemetry;
using NimBus.ServiceBus;
using System;
using System.Collections.Concurrent;

namespace NimBus.SDK;

/// <summary>
/// Creates <see cref="IHandoffClient"/> instances for endpoints resolved at
/// runtime. <c>AddNimBusHandoffClient(endpoint)</c> covers processes whose
/// endpoint set is known at registration time; this factory covers processes
/// that settle handoffs for arbitrary endpoints (e.g. the management WebApp,
/// where the endpoint arrives as a route parameter or agent zone).
/// </summary>
public interface IHandoffClientFactory
{
    /// <summary>
    /// Returns the settlement client bound to <paramref name="endpointId"/>.
    /// Clients (and their underlying senders) are created once per endpoint
    /// and cached for the factory's lifetime.
    /// </summary>
    IHandoffClient ForEndpoint(string endpointId);
}

/// <summary>
/// Default <see cref="IHandoffClientFactory"/>: builds each endpoint's client
/// exactly like the keyed <c>AddNimBusHandoffClient</c> registration does — an
/// OpenTelemetry-instrumented <see cref="Sender"/> over
/// <see cref="ServiceBusClient.CreateSender(string)"/> — and caches it per
/// endpoint. Senders live as long as the factory; the injected
/// <see cref="ServiceBusClient"/> owns their disposal.
/// </summary>
public sealed class HandoffClientFactory : IHandoffClientFactory
{
    private readonly ServiceBusClient _client;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, IHandoffClient> _clients = new(StringComparer.Ordinal);

    public HandoffClientFactory(ServiceBusClient client, ILoggerFactory loggerFactory = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _loggerFactory = loggerFactory;
    }

    public IHandoffClient ForEndpoint(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId))
            throw new ArgumentException("Endpoint must be specified.", nameof(endpointId));

        return _clients.GetOrAdd(endpointId, endpoint =>
        {
            ISender sender = NimBusOpenTelemetryDecorators.InstrumentSender(
                new Sender(_client.CreateSender(endpoint)), MessagingSystem.ServiceBus);
            return new HandoffClient(
                sender,
                new HandoffClientOptions { Endpoint = endpoint },
                _loggerFactory?.CreateLogger<HandoffClient>());
        });
    }
}
