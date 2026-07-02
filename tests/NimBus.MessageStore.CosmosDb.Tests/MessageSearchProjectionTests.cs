#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// Drift guard for <see cref="CosmosDbClient.MessageSearchProjection"/>: every
/// <see cref="MessageEntity"/> property must appear in the projection string
/// (except the deliberately-omitted <c>EventContent.EventJson</c>, which lives
/// under <c>MessageContent</c>). Fails when a new property is added to
/// MessageEntity without extending the projection — otherwise message search
/// would silently return it as null.
/// </summary>
[TestClass]
public sealed class MessageSearchProjectionTests
{
    [TestMethod]
    public void Projection_covers_every_MessageEntity_property()
    {
        var missing = typeof(MessageEntity).GetProperties()
            .Select(p => p.Name)
            .Where(name => !CosmosDbClient.MessageSearchProjection.Contains($"\"{name}\":", StringComparison.Ordinal))
            .ToList();

        Assert.AreEqual(0, missing.Count,
            $"MessageEntity properties missing from MessageSearchProjection: {string.Join(", ", missing)}. " +
            "Extend the projection in CosmosDbClient.cs (and mirror the semantics in the SQL / in-memory providers).");
    }

    [TestMethod]
    public void Projection_omits_EventJson_and_keeps_ErrorContent()
    {
        Assert.IsFalse(CosmosDbClient.MessageSearchProjection.Contains("EventJson", StringComparison.Ordinal),
            "EventJson must NOT be projected — omitting the payload is the point of the projection.");
        Assert.IsTrue(CosmosDbClient.MessageSearchProjection.Contains("\"ErrorContent\":", StringComparison.Ordinal),
            "ErrorContent must be projected whole — the error-grouped search view reads ErrorText.");
        Assert.IsTrue(CosmosDbClient.MessageSearchProjection.Contains("c.message[\"From\"]", StringComparison.Ordinal)
            && CosmosDbClient.MessageSearchProjection.Contains("c.message[\"To\"]", StringComparison.Ordinal),
            "From/To are reserved keywords in Cosmos SQL and must use bracket notation.");
    }
}
