using System.Diagnostics;
using NimBus.Core.Messages;
using NimBus.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class NimBusDiagnosticsTests
{
    private ActivityListener _listener;
    private List<Activity> _activities;

    [TestInitialize]
    public void Setup()
    {
        _activities = new List<Activity>();
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NimBusDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _listener?.Dispose();
    }

    [TestMethod]
    public void ActivitySource_HasCorrectName()
    {
        Assert.AreEqual("NimBus", NimBusDiagnostics.Source.Name);
    }

    [TestMethod]
    public void DiagnosticIdProperty_HasCorrectValue()
    {
        Assert.AreEqual("Diagnostic-Id", NimBusDiagnostics.DiagnosticIdProperty);
    }

    [TestMethod]
    public void ToServiceBusMessage_InjectsDiagnosticId_FromMessageProperty()
    {
        var message = new Message
        {
            To = "TestEvent",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent(),
            SessionId = "session-1",
            DiagnosticId = "00-abcdef1234567890abcdef1234567890-1234567890abcdef-01"
        };

        var sbMessage = MessageHelper.ToServiceBusMessage(message);

        Assert.IsTrue(sbMessage.ApplicationProperties.ContainsKey(NimBusDiagnostics.DiagnosticIdProperty));
        Assert.AreEqual("00-abcdef1234567890abcdef1234567890-1234567890abcdef-01",
            sbMessage.ApplicationProperties[NimBusDiagnostics.DiagnosticIdProperty]);
    }

    [TestMethod]
    public void ToServiceBusMessage_InjectsDiagnosticId_FromCurrentActivity()
    {
        using var activity = NimBusDiagnostics.Source.StartActivity("test", ActivityKind.Producer);

        var message = new Message
        {
            To = "TestEvent",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent(),
            SessionId = "session-1"
        };

        var sbMessage = MessageHelper.ToServiceBusMessage(message);

        Assert.IsTrue(sbMessage.ApplicationProperties.ContainsKey(NimBusDiagnostics.DiagnosticIdProperty));
        Assert.AreEqual(activity.Id, sbMessage.ApplicationProperties[NimBusDiagnostics.DiagnosticIdProperty]);
    }

    [TestMethod]
    public void ToServiceBusMessage_NoDiagnosticId_WhenNoActivityOrProperty()
    {
        // Ensure no current activity
        Activity.Current = null;

        var message = new Message
        {
            To = "TestEvent",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent(),
            SessionId = "session-1"
        };

        var sbMessage = MessageHelper.ToServiceBusMessage(message);

        Assert.IsFalse(sbMessage.ApplicationProperties.ContainsKey(NimBusDiagnostics.DiagnosticIdProperty));
    }

    [TestMethod]
    public void CreateDeferredMessage_InjectsDiagnosticId()
    {
        var message = new Message
        {
            To = "TestEvent",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent(),
            SessionId = "session-1",
            DiagnosticId = "00-abcdef1234567890abcdef1234567890-1234567890abcdef-01"
        };

        var sbMessage = MessageHelper.CreateDeferredMessage(message, "original-session", 1);

        Assert.IsTrue(sbMessage.ApplicationProperties.ContainsKey(NimBusDiagnostics.DiagnosticIdProperty));
        Assert.AreEqual("00-abcdef1234567890abcdef1234567890-1234567890abcdef-01",
            sbMessage.ApplicationProperties[NimBusDiagnostics.DiagnosticIdProperty]);
    }

    [TestMethod]
    public void Message_DiagnosticId_Property_IsSettable()
    {
        var message = new Message();
        Assert.IsNull(message.DiagnosticId);

        message.DiagnosticId = "test-id";
        Assert.AreEqual("test-id", message.DiagnosticId);
    }

    [TestMethod]
    public void IMessage_DiagnosticId_DefaultsToNull()
    {
        // Test default interface member
        IMessage message = new MessageWithoutDiagnosticId();
        Assert.IsNull(message.DiagnosticId);
    }
}

// Test class that implements IMessage without overriding DiagnosticId default
file class MessageWithoutDiagnosticId : IMessage
{
    public string EventId { get; set; }
    public string To { get; set; }
    public string SessionId { get; set; }
    public string CorrelationId { get; set; }
    public string MessageId { get; set; }
    public MessageType MessageType { get; set; }
    public MessageContent MessageContent { get; set; }
    public string ParentMessageId { get; set; }
    public string OriginatingMessageId { get; set; }
    public int? RetryCount { get; set; }
    public string From { get; set; }
    public string OriginatingFrom { get; set; }
    public string EventTypeId { get; set; }
    public string OriginalSessionId { get; set; }
    public int? DeferralSequence { get; set; }
}
