using Azure.Messaging.ServiceBus.Administration;

namespace NimBus.ServiceBus.Provisioning;

/// <summary>
/// Puts one subscription back exactly as provisioning would have created it.
/// </summary>
/// <remarks>
/// Service Bus rejects <c>receive</c> on an auto-forwarding entity, so the only way to
/// discard such a subscription's backlog is to delete and re-provision it. This is the
/// seam that lets the WebApp's subscription admin do that without reimplementing — or
/// drifting from — <see cref="ServiceBusTopologyProvisioner"/>.
/// </remarks>
public interface ITopologyRebuilder
{
    /// <summary>
    /// Creates <paramref name="expected"/> on <paramref name="topicName"/>, or brings an
    /// existing subscription up to it, including its rules.
    /// </summary>
    Task EnsureSubscriptionAsync(
        string topicName,
        ExpectedSubscription expected,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITopologyRebuilder"/>
public sealed class ServiceBusTopologyRebuilder : ITopologyRebuilder
{
    private readonly ServiceBusAdministrationClient _client;
    private readonly Action<string> _log;

    /// <param name="client">Administration client for the target namespace.</param>
    /// <param name="log">Optional progress sink; defaults to discarding the messages.</param>
    public ServiceBusTopologyRebuilder(ServiceBusAdministrationClient client, Action<string>? log = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        // Unlike `nb topology apply`, a rebuild runs inside an HTTP request with no
        // console to write to — the caller reports the outcome to the operator.
        _log = log ?? (_ => { });
    }

    /// <inheritdoc/>
    public Task EnsureSubscriptionAsync(
        string topicName,
        ExpectedSubscription expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);

        return ServiceBusTopologyProvisioner.EnsureSubscriptionAsync(
            _client, topicName, expected, _log, cancellationToken);
    }
}
