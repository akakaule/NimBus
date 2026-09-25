#pragma warning disable CA1707, CA2007

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Leaving private mode (spec 034 §5.13): incremental deployments keep resources that drop
/// out of the template, so nb removes VNet integration and deletes the network resources it
/// owns, and nothing else, after public access is back.
/// </summary>
public sealed class PrivateNetworkCleanupTests
{
    private const string Owner = "nimbus-dev";
    private const string PeSb = "/subscriptions/x/resourceGroups/rg-nimbus-dev/providers/Microsoft.Network/privateEndpoints/pe-sb-nimbus-dev-namespace";
    private const string PeWeb = "/subscriptions/x/resourceGroups/rg-nimbus-dev/providers/Microsoft.Network/privateEndpoints/pe-webapp-nimbus-dev-management-sites";

    [Fact]
    public async Task RunAsync_RemovesIntegrationThenEndpointsThenDnsInThatOrder()
    {
        var azureCli = OwnedNetwork(integrated: true);

        await Cleanup(azureCli).RunAsync("rg-nimbus-dev", Owner, Names(), PrivateDnsModeChoice.Create, CancellationToken.None);

        var webIntegration = IndexOf(azureCli, "webapp", "vnet-integration", "remove");
        var funcIntegration = IndexOf(azureCli, "functionapp", "vnet-integration", "remove");
        var endpointDelete = IndexOf(azureCli, "private-endpoint", "delete");
        var linkDelete = IndexOf(azureCli, "link", "delete");
        var zoneDelete = IndexOf(azureCli, "zone", "delete");

        Assert.All(new[] { webIntegration, funcIntegration, endpointDelete, linkDelete, zoneDelete }, index => Assert.True(index >= 0));
        Assert.True(webIntegration < endpointDelete && funcIntegration < endpointDelete);
        Assert.True(endpointDelete < linkDelete && linkDelete < zoneDelete);
    }

    [Fact]
    public async Task RunAsync_DeletesOnlyEndpointsCarryingTheOwnershipTag()
    {
        var azureCli = OwnedNetwork(integrated: false);

        await Cleanup(azureCli).RunAsync("rg-nimbus-dev", Owner, Names(), PrivateDnsModeChoice.Existing, CancellationToken.None);

        var list = Assert.Single(azureCli.Commands, c => c.Contains("private-endpoint") && c.Contains("list"));
        Assert.Contains("[?tags.\"nimbus-deployment\"=='nimbus-dev'].id", list);

        var deletes = azureCli.Commands.Where(c => c.Contains("private-endpoint") && c.Contains("delete")).ToList();
        Assert.Equal(2, deletes.Count);
        Assert.Contains(deletes, c => c.Contains(PeSb));
        Assert.Contains(deletes, c => c.Contains(PeWeb));
    }

