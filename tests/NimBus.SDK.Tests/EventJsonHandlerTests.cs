#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.EventHandlers;

namespace NimBus.SDK.Tests;

[TestClass]
public class EventJsonHandlerTests
{
    [TestMethod]
    public async Task Handle_UsesAuthoritativeContextEventTypeInHandlerMetadata()
    {
        var handler = new RecordingHandler();
        var sut = new EventJsonHandler<TestEvent>(handler);

        await sut.Handle(MessageContextStub.ForEventTypes("header.event.v1", string.Empty, "{}"));

        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual("header.event.v1", handler.LastContext?.EventType);
    }

    [TestMethod]
    public async Task Handle_LiteralNull_RejectsBeforeInvokingHandler()
    {
        var handler = new RecordingHandler();
        var sut = new EventJsonHandler<TestEvent>(handler);

        var exception = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() =>
            sut.Handle(MessageContextStub.ForEventType(nameof(TestEvent), "null")));

        Assert.IsInstanceOfType<JsonSerializationException>(exception.InnerException);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task Handle_MissingPayload_RejectsBeforeInvokingHandler()
    {
        var handler = new RecordingHandler();
        var sut = new EventJsonHandler<TestEvent>(handler);

        var exception = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() =>
            sut.Handle(MessageContextStub.ForEventType(nameof(TestEvent), null!)));

        Assert.IsInstanceOfType<JsonSerializationException>(exception.InnerException);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task Handle_MalformedJson_RejectsBeforeInvokingHandler()
    {
        var handler = new RecordingHandler();
        var sut = new EventJsonHandler<TestEvent>(handler);

        var exception = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() =>
            sut.Handle(MessageContextStub.ForEventType(nameof(TestEvent), "{")));

        Assert.IsInstanceOfType<JsonSerializationException>(exception.InnerException);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task Handle_JsonExceedingSafeMaxDepth_RejectsBeforeInvokingHandler()
    {
        var handler = new RecordingHandler();
        var sut = new EventJsonHandler<TestEvent>(handler);
        var json = string.Concat(Enumerable.Repeat("{\"Value\":", 33))
            + "{}"
            + new string('}', 33);

        var exception = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() =>
            sut.Handle(MessageContextStub.ForEventType(nameof(TestEvent), json)));

        Assert.IsInstanceOfType<JsonReaderException>(exception.InnerException);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Handle_HostileAmbientTypeNameHandling_DoesNotInstantiatePayloadType()
    {
        var previousSettings = JsonConvert.DefaultSettings;
        HostilePayload.ConstructionCount = 0;
        JsonConvert.DefaultSettings = () => new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto,
        };
        try
        {
            var handler = new RecordingHandler();
            var sut = new EventJsonHandler<TestEvent>(handler);
            var typeName = $"{typeof(HostilePayload).FullName}, {typeof(HostilePayload).Assembly.GetName().Name}";
            var json = $"{{\"Value\":{{\"$type\":{JsonConvert.SerializeObject(typeName)}}}}}";

            await sut.Handle(MessageContextStub.ForEventType(nameof(TestEvent), json));

            Assert.AreEqual(1, handler.Calls);
            Assert.AreEqual(0, HostilePayload.ConstructionCount);
            Assert.IsInstanceOfType<JObject>(handler.LastEvent?.Value);
        }
        finally
        {
            JsonConvert.DefaultSettings = previousSettings;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Handle_MutatedPublicSafeSettings_DoesNotEnableTypeNameHandling()
    {
        var previousTypeNameHandling = Constants.SafeJsonSettings.TypeNameHandling;
        HostilePayload.ConstructionCount = 0;
        Constants.SafeJsonSettings.TypeNameHandling = TypeNameHandling.Auto;
        try
        {
            var handler = new RecordingHandler();
            var sut = new EventJsonHandler<TestEvent>(handler);
            var typeName = $"{typeof(HostilePayload).FullName}, {typeof(HostilePayload).Assembly.GetName().Name}";
            var json = $"{{\"Value\":{{\"$type\":{JsonConvert.SerializeObject(typeName)}}}}}";

            await sut.Handle(MessageContextStub.ForEventType(nameof(TestEvent), json));

            Assert.AreEqual(1, handler.Calls);
            Assert.AreEqual(0, HostilePayload.ConstructionCount);
            Assert.IsInstanceOfType<JObject>(handler.LastEvent?.Value);
        }
        finally
        {
            Constants.SafeJsonSettings.TypeNameHandling = previousTypeNameHandling;
        }
    }

    public sealed class TestEvent : Event
    {
        public object? Value { get; set; }
    }

    public sealed class HostilePayload
    {
        public HostilePayload()
        {
            ConstructionCount++;
        }

        public static int ConstructionCount { get; set; }
    }

    private sealed class RecordingHandler : IEventHandler<TestEvent>
    {
        public int Calls { get; private set; }
        public TestEvent? LastEvent { get; private set; }
        public IEventHandlerContext? LastContext { get; private set; }

        public Task Handle(TestEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastEvent = message;
            LastContext = context;
            return Task.CompletedTask;
        }
    }
}
