using System;
using System.Linq;
using System.Security.Claims;
using NimBus.Core;
using NimBus.MessageStore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NimBus.WebApp.Services;

/// <summary>
/// Service for endpoint authorization and user context operations.
/// </summary>
public class EndpointAuthorizationService : IEndpointAuthorizationService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IPlatform _platform;
    private readonly ILogger<EndpointAuthorizationService> _logger;
    private readonly IConfiguration _configuration;

    public EndpointAuthorizationService(
        IHttpContextAccessor httpContextAccessor,
        IPlatform platform,
        ILogger<EndpointAuthorizationService> logger,
        IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor;
        _platform = platform;
        _logger = logger;
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public bool IsManagerOfEndpoint(string endpointId)
    {
        if (_configuration.GetValue<bool>("BypassEndpointAuthorization", false))
        {
            _logger.LogWarning("Endpoint authorization bypassed for '{EndpointId}' - BypassEndpointAuthorization is enabled", endpointId);
            return true;
        }

        var context = _httpContextAccessor.HttpContext;
        if (context?.User == null)
        {
            _logger.LogWarning("Authorization check failed: No HTTP context or user principal available");
            return false;
        }

        // Get the endpoint to check role assignments
        var endpoint = _platform.Endpoints.FirstOrDefault(e =>
            e.Id.Equals(endpointId, StringComparison.OrdinalIgnoreCase));

        if (endpoint == null)
        {
            _logger.LogWarning("Authorization check failed: Endpoint '{EndpointId}' does not exist", endpointId);
            return false;
        }

        // Check if user has the EIP_Management security group claim (admins can manage all endpoints).
        // Restrict match to the "groups" claim type so non-group claims (e.g. scp, preferred_username)
        // whose value happens to contain "EIP_Management" cannot elevate privileges.
        var userClaims = context.User.Identities.FirstOrDefault()?.Claims;
        if (userClaims != null && userClaims.Any(c => c.Type == "groups" && c.Value == "EIP_Management"))
        {
            _logger.LogInformation("User authorized for endpoint '{EndpointId}' via EIP_Management group", endpointId);
            return true;
        }

        // Get user's object ID from claims
        var userObjectId = context.User.FindFirst("oid")?.Value
            ?? context.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

        if (string.IsNullOrEmpty(userObjectId))
        {
            _logger.LogWarning("Authorization check failed: Could not retrieve user object ID from claims");
            return false;
        }

        // Check if user's object ID is in the endpoint's role assignments
        var hasRoleAssignment = endpoint.RoleAssignments
            .Any(ra => ra.PrincipalId.Equals(userObjectId, StringComparison.OrdinalIgnoreCase));

        if (hasRoleAssignment)
        {
            _logger.LogInformation("User '{UserId}' authorized for endpoint '{EndpointId}' via role assignment",
                userObjectId, endpointId);
            return true;
        }

        _logger.LogWarning("Authorization denied: User '{UserId}' has no management permission for endpoint '{EndpointId}'",
            userObjectId, endpointId);
        return false;
    }

    /// <inheritdoc/>
    public MessageAuditEntity GetMessageAuditEntity(MessageAuditType type)
    {
        var name = GetCurrentUserName();
        return new MessageAuditEntity
        {
            AuditorName = name,
            AuditTimestamp = DateTime.UtcNow,
            AuditType = type
        };
    }

    /// <inheritdoc/>
    public string? GetCurrentUserName()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context?.User == null)
        {
            return null;
        }

        // Try to get name from various claim types
        var name = context.User.FindFirst(c => c.Type.Equals("name", StringComparison.OrdinalIgnoreCase))?.Value;

        if (string.IsNullOrEmpty(name))
        {
            name = context.User.FindFirst(ClaimTypes.Name)?.Value;
        }

        if (string.IsNullOrEmpty(name))
        {
            name = context.User.FindFirst("preferred_username")?.Value;
        }

        return name;
    }
}
