namespace NimBus.CommandLine;

/// <summary>The three network states of a deployment (spec 034 §5.13).</summary>
internal enum NetworkState
{
    /// <summary>What NimBus always deployed: every endpoint public.</summary>
    Public,

    /// <summary>Private endpoints, DNS and VNet integration exist; public access stays on.</summary>
    PrivateTransition,

    /// <summary>Public network access off everywhere.</summary>
    Private,
}

/// <summary>A network setup read back from the resource group's tags.</summary>
internal sealed record StoredNetwork(NetworkState State, NetworkOptions Network, string? ServiceBusNamespaceName);

/// <summary>
/// The recorded network setup (spec 034 §5.13). The CLI stores the full private networking
/// setup as tags on the deployment's resource group before it deploys, so a rerun without
/// flags reproduces it, including the private-transition state, and an interrupted run
/// converges on the next one. Explicit flags win setting by setting.
/// </summary>
internal static class NetworkIntent
{
    public const string ModeTag = "nimbus-network-mode";
    public const string TagPrefix = "nimbus-network-";
    public const string ServiceBusNamespaceTag = "nimbus-service-bus-namespace";

    // Azure caps tag values at 256 characters.
    public const int MaxTagValueLength = 256;

    private const string PrivateEndpointSubnetTag = "nimbus-network-pe-subnet";
    private const string ResolverSubnetTag = "nimbus-network-resolver-subnet";
    private const string WebAppSubnetTag = "nimbus-network-webapp-subnet";
    private const string DnsModeTag = "nimbus-network-dns";
    private const string DnsZoneScopeTag = "nimbus-network-dns-zone-scope";
    private const string DnsLinkVnetTagPrefix = "nimbus-network-dns-link-vnet-";
    private const string MonitorTag = "nimbus-network-monitor";

    /// <summary>
    /// The tags that describe a network setup. Public mode stores only the mode; the
    /// namespace override is stored whenever one is in use.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ToTags(NetworkOptions network, string? serviceBusNamespaceName)
    {
        ArgumentNullException.ThrowIfNull(network);

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var state = TargetState(network.Mode ?? NetworkModeChoice.Public, network.AllowPublicAccess);
        Add(tags, ModeTag, ToTagValue(state));

        if (state != NetworkState.Public)
        {
            Add(tags, PrivateEndpointSubnetTag, network.PrivateEndpointSubnetId);
            Add(tags, ResolverSubnetTag, network.ResolverSubnetId);
            Add(tags, WebAppSubnetTag, network.WebAppSubnetId);
            if (network.DnsMode is { } dnsMode)
            {
                Add(tags, DnsModeTag, NetworkSelection.ToParameterValue(dnsMode));
            }

            Add(tags, DnsZoneScopeTag, network.DnsZoneScope);
            var linkedVnets = network.DnsLinkVnetIds ?? Array.Empty<string>();
            for (var i = 0; i < linkedVnets.Count; i++)
            {
                Add(tags, $"{DnsLinkVnetTagPrefix}{i + 1}", linkedVnets[i]);
            }

            if (network.MonitorPrivateLink is { } monitor)
            {
                Add(tags, MonitorTag, monitor.ToString().ToLowerInvariant());
            }
        }

        Add(tags, ServiceBusNamespaceTag, serviceBusNamespaceName);
        return tags;
    }

