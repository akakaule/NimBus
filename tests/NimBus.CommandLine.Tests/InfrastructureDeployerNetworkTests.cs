#pragma warning disable CA1707, CA2007

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// How the private networking options (spec 034) reach the two Bicep deployments, and what
/// the deployer refuses before it deploys anything.
/// </summary>
public sealed class InfrastructureDeployerNetworkTests
{
    private const string Vnet = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-net/providers/Microsoft.Network/virtualNetworks/vnet-nimbus";
    private const string EndpointVnet = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-net/providers/Microsoft.Network/virtualNetworks/vnet-endpoints";
    private const string PeSubnet = EndpointVnet + "/subnets/snet-private-endpoints";
    private const string ResolverSubnet = Vnet + "/subnets/snet-resolver";
    private const string WebAppSubnet = Vnet + "/subnets/snet-management";
    private const string DnsScope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-hub-dns";

    private static readonly NetworkOptions PrivateNetwork = new(
        Mode: NetworkModeChoice.Private,
        PrivateEndpointSubnetId: PeSubnet,
        ResolverSubnetId: ResolverSubnet,
        WebAppSubnetId: WebAppSubnet,
        DnsMode: PrivateDnsModeChoice.Existing,
        DnsZoneScope: DnsScope,
        MonitorPrivateLink: MonitorPrivateLinkChoice.None);

    [Fact]
    public async Task ApplyAsync_PublicModePassesNoNetworkParameters()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        await Deployer(azureCli).ApplyAsync(Options(NetworkOptions.None), CancellationToken.None);

        foreach (var deployment in azureCli.Deployments)
        {
            Assert.DoesNotContain(deployment.Arguments, a => a.StartsWith("networkMode=", StringComparison.Ordinal));
            Assert.DoesNotContain(deployment.Arguments, a => a.StartsWith("privateEndpointSubnetId=", StringComparison.Ordinal));
            Assert.DoesNotContain(deployment.Arguments, a => a.StartsWith("serviceBusSku=", StringComparison.Ordinal));
            Assert.DoesNotContain(deployment.Arguments, a => a.StartsWith("serviceBusNamespaceName=", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("subnet", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_PrivateModePassesTheNetworkToBothDeployments()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        await Deployer(azureCli).ApplyAsync(Options(PrivateNetwork), CancellationToken.None);

        var core = azureCli.Deployments[0].Arguments;
        Assert.Contains("networkMode=private", core);
        Assert.Contains("allowPublicAccess=false", core);
        Assert.Contains($"privateEndpointSubnetId={PeSubnet}", core);
        Assert.Contains($"resolverSubnetId={ResolverSubnet}", core);
        Assert.Contains("privateEndpointLocation=swedencentral", core);
        Assert.Contains("privateDnsMode=existing", core);
        Assert.Contains($"privateDnsZoneScope={DnsScope}", core);
        Assert.Contains("serviceBusSku=Premium", core);
        Assert.Contains("serviceBusCapacity=1", core);

        var webApp = azureCli.Deployments[1].Arguments;
        Assert.Contains("networkMode=private", webApp);
        Assert.Contains($"managementSubnetId={WebAppSubnet}", webApp);
        Assert.Contains($"privateEndpointSubnetId={PeSubnet}", webApp);
        Assert.Contains("privateDnsMode=existing", webApp);
        Assert.DoesNotContain(webApp, a => a.StartsWith("resolverSubnetId=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_TransitionKeepsPublicAccessOn()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        await Deployer(azureCli).ApplyAsync(Options(PrivateNetwork with { AllowPublicAccess = true }), CancellationToken.None);

        Assert.Contains("allowPublicAccess=true", azureCli.Deployments[0].Arguments);
        Assert.Contains("allowPublicAccess=true", azureCli.Deployments[1].Arguments);
    }

    [Fact]
    public async Task ApplyAsync_CreateModePassesLinkedVnetsAsAJsonArray()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);
        var network = PrivateNetwork with
        {
            DnsMode = PrivateDnsModeChoice.Create,
            DnsZoneScope = null,
            DnsLinkVnetIds = new[] { Vnet },
        };

        await Deployer(azureCli).ApplyAsync(Options(network), CancellationToken.None);

        var core = azureCli.Deployments[0].Arguments;
        Assert.Contains($"privateDnsLinkVnetIds=[\"{Vnet}\"]", core);
        Assert.DoesNotContain(core, a => a.StartsWith("privateDnsZoneScope=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_RefusesPrivateModeOnAStandardNamespaceBeforeDeploying()
    {
        var azureCli = CustomerNetwork(existingServiceBus: """{"tier":"Standard","capacity":1,"publicNetworkAccess":"Enabled"}""");

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(PrivateNetwork), CancellationToken.None));

        Assert.Contains("--service-bus-namespace-name", error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
    }

    [Fact]
    public async Task ApplyAsync_KeepsAnExistingPremiumNamespaceInPublicMode()
    {
        var azureCli = CustomerNetwork(existingServiceBus: """{"tier":"Premium","capacity":2,"publicNetworkAccess":"Enabled"}""");

        await Deployer(azureCli).ApplyAsync(Options(NetworkOptions.None), CancellationToken.None);

        Assert.Contains("serviceBusSku=Premium", azureCli.Deployments[0].Arguments);
        Assert.Contains("serviceBusCapacity=2", azureCli.Deployments[0].Arguments);
    }

    [Fact]
    public async Task ApplyAsync_WillNotReopenALockedNamespaceWithoutAnExplicitMode()
    {
        var azureCli = CustomerNetwork(existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Disabled"}""");

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(NetworkOptions.None), CancellationToken.None));

        Assert.Contains("--network-mode", error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
    }

    [Fact]
    public async Task ApplyAsync_NamespaceOverrideReachesBothDeploymentsAndTheFqdn()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        await Deployer(azureCli).ApplyAsync(
            Options(PrivateNetwork) with { ServiceBusNamespaceName = "sb-nimbus-dev-premium" },
            CancellationToken.None);

        Assert.Contains("serviceBusNamespaceName=sb-nimbus-dev-premium", azureCli.Deployments[0].Arguments);
        Assert.Contains("serviceBusNamespaceName=sb-nimbus-dev-premium", azureCli.Deployments[1].Arguments);
        Assert.Contains("serviceBusFullyQualifiedNamespace=sb-nimbus-dev-premium.servicebus.windows.net", azureCli.Deployments[1].Arguments);
        Assert.Contains(azureCli.Commands, c => c.Contains("servicebus", StringComparer.Ordinal) && c.Contains("sb-nimbus-dev-premium", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_RejectsAWronglyDelegatedResolverSubnetBeforeDeploying()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null, resolverDelegation: "Microsoft.Web/serverFarms");

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(PrivateNetwork), CancellationToken.None));

        Assert.Contains("Microsoft.App/environments", error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
    }

    /// <summary>Option combinations that can never work fail before login or any Azure call.</summary>
    [Fact]
    public async Task ApplyAsync_InvalidNetworkOptionsFailBeforeAnyAzureCall()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(PrivateNetwork with { DnsMode = null }), CancellationToken.None));

        Assert.Empty(azureCli.Commands);
    }

