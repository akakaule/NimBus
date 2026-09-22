#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.WebApp.Services.IntegrationIntelligence;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class IntelligenceSettingsTests
{
    [TestMethod]
    public void Saved_Settings_Override_NonSecrets_Without_Changing_Deployment_Credentials()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"] = "secret-must-stay-local",
            ["NimBus:IntegrationIntelligence:FailureClassification:AllowedEndpoints:0"] = "old-endpoint",
            ["NimBus:IntegrationIntelligence:FailureClassification:TimeoutSeconds"] = "5",
        }).Build();
        var settings = new IntelligenceAdminSettings { Enabled = true, IncludeEventPayload = true };
        var effective = settings.ApplyTo(configuration);
        Assert.IsTrue(effective.GetValue<bool>("NimBus:IntegrationIntelligence:FailureClassification:Data:IncludeEventPayload"));
        Assert.AreEqual("secret-must-stay-local", effective["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"]);
        Assert.IsNull(effective["NimBus:IntegrationIntelligence:FailureClassification:AllowedEndpoints:0"]);
        Assert.AreEqual(20, IntelligenceAdminSettings.FromConfiguration(effective).TimeoutSeconds);
        Assert.AreEqual(0, IntelligenceAdminSettings.FromConfiguration(effective).AllowedEndpoints.Length);
        Assert.IsFalse(JsonConvert.SerializeObject(settings).Contains("secret-must-stay-local", StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(configuration["NimBus:IntegrationIntelligence:Enabled"]));
        configuration["Unrelated:Setting"] = "updated";
        Assert.AreEqual("updated", effective["Unrelated:Setting"], "Non-intelligence configuration must retain its original provider behavior.");
    }

    [TestMethod]
    public void Saved_Key_Overrides_Deployment_Key_Only_When_Supplied_And_Never_Serializes()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"] = "deployment-key",
        }).Build();
        var settings = new IntelligenceAdminSettings { Enabled = true };
        Assert.AreEqual("deployment-key", settings.ApplyTo(configuration)["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"]);
        var effective = settings.ApplyTo(configuration, "saved-key");
        Assert.AreEqual("saved-key", effective["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"]);
        Assert.IsFalse(JsonConvert.SerializeObject(IntelligenceAdminSettings.FromConfiguration(effective)).Contains("saved-key", StringComparison.Ordinal));
        Assert.IsFalse(JsonConvert.SerializeObject(settings).Contains("saved-key", StringComparison.Ordinal));
        Assert.IsTrue(IntelligenceAdminSettings.IsValidApiKey("ts-abc_123.XYZ"));
        Assert.IsFalse(IntelligenceAdminSettings.IsValidApiKey(""));
        Assert.IsFalse(IntelligenceAdminSettings.IsValidApiKey("with space"));
        Assert.IsFalse(IntelligenceAdminSettings.IsValidApiKey("tab\tkey"));
        Assert.IsFalse(IntelligenceAdminSettings.IsValidApiKey(new string('k', 513)));
        var legacy = JsonConvert.DeserializeObject<IntelligenceSettingsDocument>("""{"Settings":{"Enabled":true,"Model":"jev-1.13.0","IncludeEventPayload":false,"IncludeRecentFailureHistory":true,"MaximumHistoryItems":5,"AdditionalRedactedKeys":[],"AllowedEndpoints":[],"TimeoutSeconds":20,"MinimumCategoryConfidence":0.6,"RetryLikely":0.75,"ChangeRequired":0.75},"Revision":"9f1c2c3e-0000-4000-8000-000000000000"}""")!;
        Assert.IsNull(legacy.ProtectedApiKey, "Records saved before the key field existed must still load.");
        Assert.IsTrue(legacy.Settings.Enabled);
    }

    [TestMethod]
    public void Settings_Validate_Bounds_And_Default_To_No_Payload_Sharing()
    {
        var settings = new IntelligenceAdminSettings();
        Assert.IsFalse(settings.IncludeEventPayload);
        Assert.AreEqual(0, settings.Validate().Count);
        settings.TimeoutSeconds = 21;
        settings.MinimumCategoryConfidence = double.NaN;
        settings.MaximumHistoryItems = -1;
        Assert.IsTrue(settings.Validate().Count >= 3);
    }
}
