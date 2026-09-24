#pragma warning disable CA1707, CA2007

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// The recorded network setup (spec 034 §5.13): stored as resource-group tags, merged with
/// the command line setting by setting, and guarding transitions between network states.
/// </summary>
public sealed class NetworkIntentTests
{
    private const string Vnet = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-net/providers/Microsoft.Network/virtualNetworks/vnet-nimbus";
    private const string PeSubnet = Vnet + "/subnets/snet-private-endpoints";
    private const string ResolverSubnet = Vnet + "/subnets/snet-resolver";
    private const string WebAppSubnet = Vnet + "/subnets/snet-management";
    private const string DnsScope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-hub-dns";

    private static readonly NetworkOptions Transition = new(
        Mode: NetworkModeChoice.Private,
        AllowPublicAccess: true,
        PrivateEndpointSubnetId: PeSubnet,
        ResolverSubnetId: ResolverSubnet,
        WebAppSubnetId: WebAppSubnet,
        DnsMode: PrivateDnsModeChoice.Existing,
        DnsZoneScope: DnsScope,
        MonitorPrivateLink: MonitorPrivateLinkChoice.None);

    [Fact]
    public void Tags_RoundTripTheFullSetup()
    {
        var tags = NetworkIntent.ToTags(Transition, "sb-nimbus-dev-premium");

        Assert.Equal("private-transition", tags[NetworkIntent.ModeTag]);

        var stored = NetworkIntent.FromTags(tags);
        Assert.NotNull(stored);
        Assert.Equal(NetworkState.PrivateTransition, stored.State);
        Assert.Equal(Transition, stored.Network with { DnsLinkVnetIds = null });
        Assert.Equal("sb-nimbus-dev-premium", stored.ServiceBusNamespaceName);
    }

    [Fact]
    public void Tags_StoreEachLinkedVnetSeparately()
    {
        var network = Transition with
        {
            DnsMode = PrivateDnsModeChoice.Create,
            DnsZoneScope = null,
            DnsLinkVnetIds = new[] { Vnet, Vnet + "-hub" },
        };

        var tags = NetworkIntent.ToTags(network, serviceBusNamespaceName: null);

        Assert.Equal(Vnet, tags["nimbus-network-dns-link-vnet-1"]);
        Assert.Equal(Vnet + "-hub", tags["nimbus-network-dns-link-vnet-2"]);
        Assert.Equal(new[] { Vnet, Vnet + "-hub" }, NetworkIntent.FromTags(tags)!.Network.DnsLinkVnetIds);
    }

    [Fact]
    public void Tags_PublicModeStoresOnlyTheMode()
    {
        var tags = NetworkIntent.ToTags(new NetworkOptions(Mode: NetworkModeChoice.Public), serviceBusNamespaceName: null);

        Assert.Equal(new Dictionary<string, string> { [NetworkIntent.ModeTag] = "public" }, tags);
        Assert.Equal(NetworkState.Public, NetworkIntent.FromTags(tags)!.State);
    }

