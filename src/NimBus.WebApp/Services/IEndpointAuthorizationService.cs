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
    /// Creates a message audit entity for the current user with the specified audit type.
    /// </summary>
    /// <param name="type">The type of audit action.</param>
    /// <returns>A MessageAuditEntity populated with current user information.</returns>
    MessageAuditEntity GetMessageAuditEntity(MessageAuditType type);

    /// <summary>
    /// Gets the current user's email/name from claims.
    /// </summary>
    /// <returns>The user's name or email.</returns>
    string? GetCurrentUserName();
}
