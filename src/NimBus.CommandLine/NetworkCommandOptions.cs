using McMaster.Extensions.CommandLineUtils;

namespace NimBus.CommandLine;

/// <summary>
/// The private networking options (spec 034) shared by <c>nb infra apply</c> and
/// <c>nb setup</c>, registered once so both commands describe and parse them the same way.
/// </summary>
internal sealed class NetworkCommandOptions
{
    private readonly CommandOption _networkMode;
    private readonly CommandOption _allowPublicAccess;
    private readonly CommandOption _privateEndpointSubnetId;
    private readonly CommandOption _resolverSubnetId;
    private readonly CommandOption _webAppSubnetId;
    private readonly CommandOption _privateDns;
    private readonly CommandOption _privateDnsZoneScope;
    private readonly CommandOption _privateDnsLinkVnetIds;
    private readonly CommandOption _monitorPrivateLink;
    private readonly CommandOption _serviceBusCapacity;
    private readonly CommandOption _serviceBusNamespaceName;
    private readonly CommandOption _skipTransition;

    private NetworkCommandOptions(CommandLineApplication command)
    {
        _networkMode = command.Option("--network-mode <MODE>",
            "public | private. 'private' puts every NimBus endpoint behind a private endpoint in your subnets and turns public network access off. " +
            "Defaults to the setup recorded on the resource group by an earlier run, otherwise 'public'. Every network option below is recorded the same way.",
            CommandOptionType.SingleValue);
        _skipTransition = command.Option("--skip-transition",
            "Switch an existing public deployment straight to private without the --allow-public-access pass, accepting downtime while the apps join the VNet.",
            CommandOptionType.NoValue);
        _allowPublicAccess = command.Option("--allow-public-access",
            "With --network-mode private: add private endpoints, DNS and VNet integration but keep public access on (the transition pass for an existing deployment).",
            CommandOptionType.NoValue);
        _privateEndpointSubnetId = command.Option("--private-endpoint-subnet-id <ID>",
            "Subnet resource id the private endpoints take their addresses from. Must not be delegated.",
            CommandOptionType.SingleValue);
        _resolverSubnetId = command.Option("--resolver-subnet-id <ID>",
            "Subnet resource id for the Resolver's VNet integration. Delegated to Microsoft.App/environments (Flex Consumption) or Microsoft.Web/serverFarms (Elastic Premium).",
            CommandOptionType.SingleValue);
        _webAppSubnetId = command.Option("--webapp-subnet-id <ID>",
            "Subnet resource id for the WebApp's VNet integration. Delegated to Microsoft.Web/serverFarms.",
            CommandOptionType.SingleValue);
        _privateDns = command.Option("--private-dns <MODE>",
            "create | existing | external. Who owns the privatelink DNS zones: NimBus (for VNets dedicated to NimBus), a hub resource group, or your own policy/DNS servers. Required with --network-mode private.",
            CommandOptionType.SingleValue);
        _privateDnsZoneScope = command.Option("--private-dns-zone-scope <RESOURCE-GROUP-ID>",
            "With --private-dns existing: resource group id that holds the privatelink zones.",
            CommandOptionType.SingleValue);
        _privateDnsLinkVnetIds = command.Option("--private-dns-link-vnet-id <ID>",
            "With --private-dns create: virtual network to link the zones to. Repeatable. Defaults to the private-endpoint subnet's VNet.",
            CommandOptionType.MultipleValue);
        _monitorPrivateLink = command.Option("--monitor-private-link <MODE>",
            "none. How telemetry reaches Azure Monitor in private mode; 'none' sends it through your firewall to the public endpoints. Required with --network-mode private.",
            CommandOptionType.SingleValue);
        _serviceBusCapacity = command.Option("--service-bus-capacity <N>",
            "Premium messaging units: 1, 2, 4, 8 or 16. Defaults to the existing namespace's capacity, otherwise 1.",
            CommandOptionType.SingleValue);
        _serviceBusNamespaceName = command.Option("--service-bus-namespace-name <NAME>",
            "Override the Service Bus namespace name (default: 'sb-{solution-id}-{environment}'), e.g. a new Premium namespace next to the Standard one it replaces. Pass the same value to 'nb topology apply'.",
            CommandOptionType.SingleValue);
    }

    public static NetworkCommandOptions Register(CommandLineApplication command) => new(command);

    public int? ServiceBusCapacity => NetworkSelection.ParseServiceBusCapacityOption(_serviceBusCapacity.Value());

    public string? ServiceBusNamespaceName => _serviceBusNamespaceName.Value();

    public NetworkOptions Build() => new(
        NetworkSelection.ParseNetworkModeOption(_networkMode.Value()),
        _allowPublicAccess.HasValue(),
        _privateEndpointSubnetId.Value(),
        _resolverSubnetId.Value(),
        _webAppSubnetId.Value(),
        NetworkSelection.ParsePrivateDnsOption(_privateDns.Value()),
        _privateDnsZoneScope.Value(),
        _privateDnsLinkVnetIds.Values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).ToList(),
        NetworkSelection.ParseMonitorPrivateLinkOption(_monitorPrivateLink.Value()),
        _skipTransition.HasValue());
}
