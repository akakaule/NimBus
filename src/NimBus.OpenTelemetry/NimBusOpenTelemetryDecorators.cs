using Microsoft.Extensions.Options;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.OpenTelemetry.Instrumentation;

namespace NimBus.OpenTelemetry;

/// <summary>
/// Public factory entry points for NimBus instrumentation decorators. Transport
/// providers and storage providers call these when registering their pipelines
/// so the decorator implementation stays internal to the package.
/// </summary>
public static class NimBusOpenTelemetryDecorators
{
    /// <summary>
    /// Wraps an inner <see cref="ISender"/> with the publisher instrumentation
    /// decorator. The <paramref name="messagingSystem"/> argument determines the
    /// value of the <c>messaging.system</c> attribute on the resulting span
    /// (e.g. <c>servicebus</c>, <c>rabbitmq</c>, <c>nimbus.inmemory</c> from
    /// <see cref="NimBus.Core.Diagnostics.MessagingSystem"/>).
    /// </summary>
    public static ISender InstrumentSender(ISender inner, string messagingSystem)
        => new InstrumentingSenderDecorator(inner, messagingSystem);

    /// <summary>
    /// Wraps an inner <see cref="IMessageTrackingStore"/> with the store
    /// instrumentation decorator. The <paramref name="storeProvider"/> argument
    /// is the <c>nimbus.store.provider</c> attribute value (e.g. <c>cosmos</c>,
    /// <c>sqlserver</c>, <c>inmemory</c>). When <paramref name="options"/> is
    /// provided the decorator emits per-operation spans whenever
    /// <see cref="NimBusOpenTelemetryOptions.Verbose"/> is <c>true</c>; metrics
    /// fire unconditionally.
    /// </summary>
    public static IMessageTrackingStore InstrumentMessageTrackingStore(
        IMessageTrackingStore inner,
        string storeProvider,
        IOptionsMonitor<NimBusOpenTelemetryOptions>? options = null)
        => new InstrumentingMessageTrackingStoreDecorator(inner, storeProvider, options);
}
