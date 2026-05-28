#pragma warning disable CA1707, CA2007
using System;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NimBus.Extensions.Identity;
using NimBus.WebApp.Constants;
using NimBus.WebApp.Hubs;

namespace NimBus.WebApp.Tests;

// Minimal in-process ASP.NET host that wires the real GridEventsHub (with its
// [Authorize] attribute) and the real NimBusCookieAuthenticationEvents handler.
// We deliberately avoid spinning up the full NimBus.WebApp Startup — that
// project carries Cosmos / Service Bus / NSwag dependencies the auth tests
// don't need.
//
// Two auth schemes are registered:
//   * "TestCookie" — a CookieAuthenticationHandler whose Events are populated
//     by NimBusCookieAuthenticationEvents.Create(). This is the surface under
//     test for FR-011 / FR-012.
//   * "TestAuthenticated" — a stub handler that returns a fixed authenticated
//     principal when the caller presents the X-Test-Auth header. This stands
//     in for LocalDevAuthHandler (FR-031 / FR-032).
//
// The default scheme is "TestCookie" — anonymous calls hit the cookie events
// and get a 401 on the hub path. Authenticated calls send X-Test-Auth and
// satisfy [Authorize].
internal sealed class HubAuthorizationTestServer : IAsyncDisposable
{
    public const string TestAuthScheme = "TestAuthenticated";
    public const string TestAuthHeader = "X-Test-Auth";
    public const string TestAuthHeaderValue = "yes";

    public IHost Host { get; }
    public TestServer Server { get; }
    public IHubContext<GridEventsHub> HubContext { get; }

    private HubAuthorizationTestServer(IHost host)
    {
        Host = host;
        Server = host.GetTestServer();
        HubContext = host.Services.GetRequiredService<IHubContext<GridEventsHub>>();
    }

    public static async Task<HubAuthorizationTestServer> CreateAsync()
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    services.AddRouting();
                    services.AddSignalR();

                    // Policy scheme picks the stub handler when the
                    // X-Test-Auth header is present, otherwise falls back to
                    // the cookie scheme (which fires the NimBus events on
                    // anonymous requests).
                    services
                        .AddAuthentication("TestPolicy")
                        .AddPolicyScheme("TestPolicy", "TestPolicy", o =>
                        {
                            o.ForwardDefaultSelector = ctx =>
                                ctx.Request.Headers.TryGetValue(TestAuthHeader, out var v)
                                && v == TestAuthHeaderValue
                                    ? TestAuthScheme
                                    : CookieAuthenticationDefaults.AuthenticationScheme;
                        })
                        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
                        {
                            options.LoginPath = "/account/login";
                            options.AccessDeniedPath = "/account/login";
                            options.Events = NimBusCookieAuthenticationEvents.Create();
                        })
                        .AddScheme<AuthenticationSchemeOptions, StubAuthenticatedHandler>(TestAuthScheme, _ => { });

                    services.AddAuthorization();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHub<GridEventsHub>(AppEndpoints.GridEventHub);
                    });
                });
            });

        var host = await builder.StartAsync();
        return new HubAuthorizationTestServer(host);
    }

    /// <summary>
    /// HttpClient that runs against the in-process TestServer with no
    /// authentication — used by FR-030 to assert the 401 negotiate response.
    /// </summary>
    public HttpClient CreateAnonymousClient() => Server.CreateClient();

    /// <summary>
    /// HttpClient that always carries the X-Test-Auth header so the
    /// StubAuthenticatedHandler issues an authenticated principal —
    /// stands in for the LocalDevAuthHandler for FR-031 / FR-032.
    /// </summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = Server.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHeader, TestAuthHeaderValue);
        return client;
    }

    /// <summary>
    /// Builds a SignalR HubConnection that tunnels through the TestServer's
    /// in-memory transport. The handler-factory adds the X-Test-Auth header
    /// when <paramref name="authenticated"/> is true.
    /// </summary>
    public HubConnection BuildHubConnection(bool authenticated)
    {
        var url = Server.BaseAddress + AppEndpoints.GridEventHub.TrimStart('/');
        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.WebSocketFactory = (ctx, ct) =>
                    throw new NotSupportedException("TestServer transport does not negotiate websockets; longpolling is selected.");
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                if (authenticated)
                {
                    options.Headers[TestAuthHeader] = TestAuthHeaderValue;
                }
            })
            .Build();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }

    // Stub auth handler — succeeds when the X-Test-Auth header is present.
    // Mirrors LocalDevAuthHandler's "return a fixed authenticated principal"
    // shape without dragging the production config gates.
    private sealed class StubAuthenticatedHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public StubAuthenticatedHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder) : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(TestAuthHeader, out var v) || v != TestAuthHeaderValue)
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "test-user"),
                new Claim(ClaimTypes.Name, "Test User"),
            }, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
