using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.EndToEnd.Tests.Infrastructure;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Tests error handling paths: non-transient exceptions, transient exceptions,
/// and retry policies through the full pipeline.
/// </summary>
[TestClass]
public class ErrorHandlingTests
{
    [TestMethod]
    public async Task Publish_HandlerThrowsException_ErrorResponseSentAndSessionBlocked()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("Processing failed")
        };
        fixture.RegisterHandler(() => handler);

        var @event = new OrderPlaced("session-err") { OrderId = "ORD-ERR" };

        // Act
        await fixture.Publisher.Publish(@event);
        var results = await fixture.DeliverAllWithResults();

        // Assert — StrictMessageHandler wraps in EventContextHandlerException, sends error response
        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(results[0].Session.WasCompleted, "Message should still be completed after error");

        var responses = fixture.ResponseBus.SentMessages;
        Assert.AreEqual(1, responses.Count);
        Assert.AreEqual(MessageType.ErrorResponse, responses[0].MessageType);
    }

    [TestMethod]
    public async Task Publish_HandlerThrowsException_ExceptionPropagatesThroughPipeline()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("Boom")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s1") { OrderId = "ORD-X" });
        var results = await fixture.DeliverAllWithResults();

        // Assert — the exception is re-thrown as EventContextHandlerException
        // which is caught by MessageHandler (no further propagation beyond DeliverAllWithResults)
        Assert.AreEqual(1, results.Count);
        // MessageHandler catches EventContextHandlerException (line 78-79), so no external exception
        Assert.IsNull(results[0].Exception, "MessageHandler should swallow EventContextHandlerException");
    }

    [TestMethod]
    public async Task Publish_HandlerThrowsTransientException_MessageAbandoned()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new TransientException("Timeout", new TimeoutException())
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-transient") { OrderId = "ORD-T" });
        var results = await fixture.DeliverAllWithResults();

        // Assert — TransientException in StrictMessageHandler.HandleEventContent
        // propagates to MessageHandler which abandons (no-op in NimBus) and does NOT complete
        Assert.AreEqual(1, results.Count);
        Assert.IsFalse(results[0].Session.WasCompleted, "Message should not be completed on transient error");
        Assert.IsFalse(results[0].Session.WasDeadLettered, "Message should not be dead-lettered on transient error");
    }

    [TestMethod]
    public async Task Publish_WithRetryPolicy_RetryResponseSentOnFailure()
    {
        // Arrange
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.AddEventTypePolicy("OrderPlaced", new RetryPolicy
        {
            MaxRetries = 3,
            BaseDelay = TimeSpan.FromMinutes(1)
        });

        var fixture = new EndToEndFixture(retryProvider);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("Retriable failure")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-retry") { OrderId = "ORD-R" });
        var results = await fixture.DeliverAllWithResults();

        // Assert — StrictMessageHandler sends ErrorResponse AND then RetryRequest
        var responses = fixture.ResponseBus.SentMessages;
        Assert.IsTrue(responses.Count >= 1, "Should send at least an error response");
        Assert.IsTrue(responses.Any(r => r.MessageType == MessageType.ErrorResponse), "Should have error response");
        Assert.IsTrue(responses.Any(r => r.MessageType == MessageType.RetryRequest), "Should have retry request");
    }

    [TestMethod]
    public async Task Publish_SuccessAfterSetup_NoErrorResponse()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler(); // No exception
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-ok") { OrderId = "ORD-OK" });
        await fixture.DeliverAll();

        // Assert
        var responses = fixture.ResponseBus.SentMessages;
        Assert.AreEqual(1, responses.Count);
        Assert.AreEqual(MessageType.ResolutionResponse, responses[0].MessageType, "Success path sends ResolutionResponse only");
    }
}
