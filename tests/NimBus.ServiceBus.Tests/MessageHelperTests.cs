#pragma warning disable CA1707
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.ServiceBus;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class MessageHelperTests
{
    [TestMethod]
    public void ToServiceBusMessage_WhenFromIsSet_AddsFromApplicationProperty()
    {
        var message = new Message
        {
            To = "Billing",
            From = Constants.ManagerId,
            SessionId = "session-1",
            CorrelationId = "correlation-1",
            EventId = "event-1",
            MessageType = MessageType.SkipRequest,
            MessageContent = new MessageContent(),
        };

        var result = MessageHelper.ToServiceBusMessage(message);

        Assert.AreEqual(Constants.ManagerId, result.ApplicationProperties[UserPropertyName.From.ToString()]);
    }

    [TestMethod]
    public void ToServiceBusMessage_WhenFromIsMissing_DoesNotAddFromApplicationProperty()
    {
        var message = new Message
        {
            To = "Billing",
            SessionId = "session-1",
            CorrelationId = "correlation-1",
            EventId = "event-1",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = "OrderPlaced",
                    EventJson = "{}",
                }
            },
        };

        var result = MessageHelper.ToServiceBusMessage(message);

        Assert.IsFalse(result.ApplicationProperties.ContainsKey(UserPropertyName.From.ToString()));
    }

    [TestMethod]
    public void CreateDeferredMessage_UsesOriginalSessionAndDeferralMetadata()
    {
        var message = new Message
        {
            To = Constants.DeferredSubscriptionName,
            SessionId = "ignored-session",
            CorrelationId = "correlation-1",
            EventId = "event-1",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = "OrderPlaced",
                    EventJson = "{}",
                }
            },
        };

        var result = MessageHelper.CreateDeferredMessage(message, "session-42", 7);

        Assert.AreEqual("session-42", result.SessionId);
        Assert.AreEqual(Constants.DeferredSubscriptionName, result.ApplicationProperties[UserPropertyName.To.ToString()]);
        Assert.AreEqual("session-42", result.ApplicationProperties[UserPropertyName.OriginalSessionId.ToString()]);
        Assert.AreEqual(7, result.ApplicationProperties[UserPropertyName.DeferralSequence.ToString()]);
    }
}
