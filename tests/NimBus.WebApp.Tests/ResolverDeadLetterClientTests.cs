#pragma warning disable CA1707, CA2007

using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class ResolverDeadLetterClientTests
{
    [TestMethod]
    public void DeadLetterResubmitRequest_DefaultScopeIsInvalid()
    {
        var request = new DeadLetterResubmitRequest();

        Assert.AreNotEqual(DeadLetterResubmitRequestScope.All, request.Scope);
        Assert.AreNotEqual(DeadLetterResubmitRequestScope.Reason, request.Scope);
    }

    [TestMethod]
    public void CloneForReplay_PreservesSendableMetadataAndReplacesDeadLetterFields()
    {
        var source = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("payload"),
            messageId: "original-id",
            partitionKey: "partition",
            viaPartitionKey: "via",
            sessionId: "session",
            replyToSessionId: "reply-session",
            timeToLive: TimeSpan.FromMinutes(5),
            correlationId: "correlation",
            subject: "subject",
            to: "destination",
            contentType: "application/json",
            replyTo: "reply",
            properties: new Dictionary<string, object>
            {
                ["ordinary"] = "kept",
                ["DeadLetterReason"] = "CosmosDbThrottled",
                ["DeadLetterErrorDescription"] = "sensitive broker detail",
            });

        var replay = ResolverDeadLetterClient.CloneForReplay(source);

        Assert.AreNotEqual(source.MessageId, replay.MessageId);
        Assert.AreEqual("payload", replay.Body.ToString());
        Assert.AreEqual("session", replay.SessionId);
        Assert.AreEqual("reply-session", replay.ReplyToSessionId);
        Assert.AreEqual("correlation", replay.CorrelationId);
        Assert.AreEqual("subject", replay.Subject);
        Assert.AreEqual("application/json", replay.ContentType);
        Assert.AreEqual("destination", replay.To);
        Assert.AreEqual("reply", replay.ReplyTo);
        Assert.AreEqual("partition", replay.PartitionKey);
        Assert.AreEqual("via", replay.TransactionPartitionKey);
        Assert.AreEqual(TimeSpan.FromMinutes(5), replay.TimeToLive);
        Assert.AreEqual("kept", replay.ApplicationProperties["ordinary"]);
        Assert.IsFalse(replay.ApplicationProperties.ContainsKey("DeadLetterReason"));
        Assert.IsFalse(replay.ApplicationProperties.ContainsKey("DeadLetterErrorDescription"));
        Assert.AreEqual("original-id", replay.ApplicationProperties["DeadLetterOriginalMessageId"]);
        Assert.AreEqual("CosmosDbThrottled", replay.ApplicationProperties["DeadLetterOriginalReason"]);
    }
}
