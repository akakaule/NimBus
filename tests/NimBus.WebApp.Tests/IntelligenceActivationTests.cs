#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.Extensions.IntegrationIntelligence.Controllers;
using NimBus.WebApp.RateLimiting;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.IntegrationIntelligence;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class IntelligenceActivationTests
{
    [TestMethod]
    public async Task Saved_Enable_Is_Applied_Before_Controller_Discovery_And_Payload_Construction()
    {
        var saved = new IntelligenceAdminSettings { Enabled = true, IncludeEventPayload = true };
        var overrides = saved.ApplyTo(new ConfigurationBuilder().Build());
        using var host = await CreateHost("local", false, "20", overrides: overrides);
        Assert.IsTrue(host.Services.GetRequiredService<IntegrationIntelligenceActivation>().Ready);
        Assert.IsTrue(host.Services.GetRequiredService<FailureClassificationOptions>().IncludeEventPayload);
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();
        Assert.IsTrue(routes.Any(route => route!.Contains("/classification", StringComparison.Ordinal)));
        CollectionAssert.Contains(routes, "api/admin/failure-intelligence");
    }

    [TestMethod]
    [DataRow("local")]
    [DataRow("identity")]
    [DataRow("dual")]
    [DataRow("entra")]
    public async Task Disabled_Routes_Are_404_Under_Every_Startup_Authentication_Branch(string branch)
    {
        using var host = await CreateHost(branch, false, "not-an-integer");
        using var client = host.GetTestClient();
        foreach (var route in new[] { "/api/integration-intelligence/status?endpointId=endpoint", "/api/integration-intelligence/failures/event/message/classification" })
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
        Assert.IsNull(host.Services.GetService<IFailureClassificationStore>());
    }

    [TestMethod]
    [DataRow("not-an-integer")]
    [DataRow("999")]
    public async Task Bad_Provider_Configuration_Prunes_Only_Execution_Routes(string timeout)
    {
        using var host = await CreateHost("local", true, timeout);
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();
        CollectionAssert.Contains(routes, "api/integration-intelligence/status");
        Assert.IsFalse(routes.Any(r => r!.Contains("/classification", StringComparison.Ordinal)));
        CollectionAssert.Contains(routes, "api/app/stats");
        Assert.IsFalse(host.Services.GetRequiredService<IntegrationIntelligenceActivation>().Ready);
    }

    [TestMethod]
    public async Task Request_Boundary_And_Consumers_Are_Scoped_In_The_Real_Startup()
    {
        using var host = await CreateHost("local", true, "20", descriptors =>
        {
            foreach (var type in new[] { typeof(IIntegrationIntelligenceHost), typeof(IntegrationIntelligenceStatusService), typeof(FailureClassificationService) })
                Assert.AreEqual(ServiceLifetime.Scoped, descriptors.Last(d => d.ServiceType == type).Lifetime, type.Name);
        }, configure: services => services.AddSingleton<IAccessControlSnapshotProvider, EmptySnapshot>());
        using var scope1 = host.Services.CreateScope();
        using var scope2 = host.Services.CreateScope();
        var first = scope1.ServiceProvider.GetRequiredService<IIntegrationIntelligenceHost>();
        var second = scope2.ServiceProvider.GetRequiredService<IIntegrationIntelligenceHost>();
        Assert.AreNotSame(first, second);
        var accessor = host.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("groups", "EIP_Management")], "test")) };
        Assert.IsTrue(await first.HasContributorAsync("endpoint"));
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "unprivileged")], "test")) };
        Assert.IsFalse(await second.HasContributorAsync("endpoint"), "The preceding administrator request must not grant this user access.");
        accessor.HttpContext = null;
        scope1.ServiceProvider.GetRequiredService<FailureClassificationService>();
        scope2.ServiceProvider.GetRequiredService<IntegrationIntelligenceStatusService>();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Enabled_Analyze_Policy_Respects_Global_Kill_Switch(bool rateLimit)
    {
        using var host = await CreateHost("local", true, "20", rateLimit: rateLimit);
        var endpoint = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.Single(e =>
            e.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()?.ActionName == "PostClassification");
        var policy = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()?.PolicyName;
        Assert.AreEqual(rateLimit ? RateLimitPolicyNames.Intelligence : null, policy);
    }

    private static Task<IHost> CreateHost(string branch, bool enabled, string timeout, Action<IServiceCollection>? inspect = null, bool rateLimit = true, Action<IServiceCollection>? configure = null, IConfiguration? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["NimBus:StorageProvider"] = "sqlserver", ["SqlConnection"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true",
            ["ServiceBusNamespace"] = "unit-test", ["EnableLocalDevAuthentication"] = (branch == "local").ToString(),
            ["NimBus:IntegrationIntelligence:Enabled"] = enabled.ToString(),
            ["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"] = "test-only",
            ["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:TimeoutSeconds"] = timeout,
            ["RateLimiting:Enabled"] = rateLimit.ToString(),
        };
        if (branch is "identity" or "dual") settings["NimBusIdentity:ConnectionString"] = "Server=localhost;Database=unused;Integrated Security=true";
        if (branch is "dual" or "entra")
        {
            settings["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000001";
            settings["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000002";
            settings["AzureAd:Instance"] = "https://login.microsoftonline.com/";
        }
        return new HostBuilder().UseEnvironment("Development")
            .UseDefaultServiceProvider(options => options.ValidateScopes = true)
            .ConfigureAppConfiguration(builder => { builder.AddInMemoryCollection(settings); if (overrides is not null) builder.AddConfiguration(overrides); })
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices((context, services) =>
                {
                    new Startup(context.Configuration, context.HostingEnvironment).ConfigureServices(services);
                    inspect?.Invoke(services);
                    configure?.Invoke(services);
                    // Exercise real Startup registrations/discovery without background workers reaching external services.
                    services.RemoveAll<IHostedService>();
                    services.AddControllers().AddApplicationPart(typeof(Startup).Assembly)
                        .AddApplicationPart(typeof(IntegrationIntelligenceController).Assembly);
                });
                web.Configure(app => { app.UseRouting(); app.UseAuthorization(); app.UseEndpoints(e => e.MapControllers()); });
            }).StartAsync();
    }

    private sealed class EmptySnapshot : IAccessControlSnapshotProvider
    {
        public Task<AccessControlSnapshot> GetSnapshotAsync() => Task.FromResult(AccessControlSnapshot.Empty);
        public void Invalidate() { }
    }
}
