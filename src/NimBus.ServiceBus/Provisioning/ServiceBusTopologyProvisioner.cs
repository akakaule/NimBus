using Azure.Messaging.ServiceBus.Administration;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Messages;

namespace NimBus.ServiceBus.Provisioning;

/// <summary>
/// Provisions the Service Bus topology (topics, session-enabled subscriptions, forwarding
/// subscriptions, and SQL routing rules) for every endpoint declared by an <see cref="IPlatform"/>.
/// Idempotent: existing entities are left untouched when they already match the desired shape;
/// subscriptions whose session flag or forward target differ are deleted and recreated.
/// </summary>
/// <remarks>
/// The <c>nb topology apply</c> command wraps this class for the built-in platform configuration.
/// External platforms (e.g. an integrations repository) run it in-process against their own
/// <see cref="IPlatform"/> implementation, exactly like the sample provisioner consoles do.
/// </remarks>
public sealed class ServiceBusTopologyProvisioner
{
    private readonly ServiceBusAdministrationClient _client;
    private readonly Func<IPlatform> _platformFactory;
    private readonly bool _isEmulator;
    private readonly Action<string> _log;

    /// <summary>
    /// Creates a provisioner from a Service Bus connection string. The official Service Bus
    /// emulator is detected via <c>UseDevelopmentEmulator=true</c> in the connection string and
    /// gets entity sizes/TTLs lowered to values the emulator accepts.
    /// </summary>
    /// <param name="connectionString">Service Bus namespace connection string with Manage rights.</param>
    /// <param name="platformFactory">Factory producing the platform whose topology to provision.</param>
    /// <param name="log">Optional progress sink; defaults to <see cref="Console.WriteLine(string)"/>.</param>
    public ServiceBusTopologyProvisioner(string connectionString, Func<IPlatform> platformFactory, Action<string>? log = null)
        : this(
            new ServiceBusAdministrationClient(connectionString ?? throw new ArgumentNullException(nameof(connectionString))),
            platformFactory,
            IsEmulator(connectionString),
            log)
    {
    }

    /// <summary>
    /// Creates a provisioner from a pre-built <see cref="ServiceBusAdministrationClient"/>, e.g.
    /// one constructed with a fully-qualified namespace and a <c>TokenCredential</c> so CI can
    /// provision via OIDC/managed identity instead of a shared access key. Emulator-specific
    /// entity limits are not applied on this path (the emulator only speaks connection strings).
    /// </summary>
    /// <param name="client">Administration client for the target namespace.</param>
    /// <param name="platformFactory">Factory producing the platform whose topology to provision.</param>
    /// <param name="log">Optional progress sink; defaults to <see cref="Console.WriteLine(string)"/>.</param>
    public ServiceBusTopologyProvisioner(ServiceBusAdministrationClient client, Func<IPlatform> platformFactory, Action<string>? log = null)
        : this(client, platformFactory, isEmulator: false, log)
    {
    }

    internal ServiceBusTopologyProvisioner(ServiceBusAdministrationClient client, Func<IPlatform> platformFactory, bool isEmulator, Action<string>? log = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _platformFactory = platformFactory ?? throw new ArgumentNullException(nameof(platformFactory));
        _isEmulator = isEmulator;
        _log = log ?? Console.WriteLine;
    }