    /// <summary>
    /// Zones in 'existing' or 'external' mode belong to the customer: they live in other
    /// resource groups and carry no NimBus tag, so the owned-zone query cannot return them.
    /// </summary>
    [Fact]
    public async Task RunAsync_LooksForZonesOnlyInItsOwnResourceGroupByTag()
    {
        var azureCli = new RecordingAzureCliRunner();

        await Cleanup(azureCli).RunAsync("rg-nimbus-dev", Owner, Names(), PrivateDnsModeChoice.Existing, CancellationToken.None);

        var zoneList = Assert.Single(azureCli.Commands, c => c.Contains("zone") && c.Contains("list"));
        Assert.Contains("rg-nimbus-dev", zoneList);
        Assert.Contains("[?tags.\"nimbus-deployment\"=='nimbus-dev'].name", zoneList);
        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("delete"));
    }

    /// <summary>A completed switch to public no longer records the DNS mode; an interrupted cleanup still finds its zones.</summary>
    [Fact]
    public async Task RunAsync_FindsOwnedZonesWithoutARecordedDnsMode()
    {
        var azureCli = OwnedNetwork(integrated: false);

        await Cleanup(azureCli).RunAsync("rg-nimbus-dev", Owner, Names(), recordedDnsMode: null, CancellationToken.None);

        Assert.Contains(azureCli.Commands, c => c.Contains("zone") && c.Contains("delete") && !c.Contains("link"));
    }

    [Fact]
    public async Task RunAsync_KeepsAZoneThatStillHasOtherLinks()
    {
        var azureCli = OwnedNetwork(integrated: false, customerLinkRemains: true);

        await Cleanup(azureCli).RunAsync("rg-nimbus-dev", Owner, Names(), PrivateDnsModeChoice.Create, CancellationToken.None);

        Assert.Contains(azureCli.Commands, c => c.Contains("link") && c.Contains("delete"));
        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("zone") && c.Contains("delete") && !c.Contains("link"));
    }

    [Fact]
    public async Task RunAsync_SkipsAppsThatAreNotIntegrated()
    {
        var azureCli = OwnedNetwork(integrated: false);

        await Cleanup(azureCli).RunAsync("rg-nimbus-dev", Owner, Names(), PrivateDnsModeChoice.Create, CancellationToken.None);

        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("vnet-integration") && c.Contains("remove"));
    }

    /// <summary>
    /// A failed lookup is not "nothing there": skipping the VNet disconnect and then deleting
    /// the endpoints would break apps still routing privately.
    /// </summary>
    [Theory]
    [InlineData("vnet-integration")]
    [InlineData("private-endpoint")]
    [InlineData("zone")]
    public async Task RunAsync_StopsWhenALookupFails(string failingLookup)
    {
        var azureCli = OwnedNetwork(integrated: true);
        azureCli.FailWhen = arguments => arguments.Contains(failingLookup) && arguments.Contains("list");

        await Assert.ThrowsAsync<CommandException>(() =>
            Cleanup(azureCli).RunAsync("rg-nimbus-dev", Owner, Names(), PrivateDnsModeChoice.Create, CancellationToken.None));

        if (failingLookup == "vnet-integration")
        {
            Assert.DoesNotContain(azureCli.Commands, c => c.Contains("delete") || c.Contains("remove"));
        }

        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("zone") && c.Contains("delete"));
    }

    [Fact]
    public async Task RunAsync_StopsOnAnAnswerThatIsNotAList()
    {
        var azureCli = new RecordingAzureCliRunner
        {
            Responder = arguments => arguments.Contains("vnet-integration") ? """{"unexpected":true}""" : null,
        };

        await Assert.ThrowsAsync<CommandException>(() =>
            Cleanup(azureCli).RunAsync("rg-nimbus-dev", Owner, Names(), PrivateDnsModeChoice.Create, CancellationToken.None));

        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("private-endpoint"));
    }

    /// <summary>An interrupted cleanup continues on the next run; with nothing left it does nothing.</summary>
    [Fact]
    public async Task RunAsync_IsANoOpWhenNothingIsLeft()
    {
        var azureCli = new RecordingAzureCliRunner();

        await Cleanup(azureCli).RunAsync("rg-nimbus-dev", Owner, Names(), PrivateDnsModeChoice.Create, CancellationToken.None);

        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("delete") || c.Contains("remove"));
    }

    private static PrivateNetworkCleanup Cleanup(RecordingAzureCliRunner azureCli) => new(azureCli);

    private static DeploymentNames Names() => NamingConventions.Build("nimbus", "dev");

    private static int IndexOf(RecordingAzureCliRunner azureCli, params string[] parts) =>
        azureCli.Commands.FindIndex(c => parts.All(part => c.Contains(part, StringComparer.Ordinal)));

    /// <summary>
    /// Two NimBus-owned endpoints and one NimBus-created zone with one NimBus link. With
    /// <paramref name="customerLinkRemains"/> the zone also carries a customer's link.
    /// </summary>
    private static RecordingAzureCliRunner OwnedNetwork(bool integrated, bool customerLinkRemains = false)
    {
        RecordingAzureCliRunner runner = null!;
        runner = new RecordingAzureCliRunner
        {
            Responder = arguments =>
            {
                if (arguments.Contains("vnet-integration") && arguments.Contains("list"))
                {
                    return integrated ? """[{"name":"snet"}]""" : "[]";
                }

                if (arguments.Contains("private-endpoint") && arguments.Contains("list"))
                {
                    return $$"""["{{PeSb}}","{{PeWeb}}"]""";
                }

                if (arguments.Contains("zone") && arguments.Contains("list"))
                {
                    return """["privatelink.servicebus.windows.net"]""";
                }

                if (arguments.Contains("link") && arguments.Contains("list"))
                {
                    var nimbusLinkDeleted = runner.Commands.Any(c => c.Contains("link") && c.Contains("delete"));
                    var remaining = new List<string>();
                    if (!nimbusLinkDeleted) remaining.Add("nimbus-abc");
                    var ownedOnly = arguments.Any(a => a.Contains("nimbus-deployment", StringComparison.Ordinal));
                    if (customerLinkRemains && !ownedOnly) remaining.Add("hub-link");
                    return System.Text.Json.JsonSerializer.Serialize(remaining);
                }

                return null;
            },
        };
        return runner;
    }
}
