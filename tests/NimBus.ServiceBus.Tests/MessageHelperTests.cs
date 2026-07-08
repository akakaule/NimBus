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
    public void ToServiceBusMessage_WithCloudEventIdentity_StampsCloudEventProperties()
    {
        // AC15: a response from a CloudEvents-consuming subscriber carries the inbound
        // CloudEvent identity to the Resolver as user.CloudEvent* application properties.
        var message = new Message
        {
            To = Constants.ResolverId,
            SessionId = "session-1",
            CorrelationId = "correlation-1",
            EventId = "event-1",
            MessageType = MessageType.ResolutionResponse,
            MessageContent = new MessageContent(),
            CloudEventId = "ce-1",
            CloudEventSource = "urn:ext:billing",
            CloudEventType = "InvoiceCreated",
            CloudEventSubject = "customer-42",
        };

        var result = MessageHelper.ToServiceBusMessage(message);

        Assert.AreEqual("ce-1", result.ApplicationProperties[UserPropertyName.CloudEventId.ToString()]);
        Assert.AreEqual("urn:ext:billing", result.ApplicationProperties[UserPropertyName.CloudEventSource.ToString()]);
        Assert.AreEqual("InvoiceCreated", result.ApplicationProperties[UserPropertyName.CloudEventType.ToString()]);
        Assert.AreEqual("customer-42", result.ApplicationProperties[UserPropertyName.CloudEventSubject.ToString()]);
    }

    [TestMethod]
    public void ToServiceBusMessage_NativeMessage_OmitsCloudEventProperties()
    {
        // AC14: a native message (no CloudEvent identity) must not gain any new wire
        // properties — its application-property set stays byte-identical.
        var message = new Message
        {
            To = "Billing",
            SessionId = "session-1",
            CorrelationId = "correlation-1",
            EventId = "event-1",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" }
            },
        };

        var result = MessageHelper.ToServiceBusMessage(message);

        Assert.IsFalse(result.ApplicationProperties.ContainsKey(UserPropertyName.CloudEventId.ToString()));
        Assert.IsFalse(result.ApplicationProperties.ContainsKey(UserPropertyName.CloudEventSource.ToString()));
        Assert.IsFalse(result.ApplicationProperties.ContainsKey(UserPropertyName.CloudEventType.ToString()));
        Assert.IsFalse(result.ApplicationProperties.ContainsKey(UserPropertyName.CloudEventSubject.ToString()));
    }

    [TestMethod]
    public void MessageContext_ReadsStampedCloudEventIdentityFromUserProperties()
    {
        // The Resolver reads the CloudEvent identity back off the wire via MessageContext
        // (completing the publish → wire → resolver tracking path).
        var sbMsg = new FakeCloudEventMessage { MessageId = "m", SessionId = "s" };
        sbMsg.Properties[UserPropertyName.CloudEventId.ToString()] = "ce-1";
        sbMsg.Properties[UserPropertyName.CloudEventSource.ToString()] = "urn:ext:billing";
        sbMsg.Properties[UserPropertyName.CloudEventType.ToString()] = "InvoiceCreated";
        sbMsg.Properties[UserPropertyName.CloudEventSubject.ToString()] = "customer-42";

        var context = new MessageContext(sbMsg, new InertServiceBusSession(), false, null);

        Assert.AreEqual("ce-1", context.CloudEventId);
        Assert.AreEqual("urn:ext:billing", context.CloudEventSource);
        Assert.AreEqual("InvoiceCreated", context.CloudEventType);
        Assert.AreEqual("customer-42", context.CloudEventSubject);
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