    private static InfrastructureDeployer Deployer(RecordingAzureCliRunner azureCli) =>
        new(new CommandContext(Path.GetTempPath()), azureCli);

    private static InfrastructureOptions Options(NetworkOptions network) =>
        new(
            "nimbus",
            "dev",
            "rg-nimbus-dev",
            ResourceNamePostFix: null,
            Location: "westeurope",
            WebAppVersion: "test",
            ResolverPlan: ResolverPlanChoice.FlexConsumption,
            Network: network);

    /// <summary>
    /// A customer VNet in West Europe whose private-endpoint subnet sits in a peered VNet in
    /// Sweden Central, so the endpoint location must come from the subnet, not the app.
    /// </summary>
    private static RecordingAzureCliRunner CustomerNetwork(string? existingServiceBus, string resolverDelegation = "Microsoft.App/environments") =>
        new()
        {
            Responder = arguments =>
            {
                string? ValueOf(string option)
                {
                    var index = arguments.ToList().IndexOf(option);
                    return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
                }

                if (arguments.Contains("subnet") && arguments.Contains("show"))
                {
                    return ValueOf("--ids") switch
                    {
                        PeSubnet => """{"name":"snet-private-endpoints","delegations":[]}""",
                        ResolverSubnet => $$"""{"name":"snet-resolver","delegations":["{{resolverDelegation}}"]}""",
                        WebAppSubnet => """{"name":"snet-management","delegations":["Microsoft.Web/serverFarms"]}""",
                        _ => null,
                    };
                }

                if (arguments.Contains("vnet") && arguments.Contains("show"))
                {
                    return ValueOf("--ids") == EndpointVnet ? "swedencentral" : "westeurope";
                }

                if (arguments.Contains("servicebus") && arguments.Contains("namespace") && arguments.Contains("show"))
                {
                    return existingServiceBus ?? string.Empty;
                }

                if (arguments.Contains("provider") && arguments.Contains("show"))
                {
                    return "Registered";
                }

                return null;
            },
        };
}
