using System;

namespace NimBus.Core.Messages;

/// <summary>
/// Classifies exceptions as permanent (unrecoverable) failures.
/// Permanent failures are dead-lettered immediately without consuming retry budget.
/// </summary>
public interface IPermanentFailureClassifier
{
    /// <summary>
    /// Returns true if the exception represents a permanent failure that will
    /// never succeed on retry (e.g., deserialization, validation, argument errors).
    /// </summary>
    bool IsPermanentFailure(Exception exception);
}
