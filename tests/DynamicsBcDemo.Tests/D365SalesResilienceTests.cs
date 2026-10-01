#pragma warning disable CA1707, CA2007
using System.Net;
using System.Text;
using D365Sales.Adapter.Clients;
using D365Sales.Adapter.Resilience;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace DynamicsBcDemo.Tests;

/// <summary>
/// How the Dynamics 365 adapter classifies Dataverse failures, and which of them NimBus retries —
/// the same shape as the Business Central adapter.
/// </summary>
[TestClass]
public sealed class D365SalesResilienceTests
{
    [TestMethod]
    public async Task Status429_IsThrottled_AndCarriesTheRetryAfterHint()
    {
        var client = ClientReturning(HttpStatusCode.TooManyRequests, """{"error":{"code":"0x80072322","message":"Number of requests exceeded the limit"}}""", retryAfterSeconds: 42);

        var ex = await Assert.ThrowsExactlyAsync<DataverseThrottledException>(() => client.PatchAccountAsync(Guid.NewGuid(), Columns(), CancellationToken.None));

        Assert.AreEqual(TimeSpan.FromSeconds(42), ex.RetryAfter);
        Assert.AreEqual(TimeSpan.FromSeconds(42), ((IRetryAfterHint)ex).RetryAfter);
        StringAssert.Contains(ex.Message, "429", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    [DataRow(HttpStatusCode.BadGateway)]
    [DataRow(HttpStatusCode.GatewayTimeout)]
    [DataRow(HttpStatusCode.RequestTimeout)]
    [DataRow(HttpStatusCode.InternalServerError)]
    public async Task OutageStatuses_AreUnavailable(HttpStatusCode status)
    {
        var client = ClientReturning(status, "{}");

        await Assert.ThrowsExactlyAsync<DataverseUnavailableException>(() => client.UpsertBcQuoteAsync(Guid.NewGuid(), Columns(), CancellationToken.None));
    }

    [TestMethod]
    public async Task A4xx_IsRejected_WithTheDataverseErrorCode()
    {
        var client = ClientReturning(HttpStatusCode.NotFound, """{"error":{"code":"0x80040217","message":"opportunity With Id = 0bb00000-0000-4000-8000-000000000105 Does Not Exist"}}""");

        var ex = await Assert.ThrowsExactlyAsync<DataverseRequestRejectedException>(() => client.PatchOpportunityAsync(Guid.NewGuid(), Columns(), CancellationToken.None));

        Assert.AreEqual(404, ex.StatusCode);
        Assert.AreEqual("0x80040217", ex.ErrorCode);
        StringAssert.Contains(ex.Message, "Does Not Exist", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task NoConnection_IsUnavailable()
    {
        var client = new DataverseClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("Connection refused")))
        {
            BaseAddress = new Uri("http://d365.test"),
        });

        var ex = await Assert.ThrowsExactlyAsync<DataverseUnavailableException>(() => client.WinOpportunityAsync(Guid.NewGuid(), 1000m, DateTime.UtcNow, "Won", CancellationToken.None));

        Assert.IsInstanceOfType<HttpRequestException>(ex.InnerException);
    }

    [TestMethod]
    public void RetryRules_RetryThrottlingAndOutages_ButNotRejections()
    {
        var options = new D365ResilienceOptions();
        var policies = new DefaultRetryPolicyProvider();
        D365Resilience.ConfigureRetries(policies, options);

        var throttled = policies.GetRetryPolicy("BcSalesQuoteCreated", Wrapped(new DataverseThrottledException("PATCH … → 429 Too Many Requests.", null)));
        var unavailable = policies.GetRetryPolicy("BcSalesQuoteCreated", Wrapped(new DataverseUnavailableException("PATCH … → 503 Service Unavailable.")));
        // A rejection whose body happens to mention 429 and 503 must still not be retried.
        var rejected = policies.GetRetryPolicy("BcSalesQuoteCreated", Wrapped(new DataverseRequestRejectedException("PATCH … → 400. Body: limits 429/503 apply", 400, "0x80040203")));

        Assert.IsNotNull(throttled);
        Assert.AreEqual(TimeSpan.FromSeconds(options.ThrottledBaseDelaySeconds), throttled.BaseDelay);
        Assert.IsNotNull(unavailable);
        Assert.AreEqual(TimeSpan.FromSeconds(options.UnavailableBaseDelaySeconds), unavailable.BaseDelay);
        Assert.IsNull(rejected, "A rejected request waits for an operator; it is never retried automatically.");
    }

    [TestMethod]
    public void CircuitBreaker_CountsOutages_ButNotThrottlingOrRejections()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero));
        var options = new CircuitBreakerOptions();
        D365Resilience.ConfigureCircuitBreaker(options, new D365ResilienceOptions());

        var paced = new EndpointCircuitBreaker("D365SalesEndpoint", options, clock);
        for (var i = 0; i < 10; i++)
        {
            paced.RecordFailure(Wrapped(new DataverseThrottledException("429", TimeSpan.FromSeconds(5))));
            paced.RecordFailure(Wrapped(new DataverseRequestRejectedException("400", 400, null)));
        }

        var outage = new EndpointCircuitBreaker("D365SalesEndpoint", options, clock);
        for (var i = 0; i < 10; i++)
            outage.RecordFailure(Wrapped(new DataverseUnavailableException("503")));

        Assert.AreEqual(CircuitState.Closed, paced.State, "Throttling and rejections are not outages.");
        Assert.AreEqual(CircuitState.Open, outage.State);
    }

    /// <summary>What the retry lookup and the breaker see: the handler's exception, wrapped by the pipeline.</summary>
    private static Exception Wrapped(Exception inner) => new EventContextHandlerException(inner);

    private static Dictionary<string, object?> Columns() => new() { ["name"] = "City Power & Light" };

    private static DataverseClient ClientReturning(HttpStatusCode status, string body, int? retryAfterSeconds = null) =>
        new(new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfterSeconds is int seconds)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return response;
        }))
        {
            BaseAddress = new Uri("http://d365.test"),
        });
}
