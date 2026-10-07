using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using NimBus.MessageStore;

namespace NimBus.WebApp.Services;

/// <summary>
/// Centralized audit-write contract for the management WebApp.
///
/// <para>Every privileged-action handler — Resubmit, Skip, ResubmitWithChanges,
/// CompleteHandoff, FailHandoff, SearchEvents, GetEventDetails,
/// GetEndpointDetails, EnableEndpoint, DisableEndpoint, PurgeMessages, Compose —
/// invokes <see cref="LogAuditAsync"/> exactly once, on both the success and
/// access-denied branches. The implementation writes the audit row to the
/// durable message store AND emits a structured "Webapp AuditEvent occurred"
/// log event (which Application Insights captures via ILogger telemetry).</para>
///
/// <para>A site Owner can switch audit types off in Settings → Audit logging
/// (<see cref="IAuditSettingsProvider"/>); a disabled type writes to neither sink.
/// Access-denied attempts and <see cref="MessageAuditType.UpdateAuditSettings"/> are
/// always recorded.</para>
///
/// <para>Best-effort: if the message-store write or the structured-log emit
/// fail, the failure is absorbed and logged as a warning — the privileged
/// action MUST proceed regardless. See spec 008 NFR-002 and User Story 5.</para>
/// </summary>
public interface IAuditLogService
{
    /// <summary>
    /// Records that a user invoked (or attempted to invoke) a privileged action.
    /// </summary>
    /// <param name="type">The <see cref="MessageAuditType"/> describing the action.</param>
    /// <param name="context">The current <see cref="HttpContext"/> — used to resolve
    /// the auditor's name from the request principal.</param>
    /// <param name="accessDenied">True when the authorization layer rejected the
    /// attempt; false when the action proceeded.</param>
    /// <param name="data">Optional structured action context — e.g. the JSON
    /// serialization of the search filter for <see cref="MessageAuditType.SearchEvents"/>.
    /// Truncated to ~4 KB before persistence.</param>
    /// <param name="eventId">Event id the audit row is associated with, when known.</param>
    /// <param name="endpointId">Endpoint id the audit row is associated with, when known.</param>
    /// <param name="eventTypeId">Event-type id the audit row is associated with, when known.</param>
    /// <param name="auditorNameOverride">When non-empty, recorded as the auditor instead of the
    /// principal resolved from <paramref name="context"/>. Used by non-interactive callers such as
    /// the agent settle path, whose actor identity (the agent id) is not carried on the request
    /// principal. When null/empty the auditor is resolved from <paramref name="context"/> as usual.</param>
    /// <param name="cancellationToken">Token observed by the underlying store write.</param>
    Task LogAuditAsync(
        MessageAuditType type,
        HttpContext context,
        bool accessDenied = false,
        string? data = null,
        string? eventId = null,
        string? endpointId = null,
        string? eventTypeId = null,
        string? auditorNameOverride = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an operator command that must not run unaudited. Unlike
    /// <see cref="LogAuditAsync"/>, it ignores the audit-logging selection and throws
    /// <see cref="AuditUnavailableException"/> when the row cannot be persisted, so the caller
    /// can refuse the command. The default implementation keeps the best-effort behavior of
    /// <see cref="LogAuditAsync"/> for implementations that predate it.
    /// </summary>
    /// <param name="type">The <see cref="MessageAuditType"/> describing the command.</param>
    /// <param name="context">The current <see cref="HttpContext"/>, used to resolve the auditor.</param>
    /// <param name="data">Structured command context, truncated like <see cref="LogAuditAsync"/>.</param>
    /// <param name="eventId">Event id the audit row is associated with.</param>
    /// <param name="endpointId">Endpoint id the audit row is associated with.</param>
    /// <param name="eventTypeId">Event-type id the audit row is associated with, when known.</param>
    /// <param name="cancellationToken">Token observed by the underlying store write.</param>
    Task LogRequiredAuditAsync(
        MessageAuditType type,
        HttpContext context,
        string? data = null,
        string? eventId = null,
        string? endpointId = null,
        string? eventTypeId = null,
        CancellationToken cancellationToken = default) =>
        LogAuditAsync(type, context, data: data, eventId: eventId, endpointId: endpointId,
            eventTypeId: eventTypeId, cancellationToken: cancellationToken);
}

/// <summary>
/// Thrown by <see cref="IAuditLogService.LogRequiredAuditAsync"/> when the audit row could not be
/// persisted, so the command it was recording must not run.
/// </summary>
public sealed class AuditUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AuditUnavailableException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public AuditUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the store failure.</summary>
    public AuditUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