    // The official Azure Service Bus emulator advertises itself in the
    // connection string via UseDevelopmentEmulator=true. NimBus's defaults
    // (5 GB topic size, 14-day deferred-subscription TTL) exceed the
    // emulator's hard caps (100 MB topics, conservative TTL upper bound),
    // so when we detect the emulator we drop those down to values the
    // emulator accepts. Production / real-Azure paths are untouched.
    internal static bool IsEmulator(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return false;
        return connectionString.IndexOf("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Applies the platform's topology to the target namespace.</summary>
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var platform = _platformFactory();
        await ApplyCoreAsync(_client, platform, _isEmulator, _log, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyCoreAsync(ServiceBusAdministrationClient client, IPlatform platform, bool isEmulator, Action<string> log, CancellationToken cancellationToken)
    {
        await EnsureTopicAsync(client, Constants.ResolverId, isEmulator, log, cancellationToken).ConfigureAwait(false);

        foreach (var endpoint in platform.Endpoints.OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal))
        {
            await EnsureTopicAsync(client, endpoint.Id, isEmulator, log, cancellationToken).ConfigureAwait(false);
        }

        await EnsureSessionSubscriptionAsync(client, Constants.ResolverId, Constants.ResolverId, forwardTo: null, keepDefaultRule: true, log, cancellationToken).ConfigureAwait(false);

        foreach (var endpoint in platform.Endpoints.OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal))
        {
            await EnsureEndpointTopologyAsync(client, platform, endpoint, isEmulator, log, cancellationToken).ConfigureAwait(false);
        }

        // Dynamic-forward pass (spec 022 D5): provision forward subscription + EventTypeId rule
        // for dynamically-typed events that the compiled-event loop above cannot derive.
        foreach (var fwd in platform.DynamicForwards.OrderBy(f => f.EventTypeId, StringComparer.Ordinal))
        {
            var subName = $"AgentDyn-{fwd.TargetEndpoint}";
            await EnsureForwardSubscriptionAsync(client, fwd.SourceEndpoint, subName, fwd.TargetEndpoint, log, cancellationToken).ConfigureAwait(false);
            await EnsureRuleAsync(
                client,
                fwd.SourceEndpoint,
                subName,
                $"dyn-{fwd.EventTypeId}",
                $"user.EventTypeId = '{fwd.EventTypeId}' AND user.From IS NULL",
                $"SET user.From = '{fwd.SourceEndpoint}'; SET user.EventId = newid(); SET user.To = '{fwd.TargetEndpoint}';",
                log,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureEndpointTopologyAsync(
        ServiceBusAdministrationClient client,
        IPlatform platform,
        IEndpoint endpoint,
        bool isEmulator,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        await EnsureSessionSubscriptionAsync(client, endpoint.Id, endpoint.Id, forwardTo: null, keepDefaultRule: false, log, cancellationToken).ConfigureAwait(false);
        await EnsureRuleAsync(client, endpoint.Id, endpoint.Id, $"to-{endpoint.Id}", $"user.To = '{endpoint.Id}'", action: null, log, cancellationToken).ConfigureAwait(false);

        await EnsureForwardSubscriptionAsync(client, endpoint.Id, Constants.ResolverId, Constants.ResolverId, log, cancellationToken).ConfigureAwait(false);
        await EnsureRuleAsync(client, endpoint.Id, Constants.ResolverId, $"from-{endpoint.Id}", $"user.To = '{Constants.ResolverId}'", $"SET user.From = '{endpoint.Id}'", log, cancellationToken).ConfigureAwait(false);
        await EnsureRuleAsync(client, endpoint.Id, Constants.ResolverId, $"to-{endpoint.Id}", $"user.To = '{endpoint.Id}'", action: null, log, cancellationToken).ConfigureAwait(false);

        await EnsureRuleAsync(client, endpoint.Id, endpoint.Id, "continuation", $"user.To = '{Constants.ContinuationId}'", $"SET user.To = '{endpoint.Id}'; SET user.From = '{Constants.ContinuationId}'", log, cancellationToken).ConfigureAwait(false);
        await EnsureRuleAsync(client, endpoint.Id, endpoint.Id, "retry", $"user.To = '{Constants.RetryId}'", $"SET user.To = '{endpoint.Id}'; SET user.From = '{Constants.RetryId}'", log, cancellationToken).ConfigureAwait(false);

        await EnsureDeferredSubscriptionAsync(client, endpoint.Id, isEmulator, log, cancellationToken).ConfigureAwait(false);
        await EnsureDeferredProcessorSubscriptionAsync(client, endpoint.Id, log, cancellationToken).ConfigureAwait(false);

        foreach (var eventType in endpoint.EventTypesProduced.OrderBy(eventType => eventType.Id, StringComparer.Ordinal))
        {
            foreach (var consumer in platform
                .GetConsumers(eventType)
                .Where(consumer => !string.Equals(consumer.Id, endpoint.Id, StringComparison.Ordinal))
                .DistinctBy(consumer => consumer.Id)
                .OrderBy(consumer => consumer.Id, StringComparer.Ordinal))
            {
                await EnsureForwardSubscriptionAsync(client, endpoint.Id, consumer.Id, consumer.Id, log, cancellationToken).ConfigureAwait(false);
                // Filter must require user.From IS NULL so this rule only fires on
                // ORIGINAL publishes, never on messages already forwarded into this
                // topic by another endpoint. Without that guard, an event type that
                // is produced AND consumed by both endpoints (e.g. ContactCreated in
                // CrmErpDemo) creates a forwarding loop:
                //   CRM publishes -> forwarded to ERP -> ERP's forward sub matches
                //   the same EventTypeId -> forwarded back to CRM -> ...
                // until Service Bus's MaxHopCount kicks in and dead-letters the
                // message ("Maximum transfer hop count is exceeded").
                // PublisherClient never sets From on the original publish; only the
                // action below populates it, so checking IS NULL cleanly excludes
                // forwarded copies.
                await EnsureRuleAsync(
                    client,
                    endpoint.Id,
                    consumer.Id,
                    eventType.Id,
                    $"user.EventTypeId = '{eventType.Id}' AND user.From IS NULL",
                    $"SET user.From = '{endpoint.Id}'; SET user.EventId = newid(); SET user.To = '{consumer.Id}';",
                    log,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task EnsureTopicAsync(ServiceBusAdministrationClient client, string topicName, bool isEmulator, Action<string> log, CancellationToken cancellationToken)
    {
        if (await client.TopicExistsAsync(topicName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var options = new CreateTopicOptions(topicName)
        {
            SupportOrdering = true,
            DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(10),
            EnableBatchedOperations = true,
        };

        // Real Azure namespaces accept up to 5 GB per topic; the emulator caps
        // at 100 MB and rejects anything larger. Setting the cap explicitly on
        // production matches the historical default; omitting it on the emulator
        // lets the server pick its own (100 MB) ceiling without a 400 response.
        if (!isEmulator)
        {
            options.MaxSizeInMegabytes = 5120;
        }

        await client.CreateTopicAsync(options, cancellationToken).ConfigureAwait(false);
        log($"Created topic '{topicName}'.");
    }

    private static async Task EnsureSessionSubscriptionAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        string subscriptionName,
        string? forwardTo,
        bool keepDefaultRule,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var existing = await TryGetSubscriptionAsync(client, topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await client.CreateSubscriptionAsync(CreateSubscriptionOptions(topicName, subscriptionName, requiresSession: true, forwardTo), cancellationToken).ConfigureAwait(false);
            existing = await client.GetSubscriptionAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
            log($"Created session subscription '{subscriptionName}' on topic '{topicName}'.");
        }
        else if (!existing.RequiresSession || !ForwardToMatches(existing.ForwardTo, forwardTo))
        {
            await client.DeleteSubscriptionAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
            await client.CreateSubscriptionAsync(CreateSubscriptionOptions(topicName, subscriptionName, requiresSession: true, forwardTo), cancellationToken).ConfigureAwait(false);
            log($"Recreated session subscription '{subscriptionName}' on topic '{topicName}'.");
        }

        if (!keepDefaultRule)
        {
            await DeleteRuleIfExistsAsync(client, topicName, subscriptionName, "$Default", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureForwardSubscriptionAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        string subscriptionName,
        string forwardTo,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var existing = await TryGetSubscriptionAsync(client, topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await client.CreateSubscriptionAsync(CreateSubscriptionOptions(topicName, subscriptionName, requiresSession: false, forwardTo), cancellationToken).ConfigureAwait(false);
            log($"Created forward subscription '{subscriptionName}' on topic '{topicName}' to '{forwardTo}'.");
        }
        else if (existing.RequiresSession || !ForwardToMatches(existing.ForwardTo, forwardTo))
        {
            await client.DeleteSubscriptionAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
            await client.CreateSubscriptionAsync(CreateSubscriptionOptions(topicName, subscriptionName, requiresSession: false, forwardTo), cancellationToken).ConfigureAwait(false);
            log($"Recreated forward subscription '{subscriptionName}' on topic '{topicName}' to '{forwardTo}'.");
        }

        await DeleteRuleIfExistsAsync(client, topicName, subscriptionName, "$Default", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compares an existing subscription's ForwardTo (which Azure stores as a normalised
    /// entity path — usually lowercased, sometimes the bare name and sometimes a full URL)
    /// against the desired entity name we passed at creation time.
    ///
    /// The previous implementation used <see cref="string.Equals(string?, string?, StringComparison)"/>
    /// with <see cref="StringComparison.Ordinal"/>, which rejected matches whenever Azure
    /// normalised the value (e.g. lowercasing "ErpEndpoint" to "erpendpoint", or expanding
    /// to a full sb://... URL). That caused the surrounding code to delete and recreate the
    /// subscription on every call, wiping out previously-added forwarding rules.
    /// </summary>
    private static bool ForwardToMatches(string? existingForwardTo, string? desiredForwardTo)
    {
        var existingEmpty = string.IsNullOrEmpty(existingForwardTo);
        var desiredEmpty = string.IsNullOrEmpty(desiredForwardTo);
        if (existingEmpty && desiredEmpty) return true;
        if (existingEmpty || desiredEmpty) return false;

        // Compare trailing entity names case-insensitively. Handles all observed forms:
        //   "ErpEndpoint", "erpendpoint", "sb://ns.servicebus.windows.net/erpendpoint".
        var existingTail = TrailingSegment(existingForwardTo!);
        var desiredTail = TrailingSegment(desiredForwardTo!);
        return string.Equals(existingTail, desiredTail, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrailingSegment(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? path : path.Substring(lastSlash + 1);
    }

    private static async Task EnsureDeferredSubscriptionAsync(ServiceBusAdministrationClient client, string topicName, bool isEmulator, Action<string> log, CancellationToken cancellationToken)
    {
        const string subscriptionName = "Deferred";
        var existing = await TryGetSubscriptionAsync(client, topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
        var mustRecreate = existing is null || !existing.RequiresSession;

        if (mustRecreate)
        {
            if (existing is not null)
            {
                await client.DeleteSubscriptionAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
            }

            var options = CreateSubscriptionOptions(topicName, subscriptionName, requiresSession: true, forwardTo: null);
            // 14 days matches what real Azure accepts and what operator workflows
            // assume for parking deferred messages. The emulator's documented TTL
            // upper bound is conservative and not pinned in the public docs, so
            // for emulator runs we drop to 1 hour — long enough for sample/CI
            // smoke runs, well inside any plausible upper limit.
            options.DefaultMessageTimeToLive = isEmulator ? TimeSpan.FromHours(1) : TimeSpan.FromDays(14);
            await client.CreateSubscriptionAsync(options, cancellationToken).ConfigureAwait(false);
            log($"Ensured deferred subscription '{subscriptionName}' on topic '{topicName}'.");
        }

        await DeleteRuleIfExistsAsync(client, topicName, subscriptionName, "$Default", cancellationToken).ConfigureAwait(false);
        await EnsureRuleAsync(client, topicName, subscriptionName, "DeferredFilter", "user.To = 'Deferred' AND user.OriginalSessionId IS NOT NULL", action: null, log, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureDeferredProcessorSubscriptionAsync(ServiceBusAdministrationClient client, string topicName, Action<string> log, CancellationToken cancellationToken)
    {
        const string subscriptionName = "DeferredProcessor";
        var existing = await TryGetSubscriptionAsync(client, topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
        var mustRecreate = existing is null || existing.RequiresSession;

        if (mustRecreate)
        {
            if (existing is not null)
            {
                await client.DeleteSubscriptionAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
            }

            await client.CreateSubscriptionAsync(CreateSubscriptionOptions(topicName, subscriptionName, requiresSession: false, forwardTo: null), cancellationToken).ConfigureAwait(false);
            log($"Ensured deferred processor subscription '{subscriptionName}' on topic '{topicName}'.");
        }

        await DeleteRuleIfExistsAsync(client, topicName, subscriptionName, "$Default", cancellationToken).ConfigureAwait(false);
        await EnsureRuleAsync(client, topicName, subscriptionName, "DeferredProcessorFilter", "user.To = 'DeferredProcessor'", action: null, log, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureRuleAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        string subscriptionName,
        string ruleName,
        string filter,
        string? action,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var existing = await TryGetRuleAsync(client, topicName, subscriptionName, ruleName, cancellationToken).ConfigureAwait(false);
        if (existing is not null && RuleMatches(existing, filter, action))
        {
            return;
        }

        if (existing is not null)
        {
            await client.DeleteRuleAsync(topicName, subscriptionName, ruleName, cancellationToken).ConfigureAwait(false);
        }

        var createRule = new CreateRuleOptions
        {
            Name = ruleName,
            Filter = new SqlRuleFilter(filter),
        };

        if (!string.IsNullOrWhiteSpace(action))
        {
            createRule.Action = new SqlRuleAction(action);
        }

        await client.CreateRuleAsync(topicName, subscriptionName, createRule, cancellationToken).ConfigureAwait(false);
        log($"Ensured rule '{ruleName}' on '{topicName}/{subscriptionName}'.");
    }

    private static bool RuleMatches(RuleProperties rule, string filter, string? action)
    {
        var existingFilter = (rule.Filter as SqlRuleFilter)?.SqlExpression ?? rule.Filter?.ToString() ?? string.Empty;
        var existingAction = (rule.Action as SqlRuleAction)?.SqlExpression ?? string.Empty;
        return string.Equals(existingFilter, filter, StringComparison.Ordinal) &&
               string.Equals(existingAction, action ?? string.Empty, StringComparison.Ordinal);
    }

    private static CreateSubscriptionOptions CreateSubscriptionOptions(string topicName, string subscriptionName, bool requiresSession, string? forwardTo)
    {
        var options = new CreateSubscriptionOptions(topicName, subscriptionName)
        {
            MaxDeliveryCount = 10,
            LockDuration = TimeSpan.FromSeconds(30),
            EnableBatchedOperations = true,
            EnableDeadLetteringOnFilterEvaluationExceptions = true,
            RequiresSession = requiresSession,
        };

        if (!string.IsNullOrWhiteSpace(forwardTo))
        {
            options.ForwardTo = forwardTo;
        }

        return options;
    }

    private static async Task<SubscriptionProperties?> TryGetSubscriptionAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        string subscriptionName,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetSubscriptionAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
            return response.Value;
        }
        catch (Azure.RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
        catch (Azure.Messaging.ServiceBus.ServiceBusException exception) when (exception.Reason == Azure.Messaging.ServiceBus.ServiceBusFailureReason.MessagingEntityNotFound)
        {
            return null;
        }
    }

    private static async Task<RuleProperties?> TryGetRuleAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        string subscriptionName,
        string ruleName,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetRuleAsync(topicName, subscriptionName, ruleName, cancellationToken).ConfigureAwait(false);
            return response.Value;
        }
        catch (Azure.RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
        catch (Azure.Messaging.ServiceBus.ServiceBusException exception) when (exception.Reason == Azure.Messaging.ServiceBus.ServiceBusFailureReason.MessagingEntityNotFound)
        {
            return null;
        }
    }

    private static async Task DeleteRuleIfExistsAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        string subscriptionName,
        string ruleName,
        CancellationToken cancellationToken)
    {
        var existing = await TryGetRuleAsync(client, topicName, subscriptionName, ruleName, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await client.DeleteRuleAsync(topicName, subscriptionName, ruleName, cancellationToken).ConfigureAwait(false);
        }
    }
}
