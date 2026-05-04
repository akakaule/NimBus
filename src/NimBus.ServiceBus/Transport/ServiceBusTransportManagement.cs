using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Management.ServiceBus;
using NimBus.Transport.Abstractions;

namespace NimBus.ServiceBus.Transport;

/// <summary>
/// Service Bus implementation of <see cref="ITransportManagement"/>. Translates the
/// transport-neutral, intent-based topology operations into the topic + subscription
/// vocabulary of <see cref="IServiceBusManagement"/>.
/// </summary>
/// <remarks>
/// <para>
/// NimBus's Service Bus topology models each endpoint as a topic of the same name
/// plus a self-named subscription that the endpoint's receivers consume from
/// (sessions enabled by default to preserve per-key ordering). This adapter
/// applies that convention so callers can stay transport-agnostic — they see
/// "endpoints", not "topics" or "subscriptions".
/// </para>
/// <para>
/// <see cref="IServiceBusManagement"/> does not currently expose enumeration or
/// purge primitives, so <see cref="ListEndpointsAsync"/> and
/// <see cref="PurgeEndpointAsync"/> throw <see cref="NotSupportedException"/>.
/// Operators relying on those features today drive the underlying
/// <c>ServiceBusAdministrationClient</c> directly via the <c>nb topology</c>
/// CLI; lifting them onto this adapter is tracked as a follow-up.
/// </para>
/// </remarks>
internal sealed class ServiceBusTransportManagement : ITransportManagement
{
    private readonly IServiceBusManagement _management;

    public ServiceBusTransportManagement(IServiceBusManagement management)
    {
        _management = management ?? throw new ArgumentNullException(nameof(management));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Realises the endpoint as a Service Bus topic plus a same-named subscription.
    /// Per <see cref="IServiceBusManagement.CreateSubscription"/>, the subscription
    /// is created with sessions required regardless of
    /// <see cref="EndpointConfig.RequiresOrderedDelivery"/> — Service Bus always
    /// honours session ordering, and consumers that don't need ordering simply
    /// don't set a session key. The <see cref="EndpointConfig.MaxConcurrency"/>
    /// hint is not surfaced through the broker today; consumers control concurrency
    /// at the receiver layer.
    /// </remarks>
    public async Task DeclareEndpointAsync(EndpointConfig config, CancellationToken cancellationToken)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        cancellationToken.ThrowIfCancellationRequested();

        await _management.CreateTopic(config.Name).ConfigureAwait(false);
        await _management.CreateSubscription(config.Name, config.Name).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// <see cref="IServiceBusManagement"/> does not expose subscription enumeration.
    /// Callers needing this feature should drive
    /// <c>ServiceBusAdministrationClient</c> directly via the
    /// <c>nb topology</c> CLI until <see cref="IServiceBusManagement"/> grows a
    /// list primitive.
    /// </exception>
    public Task<IReadOnlyList<EndpointInfo>> ListEndpointsAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "Listing endpoints is not yet supported by the Service Bus transport-management adapter; " +
            "IServiceBusManagement has no enumeration primitive. Drive ServiceBusAdministrationClient " +
            "directly via the nb topology CLI until this is added.");
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// <see cref="IServiceBusManagement"/> does not expose a purge primitive. Use a
    /// <c>ServiceBusReceiver</c> or the <c>nb topology purge</c> CLI to drain
    /// messages from a subscription.
    /// </exception>
    public Task PurgeEndpointAsync(string endpointName, CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "Purging endpoints is not yet supported by the Service Bus transport-management adapter; " +
            "IServiceBusManagement has no purge primitive. Use ServiceBusReceiver or the nb topology " +
            "CLI to drain messages until this is added.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// Removes the endpoint's same-named subscription. The underlying topic is
    /// retained because other subscriptions (e.g. cross-endpoint forward rules)
    /// may still depend on it; topic deletion is not exposed by
    /// <see cref="IServiceBusManagement"/> today.
    /// </remarks>
    public async Task RemoveEndpointAsync(string endpointName, CancellationToken cancellationToken)
    {
        if (endpointName is null) throw new ArgumentNullException(nameof(endpointName));
        cancellationToken.ThrowIfCancellationRequested();

        await _management.DeleteSubscription(endpointName, endpointName).ConfigureAwait(false);
    }
}