    /// <summary>Reads a stored setup back. Null when the resource group carries none.</summary>
    public static StoredNetwork? FromTags(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var lookup = new Dictionary<string, string>(tags, StringComparer.OrdinalIgnoreCase);
        if (!lookup.TryGetValue(ModeTag, out var modeValue))
        {
            return null;
        }

        var state = modeValue.Trim().ToLowerInvariant() switch
        {
            "public" => NetworkState.Public,
            "private-transition" => NetworkState.PrivateTransition,
            "private" => NetworkState.Private,
            _ => throw new CommandException(
                $"The resource group tag {ModeTag} has the unknown value '{modeValue}'. Expected public, private-transition or private; fix or remove the tag."),
        };

        lookup.TryGetValue(ServiceBusNamespaceTag, out var namespaceName);
        if (state == NetworkState.Public)
        {
            return new StoredNetwork(state, new NetworkOptions(Mode: NetworkModeChoice.Public), namespaceName);
        }

        var linkedVnets = lookup
            .Where(tag => tag.Key.StartsWith(DnsLinkVnetTagPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(tag => (Index: int.TryParse(tag.Key[DnsLinkVnetTagPrefix.Length..], out var index) ? index : int.MaxValue, tag.Value))
            .OrderBy(link => link.Index)
            .Select(link => link.Value)
            .ToList();

        var network = new NetworkOptions(
            Mode: NetworkModeChoice.Private,
            AllowPublicAccess: state == NetworkState.PrivateTransition,
            PrivateEndpointSubnetId: Get(lookup, PrivateEndpointSubnetTag),
            ResolverSubnetId: Get(lookup, ResolverSubnetTag),
            WebAppSubnetId: Get(lookup, WebAppSubnetTag),
            DnsMode: NetworkSelection.ParsePrivateDnsOption(Get(lookup, DnsModeTag)),
            DnsZoneScope: Get(lookup, DnsZoneScopeTag),
            DnsLinkVnetIds: linkedVnets.Count > 0 ? linkedVnets : null,
            MonitorPrivateLink: NetworkSelection.ParseMonitorPrivateLinkOption(Get(lookup, MonitorTag)));

        return new StoredNetwork(state, network, namespaceName);
    }

    /// <summary>
    /// Combines the command line with the stored setup, setting by setting: an explicit
    /// value wins, anything not given comes from the tags. An explicit
    /// <c>--network-mode public</c> drops the stored network; an explicit
    /// <c>--network-mode private</c> without <c>--allow-public-access</c> ends a transition.
    /// </summary>
    public static (NetworkOptions Network, string? ServiceBusNamespaceName) Merge(
        NetworkOptions explicitOptions,
        string? explicitNamespaceName,
        StoredNetwork? stored)
    {
        ArgumentNullException.ThrowIfNull(explicitOptions);

        var namespaceName = string.IsNullOrWhiteSpace(explicitNamespaceName) ? stored?.ServiceBusNamespaceName : explicitNamespaceName.Trim();

        if (stored is null || stored.State == NetworkState.Public || explicitOptions.Mode == NetworkModeChoice.Public)
        {
            // Nothing private to reuse, or the operator is leaving private mode. A stored
            // public mode still counts as the recorded choice.
            var mode = explicitOptions.Mode ?? stored?.Network.Mode;
            return (explicitOptions with { Mode = mode }, namespaceName);
        }

        var saved = stored.Network;
        var merged = new NetworkOptions(
            Mode: NetworkModeChoice.Private,
            // Only a mode given on the command line resets the transition flag; otherwise the
            // stored state stands unless the flag is passed now.
            AllowPublicAccess: explicitOptions.Mode is not null
                ? explicitOptions.AllowPublicAccess
                : saved.AllowPublicAccess || explicitOptions.AllowPublicAccess,
            PrivateEndpointSubnetId: explicitOptions.PrivateEndpointSubnetId ?? saved.PrivateEndpointSubnetId,
            ResolverSubnetId: explicitOptions.ResolverSubnetId ?? saved.ResolverSubnetId,
            WebAppSubnetId: explicitOptions.WebAppSubnetId ?? saved.WebAppSubnetId,
            DnsMode: explicitOptions.DnsMode ?? saved.DnsMode,
            // The zone scope and linked VNets belong to a DNS mode: a new mode on the command
            // line does not inherit the old mode's settings.
            DnsZoneScope: explicitOptions.DnsMode is null ? explicitOptions.DnsZoneScope ?? saved.DnsZoneScope : explicitOptions.DnsZoneScope,
            DnsLinkVnetIds: explicitOptions.DnsMode is null && explicitOptions.DnsLinkVnetIds is not { Count: > 0 }
                ? saved.DnsLinkVnetIds
                : explicitOptions.DnsLinkVnetIds,
            MonitorPrivateLink: explicitOptions.MonitorPrivateLink ?? saved.MonitorPrivateLink,
            SkipTransition: explicitOptions.SkipTransition);

        return (merged, namespaceName);
    }

    /// <summary>
    /// Where the deployment stands before this run: the stored state, else what the
    /// namespace shows (public access disabled means private), else public.
    /// </summary>
    public static NetworkState CurrentState(StoredNetwork? stored, ExistingServiceBus? existing) =>
        stored?.State ?? (existing is { IsPublicAccessDisabled: true } ? NetworkState.Private : NetworkState.Public);

    public static NetworkState TargetState(NetworkModeChoice mode, bool allowPublicAccess) =>
        mode == NetworkModeChoice.Public
            ? NetworkState.Public
            : allowPublicAccess ? NetworkState.PrivateTransition : NetworkState.Private;

    /// <summary>
    /// A direct public → private switch locks the data services before the apps join the
    /// VNet (spec 034 §5.13), so an existing deployment goes through the transition first
    /// unless the operator accepts the downtime.
    /// </summary>
    public static void ValidateTransition(NetworkState current, NetworkState target, bool deploymentExists, bool skipTransition)
    {
        if (deploymentExists && current == NetworkState.Public && target == NetworkState.Private && !skipTransition)
        {
            throw new CommandException(
                "Switching an existing public deployment straight to private would lock Service Bus, the store and storage before the apps join the VNet. " +
                "Run once with --network-mode private --allow-public-access, check that the apps resolve the private endpoints, then run again without --allow-public-access. " +
                "Pass --skip-transition to switch directly and accept the downtime.");
        }
    }

    /// <summary>
    /// What to merge into and delete from the resource group's tags to reach the desired
    /// setup. Only NimBus network tags are ever deleted; the customer's tags stay.
    /// </summary>
    public static (IReadOnlyDictionary<string, string> Merge, IReadOnlyDictionary<string, string> Delete) TagChanges(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> desired)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);

        var merge = desired
            .Where(tag => !current.TryGetValue(tag.Key, out var value) || !string.Equals(value, tag.Value, StringComparison.Ordinal))
            .ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.OrdinalIgnoreCase);

        var delete = current
            .Where(tag => IsNimBusTag(tag.Key) && !desired.ContainsKey(tag.Key))
            .ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.OrdinalIgnoreCase);

        return (merge, delete);
    }

    public static string ToTagValue(NetworkState state) => state switch
    {
        NetworkState.Public => "public",
        NetworkState.PrivateTransition => "private-transition",
        NetworkState.Private => "private",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown network state."),
    };

    private static bool IsNimBusTag(string key) =>
        key.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, ServiceBusNamespaceTag, StringComparison.OrdinalIgnoreCase);

    private static void Add(Dictionary<string, string> tags, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxTagValueLength)
        {
            throw new CommandException(
                $"'{trimmed}' is longer than the {MaxTagValueLength} characters an Azure tag can hold, so the network setup cannot be recorded on the resource group.");
        }

        tags[key] = trimmed;
    }

    private static string? Get(Dictionary<string, string> tags, string key) =>
        tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
