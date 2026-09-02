#pragma warning disable CA1707, CA2007
using System;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CrmErpDemo.AppHost.Tests;

[TestClass]
public sealed class AppHostLaunchSettingsTests
{
    [TestMethod]
    public void Https_profile_does_not_use_known_conflicting_resource_service_port()
    {
        var launchSettingsPath = Path.Combine(AppContext.BaseDirectory, "AppHost.launchSettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(launchSettingsPath));
        var environment = document.RootElement
            .GetProperty("profiles")
            .GetProperty("https")
            .GetProperty("environmentVariables");
        var endpoint = environment.GetProperty("DOTNET_RESOURCE_SERVICE_ENDPOINT_URL").GetString();
        var port = new Uri(endpoint!, UriKind.Absolute).Port;

        Assert.AreNotEqual(22080, port, "Port 22080 collides with a common Windows Hyper-V exclusion range.");
    }
}
