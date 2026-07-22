using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NimBus.SDK.Hosting;

/// <summary>
/// Records the <see cref="ISubscriberClient"/> instance the NimBus subscriber factory composed
/// with the inbox decorator, so startup validation can prove the effective registration is the
/// decorated one.
/// </summary>
internal sealed class InboxSubscriberComposition
{
    /// <summary>Gets or sets the subscriber client the NimBus factory composed.</summary>
    public ISubscriberClient? ComposedClient { get; set; }
}

/// <summary>
/// Fails host startup when <c>UseInbox</c> is configured but the effective
/// <see cref="ISubscriberClient"/> is not the NimBus-composed subscriber. Registration-time
/// guards only catch a custom client registered <em>before</em> <c>AddNimBusSubscriber</c>;
/// one registered <em>after</em> wins Microsoft DI's last-registration rule, so receivers would
/// use an undecorated client while the purge host and lifecycle notifier stay active —
/// silently disabling the deduplication the caller opted into.
/// </summary>
internal sealed class InboxSubscriberStartupValidator : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly InboxSubscriberComposition _composition;

    public InboxSubscriberStartupValidator(
        IServiceProvider serviceProvider,
        InboxSubscriberComposition composition)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _composition = composition ?? throw new ArgumentNullException(nameof(composition));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolving the effective client runs the NimBus factory when it is the winning
        // registration (which records itself in the composition holder); a later custom
        // registration returns that custom instance instead and the holder stays empty.
        var effectiveClient = _serviceProvider.GetRequiredService<ISubscriberClient>();
        if (!ReferenceEquals(_composition.ComposedClient, effectiveClient))
        {
            throw new InvalidOperationException(
                $"UseInbox is configured, but the effective {nameof(ISubscriberClient)} is not the subscriber " +
                "NimBus composed with the inbox decorator — a custom registration added after AddNimBusSubscriber " +
                "overrides the NimBus factory (the last registration wins), so inbox deduplication would never run " +
                $"while the inbox cleanup host stays active. Remove the custom {nameof(ISubscriberClient)} " +
                "registration, or drop UseInbox and apply inbox deduplication inside the custom subscriber composition.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
