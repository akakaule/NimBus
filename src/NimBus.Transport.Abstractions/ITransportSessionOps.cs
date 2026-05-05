using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Transport.Abstractions;

/// <summary>
/// Transport-neutral surface for the operator-tooling (<c>nimbus-ops</c>)
/// session and subscription operations that AdminService exposes today through
/// transport-specific primitives. Phase 6.2 task #25 (1H) lifts these calls
/// above the Service-Bus-shaped session-receiver API so RabbitMQ deployments
/// can satisfy them through a different broker model (consistent-hash partition
/// queues + parked-message store).
/// </summary>
/// <remarks>
/// <para>
/// Implementations are paired with a transport provider package
/// (<c>NimBus.ServiceBus</c>, <c>NimBus.Transport.RabbitMQ</c>) and registered
/// from inside the matching <c>Add{Provider}Transport()</c> extension. Tests
/// commonly fake this interface to exercise admin endpoints without touching a
/// real broker.
/// </para>
/// <para>
/// Returned DTOs are transport-shaped (counts + error messages); the WebApp
/// translates them into its own <c>ManagementApi</c> shapes. Cosmos / SQL
/// Server cleanup still lives in AdminService — this contract covers the
/// broker side only.
/// </para>
/// </remarks>
public interface ITransportSessionOps
{
    /// <summary>
    /// Returns a count of broker-side messages associated with
    /// <paramref name="sessionId"/> on <paramref name="endpointName"/> without
    /// removing them. Inspection only — safe to call from preview UIs.
    /// </summary>
    Task<TransportSessionPreview> PreviewSessionAsync(string endpointName, string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Drains all broker-side messages (active + deferred + parked) for
    /// <paramref name="sessionId"/> on <paramref name="endpointName"/> and
    /// clears the session-blocking state. Idempotent on a session with no
    /// pending messages. Errors are collected per-operation rather than thrown
    /// so the caller can surface partial-success outcomes to operators.
    /// </summary>
    Task<TransportSessionPurgeResult> PurgeSessionAsync(string endpointName, string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Counts messages matching <paramref name="states"/> on
    /// <paramref name="subscriptionName"/> (under <paramref name="endpointName"/>),
    /// optionally restricted to messages enqueued before
    /// <paramref name="enqueuedBeforeUtc"/>. Inspection only.
    /// </summary>
    Task<TransportSubscriptionPreview> PreviewSubscriptionAsync(
        string endpointName,
        string subscriptionName,
        IReadOnlyCollection<TransportMessageState> states,
        DateTime? enqueuedBeforeUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Drains messages matching <paramref name="states"/> from
    /// <paramref name="subscriptionName"/>. Errors are collected per-message.
    /// </summary>
    Task<TransportBulkResult> PurgeSubscriptionAsync(
        string endpointName,
        string subscriptionName,
        IReadOnlyCollection<TransportMessageState> states,
        DateTime? enqueuedBeforeUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clears any blocking state for <paramref name="sessionId"/> and triggers
    /// a "process deferred" message that drains any parked / deferred messages
    /// in send order. Service Bus implementations send to the deferred-processor
    /// subscription; RabbitMQ implementations enqueue an unblock notification
    /// the parked-message-store consumer reacts to.
    /// </summary>
    Task<TransportReprocessResult> ReprocessDeferredAsync(string endpointName, string sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// Logical message-state filter used by <see cref="ITransportSessionOps"/>
/// callers. Maps to Service Bus <c>ServiceBusMessageState</c> on Service Bus
/// and to active/parked classifications on RabbitMQ.
/// </summary>
public enum TransportMessageState
{
    /// <summary>Messages currently available for delivery.</summary>
    Active,

    /// <summary>Messages deferred / parked behind a blocked session.</summary>
    Deferred,
}

/// <summary>
/// Preview counts for a single session's broker-side state. Cosmos / SQL
/// Server counts are computed separately by AdminService.
/// </summary>
/// <param name="SessionId">The session inspected.</param>
/// <param name="ActiveMessageCount">Approximate number of active broker-side messages for the session.</param>
/// <param name="DeferredMessageCount">Approximate number of broker-side deferred messages for the session.</param>
public sealed record TransportSessionPreview(
    string SessionId,
    long ActiveMessageCount,
    long DeferredMessageCount);

/// <summary>
/// Outcome of a session-purge run. Counts are exact; the
/// <see cref="Errors"/> list captures non-fatal failures so the caller can
/// surface partial-success outcomes.
/// </summary>
public sealed record TransportSessionPurgeResult(
    string SessionId,
    long ActiveMessagesRemoved,
    long DeferredMessagesRemoved,
    long DeferredSubscriptionMessagesRemoved,
    bool SessionStateCleared,
    IReadOnlyList<string> Errors);

/// <summary>
/// Preview counts for a subscription-level purge — total messages scanned,
/// messages matching the supplied state/age filter, and the number of distinct
/// session ids those messages span.
/// </summary>
public sealed record TransportSubscriptionPreview(
    long TotalScanned,
    long TotalMatching,
    int SessionCount);

/// <summary>
/// Generic broker-side bulk-operation outcome.
/// </summary>
public sealed record TransportBulkResult(
    long Processed,
    long Succeeded,
    long Failed,
    IReadOnlyList<string> Errors);

/// <summary>
/// Outcome of a deferred-reprocessing run for a single session.
/// </summary>
public sealed record TransportReprocessResult(
    string SessionId,
    bool SessionStateCleared,
    bool ProcessRequestSent,
    IReadOnlyList<string> Errors);