    [Fact]
    public void Tags_RejectValuesLongerThanAzureAllows()
    {
        var longSubnet = Vnet + "/subnets/" + new string('a', 200);

        var error = Assert.Throws<CommandException>(() =>
            NetworkIntent.ToTags(Transition with { ResolverSubnetId = longSubnet }, serviceBusNamespaceName: null));

        Assert.Contains("256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromTags_IgnoresUnrelatedTagsAndAbsentSetups()
    {
        Assert.Null(NetworkIntent.FromTags(new Dictionary<string, string> { ["costCenter"] = "42" }));
    }

    [Fact]
    public void Merge_ReproducesTheStoredSetupWhenNothingIsGiven()
    {
        var stored = NetworkIntent.FromTags(NetworkIntent.ToTags(Transition, "sb-premium"));

        var (network, namespaceName) = NetworkIntent.Merge(NetworkOptions.None, explicitNamespaceName: null, stored);

        Assert.Equal(NetworkModeChoice.Private, network.Mode);
        Assert.True(network.AllowPublicAccess);
        Assert.Equal(PeSubnet, network.PrivateEndpointSubnetId);
        Assert.Equal("sb-premium", namespaceName);
    }

    /// <summary>Pass 2: `--network-mode private` alone locks the deployment with the stored subnets.</summary>
    [Fact]
    public void Merge_ExplicitPrivateModeWithoutTheFlagEndsTheTransition()
    {
        var stored = NetworkIntent.FromTags(NetworkIntent.ToTags(Transition, null));

        var (network, _) = NetworkIntent.Merge(new NetworkOptions(Mode: NetworkModeChoice.Private), null, stored);

        Assert.False(network.AllowPublicAccess);
        Assert.Equal(ResolverSubnet, network.ResolverSubnetId);
        Assert.Equal(PrivateDnsModeChoice.Existing, network.DnsMode);
    }

    [Fact]
    public void Merge_ExplicitSettingsWinOneByOne()
    {
        var stored = NetworkIntent.FromTags(NetworkIntent.ToTags(Transition, null));
        var otherSubnet = Vnet + "/subnets/snet-resolver-2";

        var (network, _) = NetworkIntent.Merge(new NetworkOptions(ResolverSubnetId: otherSubnet), null, stored);

        Assert.Equal(otherSubnet, network.ResolverSubnetId);
        Assert.Equal(WebAppSubnet, network.WebAppSubnetId);
        Assert.True(network.AllowPublicAccess);
    }

    [Fact]
    public void Merge_ExplicitPublicModeDropsTheStoredNetwork()
    {
        var stored = NetworkIntent.FromTags(NetworkIntent.ToTags(Transition, "sb-premium"));

        var (network, namespaceName) = NetworkIntent.Merge(new NetworkOptions(Mode: NetworkModeChoice.Public), null, stored);

        Assert.Equal(new NetworkOptions(Mode: NetworkModeChoice.Public), network);
        Assert.Equal("sb-premium", namespaceName);
    }

    [Fact]
    public void Merge_WithoutAStoredSetupKeepsTheCommandLine()
    {
        var (network, namespaceName) = NetworkIntent.Merge(Transition, "sb-x-premium", stored: null);

        Assert.Equal(Transition, network);
        Assert.Equal("sb-x-premium", namespaceName);
    }

    [Theory]
    [InlineData(null, "Public")]
    [InlineData("Enabled", "Public")]
    [InlineData("Disabled", "Private")]
    public void CurrentState_WithoutTagsFollowsTheNamespace(string? publicNetworkAccess, string expected)
    {
        var existing = publicNetworkAccess is null ? null : new ExistingServiceBus("Premium", 1, publicNetworkAccess);

        Assert.Equal(Enum.Parse<NetworkState>(expected), NetworkIntent.CurrentState(stored: null, existing));
    }

    [Fact]
    public void CurrentState_PrefersTheStoredState()
    {
        var stored = NetworkIntent.FromTags(NetworkIntent.ToTags(Transition, null));

        Assert.Equal(NetworkState.PrivateTransition, NetworkIntent.CurrentState(stored, new ExistingServiceBus("Premium", 1, "Enabled")));
    }

    [Fact]
    public void TargetState_DistinguishesTransitionFromPrivate()
    {
        Assert.Equal(NetworkState.Public, NetworkIntent.TargetState(NetworkModeChoice.Public, allowPublicAccess: false));
        Assert.Equal(NetworkState.PrivateTransition, NetworkIntent.TargetState(NetworkModeChoice.Private, allowPublicAccess: true));
        Assert.Equal(NetworkState.Private, NetworkIntent.TargetState(NetworkModeChoice.Private, allowPublicAccess: false));
    }

    /// <summary>
    /// A direct public → private switch locks the data services before the apps join the
    /// VNet, so an existing deployment must go through the transition or accept downtime.
    /// </summary>
    [Fact]
    public void ValidateTransition_RefusesADirectSwitchOnAnExistingDeployment()
    {
        var error = Assert.Throws<CommandException>(() =>
            NetworkIntent.ValidateTransition(NetworkState.Public, NetworkState.Private, deploymentExists: true, skipTransition: false));

        Assert.Contains("--allow-public-access", error.Message, StringComparison.Ordinal);
        Assert.Contains("--skip-transition", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateTransition_AllowsEveryOtherPath()
    {
        NetworkIntent.ValidateTransition(NetworkState.Public, NetworkState.Private, deploymentExists: false, skipTransition: false);
        NetworkIntent.ValidateTransition(NetworkState.Public, NetworkState.Private, deploymentExists: true, skipTransition: true);
        NetworkIntent.ValidateTransition(NetworkState.Public, NetworkState.PrivateTransition, deploymentExists: true, skipTransition: false);
        NetworkIntent.ValidateTransition(NetworkState.PrivateTransition, NetworkState.Private, deploymentExists: true, skipTransition: false);
        NetworkIntent.ValidateTransition(NetworkState.Private, NetworkState.Public, deploymentExists: true, skipTransition: false);
    }

    [Fact]
    public void TagChanges_DeletesStaleNetworkTagsAndKeepsCustomerTags()
    {
        var current = new Dictionary<string, string>
        {
            ["costCenter"] = "42",
            [NetworkIntent.ModeTag] = "private",
            ["nimbus-network-dns-zone-scope"] = DnsScope,
            ["nimbus-network-dns-link-vnet-2"] = Vnet,
        };
        var desired = new Dictionary<string, string>
        {
            [NetworkIntent.ModeTag] = "private",
            ["nimbus-network-dns"] = "create",
        };

        var (merge, delete) = NetworkIntent.TagChanges(current, desired);

        Assert.Equal(new Dictionary<string, string> { ["nimbus-network-dns"] = "create" }, merge);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["nimbus-network-dns-zone-scope"] = DnsScope,
                ["nimbus-network-dns-link-vnet-2"] = Vnet,
            },
            delete);
    }
}
