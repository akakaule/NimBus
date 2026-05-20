using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NimBus.Extensions.Identity.Data;

namespace NimBus.Extensions.Identity.Controllers;

/// <summary>
/// JSON endpoints that let the SPA know who is signed in and end the session.
/// The Razor <c>AccountController</c> POST /account/logout flow stays in place
/// for non-SPA contexts; this controller is the SPA-friendly counterpart.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthApiController : ControllerBase
{
    private readonly SignInManager<NimBusUser> _signInManager;
    private readonly UserManager<NimBusUser> _userManager;

    public AuthApiController(
        SignInManager<NimBusUser> signInManager,
        UserManager<NimBusUser> userManager)
    {
        _signInManager = signInManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Returns the current user's identity for sidebar / topbar rendering.
    /// Always 200 so the SPA can branch on <c>isAuthenticated</c> without
    /// treating an anonymous session as an error.
    /// </summary>
    [HttpGet("me")]
    [AllowAnonymous]
    public async Task<IActionResult> Me()
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return Ok(new CurrentUserResponse { IsAuthenticated = false });
        }

        var user = await _userManager.GetUserAsync(User);
        return Ok(new CurrentUserResponse
        {
            IsAuthenticated = true,
            Email = user?.Email ?? User.Identity.Name,
            DisplayName = user?.DisplayName,
        });
    }

    /// <summary>
    /// Clears the Identity cookie. SameSite=Lax on the auth cookie keeps CSRF
    /// pressure on this route minimal — the worst case from a hostile site is
    /// the operator gets unexpectedly logged out, which is not data-destructive.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return NoContent();
    }

    public sealed class CurrentUserResponse
    {
        public bool IsAuthenticated { get; set; }
        public string? Email { get; set; }
        public string? DisplayName { get; set; }
    }
}
