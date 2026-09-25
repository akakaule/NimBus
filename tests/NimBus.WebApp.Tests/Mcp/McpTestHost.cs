#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.WebApp.Mcp;
using NimBus.WebApp.RateLimiting;
using NimBus.WebApp.Services;
using CoreEndpoint = NimBus.Core.Endpoints.Endpoint;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// Hosts the production MCP registration (<c>AddNimBusOperatorMcp</c>, the request guard and
/// <c>MapNimBusOperatorMcp</c>) on a TestServer, in the same pipeline order as
/// <c>Startup.Pipeline</c>, with a stub catalog and stub endpoint authorization.
/// </summary>
internal sealed class McpTestHost : IAsyncDisposable
{
    public const string TenantId = "22222222-2222-2222-2222-222222222222";
    public const string ClientId = "11111111-1111-1111-1111-111111111111";
    public const string RemoteAddressHeader = "X-Test-Remote-Address";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("nimbus-mcp-test-signing-key-0123456789abcdef"));

    private readonly IHost _host;

    private McpTestHost(IHost host)
    {
        _host = host;
        Server = host.GetTestServer();
    }

    public TestServer Server { get; }

    public static string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    /// <summary>Endpoints the stub authorization grants Reader on.</summary>
    public static readonly string[] ReadableEndpoints = ["CrmEndpoint", "ErpEndpoint"];

    public static Task<McpTestHost> StartDisabledAsync()
        => StartAsync("Production", new Dictionary<string, string?> { ["NimBus:Mcp:Enabled"] = "false" });

    public static Task<McpTestHost> StartLocalDevelopmentAsync()
        => StartAsync("Development", new Dictionary<string, string?>
        {
            ["NimBus:Mcp:Enabled"] = "true",
            ["EnableLocalDevAuthentication"] = "true",
        });

    public static Task<McpTestHost> StartEntraAsync()
        => StartAsync("Production", new Dictionary<string, string?>
        {
            ["NimBus:Mcp:Enabled"] = "true",
            ["NimBus:Mcp:Entra:TenantId"] = TenantId,
            ["NimBus:Mcp:Entra:ClientId"] = ClientId,
            ["NimBus:Mcp:AllowedOrigins:0"] = "https://agents.example",
        });

    private static async Task<McpTestHost> StartAsync(string environment, Dictionary<string, string?> settings)
    {
        settings["Environment"] = "test";

        var builder = new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings))
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices((context, services) =>
                {
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    services.AddRouting();
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IPlatform>(new TestCatalog());
                    services.AddScoped<IEndpointAuthorizationService, StubAuthorization>();

                    // Mirrors the local-dev branch of Startup.AddAuthenticationStack.
                    var authentication = services.AddAuthentication();
                    if (context.HostingEnvironment.IsDevelopment())
                    {
                        authentication.AddScheme<AuthenticationSchemeOptions, LocalDevAuthHandler>(
                            LocalDevAuthHandler.SchemeName, null);
                    }

                    services.AddAuthorization();
                    services.AddNimBusRateLimiting(context.Configuration);
                    services.AddNimBusOperatorMcp(context.Configuration, context.HostingEnvironment);

                    // Validate test tokens offline: no metadata download.
                    services.PostConfigure<JwtBearerOptions>(McpAuthenticationSchemes.Bearer, options =>
                    {
                        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                            new OpenIdConnectConfiguration { Issuer = Issuer });
                        options.TokenValidationParameters.IssuerSigningKey = SigningKey;
                    });
                });
                web.Configure(app =>
                {
                    // Test seam: lets a test present a non-loopback caller.
                    app.Use((ctx, next) =>
                    {
                        if (ctx.Request.Headers.TryGetValue(RemoteAddressHeader, out var address))
                            ctx.Connection.RemoteIpAddress = IPAddress.Parse(address!);
                        return next(ctx);
                    });
                    app.UseRouting();
                    app.UseNimBusOperatorMcpGuard();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints => endpoints.MapNimBusOperatorMcp());
                });
            });

        return new McpTestHost(await builder.StartAsync());
    }

    /// <summary>Creates an MCP client over the TestServer, optionally with a bearer token and Origin.</summary>
    public async Task<McpClient> CreateClientAsync(string? bearerToken = null, string? origin = null)
    {
        var http = Server.CreateClient();
        if (bearerToken is not null)
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        if (origin is not null)
            http.DefaultRequestHeaders.Add("Origin", origin);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(Server.BaseAddress, "/mcp") },
            http,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    /// <summary>Sends a raw MCP tools/list POST, for asserting status codes the client would hide.</summary>
    public async Task<HttpResponseMessage> PostToolsListAsync(Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        configure?.Invoke(request);
        return await Server.CreateClient().SendAsync(request);
    }

    public static string CreateToken(
        string audience = ClientId,
        string? scopes = "nimbus.observe",
        string? roles = null,
        string issuer = "")
    {
        var claims = new Dictionary<string, object>
        {
            ["oid"] = "33333333-3333-3333-3333-333333333333",
            ["tid"] = TenantId,
            ["azp"] = "44444444-4444-4444-4444-444444444444",
            ["name"] = "Agent Operator",
        };
        if (scopes is not null) claims["scp"] = scopes;
        if (roles is not null) claims["roles"] = new[] { roles };

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer.Length == 0 ? Issuer : issuer,
            Audience = audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private sealed class TestCatalog : Platform
    {
        public TestCatalog()
        {
            AddEndpoint(new CrmEndpoint());
            AddEndpoint(new ErpEndpoint());
            AddEndpoint(new BillingEndpoint());
        }
    }

    private sealed class CustomerChanged : Event { }

    private sealed class CrmEndpoint : CoreEndpoint
    {
        public CrmEndpoint() => Produces<CustomerChanged>();
    }

    private sealed class ErpEndpoint : CoreEndpoint
    {
        public ErpEndpoint() => Consumes<CustomerChanged>();
    }

    private sealed class BillingEndpoint : CoreEndpoint { }

    private sealed class StubAuthorization : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null)
            => Task.FromResult(required <= AccessRole.Reader
                && endpointId is not null
                && ReadableEndpoints.Contains(endpointId, StringComparer.OrdinalIgnoreCase));

        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync()
            => Task.FromResult(new CurrentUserAccess { ObjectId = "33333333-3333-3333-3333-333333333333" });

        public string? GetCurrentUserName() => "Agent Operator";
    }
}
