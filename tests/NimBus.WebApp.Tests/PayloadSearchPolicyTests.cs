#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;
using NimBus.WebApp.Services;
using Endpoint = NimBus.Core.Endpoints.Endpoint;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using Api = NimBus.WebApp.ManagementApi;
using System.Reflection;
using NimBus.MessageStore.Abstractions;
using EventFilter = NimBus.MessageStore.EventFilter;

namespace NimBus.WebApp.Tests;

[TestClass]
public class PayloadSearchPolicyTests
{
    [TestMethod]
    [DataRow(false, false, true, false, 200)]
    [DataRow(true, false, true, false, 403)]
    [DataRow(true, true, true, false, 200)]
    [DataRow(false, false, false, false, 403)]
    [DataRow(false, false, true, true, 403)]
    public async Task Api_enforces_payload_search_policy(bool sensitive, bool pii, bool reader, bool unknownFilter, int expectedStatus)
    {
        Endpoint endpoint = sensitive ? new Receiver<PrivateEvent>() : new Receiver<PublicEvent>();
        var platform = new SearchPlatform(endpoint);
        var store = new InMemoryMessageStore();
        var trackingStore = DispatchProxy.Create<IMessageTrackingStore, SearchStore>();
        var recordingStore = (SearchStore)trackingStore;
        recordingStore.Inner = store;
        var sut = new EventImplementation(null!, platform, null!, null!,
            NullLogger<EventImplementation>.Instance, trackingStore, new SearchAuthorization(pii, reader), null!, null!,
            new AuditLogService(NullLogger<AuditLogService>.Instance, store), null!,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new PayloadRedaction(new EventJsonMasker(platform)), new EventJsonMasker(platform));
        var result = await sut.PostApiEventEndpointIdGetByFilterAsync(new Api.SearchRequest
        {
            EventFilter = new Api.EventFilter { Payload = "contact", EventTypeId = unknownFilter ? ["OldUnknownType"] : [] },
            MaxSearchItemsCount = 100,
        }, endpoint.Id);

        if (expectedStatus == 403)
        {
            Assert.IsTrue(result.Result is ForbidResult || result.Result is ObjectResult { StatusCode: 403 });
            Assert.IsNull(recordingStore.Filter);
        }
        else
        {
            Assert.IsNotNull(result.Value);
            if (!pii)
                CollectionAssert.AreEqual(new[] { nameof(PublicEvent) }, recordingStore.Filter!.EventTypeId);
        }
    }

    public class SearchStore : DispatchProxy
    {
        public InMemoryMessageStore Inner { get; set; } = null!;
        public EventFilter? Filter { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IMessageTrackingStore.GetEventsByFilter))
                Filter = (EventFilter)args![0]!;
            return targetMethod.Invoke(Inner, args);
        }
    }

    private sealed class SearchPlatform : NimBus.Core.Platform
    {
        public SearchPlatform(Endpoint endpoint) { AddEndpoint(endpoint); }
    }

    private sealed class SearchAuthorization(bool pii, bool reader) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(reader);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(pii);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());
        public string? GetCurrentUserName() => "reader";
    }

    [TestMethod]
    public void Non_sensitive_receiver_is_allowed()
    {
        CollectionAssert.AreEqual(new[] { nameof(PublicEvent) },
            PayloadSearchPolicy.GetNonSensitiveReceivedTypes(new Receiver<PublicEvent>()));
    }

    [TestMethod]
    public void Sensitive_nested_and_unknown_shapes_require_pii()
    {
        Assert.IsNull(PayloadSearchPolicy.GetNonSensitiveReceivedTypes(new Receiver<PrivateEvent>()));
        Assert.IsNull(PayloadSearchPolicy.GetNonSensitiveReceivedTypes(new Receiver<NestedEvent>()));
        Assert.IsNull(PayloadSearchPolicy.GetNonSensitiveReceivedTypes(new Receiver<DynamicEvent>()));
    }

    [TestMethod]
    public void Mixed_and_empty_receivers_require_pii()
    {
        Assert.IsNull(PayloadSearchPolicy.GetNonSensitiveReceivedTypes(new MixedReceiver()));
        Assert.IsNull(PayloadSearchPolicy.GetNonSensitiveReceivedTypes(new EmptyReceiver()));
    }

    public class PublicEvent : Event { public string ContactId { get; set; } = ""; }
    public class PrivateEvent : Event { [Sensitive] public string Email { get; set; } = ""; }
    public class NestedEvent : Event { public PrivateEvent[] Contacts { get; set; } = []; }
    public class DynamicEvent : Event { public object? Data { get; set; } }
    private sealed class Receiver<T> : Endpoint where T : IEvent, new()
    {
        public Receiver() { Consumes<T>(); }
    }
    private sealed class MixedReceiver : Endpoint
    {
        public MixedReceiver() { Consumes<PublicEvent>(); Consumes<PrivateEvent>(); }
    }
    private sealed class EmptyReceiver : Endpoint { }
}
