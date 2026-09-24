#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Services.ApplicationInsights;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The Application Insights query API (api.applicationinsights.io) stopped accepting API keys on
/// 2026-03-31 and now requires a Microsoft Entra bearer token. These tests resolve
/// <see cref="IApplicationInsightsService"/> from the real <see cref="Startup"/> registrations and
/// capture what the typed client sends.
/// </summary>
[TestClass]
public sealed class ApplicationInsightsQueryAuthenticationTests
{
    private const string ApplicationId = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
    private const string EntraToken = "entra-access-token";

    [TestMethod]
    [DataRow("logs")]
    [DataRow("latency")]
    public async Task Query_sends_an_entra_bearer_token_and_no_api_key(string query)
    {
        var credential = new RecordingCredential();
        var handler = new CapturingHandler();
        using var host = BuildHost(credential, handler, ApplicationId);
        var service = host.Services.GetRequiredService<IApplicationInsightsService>();

        if (query == "logs")
        {
            _ = await service.GetLogs(new Filter { EventId = "event-1" });
        }
        else
        {
            _ = await service.GetLatencyMetrics(TimeSpan.FromHours(1));
        }

        var request = handler.Requests.Single();
        Assert.AreEqual($"Bearer {EntraToken}", request.Authorization);
        Assert.IsFalse(request.SentApiKey, "The retired x-api-key header must not be sent.");
        Assert.AreEqual(
            $"https://api.applicationinsights.io/v1/apps/{ApplicationId}/query",
            request.Uri.GetLeftPart(UriPartial.Path));
        Assert.AreEqual(
            "https://api.applicationinsights.io/.default",
            credential.RequestedScopes.Single().Single());
    }

    [TestMethod]
    public async Task Query_without_an_application_id_fails_before_requesting_a_token()
    {
        var credential = new RecordingCredential();
        var handler = new CapturingHandler();
        using var host = BuildHost(credential, handler, applicationId: null);
        var service = host.Services.GetRequiredService<IApplicationInsightsService>();

        // Unchanged: with no base address HttpClient rejects the relative query URI, and the
        // event logs endpoint logs that and returns no logs.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.GetLogs(new Filter { EventId = "event-1" }));

        Assert.AreEqual(0, credential.RequestedScopes.Count, "No token should be requested.");
        Assert.AreEqual(0, handler.Requests.Count);
    }

    private static IHost BuildHost(TokenCredential credential, HttpMessageHandler handler, string? applicationId)
    {
        var settings = new Dictionary<string, string?>
        {
            ["NimBus:StorageProvider"] = "sqlserver",
            ["SqlConnection"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true",
            ["ServiceBusNamespace"] = "unit-test",
            ["EnableLocalDevAuthentication"] = "true",
            // A site deployed before the switch still carries the retired key setting.
            ["AppInsights:ApiKey"] = "retired-api-key",
        };
        if (applicationId is not null)
        {
            settings["AppInsights:ApplicationId"] = applicationId;
        }

        return new HostBuilder()
            .UseEnvironment("Development")
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(settings))
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices((context, services) =>
                {
                    new Startup(context.Configuration, context.HostingEnvironment).ConfigureServices(services);
                    services.AddSingleton(credential);
                    services.AddHttpClient<IApplicationInsightsService, ApplicationInsightsService>()
                        .ConfigurePrimaryHttpMessageHandler(() => handler);
                });
                web.Configure(_ => { });
            })
            .Build();
    }

    private sealed class RecordingCredential : TokenCredential
    {
        public List<string[]> RequestedScopes { get; } = new();

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            RequestedScopes.Add(requestContext.Scopes);
            return new AccessToken(EntraToken, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }

    private sealed record CapturedRequest(Uri Uri, string? Authorization, bool SentApiKey);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        // An empty result table. The log parser requires these columns; with no rows the
        // latency parser reads none.
        private const string EmptyResult =
            "{\"tables\":[{\"columns\":[" +
            "{\"name\":\"timestamp\",\"type\":\"datetime\"}," +
            "{\"name\":\"message\",\"type\":\"string\"}," +
            "{\"name\":\"severityLevel\",\"type\":\"int\"}," +
            "{\"name\":\"customDimensions\",\"type\":\"dynamic\"}]," +
            "\"rows\":[]}]}";

        public List<CapturedRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.Contains("x-api-key")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmptyResult),
            });
        }
    }
}
