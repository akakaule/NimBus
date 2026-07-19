namespace NimBus.Core.Messages;

/// <summary>
/// Describes how NimBus handles an exception thrown by an event handler.
/// </summary>
public enum FailureDisposition
{
    /// <summary>
    /// Use the configured retry policy and existing failed-message flow.
    /// </summary>
    Retry,

    /// <summary>
    /// Dead-letter the message immediately without consuming retry budget.
    /// </summary>
    DeadLetter,

    /// <summary>
    /// Complete the message without retrying or dead-lettering it and record a skipped outcome.
    /// </summary>
    Discard,
}
