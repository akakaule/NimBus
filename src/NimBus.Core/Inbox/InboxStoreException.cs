using NimBus.Core.Messages.Exceptions;

namespace NimBus.Core.Inbox;

/// <summary>
/// Represents a retryable inbox-store operation failure.
/// </summary>
/// <remarks>
/// Provider exception details are intentionally not retained so they cannot leak through
/// transport settlement or lifecycle paths.
/// </remarks>
public sealed class InboxStoreException : TransientException
{
    /// <summary>The stable, provider-neutral exception message.</summary>
    public const string SafeMessage = "Inbox store operation failed.";

    /// <summary>
    /// Initializes a new instance of the <see cref="InboxStoreException"/> class.
    /// </summary>
    public InboxStoreException()
        : base(SafeMessage)
    {
    }
}
