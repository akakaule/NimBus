#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Spec 026 phase D: whole-payload redaction helper semantics and the Reader
/// floor on the previously-ungated cross-endpoint read APIs.
/// </summary>
[TestClass]
public class PayloadRedactionTests
{
    [TestMethod]
    public void Redact_event_replaces_payload_and_keeps_type_id()
    {
        var e = new Event
        {
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventJson = "{\"cpr\":\"010101-1234\"}", EventTypeId = "OrderPlaced" },
            },
        };

        PayloadRedaction.Redact(e);

        Assert.AreEqual(PayloadRedaction.Placeholder, e.MessageContent.EventContent.EventJson);
        Assert.AreEqual("OrderPlaced", e.MessageContent.EventContent.EventTypeId);
    }

    [TestMethod]
    public void Redact_is_null_safe_and_leaves_empty_payloads_untouched()
    {
        Assert.IsNull(PayloadRedaction.Redact((Event?)null));
        Assert.IsNull(PayloadRedaction.Redact((Message?)null));
        Assert.IsNull(PayloadRedaction.Redact((EventDetails?)null));
        Assert.IsNull(PayloadRedaction.Redact((EndpointStatus?)null));

        var noPayload = new Event { MessageContent = new MessageContent { EventContent = new EventContent() } };
        PayloadRedaction.Redact(noPayload);
        Assert.IsNull(noPayload.MessageContent.EventContent.EventJson);
    }

    [TestMethod]
    public void Redact_message_and_details_and_status_and_log_and_subscription()
    {
        var message = new Message { EventContent = "{\"secret\":1}" };
        PayloadRedaction.Redact(message);
        Assert.AreEqual(PayloadRedaction.Placeholder, message.EventContent);

        var details = new EventDetails
        {
            FailedMessage = new Message { EventContent = "{\"a\":1}" },
            OriginatingMessage = new Message { EventContent = "{\"b\":2}" },
        };
        PayloadRedaction.Redact(details);
        Assert.AreEqual(PayloadRedaction.Placeholder, details.FailedMessage.EventContent);
        Assert.AreEqual(PayloadRedaction.Placeholder, details.OriginatingMessage.EventContent);

        var status = new EndpointStatus
        {
            EnrichedUnresolvedEvents = new UnresolvedEvents
            {
                new Event
                {
                    MessageContent = new MessageContent
                    {
                        EventContent = new EventContent { EventJson = "{\"x\":1}" },
                    },
                },
            },
        };
        PayloadRedaction.Redact(status);
        Assert.AreEqual(
            PayloadRedaction.Placeholder,
            status.EnrichedUnresolvedEvents.Single().MessageContent.EventContent.EventJson);

        var log = new EventLogEntry { Payload = "{\"y\":2}" };
        PayloadRedaction.Redact(log);
        Assert.AreEqual(PayloadRedaction.Placeholder, log.Payload);

        var sub = new ManagementApi.EndpointSubscription { Payload = "fragment" };
        PayloadRedaction.RedactSubscription(sub);
        Assert.IsNull(sub.Payload);
    }
}

/// <summary>
/// The Reader floor: cross-endpoint reads (metrics, event types, message
/// search) require at least a site Reader; role-less authenticated users get
/// 403, Readers get through.
/// </summary>
[TestClass]
public class ReaderFloorTests
{
    private sealed class StubAuthz : IEndpointAuthorizationService
    {
        public AccessRole SiteRole { get; init; } = AccessRole.None;
        public bool PiiReader { get; init; }

        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null)
            => Task.FromResult(SiteRole >= required);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(PiiReader);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync()
            => Task.FromResult(new CurrentUserAccess { SiteRole = SiteRole, IsPiiReader = PiiReader });

        public string? GetCurrentUserName() => "test-user";
    }

    private static MetricsImplementation Metrics(StubAuthz authz) => new(
        new InMemoryMessageStore(),
        new StoreResultCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
        authz);

    private static MessageImplementation Messages(StubAuthz authz) => new(
        new InMemoryMessageStore(),
        NullLogger<MessageImplementation>.Instance,
        authz);

    [TestMethod]
    public async Task Metrics_require_site_reader()
    {
        var denied = Metrics(new StubAuthz());
        Assert.IsInstanceOfType((await denied.GetMetricsOverviewAsync(Period._1d)).Result, typeof(ForbidResult));
        Assert.IsInstanceOfType((await denied.GetMetricsLatencyAsync(Period._1d)).Result, typeof(ForbidResult));
        Assert.IsInstanceOfType((await denied.GetMetricsFailedInsightsAsync(Period._1d)).Result, typeof(ForbidResult));
        Assert.IsInstanceOfType((await denied.GetMetricsTimeseriesAsync(Period._1d)).Result, typeof(ForbidResult));

        var reader = Metrics(new StubAuthz { SiteRole = AccessRole.Reader });
        Assert.IsNotInstanceOfType((await reader.GetMetricsOverviewAsync(Period._1d)).Result, typeof(ForbidResult));
    }

    [TestMethod]
    public async Task Message_search_requires_site_reader()
    {
        var denied = Messages(new StubAuthz());
        var result = await denied.PostMessagesSearchAsync(new MessageSearchRequest());
        Assert.IsInstanceOfType(result.Result, typeof(ForbidResult));

        var reader = Messages(new StubAuthz { SiteRole = AccessRole.Reader });
        var ok = await reader.PostMessagesSearchAsync(new MessageSearchRequest());
        Assert.IsNull(ok.Result, "A site Reader's search must not be rejected.");
    }
}
