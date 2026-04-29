using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using NimBus.Extensions.Identity.Data;
using System.Text.Encodings.Web;

namespace NimBus.Extensions.Identity.Controllers;

[Route("account")]
[AllowAnonymous]
public class AccountController : Controller
{
    private readonly UserManager<NimBusUser> _userManager;
    private readonly SignInManager<NimBusUser> _signInManager;
    private readonly IEmailSender _emailSender;
    private readonly NimBusIdentityOptions _options;

    public AccountController(
        UserManager<NimBusUser> userManager,
        SignInManager<NimBusUser> signInManager,
        IEmailSender emailSender,
        NimBusIdentityOptions options)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _emailSender = emailSender;
        _options = options;
    }

    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl ?? "/";
        ViewData["EnableEntraId"] = _options.EnableEntraIdLogin;
        return View();
    }

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string email, string password, bool rememberMe = false, string? returnUrl = null)
    {
        returnUrl ??= "/";
        ViewData["ReturnUrl"] = returnUrl;
        ViewData["EnableEntraId"] = _options.EnableEntraIdLogin;

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            ViewData["Error"] = "Email and password are required.";
            return View();
        }

        var user = await _userManager.FindByEmailAsync(email);
        if (user == null)
        {
            ViewData["Error"] = "Invalid email or password.";
            return View();
        }

        if (_options.RequireEmailConfirmation && !await _userManager.IsEmailConfirmedAsync(user))
        {
            ViewData["Error"] = "Please confirm your email before signing in.";
            return View();
        }

        var result = await _signInManager.PasswordSignInAsync(user, password, isPersistent: rememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
            return LocalRedirect(returnUrl);

        if (result.IsLockedOut)
            ViewData["Error"] = "Account locked. Try again in 15 minutes.";
        else
            ViewData["Error"] = "Invalid email or password.";

        return View();
    }

    [HttpGet("register")]
    public IActionResult Register()
    {
        return View();
    }

    [HttpPost("register")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(string email, string displayName, string password, string confirmPassword)
    {
        if (password != confirmPassword)
        {
            ViewData["Error"] = "Passwords do not match.";
            return View();
        }

        var user = new NimBusUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName
        };

        var result = await _userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            ViewData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
            return View();
        }

        if (_options.RequireEmailConfirmation)
        {
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var callbackUrl = Url.Action("ConfirmEmail", "Account",
                new { userId = user.Id, token }, protocol: Request.Scheme)!;

            await _emailSender.SendEmailAsync(email, "Confirm your NimBus account",
                $"Please confirm your account by <a href='{HtmlEncoder.Default.Encode(callbackUrl)}'>clicking here</a>.");

            ViewData["Message"] = "Registration successful. Please check your email to confirm your account.";
            return View("ConfirmEmail");
        }

        await _signInManager.SignInAsync(user, isPersistent: true);
        return LocalRedirect("/");
    }

    [HttpGet("confirm-email")]
    public async Task<IActionResult> ConfirmEmail(string? userId, string? token)
    {
        if (userId == null || token == null)
        {
            ViewData["Message"] = "Invalid confirmation link.";
            return View();
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            ViewData["Message"] = "User not found.";
            return View();
        }

        var result = await _userManager.ConfirmEmailAsync(user, token);
        ViewData["Message"] = result.Succeeded
            ? "Email confirmed. You can now sign in."
            : "Error confirming email. The link may have expired.";

        return View();
    }

    [HttpGet("forgot-password")]
    public IActionResult ForgotPassword() => View();

    [HttpPost("forgot-password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(string email)
    {
        var user = await _userManager.FindByEmailAsync(email);
        if (user != null && await _userManager.IsEmailConfirmedAsync(user))
        {
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var callbackUrl = Url.Action("ResetPassword", "Account",
                new { token }, protocol: Request.Scheme)!;

            await _emailSender.SendEmailAsync(email, "Reset your NimBus password",
                $"Reset your password by <a href='{HtmlEncoder.Default.Encode(callbackUrl)}'>clicking here</a>.");
        }

        // Always show success to prevent email enumeration
        ViewData["Message"] = "If an account with that email exists, a reset link has been sent.";
        return View();
    }

    [HttpGet("reset-password")]
    public IActionResult ResetPassword(string? token)
    {
        if (token == null)
            return RedirectToAction("Login");

        ViewData["Token"] = token;
        return View();
    }

    [HttpPost("reset-password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(string email, string token, string password, string confirmPassword)
    {
        if (password != confirmPassword)
        {
            ViewData["Error"] = "Passwords do not match.";
            ViewData["Token"] = token;
            return View();
        }

        var user = await _userManager.FindByEmailAsync(email);
        if (user == null)
        {
            // Don't reveal that the user does not exist
            ViewData["Message"] = "Password has been reset. You can now sign in.";
            return View("ConfirmEmail");
        }

        var result = await _userManager.ResetPasswordAsync(user, token, password);
        if (result.Succeeded)
        {
            ViewData["Message"] = "Password has been reset. You can now sign in.";
            return View("ConfirmEmail");
        }

        ViewData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
        ViewData["Token"] = token;
        return View();
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return LocalRedirect("/account/login");
    }
}
