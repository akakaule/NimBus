using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.EndToEnd.Tests.Infrastructure;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Tests for batch publishing, deterministic message IDs, and event validation.
/// </summary>
[TestClass]
public class BatchAndValidationTests
{
    [TestMethod]
    public async Task PublishBatch_AllEventsHandled()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var events = Enumerable.Range(1, 10)
            .Select(i => new OrderPlaced($"session-{i}") { OrderId = $"BATCH-{i:D3}", Amount = i * 10m })
            .ToList();

        // Act
        await fixture.Publisher.PublishBatch(events.Cast<NimBus.Core.Events.IEvent>(), "batch-correlation");
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(10, handler.ReceivedEvents.Count, "All 10 events should be handled");
        for (int i = 0; i < 10; i++)
        {
            Assert.AreEqual($"BATCH-{i + 1:D3}", handler.ReceivedEvents[i].OrderId);
        }
    }

    [TestMethod]
    public async Task PublishBatch_SharedCorrelationId()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var events = new[]
        {
            new OrderPlaced("s1") { OrderId = "B-001" },
            new OrderPlaced("s2") { OrderId = "B-002" },
        };

        // Act
        await fixture.Publisher.PublishBatch(events, "shared-corr");
        await fixture.DeliverAll();

        // Assert — all events in batch share the same correlation ID
        Assert.AreEqual(2, handler.ReceivedContexts.Count);
        Assert.AreEqual("shared-corr", handler.ReceivedContexts[0].CorrelationId);
        Assert.AreEqual("shared-corr", handler.ReceivedContexts[1].CorrelationId);
    }

    [TestMethod]
    public async Task Publish_DeterministicMessageId_SamePayloadProducesSameId()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var event1 = new OrderPlaced("session-1") { OrderId = "DET-001", Amount = 42m };
        var event2 = new OrderPlaced("session-1") { OrderId = "DET-001", Amount = 42m };

        // Act — publish same event twice with auto-generated IDs
        await fixture.Publisher.Publish(event1, "session-1", "corr-1");
        await fixture.Publisher.Publish(event2, "session-1", "corr-2");

        // Assert — MessageIds are deterministic based on serialized content
        var messages = fixture.PublishBus.SentMessages;
        Assert.AreEqual(2, messages.Count);
        Assert.AreEqual(messages[0].MessageId, messages[1].MessageId,
            "Same event payload should produce same deterministic MessageId");
    }

    [TestMethod]
    public async Task Publish_DifferentPayloads_ProduceDifferentMessageIds()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var event1 = new OrderPlaced("session-1") { OrderId = "DET-001", Amount = 10m };
        var event2 = new OrderPlaced("session-1") { OrderId = "DET-002", Amount = 20m };

        // Act
        await fixture.Publisher.Publish(event1, "session-1", "corr-1");
        await fixture.Publisher.Publish(event2, "session-1", "corr-2");

        // Assert
        var messages = fixture.PublishBus.SentMessages;
        Assert.AreEqual(2, messages.Count);
        Assert.AreNotEqual(messages[0].MessageId, messages[1].MessageId,
            "Different payloads should produce different MessageIds");
    }

    [TestMethod]
    public async Task Publish_InvalidEvent_ThrowsValidationException()
    {
        // Arrange
        var fixture = new EndToEndFixture();

        var invalidEvent = new InvalidEvent(); // RequiredField is null

        // Act & Assert — PublisherClient.Publish calls event.Validate() which throws
        await Assert.ThrowsExceptionAsync<System.ComponentModel.DataAnnotations.ValidationException>(
            () => fixture.Publisher.Publish(invalidEvent));
    }

    [TestMethod]
    public async Task Publish_ValidEvent_DoesNotThrow()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var validEvent = new OrderPlaced("s1") { OrderId = "VALID-001" };

        // Act — should not throw
        await fixture.Publisher.Publish(validEvent);
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(1, handler.ReceivedEvents.Count);
    }
}
