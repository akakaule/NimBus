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
