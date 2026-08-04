#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Spec 025 (revision-6 finding 3): a marked timeout whose shared message
/// history is absent (or expired) resubmits through the per-endpoint fallback
/// (<c>GetMessageWithFallback</c> → <c>MessageEntityFromUnresolvedEvent</c>)
/// and still reaches the ManagerClient with its logical identity and workflow
/// conversation ID; an unmarked fallback conversion stays byte-identical.
/// </summary>
[TestClass]
public sealed class EventImplementationFallbackResubmitTests
{
    private const string EventId = "evt-1";
    private const string EndpointId = "SubscriberEp";

    [TestMethod]
    public async Task Resubmit_FallbackSourcedMarkedTimeout_KeepsTimeoutIdentity()
    {
        var due = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var store = new NullOnMissingMessageStore();
        await store.UploadFailedMessage(EventId, "sess-1", EndpointId, UnresolvedTimeoutEvent(
            scheduledMessageId: "order-42:payment-timeout:1",
            scheduledEnqueueTimeUtc: due,
            workflowCorrelationId: "workflow-conversation"));

        var manager = new CapturingManagerClient();
        var sut = CreateSut(store, manager);

        var result = await sut.PostResubmitEventIdsAsync(EventId, "attempt-1");

        Assert.IsInstanceOfType(result, typeof(OkResult));
        Assert.IsNotNull(manager.ErrorResponse, "The fallback-sourced entity must reach the ManagerClient");
        Assert.AreEqual("order-42:payment-timeout:1", manager.ErrorResponse!.ScheduledMessageId,
            "A fallback-sourced entity must keep the logical timeout identity");
        Assert.AreEqual(due, manager.ErrorResponse.ScheduledEnqueueTimeUtc);
        Assert.AreEqual("workflow-conversation", manager.ErrorResponse.WorkflowCorrelationId);
        Assert.AreEqual(EndpointId, manager.Endpoint);
    }

    [TestMethod]
    public async Task Resubmit_FallbackSourcedUnmarkedEvent_ConversionStaysByteIdentical()
    {
        var store = new NullOnMissingMessageStore();
        await store.UploadFailedMessage(EventId, "sess-1", EndpointId, UnresolvedTimeoutEvent(
            scheduledMessageId: null,
            scheduledEnqueueTimeUtc: null,
            workflowCorrelationId: null));

        var manager = new CapturingManagerClient();
        var sut = CreateSut(store, manager);

        var result = await sut.PostResubmitEventIdsAsync(EventId, "attempt-1");

        Assert.IsInstanceOfType(result, typeof(OkResult));
        Assert.IsNull(manager.ErrorResponse!.ScheduledMessageId);
        Assert.IsNull(manager.ErrorResponse.ScheduledEnqueueTimeUtc);
        Assert.IsNull(manager.ErrorResponse.WorkflowCorrelationId);
    }

    private static UnresolvedEvent UnresolvedTimeoutEvent(
        string scheduledMessageId,
        DateTimeOffset? scheduledEnqueueTimeUtc,
        string workflowCorrelationId) => new()
    {
        EventId = EventId,
        SessionId = "sess-1",
        EndpointId = EndpointId,
        CorrelationId = "attempt-1",
        ResolutionStatus = ResolutionStatus.Failed,
        MessageType = MessageType.ErrorResponse,
        LastMessageId = "attempt-1",
        OriginatingMessageId = "req-1",
        ParentMessageId = "req-1",
        From = EndpointId,
        To = "Resolver",
        EventTypeId = "PaymentTimedOut",
        EnqueuedTimeUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
        UpdatedAt = DateTime.UtcNow,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "PaymentTimedOut", EventJson = "{}" },
        },
        ScheduledMessageId = scheduledMessageId,
        ScheduledEnqueueTimeUtc = scheduledEnqueueTimeUtc,
        WorkflowCorrelationId = workflowCorrelationId,
    };

    private static EventImplementation CreateSut(InMemoryMessageStore store, IManagerClient managerClient) =>
        new(
            applicationInsightsService: null!,
            new FakePlatform(new[] { EndpointId }),
            managerClient,
            handoffClientFactory: null!,
            NullLogger<EventImplementation>.Instance,
            store,
            new AllowAllAuthorizationService(),
            adminService: null!,
            serviceBusClient: null!,
            new NoOpAuditLogService(),
            handoffSettlement: null!,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

    /// <summary>
    /// The Cosmos provider returns null for a missing message; the in-memory
    /// conformance store throws. Re-implement the interface so the fallback
    /// path (which triggers on null) is reachable in this test.
    /// </summary>
    private sealed class NullOnMissingMessageStore : InMemoryMessageStore, INimBusMessageStore
    {
        public new async Task<MessageEntity> GetMessage(string eventId, string messageId)
        {
            try
            {
                return await base.GetMessage(eventId, messageId);
            }
            catch (MessageNotFoundException)
            {
                return null!;
            }
        }
    }

    private sealed class CapturingManagerClient : IManagerClient
    {
        public MessageEntity ErrorResponse { get; private set; }
        public string Endpoint { get; private set; }

        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson)
        {
            ErrorResponse = errorResponse;
            Endpoint = endpoint;
            return Task.CompletedTask;
        }

        public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId) => throw new NotSupportedException();
    }

    private sealed class FakePlatform : IPlatform
    {
        private readonly List<IEndpoint> _endpoints;

        public FakePlatform(IEnumerable<string> endpointIds)
        {
            _endpoints = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToList();
        }

        public IEnumerable<IEndpoint> Endpoints => _endpoints;
        public IEnumerable<IEventType> EventTypes => Enumerable.Empty<IEventType>();
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        public FakeEndpoint(string id)
        {
            Id = id;
        }

        public string Id { get; }
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => Enumerable.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesConsumed => Enumerable.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Enumerable.Empty<IRoleAssignment>();
    }

    private sealed class NoOpAuditLogService : IAuditLogService
    {
        public Task LogAuditAsync(
            MessageAuditType type,
            HttpContext context,
            bool accessDenied = false,
            string? data = null,
            string? eventId = null,
            string? endpointId = null,
            string? eventTypeId = null,
            string? auditorNameOverride = null,
            System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AllowAllAuthorizationService : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(true);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess
        {
            SiteRole = AccessRole.Owner,
            IsPiiReader = true,
        });

        public string? GetCurrentUserName() => "test-user";
    }
}
