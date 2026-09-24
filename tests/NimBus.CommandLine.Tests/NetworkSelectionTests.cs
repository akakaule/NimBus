#pragma warning disable CA1707, CA2007

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Pure rules behind the private networking options (spec 034): which inputs private mode
/// needs, which subnets qualify, and which Service Bus tier a deployment may use.
/// </summary>
public sealed class NetworkSelectionTests
{
    private const string Vnet = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-net/providers/Microsoft.Network/virtualNetworks/vnet-nimbus";
    private const string PeSubnet = Vnet + "/subnets/snet-private-endpoints";
    private const string ResolverSubnet = Vnet + "/subnets/snet-resolver";
    private const string WebAppSubnet = Vnet + "/subnets/snet-management";
    private const string DnsScope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-hub-dns";

    private static NetworkOptions ValidPrivate() => new(
        Mode: NetworkModeChoice.Private,
        PrivateEndpointSubnetId: PeSubnet,
        ResolverSubnetId: ResolverSubnet,
        WebAppSubnetId: WebAppSubnet,
        DnsMode: PrivateDnsModeChoice.Create,
        MonitorPrivateLink: MonitorPrivateLinkChoice.None);

    [Fact]
    public void ValidateOptions_AcceptsCompletePrivateOptions()
    {
        NetworkSelection.ValidateOptions(ValidPrivate());
    }

    [Fact]
    public void ValidateOptions_AcceptsPublicModeWithNothingElse()
    {
        NetworkSelection.ValidateOptions(NetworkOptions.None);
        NetworkSelection.ValidateOptions(new NetworkOptions(Mode: NetworkModeChoice.Public));
    }

    [Fact]
    public void ValidateOptions_RejectsPrivateOnlyOptionsOutsidePrivateMode()
    {
        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ValidateOptions(new NetworkOptions(PrivateEndpointSubnetId: PeSubnet)));

