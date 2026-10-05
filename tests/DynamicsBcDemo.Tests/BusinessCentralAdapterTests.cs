#pragma warning disable CA1707, CA2007
using System.Net;
using System.Text;
using BusinessCentral.Adapter.Clients;
using BusinessCentral.Adapter.Resilience;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace DynamicsBcDemo.Tests;

/// <summary>
/// How the Business Central adapter classifies failures, and which of them NimBus retries. Retry
/// rules match on exception types (AddExceptionRule&lt;T&gt;) — never on a bare status code in the text.
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

        var ex = await Assert.ThrowsExactlyAsync<BcRequestRejectedException>(() => client.UpsertCrmOpportunityAsync(Guid.NewGuid(), Opportunity(), CancellationToken.None));

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
    [DataRow(HttpStatusCode.Created, ProspectUpsertResult.Created)]
    [DataRow(HttpStatusCode.OK, ProspectUpsertResult.Updated)]
    [DataRow(HttpStatusCode.Conflict, ProspectUpsertResult.OwnedByBusinessCentral)]
    public async Task ProspectUpsert_MapsTheAnswer_ToAnOutcomeNotAFailure(HttpStatusCode status, ProspectUpsertResult expected)
    {
        var result = await ClientReturning(status, "{}").UpsertProspectAsync(Guid.NewGuid(), Prospect(), CancellationToken.None);

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void RetryRules_RetryThrottlingAndOutages_ButNotRejections()
    {
        var options = new BcResilienceOptions();
        var policies = new DefaultRetryPolicyProvider();
        BcResilience.ConfigureRetries(policies, options);

        var throttled = policies.GetRetryPolicy("D365OpportunityUpdated", Wrapped(new BcThrottledException("PUT … → 429 Too Many Requests.", null)));
        var unavailable = policies.GetRetryPolicy("D365OpportunityUpdated", Wrapped(new BcUnavailableException("PUT … → 503 Service Unavailable.")));
        // A rejection whose body happens to mention 429 and 503 must still not be retried.
        var rejected = policies.GetRetryPolicy("D365OpportunityUpdated", Wrapped(new BcRequestRejectedException("PUT … → 422. Body: limits 429/503 apply", 422, "Application_SalespersonNotFound")));

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
        // The first outage retry comes after the demo's default 20 s update window, so it succeeds and
        // the scene ends on its own; a retry inside the window would fail again and count against the
        // circuit breaker.
        var policies = new DefaultRetryPolicyProvider();
        BcResilience.ConfigureRetries(policies, new BcResilienceOptions());

        var first = policies.GetRetryPolicy("D365OpportunityUpdated", Wrapped(new BcUnavailableException("503")))!.GetDelay(0);

        Assert.IsTrue(first > TimeSpan.FromSeconds(20), $"First retry after {first}.");
    }

    [TestMethod]
    public void AnUpdateWindowBurst_OpensTheCircuit_EvenRightAfterABusyScene()
    {
        // Scene 6b follows scene 6a, so the adapter has just made several successful calls. During
        // the window only one call per burst account fails — its opportunity waits behind it — so the
        // sampling window must be short enough that the earlier successes don't dilute the outage.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var options = new CircuitBreakerOptions();
        BcResilience.ConfigureCircuitBreaker(options, new BcResilienceOptions());
        var breaker = new EndpointCircuitBreaker("BusinessCentralEndpoint", options, clock);

        for (var i = 0; i < 7; i++)
            breaker.RecordSuccess();
        clock.Advance(TimeSpan.FromSeconds(15));
        // What the circuit-breaker recorder sees: the handler's exception, wrapped by the pipeline.
        for (var i = 0; i < 6; i++)
            breaker.RecordFailure(new EventContextHandlerException(new BcUnavailableException("PUT … → 503 Service Unavailable.")));

        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    /// <summary>What the retry lookup sees: the handler's exception, wrapped by the pipeline.</summary>
    private static Exception Wrapped(Exception inner) => new EventContextHandlerException(inner);

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

    private static CrmOpportunityBody Opportunity() => new(
        "OPP-10029", "Connectors for offshore wind export cable", Guid.NewGuid(), "City Power & Light", null,
        "robin.hale@contososubsea.example", 60000m, "EUR", null, "CONNECT", "Open");

    private static ProspectUpsertBody Prospect() =>
        new(new ProspectBody("City Power & Light", null, null, "Gothenburg", null, "SE", null, null), null);
}
