#pragma warning disable CA1707, CA2007
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.Identity;

namespace NimBus.WebApp.Tests;

// Unit tests for the cookie-event handlers (FR-011 / FR-012). The
// integration tests in GridEventsHubAuthorizationTests cover the end-to-end
// behaviour against a live hub; these tests are the focused, fast-running
// proofs of the per-path branching that the integration suite relies on.
[TestClass]
public sealed class NimBusCookieAuthenticationEventsTests
{
    [TestMethod]
    [DataRow("/hubs/gridevents", true)]
    [DataRow("/hubs/gridevents/negotiate", true)]
    [DataRow("/HUBS/GRIDEVENTS", true)]
    [DataRow("/api/", true)]
    [DataRow("/api/messages", true)]
    [DataRow("/account/login", false)]
    [DataRow("/Endpoints", false)]
    [DataRow("/", false)]
    [DataRow("", false)]
    public void IsApiOrHubPath_ClassifiesPathsAsExpected(string path, bool expected)
    {
        var actual = NimBusCookieAuthenticationEvents.IsApiOrHubPath(new PathString(path));
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public async Task OnRedirectToLogin_HubPath_Returns401()
    {
        var ctx = await InvokeRedirectToLoginAsync("/hubs/gridevents/negotiate");
        Assert.AreEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.IsNull(ctx.Response.Headers.Location.ToString().NullIfEmpty(),
            "Hub-path 401s must NOT include a Location header (no redirect).");
    }

    [TestMethod]
    public async Task OnRedirectToLogin_ApiPath_Returns401()
    {
        var ctx = await InvokeRedirectToLoginAsync("/api/messages");
        Assert.AreEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task OnRedirectToLogin_MvcPath_FallsThroughToRedirect()
    {
        var ctx = await InvokeRedirectToLoginAsync("/endpoints");
        // Default behaviour: 302 redirect — the cookie scheme writes the
        // redirect via the context's RedirectUri property.
        Assert.AreNotEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task OnRedirectToAccessDenied_HubPath_Returns403()
    {
        var ctx = await InvokeRedirectToAccessDeniedAsync("/hubs/gridevents");
        Assert.AreEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task OnRedirectToAccessDenied_ApiPath_Returns403()
    {
        var ctx = await InvokeRedirectToAccessDeniedAsync("/api/admin");
        Assert.AreEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [TestMethod]
    public async Task OnRedirectToAccessDenied_MvcPath_FallsThroughToRedirect()
    {
        var ctx = await InvokeRedirectToAccessDeniedAsync("/Home");
        Assert.AreNotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    private static async Task<HttpContext> InvokeRedirectToLoginAsync(string path)
    {
        var events = NimBusCookieAuthenticationEvents.Create();
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        var redirectCtx = new RedirectContext<CookieAuthenticationOptions>(
            ctx,
            new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationProperties(),
            "/account/login");
        await events.OnRedirectToLogin(redirectCtx);
        return ctx;
    }

    private static async Task<HttpContext> InvokeRedirectToAccessDeniedAsync(string path)
    {
        var events = NimBusCookieAuthenticationEvents.Create();
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        var redirectCtx = new RedirectContext<CookieAuthenticationOptions>(
            ctx,
            new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationProperties(),
            "/account/login");
        await events.OnRedirectToAccessDenied(redirectCtx);
        return ctx;
    }
}

file static class StringExtensions
{
    public static string? NullIfEmpty(this string value) => string.IsNullOrEmpty(value) ? null : value;
}
