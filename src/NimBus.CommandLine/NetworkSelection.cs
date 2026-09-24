using System.Globalization;
using System.Text.RegularExpressions;

namespace NimBus.CommandLine;

/// <summary>Network mode of a deployment (spec 034). Public is what NimBus always deployed.</summary>
internal enum NetworkModeChoice
{
    Public,
    Private,
}

/// <summary>Who owns the privatelink DNS zones (spec 034 §5.3).</summary>
internal enum PrivateDnsModeChoice
{
    /// <summary>NimBus creates the zones and links them to the given VNets.</summary>
    Create,

    /// <summary>The zones exist in a customer resource group, usually the hub.</summary>
    Existing,

    /// <summary>No zone groups; the customer's policy or DNS servers write the records.</summary>
    External,
}

/// <summary>How Application Insights telemetry reaches Azure Monitor in private mode.</summary>
internal enum MonitorPrivateLinkChoice
{
    None,
    Existing,
    Create,
}

/// <summary>Role a customer subnet plays in a private deployment.</summary>
internal enum SubnetRole
{
    PrivateEndpoints,
    Resolver,
    WebApp,
}

/// <summary>
/// Private networking inputs (spec 034). Null members mean "not given on the command line".
/// </summary>
internal sealed record NetworkOptions(
    NetworkModeChoice? Mode = null,
    bool AllowPublicAccess = false,
    string? PrivateEndpointSubnetId = null,
    string? ResolverSubnetId = null,
    string? WebAppSubnetId = null,
    PrivateDnsModeChoice? DnsMode = null,
    string? DnsZoneScope = null,
    IReadOnlyList<string>? DnsLinkVnetIds = null,
    MonitorPrivateLinkChoice? MonitorPrivateLink = null)
{
    public static NetworkOptions None { get; } = new();
}

/// <summary>The existing Service Bus namespace, as far as the deployment needs to know it.</summary>
internal sealed record ExistingServiceBus(string Tier, int Capacity, string? PublicNetworkAccess)
{
    public bool IsPremium => string.Equals(Tier, "Premium", StringComparison.OrdinalIgnoreCase);

