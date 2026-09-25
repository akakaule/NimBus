#pragma warning disable CA1707, CA2007
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
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

    private static void AssertMessageNotFound(IActionResult result)
    {
        var notFound = result as NotFoundObjectResult;
        Assert.IsNotNull(notFound, $"Expected 404, got {result?.GetType().Name}");
        Assert.AreEqual("Message not found", notFound.Value);
    }

    private static EventImplementation Create() =>
        new(null!, new Catalog(), null!, null!, NullLogger<EventImplementation>.Instance,
            new InMemoryMessageStore(), new Authorization(), null!, new DeferredMessageInspectorTests.FakeClient(), new Audit(), null!,
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
    private sealed class Audit : IAuditLogService
    {
        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
