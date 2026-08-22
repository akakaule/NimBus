using System.Collections.Generic;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using NimBus.Extensions.Identity.Controllers;

namespace NimBus.WebApp;

/// <summary>
/// Removes the NimBus Identity controllers (<see cref="AccountController"/>,
/// <see cref="AuthApiController"/>) from MVC's controller feature on
/// deployments that never called AddNimBusIdentity.
/// <para>
/// NimBus.Extensions.Identity is a Razor class library, so the Razor SDK writes
/// an [ApplicationPart] attribute for it into this app's assembly on every
/// build — MVC discovers its controllers whether or not Identity is wired in.
/// Those controllers take SignInManager/UserManager, which only
/// AddNimBusIdentity registers, so on an Entra-only (or auth-less) deployment
/// GET /api/auth/me answered 500 "Unable to resolve service for type
/// SignInManager&lt;NimBusUser&gt;". The SPA reads a 404 on that route as
/// "identity is not wired in" and hides the account UI quietly
/// (sidebar-user-footer.tsx), so dropping the routes is what the client
/// already expects.
/// </para>
/// </summary>
internal sealed class IdentityControllersDisabledFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
{
    private static readonly Assembly IdentityAssembly = typeof(AccountController).Assembly;

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        // Runs after the built-in ControllerFeatureProvider has populated the
        // list, so this prunes rather than filters.
        for (var i = feature.Controllers.Count - 1; i >= 0; i--)
        {
            if (feature.Controllers[i].Assembly == IdentityAssembly)
            {
                feature.Controllers.RemoveAt(i);
            }
        }
    }
}
