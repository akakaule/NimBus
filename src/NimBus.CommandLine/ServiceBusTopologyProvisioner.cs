using Azure.Messaging.ServiceBus.Administration;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Messages;

namespace NimBus.CommandLine;

public sealed class ServiceBusTopologyProvisioner
{
    private readonly AzureCliRunner? _az;
    private readonly Func<TopologyOptions, CancellationToken, Task<string>>? _connectionStringProvider;
    private readonly string? _connectionString;
    private readonly Func<string, ServiceBusAdministrationClient> _clientFactory;
    private readonly Func<IPlatform> _platformFactory;

    public ServiceBusTopologyProvisioner(string connectionString, Func<IPlatform> platformFactory)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _clientFactory = static cs => new ServiceBusAdministrationClient(cs);
        _platformFactory = platformFactory ?? throw new ArgumentNullException(nameof(platformFactory));
    }

    // The official Azure Service Bus emulator advertises itself in the
    // connection string via UseDevelopmentEmulator=true. NimBus's defaults
    // (5 GB topic size, 14-day deferred-subscription TTL) exceed the
    // emulator's hard caps (100 MB topics, conservative TTL upper bound),
    // so when we detect the emulator we drop those down to values the
    // emulator accepts. Production / real-Azure paths are untouched.
    private static bool IsEmulator(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return false;
        return connectionString.IndexOf("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal ServiceBusTopologyProvisioner(AzureCliRunner az)
        : this(
            az,
            static (options, cancellationToken, runner) => ReadConnectionStringAsync(runner, options, cancellationToken),
            static connectionString => new ServiceBusAdministrationClient(connectionString),
            static () => new PlatformConfiguration())
    {
    }

    internal ServiceBusTopologyProvisioner(
        AzureCliRunner az,
        Func<TopologyOptions, CancellationToken, AzureCliRunner, Task<string>> connectionStringProvider,
        Func<string, ServiceBusAdministrationClient> clientFactory,
        Func<IPlatform> platformFactory)
    {
        _az = az;
        _connectionStringProvider = (options, cancellationToken) => connectionStringProvider(options, cancellationToken, _az);
        _clientFactory = clientFactory;
        _platformFactory = platformFactory;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        if (_connectionString is null)
            throw new InvalidOperationException("This overload requires the connection-string constructor.");

        var platform = _platformFactory();
        var client = _clientFactory(_connectionString);

        await ApplyCoreAsync(client, platform, IsEmulator(_connectionString), cancellationToken).ConfigureAwait(false);
    }

    internal async Task ApplyAsync(TopologyOptions options, CancellationToken cancellationToken)
    {
        if (_connectionStringProvider is null)
            throw new InvalidOperationException("This overload requires the AzureCliRunner constructor.");

        var connectionString = await _connectionStringProvider(options, cancellationToken).ConfigureAwait(false);

        var platform = _platformFactory();
        var client = _clientFactory(connectionString);

        await ApplyCoreAsync(client, platform, IsEmulator(connectionString), cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyCoreAsync(ServiceBusAdministrationClient client, IPlatform platform, bool isEmulator, CancellationToken cancellationToken)
    {
        await EnsureTopicAsync(client, Constants.ResolverId, isEmulator, cancellationToken).ConfigureAwait(false);

        foreach (var endpoint in platform.Endpoints.OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal))
        {
            await EnsureTopicAsync(client, endpoint.Id, isEmulator, cancellationToken).ConfigureAwait(false);
        }

        await EnsureSessionSubscriptionAsync(client, Constants.ResolverId, Constants.ResolverId, forwardTo: null, keepDefaultRule: true, cancellationToken).ConfigureAwait(false);

        foreach (var endpoint in platform.Endpoints.OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal))
        {
            await EnsureEndpointTopologyAsync(client, platform, endpoint, isEmulator, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureEndpointTopologyAsync(
        ServiceBusAdministrationClient client,
        IPlatform platform,
        IEndpoint endpoint,
        bool isEmulator,
        CancellationToken cancellationToken)
    {
        await EnsureSessionSubscriptionAsync(client, endpoint.Id, endpoint.Id, forwardTo: null, keepDefaultRule: false, cancellationToken).ConfigureAwait(false);
        await EnsureRuleAsync(client, endpoint.Id, endpoint.Id, $"to-{endpoint.Id}", $"user.To = '{endpoint.Id}'", action: null, cancellationToken).ConfigureAwait(false);

        await EnsureForwardSubscriptionAsync(client, endpoint.Id, Constants.ResolverId, Constants.ResolverId, cancellationToken).ConfigureAwait(false);
        await EnsureRuleAsync(client, endpoint.Id, Constants.ResolverId, $"from-{endpoint.Id}", $"user.To = '{Constants.ResolverId}'", $"SET user.From = '{endpoint.Id}'", cancellationToken).ConfigureAwait(false);
        await EnsureRuleAsync(client, endpoint.Id, Constants.ResolverId, $"to-{endpoint.Id}", $"user.To = '{endpoint.Id}'", action: null, cancellationToken).ConfigureAwait(false);

        await EnsureRuleAsync(client, endpoint.Id, endpoint.Id, "continuation", $"user.To = '{Constants.ContinuationId}'", $"SET user.To = '{endpoint.Id}'; SET user.From = '{Constants.ContinuationId}'", cancellationToken).ConfigureAwait(false);
        await EnsureRuleAsync(client, endpoint.Id, endpoint.Id, "retry", $"user.To = '{Constants.RetryId}'", $"SET user.To = '{endpoint.Id}'; SET user.From = '{Constants.RetryId}'", cancellationToken).ConfigureAwait(false);

        await EnsureDeferredSubscriptionAsync(client, endpoint.Id, isEmulator, cancellationToken).ConfigureAwait(false);
        await EnsureDeferredProcessorSubscriptionAsync(client, endpoint.Id, cancellationToken).ConfigureAwait(false);

        foreach (var eventType in endpoint.EventTypesProduced.OrderBy(eventType => eventType.Id, StringComparer.Ordinal))
        {
            foreach (var consumer in platform
                .GetConsumers(eventType)
                .Where(consumer => !string.Equals(consumer.Id, endpoint.Id, StringComparison.Ordinal))
                .DistinctBy(consumer => consumer.Id)
                .OrderBy(consumer => consumer.Id, StringComparer.Ordinal))
            {
                await EnsureForwardSubscriptionAsync(client, endpoint.Id, consumer.Id, consumer.Id, cancellationToken).ConfigureAwait(false);
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
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> ReadConnectionStringAsync(AzureCliRunner az, TopologyOptions options, CancellationToken cancellationToken)
    {
        var names = NamingConventions.Build(options.SolutionId, options.Environment);

        await az.EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);

        return await az.CaptureValueAsync(
            new[]
            {
                "servicebus", "namespace", "authorization-rule", "keys", "list",
                "--resource-group", options.ResourceGroupName,
                "--namespace-name", names.ServiceBusNamespace,
                "--name", "RootManageSharedAccessKey",
                "--query", "primaryConnectionString",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to read the Service Bus connection string for '{names.ServiceBusNamespace}'.").ConfigureAwait(false);
    }

    private static async Task EnsureTopicAsync(ServiceBusAdministrationClient client, string topicName, bool isEmulator, CancellationToken cancellationToken)
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
        CliOutput.WriteLine($"Created topic '{topicName}'.");
    }

    private static async Task EnsureSessionSubscriptionAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        string subscriptionName,
        string? forwardTo,
        bool keepDefaultRule,
        CancellationToken cancellationToken)
    {
        var existing = await TryGetSubscriptionAsync(client, topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await client.CreateSubscriptionAsync(CreateSubscriptionOptions(topicName, subscriptionName, requiresSession: true, forwardTo), cancellationToken).ConfigureAwait(false);
            existing = await client.GetSubscriptionAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
            CliOutput.WriteLine($"Created session subscription '{subscriptionName}' on topic '{topicName}'.");
        }
        else if (!existing.RequiresSession || !ForwardToMatches(existing.ForwardTo, forwardTo))
        {
            await client.DeleteSubscriptionAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
            await client.CreateSubscriptionAsync(CreateSubscriptionOptions(topicName, subscriptionName, requiresSession: true, forwardTo), cancellationToken).ConfigureAwait(false);
            CliOutput.WriteLine($"Recreated session subscription '{subscriptionName}' on topic '{topicName}'.");
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
        CancellationToken cancellationToken)
    {
        var existing = await TryGetSubscriptionAsync(client, topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await client.CreateSubscriptionAsync(CreateSubscriptionOptions(topicName, subscriptionName, requiresSession: false, forwardTo), cancellationToken).ConfigureAwait(false);
            CliOutput.WriteLine($"Created forward subscription '{subscriptionName}' on topic '{topicName}' to '{forwardTo}'.");
        }
        else if (existing.RequiresSession || !ForwardToMatches(existing.ForwardTo, forwardTo))
        {
            await client.DeleteSubscriptionAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
            await client.CreateSubscriptionAsync(CreateSubscriptionOptions(topicName, subscriptionName, requiresSession: false, forwardTo), cancellationToken).ConfigureAwait(false);
            CliOutput.WriteLine($"Recreated forward subscription '{subscriptionName}' on topic '{topicName}' to '{forwardTo}'.");
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

    private static async Task EnsureDeferredSubscriptionAsync(ServiceBusAdministrationClient client, string topicName, bool isEmulator, CancellationToken cancellationToken)
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
            CliOutput.WriteLine($"Ensured deferred subscription '{subscriptionName}' on topic '{topicName}'.");
        }

        await DeleteRuleIfExistsAsync(client, topicName, subscriptionName, "$Default", cancellationToken).ConfigureAwait(false);
        await EnsureRuleAsync(client, topicName, subscriptionName, "DeferredFilter", "user.To = 'Deferred' AND user.OriginalSessionId IS NOT NULL", action: null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureDeferredProcessorSubscriptionAsync(ServiceBusAdministrationClient client, string topicName, CancellationToken cancellationToken)
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
            CliOutput.WriteLine($"Ensured deferred processor subscription '{subscriptionName}' on topic '{topicName}'.");
        }

        await DeleteRuleIfExistsAsync(client, topicName, subscriptionName, "$Default", cancellationToken).ConfigureAwait(false);
        await EnsureRuleAsync(client, topicName, subscriptionName, "DeferredProcessorFilter", "user.To = 'DeferredProcessor'", action: null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureRuleAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        string subscriptionName,
        string ruleName,
        string filter,
        string? action,
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
        CliOutput.WriteLine($"Ensured rule '{ruleName}' on '{topicName}/{subscriptionName}'.");
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

