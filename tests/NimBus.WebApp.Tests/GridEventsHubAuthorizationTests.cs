#pragma warning disable CA1707, CA2007
using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Constants;
using NimBus.WebApp.Hubs;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class GridEventsHubAuthorizationTests
{
    // FR-001 / FR-002 — verify the [Authorize] attribute is applied at the
    // class level, with no policy override (the default policy is the
    // RequireAuthenticatedUser policy used everywhere else in the WebApp).
    [TestMethod]
    public void GridEventsHub_HasClassLevelAuthorizeAttribute()
    {
        var attr = typeof(GridEventsHub).GetCustomAttribute<AuthorizeAttribute>(inherit: false);
        Assert.IsNotNull(attr, "GridEventsHub must carry a class-level [Authorize] attribute (spec 010 FR-001/FR-002).");
        Assert.IsNull(attr!.Policy, "No named policy expected — RequireAuthenticatedUser (default) is sufficient.");
        Assert.IsNull(attr.Roles, "No role requirements expected.");
    }

    // FR-030 — anonymous POST to the SignalR negotiate endpoint returns 401.
    // This is the headline contract of spec 010: an attacker / unauthenticated
    // browser cannot open the hub WebSocket.
    [TestMethod]
    public async Task Anonymous_NegotiatePost_Returns401()
    {
        await using var harness = await HubAuthorizationTestServer.CreateAsync();
        using var client = harness.CreateAnonymousClient();

        var url = AppEndpoints.GridEventHub + "/negotiate?negotiateVersion=1";
        using var content = new StringContent(string.Empty);
        var response = await client.PostAsync(url, content);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "Anonymous negotiate must return 401 — cookie events convert the 302 redirect (spec 010 FR-011).");
    }

    // FR-031 — an authenticated client (LocalDevAuthHandler stand-in) must
    // successfully negotiate and complete the long-polling handshake to the hub.
    [TestMethod]
    public async Task Authenticated_NegotiatePost_ReturnsConnectionToken()
    {
        await using var harness = await HubAuthorizationTestServer.CreateAsync();
        using var client = harness.CreateAuthenticatedClient();

        var url = AppEndpoints.GridEventHub + "/negotiate?negotiateVersion=1";
        using var content = new StringContent(string.Empty);
        var response = await client.PostAsync(url, content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "Authenticated client must reach the negotiate endpoint successfully.");
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "connectionToken",
            "Negotiate response must include a connectionToken — proves the hub responded, not the auth middleware.");
    }

    // FR-031 / FR-032 — an authenticated client opens a full HubConnection and
    // receives a broadcast invocation. This exercises the SignalR pipeline
    // beyond the negotiate step.
    [TestMethod]
    public async Task Authenticated_HubConnection_ReceivesEndpointUpdate()
    {
        await using var harness = await HubAuthorizationTestServer.CreateAsync();

        await using var connection = harness.BuildHubConnection(authenticated: true);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>(EventSignalNames.EndpointUpdate, payload => received.TrySetResult(payload));

        await connection.StartAsync();
        Assert.AreEqual(HubConnectionState.Connected, connection.State,
            "Authenticated SignalR connection must reach Connected (spec 010 FR-031).");

        // Broadcast through the real IHubContext — proves end-to-end
        // delivery to the authenticated client (FR-032).
        await harness.HubContext.Clients.All.SendAsync(EventSignalNames.EndpointUpdate, "endpoint-1");

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.AreSame(received.Task, completed,
            "Authenticated hub client must receive the EndpointUpdate broadcast within 5s (spec 010 FR-032).");
        Assert.AreEqual("endpoint-1", await received.Task);

        await connection.StopAsync();
    }

    // FR-033 — the anonymous HubConnection.StartAsync() call must fail with an
    // HTTP 401, NOT a transport-level "negotiate failed: unexpected response"
    // (which is what happens without the cookie events override).
    [TestMethod]
    public async Task Anonymous_HubConnectionStart_FailsWith401()
    {
        await using var harness = await HubAuthorizationTestServer.CreateAsync();
        await using var connection = harness.BuildHubConnection(authenticated: false);

        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => connection.StartAsync(),
            "Anonymous Start() must throw — the connection cannot proceed past the negotiate POST.");

        // SignalR client surfaces the 401 via HttpRequestException.StatusCode
        // (set on .NET 5+). The message includes the status code as a fallback.
        Assert.IsTrue(
            ex.StatusCode == HttpStatusCode.Unauthorized
            || ex.Message.Contains("401", StringComparison.Ordinal),
            $"Expected a 401-flavoured failure; got: {ex.Message}");
    }
}
