#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NimBus.Adapters.Dataverse.Contracts;
using NimBus.Core;
using NimBus.Core.Messages.PII;

namespace NimBus.Adapters.Dataverse.Tests;

/// <summary>Projected columns are configuration-driven, so the contract must fail closed for readers without the PII role.</summary>
[TestClass]
public sealed class PrivacyTests
{
    private sealed class DataversePlatform : Platform
    {
        public DataversePlatform() => AddEndpoint(new DataverseEndpoint());
    }

    private static JObject MaskedUpdate()
    {
        var context = ContextReaderTests.Context();
        var entity = (JObject)context["InputParameters"]![0]!["value"]!;
        entity["Attributes"]![1]!["value"] = "person@example.test";
        context["PreEntityImages"] = new JArray(new JObject { ["key"] = "Before", ["value"] = entity.DeepClone() });
        context["PostEntityImages"] = new JArray(new JObject { ["key"] = "After", ["value"] = entity.DeepClone() });
        var options = ContextReaderTests.Options();
        options.PreImageAlias = "Before";
        options.PostImageAlias = "After";
        var record = new DataverseContextReader(options).Read(context.ToString());
        var json = JsonConvert.SerializeObject(record);
        Assert.IsTrue(json.Contains("person@example.test", StringComparison.Ordinal), "Fixture must carry the value the masker has to hide.");
        return JObject.Parse(new EventJsonMasker(new DataversePlatform()).Mask(nameof(DataverseRecordUpdatedV1), json));
    }

    [TestMethod]
    public void Projected_attributes_and_images_are_masked_for_readers_without_the_pii_role()
    {
        var masked = MaskedUpdate();
        foreach (var container in new[] { "Attributes", "Before", "After" })
        {
            Assert.AreEqual("***", (string?)masked[container]!["emailaddress1"], container);
            Assert.AreEqual("***", (string?)masked[container]!["name"], container);
        }
        Assert.IsFalse(masked.ToString().Contains("person@example.test", StringComparison.Ordinal));
        Assert.IsTrue((bool)masked[EventJsonMasker.PiiMaskedMarker]!);
    }

    [TestMethod]
    public void Routing_and_identity_fields_stay_readable_while_payload_is_masked()
    {
        var masked = MaskedUpdate();
        Assert.AreEqual("account", (string?)masked[nameof(DataverseRecordEvent.Table)]);
        Assert.AreEqual("55555555-5555-5555-5555-555555555555", (string?)masked[nameof(DataverseRecordEvent.RecordId)]);
        Assert.AreEqual($"dataverse:11111111-1111-1111-1111-111111111111:account:55555555-5555-5555-5555-555555555555",
            (string?)masked[nameof(DataverseRecordEvent.SessionId)]);
    }

    [TestMethod]
    public void Allowlisted_table_without_columns_is_rejected()
    {
        var options = ContextReaderTests.Options();
        options.Tables = new(StringComparer.Ordinal) { ["account"] = [] };
        Assert.ThrowsExactly<ArgumentException>(options.Validate);
    }
}
