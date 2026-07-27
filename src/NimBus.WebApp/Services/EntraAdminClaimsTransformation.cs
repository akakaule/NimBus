using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;

namespace NimBus.WebApp.Services;

/// <summary>
/// Grants the internal <c>EIP_Management</c> platform-admin marker to Microsoft
/// Entra ID sign-ins based on configuration. Entra tokens carry group
/// <em>object IDs</em> (GUIDs) in the <c>groups</c> claim — never group names —
/// so the literal <c>groups == "EIP_Management"</c> checks throughout the WebApp
/// can never match an Entra principal on their own. This transformation bridges
/// the gap the same way <c>NimBusClaimsTransformation</c> does for Identity users.
/// </summary>
/// <remarks>
/// Configuration (both keys accept a JSON array or a comma/semicolon-separated string):
/// <list type="bullet">
/// <item><c>Authorization:AdminGroupObjectIds</c> — Entra group object IDs whose members are platform admins.</item>
/// <item><c>Authorization:AdminUserObjectIds</c> — individual user object IDs (oid) that are platform admins.</item>
/// </list>
/// </remarks>
public class EntraAdminClaimsTransformation : IClaimsTransformation
{
    private const string AdminMarker = "EIP_Management";
    private const string GroupsClaimType = "groups";
    private const string ObjectIdClaimType = "oid";
    private const string ObjectIdLongClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private readonly string[] _adminGroupIds;
    private readonly string[] _adminUserIds;

    public EntraAdminClaimsTransformation(IConfiguration configuration)
    {
        _adminGroupIds = ReadIdList(configuration, "Authorization:AdminGroupObjectIds");
        _adminUserIds = ReadIdList(configuration, "Authorization:AdminUserObjectIds");
    }

    /// <inheritdoc/>
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        if (_adminGroupIds.Length == 0 && _adminUserIds.Length == 0)
            return Task.FromResult(principal);

        // The authorization checks read Identities.First(); IClaimsTransformation
        // runs on every authenticate, so guard against double-adding.
        if (principal.HasClaim(c => c.Type == GroupsClaimType && c.Value == AdminMarker))
            return Task.FromResult(principal);

        var isAdminByGroup = _adminGroupIds.Length > 0 && principal.Claims
            .Where(c => c.Type == GroupsClaimType)
            .Any(c => _adminGroupIds.Contains(c.Value, StringComparer.OrdinalIgnoreCase));

        var objectId = principal.FindFirst(ObjectIdClaimType)?.Value
            ?? principal.FindFirst(ObjectIdLongClaimType)?.Value;
        var isAdminByUser = objectId != null
            && _adminUserIds.Contains(objectId, StringComparer.OrdinalIgnoreCase);

        if (isAdminByGroup || isAdminByUser)
        {
            identity.AddClaim(new Claim(GroupsClaimType, AdminMarker));
        }

        return Task.FromResult(principal);
    }

    private static string[] ReadIdList(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);

        // Array shape: Authorization:AdminGroupObjectIds:0, :1, ...
        var values = section.GetChildren().Select(c => c.Value).ToList();

        // Flat shape: a single comma/semicolon-separated string.
        if (values.Count == 0 && !string.IsNullOrWhiteSpace(section.Value))
            values = section.Value.Split(',', ';').ToList<string?>();

        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToArray();
    }
}
