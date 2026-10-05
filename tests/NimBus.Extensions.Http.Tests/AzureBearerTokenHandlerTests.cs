#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.Http;

namespace NimBus.Extensions.Http.Tests;

[TestClass]
public class AzureBearerTokenHandlerTests
{
    private const string Scope = "https://contoso.crm.dynamics.com/.default";

    [TestMethod]
    public async Task SendAsync_SetsTheBearerHeader()
    {
        var credential = new CountingCredential(TimeSpan.FromHours(1));
        var server = new RecordingHandler();
        using var client = new HttpClient(new AzureBearerTokenHandler(credential, Scope) { InnerHandler = server });

        await client.GetAsync(new Uri("https://example.test/accounts"));

        Assert.AreEqual("Bearer", server.LastAuthorization?.Scheme);
        Assert.AreEqual("token-1", server.LastAuthorization?.Parameter);
        CollectionAssert.AreEqual(new[] { Scope }, credential.LastScopes);
    }

    [TestMethod]
    public async Task GetTokenAsync_ReusesTheTokenUntilTheRefreshMargin()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-02T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var credential = new CountingCredential(TimeSpan.FromHours(1), clock);
        var cache = new AzureAccessTokenCache(credential, clock, Scope);

        Assert.AreEqual("token-1", await cache.GetTokenAsync());
        clock.Advance(TimeSpan.FromMinutes(54));
        Assert.AreEqual("token-1", await cache.GetTokenAsync());
        Assert.AreEqual(1, credential.Calls);

        clock.Advance(TimeSpan.FromMinutes(1)); // 5 minutes before expiry
        Assert.AreEqual("token-2", await cache.GetTokenAsync());
        Assert.AreEqual(2, credential.Calls);
    }

    [TestMethod]
    public async Task GetTokenAsync_RefreshesAtRefreshOn()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-02T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var credential = new CountingCredential(TimeSpan.FromHours(1), clock, refreshAfter: TimeSpan.FromMinutes(30));
        var cache = new AzureAccessTokenCache(credential, clock, Scope);

        await cache.GetTokenAsync();
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.AreEqual("token-2", await cache.GetTokenAsync());
    }

    [TestMethod]
    public async Task GetTokenAsync_ConcurrentFirstCallsShareOneRequest()
    {
        var credential = new CountingCredential(TimeSpan.FromHours(1)) { Delay = TimeSpan.FromMilliseconds(100) };
        var cache = new AzureAccessTokenCache(credential, Scope);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.GetTokenAsync().AsTask()));

        Assert.AreEqual(1, credential.Calls);
        Assert.IsTrue(tokens.All(t => t == "token-1"));
    }

    [TestMethod]
    public async Task AddAzureBearerToken_SharesOneCacheAcrossHandlerRotations()
    {
        var credential = new CountingCredential(TimeSpan.FromHours(1));
        var services = new ServiceCollection();
        services.AddSingleton<TokenCredential>(credential);
        services.AddHttpClient("dataverse")
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler())
            .AddAzureBearerToken(Scope);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get("dataverse");

        // Build the handler chain three times, as IHttpClientFactory does each time a handler expires.
        for (var i = 0; i < 3; i++)
        {
            var builder = provider.GetRequiredService<HttpMessageHandlerBuilder>();
            builder.Name = "dataverse";
            foreach (var configure in options.HttpMessageHandlerBuilderActions)
            {
                configure(builder);
            }

            using var client = new HttpClient(builder.Build());
            await client.GetAsync(new Uri("https://example.test/accounts"));
        }

        Assert.AreEqual(1, credential.Calls);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(new string[0])]
    [DataRow(new[] { " " })]
    public void Constructor_RejectsMissingScopes(string[]? scopes)
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AzureAccessTokenCache(new CountingCredential(TimeSpan.FromHours(1)), scopes!));
    }

    private sealed class CountingCredential(TimeSpan lifetime, TimeProvider? clock = null, TimeSpan? refreshAfter = null) : TokenCredential
    {
        private int _calls;

        public int Calls => _calls;
        public string[]? LastScopes { get; private set; }
        public TimeSpan Delay { get; init; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            LastScopes = requestContext.Scopes;
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            var now = (clock ?? TimeProvider.System).GetUtcNow();
            return new AccessToken(
                $"token-{call}",
                now + lifetime,
                refreshOn: refreshAfter is { } after ? now + after : null);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public System.Net.Http.Headers.AuthenticationHeaderValue? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