    public bool IsPublicAccessDisabled => string.Equals(PublicNetworkAccess, "Disabled", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A customer subnet as read from Azure: its name, its VNet's region and its delegations.</summary>
internal sealed record SubnetInfo(string Id, string Name, string VnetLocation, IReadOnlyList<string> Delegations);

/// <summary>
/// Pure rules for the private networking options (spec 034): what private mode requires,
/// which subnets qualify, and which Service Bus tier a deployment may use. Azure lookups
/// live in <see cref="InfrastructureDeployer"/>; everything here is decided from values.
/// </summary>
internal static class NetworkSelection
{
    public const string FlexDelegation = "Microsoft.App/environments";
    public const string AppServiceDelegation = "Microsoft.Web/serverFarms";

    private static readonly int[] ServiceBusCapacities = { 1, 2, 4, 8, 16 };

    private static readonly Regex SubnetIdPattern = new(
        @"^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft\.Network/virtualNetworks/[^/]+/subnets/[^/]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex VnetIdPattern = new(
        @"^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft\.Network/virtualNetworks/[^/]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ResourceGroupIdPattern = new(
        @"^/subscriptions/[^/]+/resourceGroups/[^/]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Service Bus namespace names: 6-50 characters, letters, digits and hyphens, starting
    // with a letter and ending with a letter or digit.
    private static readonly Regex ServiceBusNamespacePattern = new(
        @"^[a-zA-Z][a-zA-Z0-9-]{4,48}[a-zA-Z0-9]$",
        RegexOptions.CultureInvariant);

    public static NetworkModeChoice? ParseNetworkModeOption(string? value) => Normalize(value) switch
    {
        null => null,
        "public" => NetworkModeChoice.Public,
        "private" => NetworkModeChoice.Private,
        _ => throw new CommandException($"Unknown --network-mode value '{value}'. Expected 'public' or 'private'."),
    };

    public static PrivateDnsModeChoice? ParsePrivateDnsOption(string? value) => Normalize(value) switch
    {
        null => null,
        "create" => PrivateDnsModeChoice.Create,
        "existing" => PrivateDnsModeChoice.Existing,
        "external" => PrivateDnsModeChoice.External,
        _ => throw new CommandException($"Unknown --private-dns value '{value}'. Expected 'create', 'existing' or 'external'."),
    };

    public static MonitorPrivateLinkChoice? ParseMonitorPrivateLinkOption(string? value) => Normalize(value) switch
    {
        null => null,
        "none" => MonitorPrivateLinkChoice.None,
        "existing" => MonitorPrivateLinkChoice.Existing,
        "create" => MonitorPrivateLinkChoice.Create,
        _ => throw new CommandException($"Unknown --monitor-private-link value '{value}'. Expected 'none', 'existing' or 'create'."),
    };

    /// <summary>Parses --service-bus-capacity (Premium messaging units). Null/blank means "not given".</summary>
    public static int? ParseServiceBusCapacityOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && ServiceBusCapacities.Contains(parsed))
        {
            return parsed;
        }

        throw new CommandException($"Invalid --service-bus-capacity value '{value}'. Premium namespaces take 1, 2, 4, 8 or 16 messaging units.");
    }

    public static void ValidateServiceBusNamespaceName(string name)
    {
        if (!ServiceBusNamespacePattern.IsMatch(name))
        {
            throw new CommandException(
                $"Invalid --service-bus-namespace-name '{name}'. Use 6-50 letters, digits and hyphens, starting with a letter and ending with a letter or digit.");
        }
    }

    /// <summary>
    /// Checks that need no Azure call, so a bad combination fails before login or any
    /// deployment side effect.
    /// </summary>
    public static void ValidateOptions(NetworkOptions network)
    {
        ArgumentNullException.ThrowIfNull(network);

        if (network.Mode != NetworkModeChoice.Private)
        {
            var privateOnly = PrivateOnlyOptionsGiven(network).ToList();
            if (privateOnly.Count > 0)
            {
                throw new CommandException(
                    $"{string.Join(", ", privateOnly)} {(privateOnly.Count == 1 ? "applies" : "apply")} only with --network-mode private.");
            }

            return;
        }

        RequireSubnetId(network.PrivateEndpointSubnetId, "--private-endpoint-subnet-id");
        RequireSubnetId(network.ResolverSubnetId, "--resolver-subnet-id");
        RequireSubnetId(network.WebAppSubnetId, "--webapp-subnet-id");

        var subnets = new[] { network.PrivateEndpointSubnetId!, network.ResolverSubnetId!, network.WebAppSubnetId! };
        if (subnets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != subnets.Length)
        {
            throw new CommandException(
                "--private-endpoint-subnet-id, --resolver-subnet-id and --webapp-subnet-id must be three different subnets: " +
                "an integration subnet cannot host private endpoints, and each app's integration subnet carries its own delegation.");
        }

        switch (network.DnsMode)
        {
            case null:
                throw new CommandException(
                    "--private-dns is required with --network-mode private: 'create' (NimBus creates the privatelink zones; for VNets dedicated to NimBus), " +
                    "'existing' (zones in a hub resource group, with --private-dns-zone-scope) or 'external' (customer policy or DNS writes the records).");
            case PrivateDnsModeChoice.Existing when string.IsNullOrWhiteSpace(network.DnsZoneScope):
                throw new CommandException("--private-dns-zone-scope (the resource group id holding the privatelink zones) is required with --private-dns existing.");
            case PrivateDnsModeChoice.Existing when !ResourceGroupIdPattern.IsMatch(network.DnsZoneScope!.Trim().TrimEnd('/')):
                throw new CommandException(
                    $"Invalid --private-dns-zone-scope '{network.DnsZoneScope}'. Expected a resource group id: /subscriptions/<id>/resourceGroups/<name>.");
        }

        if (network.DnsMode != PrivateDnsModeChoice.Existing && !string.IsNullOrWhiteSpace(network.DnsZoneScope))
        {
            throw new CommandException("--private-dns-zone-scope only applies with --private-dns existing.");
        }

        var linkedVnets = network.DnsLinkVnetIds ?? Array.Empty<string>();
        if (linkedVnets.Count > 0 && network.DnsMode != PrivateDnsModeChoice.Create)
        {
            throw new CommandException("--private-dns-link-vnet-id only applies with --private-dns create.");
        }

        foreach (var vnetId in linkedVnets)
        {
            if (!VnetIdPattern.IsMatch(vnetId))
            {
                throw new CommandException(
                    $"Invalid --private-dns-link-vnet-id '{vnetId}'. Expected a virtual network id: /subscriptions/<id>/resourceGroups/<rg>/providers/Microsoft.Network/virtualNetworks/<name>.");
            }
        }

        switch (network.MonitorPrivateLink)
        {
            case null:
                throw new CommandException(
                    "--monitor-private-link is required with --network-mode private. This version supports 'none': telemetry leaves through " +
                    "your firewall to the public Azure Monitor endpoints. Private monitoring ('existing', 'create') arrives in a later release.");
            case MonitorPrivateLinkChoice.Existing or MonitorPrivateLinkChoice.Create:
                throw new CommandException(
                    $"--monitor-private-link {network.MonitorPrivateLink.Value.ToString().ToLowerInvariant()} is not available yet; pass 'none'. " +
                    "Telemetry then leaves through your firewall to the public Azure Monitor endpoints.");
        }
    }

    /// <summary>
    /// Decides the network mode. An explicit flag wins. Without one, a namespace whose
    /// public access is already disabled means the deployment is private, and the CLI will
    /// not guess the missing network options or silently reopen it.
    /// </summary>
    public static NetworkModeChoice ResolveNetworkMode(NetworkModeChoice? requested, ExistingServiceBus? existing, string namespaceName)
    {
        if (requested is { } explicitMode) return explicitMode;

        if (existing is { IsPublicAccessDisabled: true })
        {
            throw new CommandException(
                $"The Service Bus namespace '{namespaceName}' has public network access disabled, so this deployment is private. " +
                "Pass --network-mode private with its network options, or --network-mode public to reopen it.");
        }

        return NetworkModeChoice.Public;
    }

    /// <summary>
    /// Decides the Service Bus tier and capacity to pass to Bicep. Null means "leave the
    /// template default", which keeps a public deployment's parameters exactly as before.
    /// </summary>
    public static (string? Sku, int? Capacity) ResolveServiceBusSku(bool isPrivate, ExistingServiceBus? existing, int? requestedCapacity, string namespaceName)
    {
        if (existing is { IsPremium: false } && isPrivate)
        {
            throw new CommandException(
                $"The Service Bus namespace '{namespaceName}' is on the Standard tier, which has no private endpoints, and Azure cannot convert it to Premium in place. " +
                "Deploy a new Premium namespace next to it with --service-bus-namespace-name, drain the old one, and re-point your adapters " +
                "(docs/spec/034-private-networking/spec.md §6 lists this and the other options).");
        }

        if (existing is { IsPremium: true })
        {
            return ("Premium", requestedCapacity ?? Math.Max(existing.Capacity, 1));
        }

        if (isPrivate)
        {
            return ("Premium", requestedCapacity ?? 1);
        }

        if (requestedCapacity is not null)
        {
            throw new CommandException("--service-bus-capacity only applies to Premium namespaces, which private mode deploys.");
        }

        return (null, null);
    }

    /// <summary>Free and Shared plans support neither VNet integration nor private endpoints.</summary>
    public static void ValidateManagementPlanSku(string skuName)
    {
        if (!PlanSelection.SupportsAlwaysOn(skuName))
        {
            throw new CommandException(
                $"The management App Service Plan SKU '{skuName}' supports neither VNet integration nor private endpoints. Use B1 or above with --network-mode private.");
        }
    }

    /// <summary>
    /// Checks one customer subnet against its role. <paramref name="appLocation"/> is the
    /// region of the app that integrates with the subnet; regional VNet integration needs
    /// the VNet in the same region. Private endpoints may sit in another region.
    /// </summary>
    public static void ValidateSubnet(SubnetInfo subnet, SubnetRole role, ResolverPlanChoice resolverPlan, string? appLocation)
    {
        ArgumentNullException.ThrowIfNull(subnet);

        if (role == SubnetRole.PrivateEndpoints)
        {
            if (subnet.Delegations.Count > 0)
            {
                throw new CommandException(
                    $"The private-endpoint subnet '{subnet.Id}' is delegated to {string.Join(", ", subnet.Delegations)}. Private endpoints need an undelegated subnet.");
            }

            return;
        }

        var required = role == SubnetRole.Resolver && resolverPlan == ResolverPlanChoice.FlexConsumption
            ? FlexDelegation
            : AppServiceDelegation;
        var option = role == SubnetRole.Resolver ? "--resolver-subnet-id" : "--webapp-subnet-id";

        if (!subnet.Delegations.Contains(required, StringComparer.OrdinalIgnoreCase))
        {
            var plan = role == SubnetRole.Resolver ? $"the {resolverPlan} Resolver" : "the WebApp";
            throw new CommandException(
                $"The subnet '{subnet.Id}' ({option}) must be delegated to {required} for {plan}.");
        }

        if (role == SubnetRole.Resolver && resolverPlan == ResolverPlanChoice.FlexConsumption && subnet.Name.Contains('_', StringComparison.Ordinal))
        {
            throw new CommandException(
                $"Flex Consumption does not support subnet names containing '_' ('{subnet.Name}'). Use another subnet for {option}.");
        }

        if (appLocation is not null && !SameRegion(subnet.VnetLocation, appLocation))
        {
            throw new CommandException(
                $"The subnet '{subnet.Id}' ({option}) is in a VNet in {subnet.VnetLocation}, but the app it serves is in {appLocation}. " +
                "Regional VNet integration needs the VNet in the app's region.");
        }
    }

    public static string VnetIdOf(string subnetId)
    {
        var index = subnetId.IndexOf("/subnets/", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? subnetId : subnetId[..index];
    }

    public static string SubnetNameOf(string subnetId) => subnetId[(subnetId.LastIndexOf('/') + 1)..];

    public static string ToParameterValue(PrivateDnsModeChoice mode) => mode switch
    {
        PrivateDnsModeChoice.Create => "create",
        PrivateDnsModeChoice.Existing => "existing",
        PrivateDnsModeChoice.External => "external",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown DNS mode."),
    };

    private static IEnumerable<string> PrivateOnlyOptionsGiven(NetworkOptions network)
    {
        if (network.AllowPublicAccess) yield return "--allow-public-access";
        if (!string.IsNullOrWhiteSpace(network.PrivateEndpointSubnetId)) yield return "--private-endpoint-subnet-id";
        if (!string.IsNullOrWhiteSpace(network.ResolverSubnetId)) yield return "--resolver-subnet-id";
        if (!string.IsNullOrWhiteSpace(network.WebAppSubnetId)) yield return "--webapp-subnet-id";
        if (network.DnsMode is not null) yield return "--private-dns";
        if (!string.IsNullOrWhiteSpace(network.DnsZoneScope)) yield return "--private-dns-zone-scope";
        if (network.DnsLinkVnetIds is { Count: > 0 }) yield return "--private-dns-link-vnet-id";
        if (network.MonitorPrivateLink is not null) yield return "--monitor-private-link";
    }

    private static void RequireSubnetId(string? value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandException($"{option} is required with --network-mode private.");
        }

        if (!SubnetIdPattern.IsMatch(value))
        {
            throw new CommandException(
                $"Invalid {option} '{value}'. Expected a subnet id: /subscriptions/<id>/resourceGroups/<rg>/providers/Microsoft.Network/virtualNetworks/<vnet>/subnets/<subnet>.");
        }
    }

    // Azure reports regions both as names ("westeurope") and display names ("West Europe").
    private static bool SameRegion(string left, string right) =>
        string.Equals(
            left.Replace(" ", string.Empty, StringComparison.Ordinal),
            right.Replace(" ", string.Empty, StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
