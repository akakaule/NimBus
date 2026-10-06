using System.Threading.Tasks;

namespace NimBus.WebApp.Services;

/// <summary>
/// Role-based authorization for the management WebApp (spec 026). Roles are
/// resolved from the storage-backed access-control lists unioned with
/// claim-based compat grants (the <c>EIP_Management</c> marker claim maps to
/// site <see cref="AccessRole.Owner"/>; code-defined endpoint RoleAssignments
/// map to endpoint Owner), so a fresh, empty store never locks out configured
/// administrators.
/// </summary>
public interface IEndpointAuthorizationService
{
    /// <summary>
    /// Whether the current principal holds at least <paramref name="required"/> —
    /// site-wide, or on <paramref name="endpointId"/> when given (effective role
    /// is the max of the site role and the endpoint-scoped role).
    /// </summary>
    Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null);

    /// <summary>
    /// Like <see cref="HasRoleAsync"/>, but resolved from access-control lists re-read from the
    /// store rather than the cached snapshot, so a grant revoked moments ago is honored. Operator
    /// commands check this just before they change a message. A store failure fails closed:
    /// only claim-based and code-defined grants remain. The default implementation falls back to
    /// <see cref="HasRoleAsync"/> for implementations that predate it.
    /// </summary>
    Task<bool> HasRoleFreshAsync(AccessRole required, string endpointId) => HasRoleAsync(required, endpointId);

    /// <summary>
    /// Whether the current principal may view raw event payloads. Deliberately
    /// NOT implied by <see cref="AccessRole.Owner"/> or any compat grant
    /// (spec 021: PII reveal is a separately-granted capability).
    /// </summary>
    Task<bool> CanReadPiiAsync();

    /// <summary>The current principal's full resolved access (for /api/access-control/me and UI gating).</summary>
    Task<CurrentUserAccess> GetCurrentUserAccessAsync();

    /// <summary>Gets the current user's display name / email from claims.</summary>
    string? GetCurrentUserName();
}
