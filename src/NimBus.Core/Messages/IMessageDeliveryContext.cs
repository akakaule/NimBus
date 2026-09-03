namespace NimBus.Core.Messages;

/// <summary>
/// Exposes transport delivery metadata when it is available.
/// </summary>
public interface IMessageDeliveryContext
{
    /// <summary>
    /// Gets the one-based broker delivery count for the current message.
    /// </summary>
    int DeliveryCount { get; }
}
