using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp;

/// <summary>
/// Grants anonymous access to individual NSwag-generated controller actions.
/// The generated controllers (ApiContract.g.cs) cannot carry per-action
/// attributes, and a class-level [AllowAnonymous] would silently exempt every
/// action on the controller — including ones added later. This convention
/// scopes the exemption to exactly the actions that must stay anonymous:
/// /api/app/stats, which health probes and status monitors call without
/// credentials and which only returns non-sensitive information (environment
/// name, version, storage provider). /api/me on the same controller stays
/// behind the global authorization filter.
/// </summary>
internal sealed class AllowAnonymousActionsConvention : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            if (controller.ControllerType != typeof(ApplicationApiController))
            {
                continue;
            }

            foreach (var action in controller.Actions)
            {
                if (action.ActionMethod.Name == nameof(ApplicationApiController.GetApiAppStats))
                {
                    action.Filters.Add(new AllowAnonymousFilter());
                }
            }
        }
    }
}