        Assert.Contains("--private-endpoint-subnet-id", error.Message, StringComparison.Ordinal);
        Assert.Contains("--network-mode private", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pe", "--private-endpoint-subnet-id")]
    [InlineData("resolver", "--resolver-subnet-id")]
    [InlineData("webapp", "--webapp-subnet-id")]
    public void ValidateOptions_RequiresEverySubnetInPrivateMode(string missing, string option)
    {
        var network = missing switch
        {
            "pe" => ValidPrivate() with { PrivateEndpointSubnetId = null },
            "resolver" => ValidPrivate() with { ResolverSubnetId = null },
            _ => ValidPrivate() with { WebAppSubnetId = null },
        };

        var error = Assert.Throws<CommandException>(() => NetworkSelection.ValidateOptions(network));

        Assert.Contains(option, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOptions_RequiresAnExplicitDnsModeInPrivateMode()
    {
        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ValidateOptions(ValidPrivate() with { DnsMode = null }));

        Assert.Contains("--private-dns", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOptions_ExistingDnsNeedsAZoneScope()
    {
        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ValidateOptions(ValidPrivate() with { DnsMode = PrivateDnsModeChoice.Existing }));

        Assert.Contains("--private-dns-zone-scope", error.Message, StringComparison.Ordinal);

        NetworkSelection.ValidateOptions(ValidPrivate() with { DnsMode = PrivateDnsModeChoice.Existing, DnsZoneScope = DnsScope });
    }

    [Fact]
    public void ValidateOptions_ZoneScopeAppliesOnlyToExistingDns()
    {
        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ValidateOptions(ValidPrivate() with { DnsZoneScope = DnsScope }));

        Assert.Contains("--private-dns existing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOptions_LinkedVnetsApplyOnlyToCreatedDns()
    {
        NetworkSelection.ValidateOptions(ValidPrivate() with { DnsLinkVnetIds = new[] { Vnet } });

        var error = Assert.Throws<CommandException>(() => NetworkSelection.ValidateOptions(ValidPrivate() with
        {
            DnsMode = PrivateDnsModeChoice.External,
            DnsLinkVnetIds = new[] { Vnet },
        }));

        Assert.Contains("--private-dns-link-vnet-id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOptions_RequiresAnExplicitMonitoringChoiceInPrivateMode()
    {
        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ValidateOptions(ValidPrivate() with { MonitorPrivateLink = null }));

        Assert.Contains("--monitor-private-link", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("create")]
    public void ValidateOptions_MonitoringPrivateLinkIsNotAvailableYet(string option)
    {
        var choice = NetworkSelection.ParseMonitorPrivateLinkOption(option);
        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ValidateOptions(ValidPrivate() with { MonitorPrivateLink = choice }));

        Assert.Contains("'none'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("snet-resolver")]
    [InlineData("/subscriptions/x/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/vnet")]
    [InlineData("/subscriptions/x/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/st/subnets/a")]
    public void ValidateOptions_RejectsMalformedSubnetIds(string subnetId)
    {
        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ValidateOptions(ValidPrivate() with { ResolverSubnetId = subnetId }));

        Assert.Contains("--resolver-subnet-id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOptions_RejectsASubnetUsedForTwoRoles()
    {
        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ValidateOptions(ValidPrivate() with { WebAppSubnetId = PeSubnet }));

        Assert.Contains("different subnets", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("16", 16)]
    [InlineData(null, null)]
    public void ParseServiceBusCapacityOption_AcceptsPremiumMessagingUnits(string? value, int? expected)
    {
        Assert.Equal(expected, NetworkSelection.ParseServiceBusCapacityOption(value));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("0")]
    [InlineData("many")]
    public void ParseServiceBusCapacityOption_RejectsOtherValues(string value)
    {
        Assert.Throws<CommandException>(() => NetworkSelection.ParseServiceBusCapacityOption(value));
    }

    [Theory]
    [InlineData("sb-nimbus-prod-premium")]
    [InlineData("sbnimbus")]
    public void ValidateServiceBusNamespaceName_AcceptsAzureNames(string name)
    {
        NetworkSelection.ValidateServiceBusNamespaceName(name);
    }

    [Theory]
    [InlineData("sb")]
    [InlineData("1sb-nimbus")]
    [InlineData("sb-nimbus-")]
    [InlineData("sb_nimbus")]
    public void ValidateServiceBusNamespaceName_RejectsInvalidNames(string name)
    {
        Assert.Throws<CommandException>(() => NetworkSelection.ValidateServiceBusNamespaceName(name));
    }

    [Fact]
    public void ResolveServiceBusSku_NewPrivateDeploymentGetsPremium()
    {
        Assert.Equal(("Premium", 1), NetworkSelection.ResolveServiceBusSku(isPrivate: true, existing: null, requestedCapacity: null, "sb-x"));
        Assert.Equal(("Premium", 4), NetworkSelection.ResolveServiceBusSku(isPrivate: true, existing: null, requestedCapacity: 4, "sb-x"));
    }

    [Fact]
    public void ResolveServiceBusSku_NewPublicDeploymentKeepsTheTemplateDefault()
    {
        Assert.Equal((null, null), NetworkSelection.ResolveServiceBusSku(isPrivate: false, existing: null, requestedCapacity: null, "sb-x"));
    }

    [Fact]
    public void ResolveServiceBusSku_ExistingPremiumIsNeverSentStandard()
    {
        var existing = new ExistingServiceBus("Premium", 2, "Enabled");

        Assert.Equal(("Premium", 2), NetworkSelection.ResolveServiceBusSku(isPrivate: false, existing, requestedCapacity: null, "sb-x"));
        Assert.Equal(("Premium", 8), NetworkSelection.ResolveServiceBusSku(isPrivate: false, existing, requestedCapacity: 8, "sb-x"));
    }

    [Fact]
    public void ResolveServiceBusSku_RefusesPrivateModeOnAnExistingStandardNamespace()
    {
        var existing = new ExistingServiceBus("Standard", 0, null);

        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ResolveServiceBusSku(isPrivate: true, existing, requestedCapacity: null, "sb-nimbus-prod"));

        Assert.Contains("sb-nimbus-prod", error.Message, StringComparison.Ordinal);
        Assert.Contains("--service-bus-namespace-name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveServiceBusSku_CapacityNeedsPremium()
    {
        Assert.Throws<CommandException>(() =>
            NetworkSelection.ResolveServiceBusSku(isPrivate: false, existing: null, requestedCapacity: 2, "sb-x"));
        Assert.Throws<CommandException>(() =>
            NetworkSelection.ResolveServiceBusSku(isPrivate: false, new ExistingServiceBus("Standard", 0, null), requestedCapacity: 2, "sb-x"));
    }

    [Fact]
    public void ResolveNetworkMode_ExplicitChoiceWins()
    {
        var locked = new ExistingServiceBus("Premium", 1, "Disabled");

        Assert.Equal(NetworkModeChoice.Public, NetworkSelection.ResolveNetworkMode(NetworkModeChoice.Public, locked, "sb-x"));
        Assert.Equal(NetworkModeChoice.Private, NetworkSelection.ResolveNetworkMode(NetworkModeChoice.Private, null, "sb-x"));
    }

    [Fact]
    public void ResolveNetworkMode_DefaultsToPublic()
    {
        Assert.Equal(NetworkModeChoice.Public, NetworkSelection.ResolveNetworkMode(null, null, "sb-x"));
        Assert.Equal(NetworkModeChoice.Public, NetworkSelection.ResolveNetworkMode(null, new ExistingServiceBus("Standard", 0, null), "sb-x"));
    }

    /// <summary>A rerun without flags must never silently reopen a locked deployment.</summary>
    [Fact]
    public void ResolveNetworkMode_RefusesToGuessForALockedNamespace()
    {
        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ResolveNetworkMode(null, new ExistingServiceBus("Premium", 1, "Disabled"), "sb-nimbus-prod"));

        Assert.Contains("--network-mode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateManagementPlanSku_RejectsFreeAndSharedInPrivateMode()
    {
        Assert.Throws<CommandException>(() => NetworkSelection.ValidateManagementPlanSku("F1"));
        Assert.Throws<CommandException>(() => NetworkSelection.ValidateManagementPlanSku("D1"));
        NetworkSelection.ValidateManagementPlanSku("B1");
    }

    [Fact]
    public void ValidateSubnet_PrivateEndpointSubnetMustNotBeDelegated()
    {
        var subnet = new SubnetInfo(PeSubnet, "snet-private-endpoints", "westeurope", new[] { "Microsoft.Web/serverFarms" });

        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ValidateSubnet(subnet, SubnetRole.PrivateEndpoints, ResolverPlanChoice.FlexConsumption, appLocation: null));

        Assert.Contains("delegated", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FlexConsumption", "Microsoft.App/environments")]
    [InlineData("ElasticPremium", "Microsoft.Web/serverFarms")]
    public void ValidateSubnet_ResolverSubnetNeedsThePlansDelegation(string planOption, string delegation)
    {
        var plan = PlanSelection.ParseResolverPlanOption(planOption)!.Value;
        var correct = new SubnetInfo(ResolverSubnet, "snet-resolver", "westeurope", new[] { delegation });
        NetworkSelection.ValidateSubnet(correct, SubnetRole.Resolver, plan, "westeurope");

        var undelegated = correct with { Delegations = Array.Empty<string>() };
        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ValidateSubnet(undelegated, SubnetRole.Resolver, plan, "westeurope"));
        Assert.Contains(delegation, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSubnet_WebAppSubnetNeedsServerFarmsDelegation()
    {
        var subnet = new SubnetInfo(WebAppSubnet, "snet-management", "westeurope", new[] { "Microsoft.App/environments" });

        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ValidateSubnet(subnet, SubnetRole.WebApp, ResolverPlanChoice.FlexConsumption, "westeurope"));

        Assert.Contains("Microsoft.Web/serverFarms", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSubnet_IntegrationSubnetMustShareTheAppsRegion()
    {
        var subnet = new SubnetInfo(ResolverSubnet, "snet-resolver", "northeurope", new[] { "Microsoft.App/environments" });

        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ValidateSubnet(subnet, SubnetRole.Resolver, ResolverPlanChoice.FlexConsumption, "West Europe"));

        Assert.Contains("northeurope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSubnet_FlexRejectsUnderscoresInTheSubnetName()
    {
        var subnet = new SubnetInfo(ResolverSubnet, "snet_resolver", "westeurope", new[] { "Microsoft.App/environments" });

        var error = Assert.Throws<CommandException>(() =>
            NetworkSelection.ValidateSubnet(subnet, SubnetRole.Resolver, ResolverPlanChoice.FlexConsumption, "westeurope"));

        Assert.Contains("_", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VnetIdOf_StripsTheSubnetSegment()
    {
        Assert.Equal(Vnet, NetworkSelection.VnetIdOf(PeSubnet));
    }
}
