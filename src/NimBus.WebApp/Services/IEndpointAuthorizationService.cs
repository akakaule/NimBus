using System;
using NimBus.MessageStore;

namespace NimBus.WebApp.Services;

/// <summary>
/// Service for endpoint authorization and user context operations.
/// </summary>
public interface IEndpointAuthorizationService
{
    /// <summary>
    /// Checks if the current user has management permissions for the specified endpoint.
    /// </summary>
    /// <param name="endpointId">The endpoint ID to check.</param>
    /// <returns>True if the user can manage the endpoint, false otherwise.</returns>
    bool IsManagerOfEndpoint(string endpointId);

    /// <summary>
    /// Checks if the current user is a platform administrator — someone who can
    /// manage every endpoint (the EIP_Management security group, or any user when
    /// <c>BypassEndpointAuthorization</c> is enabled). Gates cross-endpoint
    /// surfaces such as unscoped audit searches.
    /// </summary>
    bool IsPlatformAdministrator();

    /// <summary>
    /// Creates a message audit entity for the current user with the specified audit type.
    /// </summary>
    /// <param name="type">The type of audit action.</param>
    /// <returns>A MessageAuditEntity populated with current user information.</returns>
    [Obsolete("Use IAuditLogService.LogAuditAsync — see spec 008 (centralized audit log service). This bridge remains for any legacy caller that has not yet migrated; it will be removed in a follow-up release.", error: false)]
    MessageAuditEntity GetMessageAuditEntity(MessageAuditType type);

    /// <summary>
    /// Gets the current user's email/name from claims.
    /// </summary>
    /// <returns>The user's name or email.</returns>
    string? GetCurrentUserName();
}
