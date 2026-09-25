#pragma warning disable CA1707, CA2007
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Single-row store lookups return null for a missing row on every provider, so the
/// event endpoints must answer 404 for an unknown event instead of mapping null (500)
/// or reporting a missing endpoint container.
/// </summary>
[TestClass]
public sealed class EventImplementationMissingEventTests
{
    [TestMethod]
    public async Task Unknown_unsupported_event_is_not_found()
    {
        var sut = Create();

        var result = await sut.GetEventUnsupportedEndpointIdEventIdAsync("Crm", "missing", "session");

        var notFound = result.Result as NotFoundObjectResult;
        Assert.IsNotNull(notFound);
        Assert.AreEqual("Event not found", notFound.Value);
    }

    [TestMethod]
    public async Task Unknown_deadlettered_event_is_not_found()
    {
        var sut = Create();

        var result = await sut.GetEventDeadletterEndpointIdEventIdAsync("Crm", "missing", "session");

        var notFound = result.Result as NotFoundObjectResult;
        Assert.IsNotNull(notFound);
        Assert.AreEqual("Event not found", notFound.Value);
    }

    [TestMethod]
    public async Task Unknown_event_is_not_found()
    {
        var sut = Create();

        var result = await sut.GetEventIdAsync("missing", "Crm");

        var notFound = result.Result as NotFoundObjectResult;
        Assert.IsNotNull(notFound);
        Assert.AreEqual("Event not found", notFound.Value);
    }

    [TestMethod]
    public async Task Resubmit_of_unknown_message_is_not_found()
    {
        var sut = Create();

        var result = await sut.PostResubmitEventIdsAsync("missing", "missing-message");

        AssertMessageNotFound(result);
    }

    [TestMethod]
    public async Task Skip_of_unknown_message_is_not_found()
    {
        var sut = Create();

        var result = await sut.PostSkipEventIdsAsync("missing", "missing-message");

        AssertMessageNotFound(result);
    }

    [TestMethod]
    public async Task Resubmit_with_changes_of_unknown_message_is_not_found()
    {
        var sut = Create();

        var result = await sut.PostResubmitWithChangesEventIdsAsync(
            new ResubmitWithChanges { EventTypeId = "CrmAccountCreated", EventContent = "{}" }, "missing", "missing-message");

        AssertMessageNotFound(result);
    }

    [TestMethod]
    public async Task Skip_proceeds_when_the_originating_request_is_missing()
    {
        // An ErrorResponse without its own event type whose originating request is
        // gone: skip routes on To and does not need the event type, so it must not NRE.
        var store = new InMemoryMessageStore();
        await store.StoreMessage(new MessageEntity
        {
            EventId = "evt-1",
            MessageId = "term-1",
            SessionId = "sess-1",
            MessageType = NimBus.Core.Messages.MessageType.ErrorResponse,
            From = "Crm",
            To = "Resolver",
            OriginatingMessageId = "req-gone",
        });
        var manager = new SkipCapturingManagerClient();

        var result = await Create(store, manager).PostSkipEventIdsAsync("evt-1", "term-1");

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.AreEqual("Crm", manager.Endpoint);
        Assert.IsNull(manager.EventTypeId);
    }

    private static void AssertMessageNotFound(IActionResult result)
    {
        var notFound = result as NotFoundObjectResult;
        Assert.IsNotNull(notFound, $"Expected 404, got {result?.GetType().Name}");
        Assert.AreEqual("Message not found", notFound.Value);
    }

    private static EventImplementation Create(InMemoryMessageStore? store = null, IManagerClient? manager = null) =>
        new(null!, new Catalog(), manager!, null!, NullLogger<EventImplementation>.Instance,
            store ?? new InMemoryMessageStore(), new Authorization(), null!, new DeferredMessageInspectorTests.FakeClient(), new Audit(), null!,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, PayloadRedactionTests.NewRedaction(),
            NimBus.Core.Messages.PII.NullEventJsonMasker.Instance);

    private sealed class Catalog : Platform { public Catalog() { AddEndpoint(new Crm()); } }
    private sealed class Crm : NimBus.Core.Endpoints.Endpoint { }
    private sealed class Authorization : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(true);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());
        public string GetCurrentUserName() => "operator";
    }
    private sealed class SkipCapturingManagerClient : IManagerClient
    {
        public string? Endpoint { get; private set; }
        public string? EventTypeId { get; private set; }

        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson) =>
            throw new NotSupportedException();

        public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId)
        {
            Endpoint = endpoint;
            EventTypeId = eventTypeId;
            return Task.CompletedTask;
        }
    }
    private sealed class Audit : IAuditLogService
    {
        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
