using NimBus.Core.Messages;
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
}
