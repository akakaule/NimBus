#pragma warning disable CA1707, CA2007
using System;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Verifies that the anonymous exemption on the NSwag-generated controllers is
/// scoped to exactly the intended actions: /api/app/stats stays anonymous (for
/// health probes), /api/me requires authentication despite living on the same
/// controller, and the storage-hook webhook stays anonymous (key-gated).
/// Mirrors the production setup: global AuthorizeFilter requiring an
/// authenticated user plus the AllowAnonymousActionsConvention.
/// </summary>
[TestClass]
public class AnonymousEndpointsTests
{
    private const string TestAuthScheme = "TestAuthenticated";
    private const string TestAuthHeader = "X-Test-Auth";

    private static IHost _host = null!;
    private static TestServer _server = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    services.AddSingleton<IApplicationApiController>(new StubApplicationApi());
                    services.AddSingleton<IStorageHookApiController>(new StubStorageHookApi());

                    services
                        .AddAuthentication(TestAuthScheme)
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(TestAuthScheme, _ => { });
                    services.AddAuthorization();

                    services.AddControllers(options =>
                    {
                        var policy = new AuthorizationPolicyBuilder()
                            .RequireAuthenticatedUser()
                            .Build();
                        options.Filters.Add(new AuthorizeFilter(policy));
                        options.Conventions.Add(new AllowAnonymousActionsConvention());
                    })
                    .AddApplicationPart(typeof(ApplicationApiController).Assembly);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            });

        _host = await builder.StartAsync();
        _server = _host.GetTestServer();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [TestMethod]
    public async Task App_stats_is_reachable_anonymously()
    {
        using var client = _server.CreateClient();
        using var response = await client.GetAsync("/api/app/stats");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Me_requires_authentication()
    {
        using var client = _server.CreateClient();
        using var response = await client.GetAsync("/api/me");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Me_succeeds_when_authenticated()
    {
        using var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHeader, "yes");
        using var response = await client.GetAsync("/api/me");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Storage_hook_webhook_is_reachable_anonymously()
    {
        using var client = _server.CreateClient();
        using var response = await client.PostAsync("/api/storagehook/cosmos/ep-1", content: null);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class StubApplicationApi : IApplicationApiController
    {
        public Task<ActionResult<ApplicationStatus>> GetApiAppStatsAsync()
            => Task.FromResult<ActionResult<ApplicationStatus>>(new OkObjectResult(new ApplicationStatus()));

        public Task<ActionResult<UserInfo>> GetMeAsync()
            => Task.FromResult<ActionResult<UserInfo>>(new OkObjectResult(new UserInfo()));
    }

    private sealed class StubStorageHookApi : IStorageHookApiController
    {
        public Task<IActionResult> StoragehookReceiveCosmosAsync(string endpointId)
            => Task.FromResult<IActionResult>(new OkResult());
    }

    private sealed class HeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public HeaderAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder) : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(TestAuthHeader))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "test-user"),
                new Claim(ClaimTypes.Name, "Test User"),
            }, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
