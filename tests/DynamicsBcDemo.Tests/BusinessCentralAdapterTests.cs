#pragma warning disable CA1707, CA2007
using System.Net;
using System.Text;
using BusinessCentral.Adapter.Clients;
using BusinessCentral.Adapter.Resilience;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;

namespace DynamicsBcDemo.Tests;

/// <summary>
/// How the Business Central adapter classifies failures, and which of them NimBus retries. Retry
/// rules match on the text NimBus builds from the exception (type name, message, stack trace), so
/// they are keyed on exception type names — never on a bare status code.
/// </summary>
[TestClass]
public sealed class BusinessCentralAdapterTests
{
    [TestMethod]
    public async Task Status429_IsThrottled_WithTheRetryAfterHint()
    {
        var client = ClientReturning(HttpStatusCode.TooManyRequests, """{"error":{"code":"Application_TooManyRequests","message":"Too many requests"}}""", retryAfterSeconds: 4);

        var ex = await Assert.ThrowsExactlyAsync<BcThrottledException>(() => client.GetCustomerAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.AreEqual(TimeSpan.FromSeconds(4), ex.RetryAfter);
        StringAssert.Contains(ex.Message, "429", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    [DataRow(HttpStatusCode.GatewayTimeout)]
    [DataRow(HttpStatusCode.RequestTimeout)]
    public async Task OutageStatuses_AreUnavailable(HttpStatusCode status)
    {
        var client = ClientReturning(status, """{"error":{"code":"ServiceUnavailable_UpdateWindow","message":"Business Central is being updated"}}""");

        await Assert.ThrowsExactlyAsync<BcUnavailableException>(() => client.GetCustomerAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [TestMethod]
    public async Task A422_IsRejected_WithBusinessCentralsErrorCode()
    {
        var client = ClientReturning(HttpStatusCode.UnprocessableEntity, """{"error":{"code":"Application_SalespersonNotFound","message":"No salesperson with e-mail 'robin.hale@contososubsea.example'"}}""");

        var ex = await Assert.ThrowsExactlyAsync<BcRequestRejectedException>(() => client.CreateQuoteRequestAsync(QuoteRequest(), CancellationToken.None));

        Assert.AreEqual(422, ex.StatusCode);
        Assert.AreEqual("Application_SalespersonNotFound", ex.ErrorCode);
        StringAssert.Contains(ex.Message, "robin.hale@contososubsea.example", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task NoConnection_IsUnavailable()
    {
        var client = new BusinessCentralClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("Connection refused")))
        {
            BaseAddress = new Uri("http://bc.test"),
        });

        await Assert.ThrowsExactlyAsync<BcUnavailableException>(() => client.GetCustomerAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [TestMethod]
    public async Task ProspectUpdate_MapsNotFoundAndConflict_ToOutcomesNotFailures()
    {
        var unknown = await ClientReturning(HttpStatusCode.NotFound, "{}").UpdateProspectAsync(Guid.NewGuid(), ProspectPatch(), CancellationToken.None);
        var owned = await ClientReturning(HttpStatusCode.Conflict, "{}").UpdateProspectAsync(Guid.NewGuid(), ProspectPatch(), CancellationToken.None);

        Assert.AreEqual(ProspectUpdateResult.NotInBusinessCentral, unknown);
        Assert.AreEqual(ProspectUpdateResult.OwnedByBusinessCentral, owned);
    }

    [TestMethod]
    public void RetryRules_RetryThrottlingAndOutages_ButNotRejections()
    {
        var options = new BcResilienceOptions();
        var policies = new DefaultRetryPolicyProvider();
        BcResilience.ConfigureRetries(policies, options);

        var throttled = policies.GetRetryPolicy("CreateBcSalesQuote", MatchedText(new BcThrottledException("GET … → 429 Too Many Requests.", null)));
        var unavailable = policies.GetRetryPolicy("CreateBcSalesQuote", MatchedText(new BcUnavailableException("POST … → 503 Service Unavailable.")));
        // A rejection whose body happens to mention 429 and 503 must still not be retried.
        var rejected = policies.GetRetryPolicy("CreateBcSalesQuote", MatchedText(new BcRequestRejectedException("POST … → 422. Body: limits 429/503 apply", 422, "Application_ItemBlocked")));

        Assert.IsNotNull(throttled);
        Assert.AreEqual(TimeSpan.FromSeconds(options.ThrottledBaseDelaySeconds), throttled.BaseDelay);
        Assert.AreEqual(BackoffStrategy.Exponential, throttled.Strategy);
        Assert.IsNotNull(unavailable);
        Assert.AreEqual(TimeSpan.FromSeconds(options.UnavailableBaseDelaySeconds), unavailable.BaseDelay);
        Assert.IsNull(rejected, "A rejected request waits for an operator; it is never retried automatically.");
    }

    [TestMethod]
    public void UnavailableRetries_LandAfterTheDemoUpdateWindow()
    {
        // Failed retries count as circuit-breaker successes, so the first outage retry must come
        // after the demo's default 20 s update window; otherwise a probe could close the circuit
        // while Business Central is still down.
        var policies = new DefaultRetryPolicyProvider();
        BcResilience.ConfigureRetries(policies, new BcResilienceOptions());

        var first = policies.GetRetryPolicy("CreateBcSalesQuote", MatchedText(new BcUnavailableException("503")))!.GetDelay(0);

        Assert.IsTrue(first > TimeSpan.FromSeconds(20), $"First retry after {first}.");
    }

    /// <summary>The text NimBus matches retry rules against: "{inner} {wrapper}", stack traces included.</summary>
    private static string MatchedText(Exception inner) => $"{inner} NimBus.Core.Messages.Exceptions.EventContextHandlerException: handler failed";

    private static BusinessCentralClient ClientReturning(HttpStatusCode status, string body, int? retryAfterSeconds = null) =>
        new(new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfterSeconds is int seconds)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return response;
        }))
        {
            BaseAddress = new Uri("http://bc.test"),
        });

    private static QuoteRequestBody QuoteRequest() => new(
        Guid.NewGuid(), Guid.NewGuid(), "OPP-10016", "Launch and recovery system", 1, null,
        new ProspectBody("Trey Research Vessels", null, null, "Seattle", null, "US", null, null),
        null, "robin.hale@contososubsea.example", "EUR", [new QuoteRequestLineBody("LARS-AF5", 1, null)]);

    private static ProspectPatchBody ProspectPatch() =>
        new(new ProspectBody("Trey Research Vessels", null, null, "Seattle", null, "US", null, null), null);
}
