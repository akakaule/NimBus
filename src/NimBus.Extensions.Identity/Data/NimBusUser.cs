using Microsoft.AspNetCore.Identity;

namespace NimBus.Extensions.Identity.Data;

/// <summary>
/// NimBus user entity extending ASP.NET Core Identity.
/// </summary>
public class NimBusUser : IdentityUser
{
    /// <summary>
    /// Display name shown in audit trails and the WebApp UI.
    /// </summary>
    public string? DisplayName { get; set; }
}
