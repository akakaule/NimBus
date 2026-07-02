using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.WebApp.Services;

/// <summary>
/// Default <see cref="IAuditLogService"/> implementation. Writes each audit row
/// to the durable <see cref="INimBusMessageStore"/> and emits a structured
/// "Webapp AuditEvent occurred" log event (captured by Application Insights via
/// <see cref="ILogger"/> telemetry). Both writes are best-effort — failures
/// are absorbed and logged as warnings so the user's privileged action proceeds.
/// </summary>
public sealed class AuditLogService : IAuditLogService
{
    /// <summary>4 KB cap on the <c>Data</c> field per NFR-004.</summary>
    internal const int DataTruncationLimit = 4096;
    internal const string TruncationSuffix = "… [truncated]";
    internal const string AnonymousAuditorName = "anonymous";
    internal const string StructuredLogMessage = "Webapp AuditEvent occurred";

    private readonly ILogger<AuditLogService> _logger;
    private readonly INimBusMessageStore _messageStore;

    public AuditLogService(
        ILogger<AuditLogService> logger,
        INimBusMessageStore messageStore)
    {
        _logger = logger;
        _messageStore = messageStore;
    }

    /// <inheritdoc/>
    public async Task LogAuditAsync(
        MessageAuditType type,
        HttpContext context,
        bool accessDenied = false,
        string? data = null,
        string? eventId = null,
        string? endpointId = null,
        string? eventTypeId = null,
        string? auditorNameOverride = null,
        CancellationToken cancellationToken = default)
    {
        // Build the entity upfront so both sinks see the same payload.
        var entity = new MessageAuditEntity
        {
            AuditorName = string.IsNullOrWhiteSpace(auditorNameOverride)
                ? ResolveAuditorName(context)
                : auditorNameOverride,
            AuditTimestamp = DateTime.UtcNow,
            AuditType = type,
            AccessDenied = accessDenied,
            Data = TruncateData(data),
            EventId = eventId,
            EndpointId = endpointId,
        };

        // (1) Durable sink: message store. Catch and log so a transient store
        //     failure cannot fail the user action — see User Story 5.
        try
        {
            await _messageStore.StoreMessageAudit(eventId ?? string.Empty, entity, endpointId, eventTypeId)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Audit write to message store failed for AuditType={AuditType} EventId={EventId} EndpointId={EndpointId} AuditorName={AuditorName}",
                entity.AuditType, entity.EventId, entity.EndpointId, entity.AuditorName);
        }

        // (2) Short-term sink: App Insights via structured ILogger. The
        //     BeginScope dictionary keys MUST match the entity property names
        //     so KQL queries can pivot on customDimensions.<FieldName>.
        try
        {
            var scope = new Dictionary<string, object?>
            {
                [nameof(MessageAuditEntity.AuditorName)] = entity.AuditorName,
                [nameof(MessageAuditEntity.AuditTimestamp)] = entity.AuditTimestamp,
                [nameof(MessageAuditEntity.AuditType)] = entity.AuditType.ToString(),
                [nameof(MessageAuditEntity.Comment)] = entity.Comment,
                [nameof(MessageAuditEntity.AccessDenied)] = entity.AccessDenied,
                [nameof(MessageAuditEntity.Data)] = entity.Data,
                [nameof(MessageAuditEntity.EventId)] = entity.EventId,
                [nameof(MessageAuditEntity.EndpointId)] = entity.EndpointId,
            };

            using (_logger.BeginScope(scope))
            {
                _logger.LogInformation(StructuredLogMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Audit App Insights emit failed for AuditType={AuditType} EventId={EventId} EndpointId={EndpointId} AuditorName={AuditorName}",
                entity.AuditType, entity.EventId, entity.EndpointId, entity.AuditorName);
        }
    }

    internal static string ResolveAuditorName(HttpContext? context)
    {
        var user = context?.User;
        if (user == null)
        {
            return AnonymousAuditorName;
        }

        // Per spec FR-020: name claim → ClaimTypes.Name → preferred_username → "anonymous"
        var name = user.FindFirst(c => string.Equals(c.Type, "name", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        name = user.FindFirst(ClaimTypes.Name)?.Value;
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        name = user.FindFirst("preferred_username")?.Value;
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        return AnonymousAuditorName;
    }

    internal static string? TruncateData(string? data)
    {
        if (string.IsNullOrEmpty(data) || data.Length <= DataTruncationLimit)
        {
            return data;
        }

        return string.Concat(data.AsSpan(0, DataTruncationLimit), TruncationSuffix);
    }
}
