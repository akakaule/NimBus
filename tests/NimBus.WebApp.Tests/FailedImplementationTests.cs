#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using FailedEventHistogram = NimBus.MessageStore.States.FailedEventHistogram;
using FailedEventHistogramRow = NimBus.MessageStore.States.FailedEventHistogramRow;
using StoreResolutionStatus = NimBus.MessageStore.ResolutionStatus;
using MessageContent = NimBus.Core.Messages.MessageContent;
using ErrorContent = NimBus.Core.Messages.ErrorContent;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The Failed page API: authorization scoping, auditing, histogram windows and zero-fill, and
/// grouping failures by error category and pattern.
/// </summary>
[TestClass]
public sealed class FailedImplementationTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 10, 17, 30, DateTimeKind.Utc);

    [TestMethod]
    public async Task Search_covers_only_the_endpoints_the_caller_can_read()
    {
        var harness = await CreateHarness(readable: new[] { "Crm", "Erp" });

        var result = await harness.Controller.PostFailedSearchAsync(new FailedSearchRequest { Filter = new FailedSearchFilter() });

        var events = result.Value!.Events.ToList();
        CollectionAssert.AreEquivalent(new[] { "crm-1", "crm-2", "erp-1" }, events.Select(e => e.EventId).ToList());
        var audits = await AllAudits(harness.Store);
        Assert.AreEqual(1, audits.Count, "One audit row per search.");
        Assert.AreEqual(MessageAuditType.SearchEvents, audits[0].Audit.AuditType);
        Assert.IsFalse(audits[0].Audit.AccessDenied);
    }

    [TestMethod]
    public async Task Search_naming_an_unreadable_endpoint_is_forbidden_and_audited()
    {
        var harness = await CreateHarness(readable: new[] { "Crm" });

        var result = await harness.Controller.PostFailedSearchAsync(new FailedSearchRequest
        {
            Filter = new FailedSearchFilter { EndpointIds = new List<string> { "Crm", "billing" } },
        });

        Assert.IsInstanceOfType<ForbidResult>(result.Result);
        var audit = (await AllAudits(harness.Store)).Single();
        Assert.IsTrue(audit.Audit.AccessDenied);
        Assert.AreEqual("Billing", audit.EndpointId, "The denied endpoint is recorded under its canonical id.");
    }

    [TestMethod]
    public async Task Search_naming_an_unknown_endpoint_is_a_bad_request()
    {
        var harness = await CreateHarness(readable: new[] { "Crm" });

        var result = await harness.Controller.PostFailedSearchAsync(new FailedSearchRequest
        {
            Filter = new FailedSearchFilter { EndpointIds = new List<string> { "Nope" } },
        });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task Search_filters_by_status_and_attaches_report_flags()
    {
        var harness = await CreateHarness(readable: new[] { "Crm", "Erp", "Billing" });
        await harness.Store.SetEventReport("Erp", "erp-1", true, "ops", "INC-1");

        var result = await harness.Controller.PostFailedSearchAsync(new FailedSearchRequest
        {
            Filter = new FailedSearchFilter { Statuses = new List<Statuses> { Statuses.DeadLettered } },
        });

        var events = result.Value!.Events.ToList();
        Assert.AreEqual("erp-1", events.Single().EventId);
        Assert.IsTrue(events.Single().IsReported);
        Assert.AreEqual("INC-1", events.Single().TicketId);
    }

    [TestMethod]
    public async Task Search_pages_with_a_continuation_token()
    {
        var harness = await CreateHarness(readable: new[] { "Crm", "Erp", "Billing" });

        var first = (await harness.Controller.PostFailedSearchAsync(new FailedSearchRequest { MaxSearchItemsCount = 3 })).Value!;
        var second = (await harness.Controller.PostFailedSearchAsync(new FailedSearchRequest { MaxSearchItemsCount = 3, ContinuationToken = first.ContinuationToken })).Value!;

        Assert.AreEqual(3, first.Events.Count);
        Assert.IsNotNull(first.ContinuationToken);
        Assert.AreEqual(1, second.Events.Count);
        Assert.IsNull(second.ContinuationToken);
        Assert.AreEqual(4, first.Events.Concat(second.Events).Select(e => e.EventId).Distinct().Count());
    }

    [TestMethod]
    [DataRow(Period._1h, 5, 12)]
    [DataRow(Period._12h, 30, 24)]
    [DataRow(Period._1d, 60, 24)]
    [DataRow(Period._3d, 180, 24)]
    [DataRow(Period._7d, 360, 28)]
    [DataRow(Period._30d, 1440, 30)]
    public void ResolveWindow_presets_pick_the_bucket_and_end_after_now(Period period, int bucketMinutes, int bars)
    {
        var (from, to, bucket) = FailedImplementation.ResolveWindow(period, null, null, Now)!.Value;

        Assert.AreEqual(TimeSpan.FromMinutes(bucketMinutes), bucket);
        Assert.AreEqual(bars, (int)((to - from).Ticks / bucket.Ticks));
        Assert.IsTrue(to > Now && to - bucket <= Now, "The last bucket holds now.");
        Assert.AreEqual(0, to.Ticks % bucket.Ticks, "Buckets are aligned to whole multiples of their size.");
    }

    [TestMethod]
    [DataRow(2, 5)]
    [DataRow(24 * 7, 180)]
    [DataRow(24 * 60, 1440)]
    public void ResolveWindow_custom_ranges_pick_the_smallest_bucket_within_60_bars(int hours, int bucketMinutes)
    {
        var from = new DateTime(2026, 9, 1, 8, 3, 0, DateTimeKind.Utc);

        var (alignedFrom, to, bucket) = FailedImplementation.ResolveWindow(Period._1d, from, from.AddHours(hours), Now)!.Value;

        Assert.AreEqual(TimeSpan.FromMinutes(bucketMinutes), bucket);
        Assert.IsTrue(alignedFrom <= from && to >= from.AddHours(hours), "The aligned window covers the requested one.");
        Assert.IsTrue((to - alignedFrom).Ticks / bucket.Ticks <= FailedImplementation.MaxBuckets + 1);
    }

    [TestMethod]
    public void ResolveWindow_rejects_an_inverted_or_oversized_custom_range()
    {
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.IsNull(FailedImplementation.ResolveWindow(Period._1d, from, from, Now));
        Assert.IsNull(FailedImplementation.ResolveWindow(Period._1d, from, from.AddDays(91), Now));
    }

    [TestMethod]
    public async Task Histogram_with_an_invalid_custom_window_is_a_bad_request()
    {
        var harness = await CreateHarness(readable: new[] { "Crm" });

        var result = await harness.Controller.PostFailedHistogramAsync(new FailedHistogramRequest
        {
            From = Now,
            To = Now.AddHours(-1),
        });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public void BuildHistogram_zero_fills_every_bucket_and_totals_match_the_buckets()
    {
        var from = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
        var rows = new FailedEventHistogram
        {
            Rows = new[]
            {
                new FailedEventHistogramRow { BucketStartUtc = from, EndpointId = "Crm", Status = "Failed", Count = 2 },
                new FailedEventHistogramRow { BucketStartUtc = from.AddHours(2), EndpointId = "Crm", Status = "DeadLettered", Count = 1 },
                new FailedEventHistogramRow { BucketStartUtc = from.AddHours(2), EndpointId = "Erp", Status = "Unsupported", Count = 4 },
            },
        };

        var histogram = FailedImplementation.BuildHistogram(rows, from, from.AddHours(4), TimeSpan.FromHours(1));

        Assert.AreEqual(60, histogram.BucketMinutes);
        CollectionAssert.AreEqual(
            new[] { from, from.AddHours(1), from.AddHours(2), from.AddHours(3) },
            histogram.Buckets.Select(b => b.Start).ToList());
        Assert.AreEqual(0, histogram.Buckets[1].Failed + histogram.Buckets[1].DeadLettered + histogram.Buckets[1].Unsupported);
        Assert.AreEqual(1, histogram.Buckets[2].DeadLettered);
        Assert.AreEqual(4, histogram.Buckets[2].ByEndpoint["Erp"]);
        Assert.AreEqual(histogram.Buckets.Sum(b => b.Failed), histogram.Totals.Failed);
        Assert.AreEqual(histogram.Buckets.Sum(b => b.DeadLettered), histogram.Totals.DeadLettered);
        Assert.AreEqual(histogram.Buckets.Sum(b => b.Unsupported), histogram.Totals.Unsupported);
        Assert.AreEqual("Erp", histogram.Totals.ByEndpoint[0].EndpointId, "Endpoints are ordered by total, largest first.");
    }

    [TestMethod]
    public async Task Histogram_counts_the_readable_backlog()
    {
        var harness = await CreateHarness(readable: new[] { "Crm", "Erp" });

        var histogram = (await harness.Controller.PostFailedHistogramAsync(new FailedHistogramRequest { Period = Period._1d })).Value!;

        Assert.AreEqual(2, histogram.Totals.Failed);
        Assert.AreEqual(1, histogram.Totals.DeadLettered);
        Assert.AreEqual(0, histogram.Totals.Unsupported, "Billing is not readable.");
    }

    [TestMethod]
    public void BuildErrorGroups_groups_by_category_then_pattern_like_insights()
    {
        var failures = new[]
        {
            Failure("Crm", "a", StoreResolutionStatus.Failed, "HttpRequestException: 503 from https://erp/api/orders/12345", minutesAgo: 1),
            Failure("Crm", "b", StoreResolutionStatus.Failed, "HttpRequestException: 503 from https://erp/api/orders/67890", minutesAgo: 2),
            Failure("Erp", "c", StoreResolutionStatus.Failed, "HttpRequestException: timeout", minutesAgo: 3),
            Failure("Erp", "d", StoreResolutionStatus.Unsupported, null, minutesAgo: 4),
        };

        var result = FailedImplementation.BuildErrorGroups(failures, truncated: false);

        Assert.AreEqual(4, result.Total);
        var http = result.Groups[0];
        Assert.AreEqual("HttpRequestException", http.ErrorCategory);
        Assert.AreEqual(3, http.Count);
        CollectionAssert.AreEquivalent(new[] { "Crm", "Erp" }, http.Endpoints.ToList());
        Assert.AreEqual(2, http.SubGroups.Count, "Ids differing only by a number share one pattern.");
        Assert.AreEqual(2, http.SubGroups[0].Count);
        CollectionAssert.AreEqual(new[] { "a", "b" }, http.SubGroups[0].Events.Select(e => e.EventId).ToList(), "Newest first.");
        Assert.AreEqual("HttpRequestException: 503 from https://erp/api/orders/12345", http.ExampleErrorText, "The newest failure is the example.");

        var unsupported = result.Groups[1];
        Assert.AreEqual("Unsupported", unsupported.ErrorCategory, "An Unsupported event without an error groups as Unsupported.");
        Assert.AreEqual("Unsupported", unsupported.SubGroups.Single().Events.Single().ResolutionStatus);
    }

    [TestMethod]
    public void ErrorTextOf_falls_back_to_the_dead_letter_description()
    {
        var failure = Failure("Crm", "a", StoreResolutionStatus.DeadLettered, null, minutesAgo: 1);
        failure.DeadLetterErrorDescription = "MaxDeliveryCount exceeded";

        Assert.AreEqual("MaxDeliveryCount exceeded", FailedImplementation.ErrorTextOf(failure));
    }

    [TestMethod]
    public async Task ErrorGroups_cover_the_readable_backlog_and_are_audited()
    {
        var harness = await CreateHarness(readable: new[] { "Crm", "Erp" });

        var result = (await harness.Controller.PostFailedErrorGroupsAsync(new FailedErrorGroupsRequest { Filter = new FailedSearchFilter() })).Value!;

        Assert.AreEqual(3, result.Total);
        Assert.IsFalse(result.Truncated);
        Assert.AreEqual(3, result.Groups.Sum(g => g.Count));
        Assert.AreEqual(1, (await AllAudits(harness.Store)).Count);
    }

    private static NimBus.MessageStore.UnresolvedEvent Failure(string endpointId, string eventId, StoreResolutionStatus status, string? errorText, int minutesAgo) => new()
    {
        EndpointId = endpointId,
        EventId = eventId,
        SessionId = "s-" + eventId,
        EventTypeId = "OrderPlaced",
        ResolutionStatus = status,
        UpdatedAt = Now.AddMinutes(-minutesAgo),
        MessageContent = new MessageContent
        {
            ErrorContent = errorText == null ? null : new ErrorContent { ErrorText = errorText },
        },
    };

    private static async Task<List<AuditSearchItem>> AllAudits(InMemoryMessageStore store) =>
        (await store.SearchAudits(new AuditFilter(), continuationToken: null, maxItemCount: 50)).Audits.ToList();

    private static async Task<Harness> CreateHarness(string[] readable)
    {
        var store = new InMemoryMessageStore();
        await Upload(store, "Crm", "crm-1", StoreResolutionStatus.Failed, "HttpRequestException: 503");
        await Upload(store, "Crm", "crm-2", StoreResolutionStatus.Failed, "HttpRequestException: 503");
        await Upload(store, "Crm", "crm-3", StoreResolutionStatus.Completed, null);
        await Upload(store, "Erp", "erp-1", StoreResolutionStatus.DeadLettered, "MaxDeliveryCount exceeded");
        await Upload(store, "Billing", "billing-1", StoreResolutionStatus.Unsupported, null);

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("name", "operator") }, authenticationType: "Test")),
        };
        var controller = new FailedImplementation(
            new FakePlatform("Crm", "Erp", "Billing"),
            store,
            new ReadableEndpointsAuthorization(readable),
            new AuditLogService(NullLogger<AuditLogService>.Instance, store),
            new StubHttpContextAccessor { HttpContext = context },
            PayloadRedactionTests.NewRedaction(),
            NullLogger<FailedImplementation>.Instance);
        return new Harness(controller, store);
    }

    private static Task Upload(InMemoryMessageStore store, string endpointId, string eventId, StoreResolutionStatus status, string? errorText)
    {
        var content = new NimBus.MessageStore.UnresolvedEvent
        {
            EndpointId = endpointId,
            EventId = eventId,
            SessionId = "s-" + eventId,
            EventTypeId = "OrderPlaced",
            LastMessageId = "m-" + eventId,
            MessageContent = new MessageContent
            {
                ErrorContent = errorText == null ? null : new ErrorContent { ErrorText = errorText },
            },
        };
        var sessionId = content.SessionId;
        return status switch
        {
            StoreResolutionStatus.Failed => store.UploadFailedMessage(eventId, sessionId, endpointId, content),
            StoreResolutionStatus.DeadLettered => store.UploadDeadletteredMessage(eventId, sessionId, endpointId, content),
            StoreResolutionStatus.Unsupported => store.UploadUnsupportedMessage(eventId, sessionId, endpointId, content),
            _ => store.UploadCompletedMessage(eventId, sessionId, endpointId, content),
        };
    }

    private sealed record Harness(FailedImplementation Controller, InMemoryMessageStore Store);

    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class ReadableEndpointsAuthorization : IEndpointAuthorizationService
    {
        private readonly HashSet<string> _readable;

        public ReadableEndpointsAuthorization(IEnumerable<string> readable) =>
            _readable = new HashSet<string>(readable, StringComparer.Ordinal);

        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) =>
            Task.FromResult(required == AccessRole.Reader && endpointId != null && _readable.Contains(endpointId));

        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());

        public string? GetCurrentUserName() => "operator";
    }

    private sealed class FakePlatform : IPlatform
    {
        private readonly IEndpoint[] _endpoints;

        public FakePlatform(params string[] endpointIds) =>
            _endpoints = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToArray();

        public IEnumerable<IEndpoint> Endpoints => _endpoints;
        public IEnumerable<IEventType> EventTypes => Array.Empty<IEventType>();
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Array.Empty<IEndpoint>();
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Array.Empty<IEndpoint>();
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        public FakeEndpoint(string id) => Id = id;

        public string Id { get; }
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesConsumed => Array.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesProduced => Array.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();
    }
}
