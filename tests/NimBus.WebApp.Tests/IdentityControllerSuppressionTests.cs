#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.Identity.Controllers;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Tests;

/// <summary>
/// A deployment that never calls AddNimBusIdentity has no
/// SignInManager/UserManager in the container, but the Identity extension is a
/// Razor class library so MVC discovers its controllers anyway. Routing
/// /api/auth/me there produced a 500 on activation; the SPA reads a 404 as
/// "identity is not wired in". These tests pin the suppression that turns the
/// 500 back into a 404 — and the control case proves the routes really would
/// appear without it.
/// </summary>
[TestClass]
public class IdentityControllerSuppressionTests
{
    [TestMethod]
    public async Task Identity_routes_disappear_when_the_suppressor_is_installed()
    {
        using var host = await BuildHostAsync(suppressIdentityControllers: true);

        var routes = Routes(host);

        CollectionAssert.DoesNotContain(routes, "api/auth/me", "Identity is not registered, so /api/auth/me must 404 rather than 500 on activation.");
        Assert.IsFalse(
            routes.Any(r => r.StartsWith("api/auth/", StringComparison.OrdinalIgnoreCase) || r.StartsWith("account/", StringComparison.OrdinalIgnoreCase)),
            "No controller from the Identity extension may be routed when Identity is not registered.");
        CollectionAssert.Contains(routes, "api/app/stats", "Suppression must be scoped to the Identity assembly — the app's own controllers stay routed.");
    }

    [TestMethod]
    public async Task Identity_routes_are_present_without_the_suppressor()
    {
        using var host = await BuildHostAsync(suppressIdentityControllers: false);

        CollectionAssert.Contains(
            Routes(host),
            "api/auth/me",
            "The Identity application part is added by the Razor SDK on every build — if this ever stops being true the suppression is dead code.");
    }

    private static List<string> Routes(IHost host)
        => host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToList();

    private static async Task<IHost> BuildHostAsync(bool suppressIdentityControllers)
    {
        return await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    services.AddRouting();

                    // Under `dotnet test` the entry assembly is the test host, so
                    // the application parts production gets from the Razor SDK's
                    // generated [ApplicationPart] attributes are added by hand.
                    var mvc = services.AddControllers()
                        .AddApplicationPart(typeof(ApplicationApiController).Assembly)
                        .AddApplicationPart(typeof(AccountController).Assembly);

                    if (suppressIdentityControllers)
                    {
                        mvc.ConfigureApplicationPartManager(apm =>
                            apm.FeatureProviders.Add(new IdentityControllersDisabledFeatureProvider()));
                    }
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .StartAsync();
    }
}
