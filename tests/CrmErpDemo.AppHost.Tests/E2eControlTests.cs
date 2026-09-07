#pragma warning disable CA1707, CA2007

using CrmErpDemo.Contracts.E2E;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CrmErpDemo.AppHost.Tests;

[TestClass]
public sealed class E2eControlTests
{
    [TestMethod]
    [DataRow("Production", true)]
    [DataRow("Development", false)]
    public async Task Control_routes_are_absent_outside_the_explicit_development_profile(string environment, bool enabled)
    {
        await using var app = CreateApp(environment, enabled);
        Assert.IsNull(app.MapE2eControls());
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/e2e/ready");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Control_routes_require_the_key_and_reject_invalid_scripts()
    {
        await using var app = CreateApp("Development", true);
        Assert.IsNotNull(app.MapE2eControls());
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var unauthorized = await client.GetAsync("/api/e2e/ready");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        client.DefaultRequestHeaders.Add(E2eSettings.Header, "wrong");
        using var wrong = await client.GetAsync("/api/e2e/ready");
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
        client.DefaultRequestHeaders.Remove(E2eSettings.Header);
        client.DefaultRequestHeaders.Add(E2eSettings.Header, new string('x', 32));
        using var authorized = await client.GetAsync("/api/e2e/ready");
        Assert.AreEqual(HttpStatusCode.OK, authorized.StatusCode);
        using var invalid = await client.PutAsJsonAsync($"/api/e2e/sessions/{Guid.NewGuid()}", new E2eScript("Updated", "handler", ["invalid"]));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private static WebApplication CreateApp(string environment, bool enabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["E2E:Enabled"] = enabled.ToString(), ["E2E:Key"] = new string('x', 32),
        });
        builder.Services.AddSingleton<E2eRegistry>();
        return builder.Build();
    }

    [TestMethod]
    public void Controls_require_development_explicit_opt_in_and_a_key()
    {
        Assert.IsFalse(E2eSettings.IsEnabled("Production", true, new string('x', 32)));
        Assert.IsFalse(E2eSettings.IsEnabled("Development", false, new string('x', 32)));
        Assert.IsFalse(E2eSettings.IsEnabled("Development", true, ""));
        Assert.IsTrue(E2eSettings.IsEnabled("Development", true, new string('x', 32)));
    }

    [TestMethod]
    public void Scripts_are_session_and_stage_scoped_and_consumed_once()
    {
        var registry = new E2eRegistry();
        var session = Guid.NewGuid();
        registry.Configure(session, new E2eScript("Updated", "handler", ["retry", "discard"]));
        Assert.AreEqual("continue", registry.Attempt(Guid.NewGuid(), "Updated", "handler", "m0", "e0", "o0").Action);
        Assert.AreEqual("continue", registry.Attempt(session, "Updated", "middleware", "m0", "e0", "o0").Action);
        Assert.AreEqual("retry", registry.Attempt(session, "Updated", "handler", "m1", "e1", "o1").Action);
        Assert.AreEqual("discard", registry.Attempt(session, "Updated", "handler", "m2", "e1", "o1").Action);
        Assert.AreEqual("continue", registry.Attempt(session, "Updated", "handler", "m3", "e2", "o2").Action);
        Assert.HasCount(3, registry.Snapshot(session));
    }

    [TestMethod]
    public void Reconfiguration_preserves_attempt_history_and_rejects_unbounded_scripts()
    {
        var registry = new E2eRegistry();
        var session = Guid.NewGuid();
        registry.Configure(session, new E2eScript("Updated", "handler", ["fail"]));
        registry.Attempt(session, "Updated", "handler", "m1", "e1", "o1");
        registry.Configure(session, new E2eScript("Updated", "handler", ["continue"]));
        Assert.HasCount(1, registry.Snapshot(session));
        Assert.Throws<ArgumentException>(() => registry.Configure(session, new E2eScript("Updated", "handler", ["unknown"])));
        Assert.Throws<ArgumentException>(() => registry.Configure(session, new E2eScript("Updated", "handler", Enumerable.Repeat("fail", 101).ToArray())));
    }

    [TestMethod]
    public async Task Concurrent_attempts_consume_exactly_the_configured_failure_budget()
    {
        var registry = new E2eRegistry();
        var session = Guid.NewGuid();
        registry.Configure(session, new E2eScript("Updated", "handler", Enumerable.Repeat("retry", 5).ToArray()));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            registry.Attempt(session, "Updated", "handler", $"m{i}", "e1", "o1"))));
        Assert.AreEqual(5, attempts.Count(attempt => attempt.Action == "retry"));
        Assert.HasCount(20, registry.Snapshot(session));
    }
}
