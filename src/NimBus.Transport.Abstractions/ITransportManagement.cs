using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Transport.Abstractions;

/// <summary>
/// Intent-based, provider-neutral management surface for declaring and inspecting
/// endpoint topology. Implementations translate these high-level intents into the
/// transport's native primitives — for Azure Service Bus that means
/// queues/topics/subscriptions/forwarding rules, for RabbitMQ that means
/// exchanges/queues/bindings/dead-letter queues. Callers MUST NOT assume any
/// particular underlying broker construct.
/// </summary>
public interface ITransportManagement
{
    /// <summary>
    /// Idempotently declares the topology required for an endpoint described by
    /// <paramref name="config"/>. When the endpoint already exists with a compatible
    /// shape, the call is a no-op; when it exists with an incompatible shape, the
    /// implementation MAY throw or reconcile depending on transport semantics.
    /// </summary>
    /// <param name="config">Logical endpoint definition the transport should realise.</param>
    /// <param name="cancellationToken">Cancels the in-flight management call.</param>
    Task DeclareEndpointAsync(EndpointConfig config, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a snapshot of the endpoints visible to the transport. Used by the
    /// management UI, the <c>nb topology</c> CLI, and conformance tests. Order is
    /// implementation-defined; callers should not assume stability.
    /// </summary>
    /// <param name="cancellationToken">Cancels the in-flight management call.</param>
    Task<IReadOnlyList<EndpointInfo>> ListEndpointsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes all messages currently buffered on the given endpoint without
    /// removing the endpoint itself. Intended for development and conformance
    /// scenarios; production tooling should prefer targeted resubmit/skip flows.
    /// </summary>
    /// <param name="endpointName">Logical endpoint name as supplied to <see cref="DeclareEndpointAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the in-flight management call.</param>
    Task PurgeEndpointAsync(string endpointName, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the endpoint and any associated broker constructs (queues, bindings,
    /// dead-letter destinations). Idempotent — calling on an unknown endpoint is a
    /// no-op rather than an error.
    /// </summary>
    /// <param name="endpointName">Logical endpoint name as supplied to <see cref="DeclareEndpointAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the in-flight management call.</param>
    Task RemoveEndpointAsync(string endpointName, CancellationToken cancellationToken);
}

/// <summary>
/// Minimal, transport-agnostic description of an endpoint for
/// <see cref="ITransportManagement.DeclareEndpointAsync"/>. Provider-specific
/// settings (Service Bus auto-forward target, RabbitMQ partition count, etc.) are
/// expressed through capabilities and provider options, not by leaking native types
/// into this contract.
/// </summary>
/// <param name="Name">Logical endpoint name; the transport maps this to its native namespace.</param>
/// <param name="RequiresOrderedDelivery">When true, the transport must realise the endpoint with per-session/per-key ordered delivery.</param>
/// <param name="MaxConcurrency">Upper bound on concurrent in-flight messages the host expects to process. <c>null</c> defers to provider defaults.</param>
public sealed record EndpointConfig(
    string Name,
    bool RequiresOrderedDelivery,
    int? MaxConcurrency);

/// <summary>
/// Read-only summary of an endpoint as observed by the transport. Returned from
/// <see cref="ITransportManagement.ListEndpointsAsync"/>.
/// </summary>
/// <param name="Name">Logical endpoint name as previously declared.</param>
/// <param name="ActiveMessageCount">Approximate number of messages awaiting delivery, or <c>null</c> when the transport cannot report it.</param>
/// <param name="DeadLetterMessageCount">Approximate number of dead-lettered messages, or <c>null</c> when the transport cannot report it.</param>
public sealed record EndpointInfo(
    string Name,
    long? ActiveMessageCount,
    long? DeadLetterMessageCount);
