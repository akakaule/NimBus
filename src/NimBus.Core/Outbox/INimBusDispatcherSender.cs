using NimBus.Core.Messages;

namespace NimBus.Core.Outbox
{
    /// <summary>
    /// Marker contract for the sender used by <see cref="OutboxDispatcher"/> to forward
    /// outbox-persisted messages to the underlying transport. Transport packages
    /// register a concrete <see cref="ISender"/> that implements this interface so the
    /// dispatcher resolves it without taking a compile-time dependency on a specific
    /// transport.
    /// </summary>
    public interface INimBusDispatcherSender : ISender
    {
    }
}
