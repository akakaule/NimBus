using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Tests that events are routed to the correct handler based on event type.
/// </summary>
[TestClass]
public class EventRoutingTests
{
    [TestMethod]
    public async Task Publish_DifferentEventTypes_RoutedToCorrectHandlers()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var orderPlacedHandler = new RecordingOrderPlacedHandler();
        var orderCancelledHandler = new RecordingOrderCancelledHandler();
        fixture.RegisterHandler(() => orderPlacedHandler);
        fixture.RegisterHandler(() => orderCancelledHandler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s1") { OrderId = "ORD-100" });
        await fixture.Publisher.Publish(new OrderCancelled("s2") { OrderId = "ORD-101", Reason = "Changed mind" });
        await fixture.Publisher.Publish(new OrderPlaced("s3") { OrderId = "ORD-102" });
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(2, orderPlacedHandler.ReceivedEvents.Count, "OrderPlaced handler should receive 2 events");
        Assert.AreEqual(1, orderCancelledHandler.ReceivedEvents.Count, "OrderCancelled handler should receive 1 event");

        Assert.AreEqual("ORD-100", orderPlacedHandler.ReceivedEvents[0].OrderId);
        Assert.AreEqual("ORD-102", orderPlacedHandler.ReceivedEvents[1].OrderId);
        Assert.AreEqual("ORD-101", orderCancelledHandler.ReceivedEvents[0].OrderId);
        Assert.AreEqual("Changed mind", orderCancelledHandler.ReceivedEvents[0].Reason);
    }

    [TestMethod]
    public async Task Publish_UnregisteredEventType_UnsupportedResponseSent()
    {
        // Arrange — register OrderPlaced handler but NOT UnknownEvent
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act — publish UnknownEvent (no handler registered)
        await fixture.Publisher.Publish(new UnknownEvent { Data = "test" });
        await fixture.DeliverAll();

        // Assert — StrictMessageHandler sends UnsupportedResponse for unregistered types
        Assert.AreEqual(0, handler.ReceivedEvents.Count, "OrderPlaced handler should not be invoked");
        var responses = fixture.ResponseBus.SentMessages;
        Assert.AreEqual(1, responses.Count);
        Assert.AreEqual(MessageType.UnsupportedResponse, responses[0].MessageType);
    }

    [TestMethod]
    public async Task Publish_MixedEventTypes_EachHandlerGetsCorrectContext()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var orderPlacedHandler = new RecordingOrderPlacedHandler();
        var orderCancelledHandler = new RecordingOrderCancelledHandler();
        fixture.RegisterHandler(() => orderPlacedHandler);
        fixture.RegisterHandler(() => orderCancelledHandler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s1") { OrderId = "ORD-200" });
        await fixture.Publisher.Publish(new OrderCancelled("s2") { OrderId = "ORD-201", Reason = "Defective" });
        await fixture.DeliverAll();

        // Assert — each handler's context reflects the correct event type
        Assert.AreEqual("OrderPlaced", orderPlacedHandler.ReceivedContexts[0].EventType);
        Assert.AreEqual("OrderCancelled", orderCancelledHandler.ReceivedContexts[0].EventType);
    }

    [TestMethod]
    public async Task Publish_SameEventTypeTwice_HandlerInvokedTwice()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act — publish two distinct OrderPlaced events
        await fixture.Publisher.Publish(new OrderPlaced("s1") { OrderId = "ORD-300", Amount = 10m });
        await fixture.Publisher.Publish(new OrderPlaced("s2") { OrderId = "ORD-301", Amount = 20m });
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(2, handler.ReceivedEvents.Count);
        Assert.AreEqual(10m, handler.ReceivedEvents[0].Amount);
        Assert.AreEqual(20m, handler.ReceivedEvents[1].Amount);
    }
}
