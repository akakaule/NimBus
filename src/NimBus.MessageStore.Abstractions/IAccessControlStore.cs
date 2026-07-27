using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Storage for the site-wide and endpoint-scoped access-control lists (spec 026).
/// Documents are whole-replaced on write; read-modify-write and entry
/// normalization/dedup are the caller's concern.
/// </summary>
public interface IAccessControlStore
{
    /// <summary>Returns the site-wide ACL, or null when none has been written yet.</summary>
    Task<AccessControlList?> GetSiteAccessControl();

    /// <summary>Upserts the site-wide ACL (whole-document replace).</summary>
    Task SetSiteAccessControl(AccessControlList accessControl);

    /// <summary>Returns the ACL for one endpoint, or null when none has been written yet.</summary>
    Task<AccessControlList?> GetEndpointAccessControl(string endpointId);

    /// <summary>Returns every endpoint-scoped ACL (excludes the site document).</summary>
    Task<IReadOnlyList<AccessControlList>> GetEndpointAccessControls();

    /// <summary>Upserts the ACL for one endpoint (whole-document replace).</summary>
    Task SetEndpointAccessControl(string endpointId, AccessControlList accessControl);
}
