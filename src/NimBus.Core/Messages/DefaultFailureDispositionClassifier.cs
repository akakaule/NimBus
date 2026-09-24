using System;

namespace NimBus.Core.Messages;

/// <summary>
/// The classifier used when none is registered: every handler failure is
/// <see cref="FailureDisposition.Retry"/>.
/// </summary>
public sealed class DefaultFailureDispositionClassifier : IFailureDispositionClassifier
{
    /// <inheritdoc />
    public FailureDisposition Classify(Exception exception, string eventTypeId, string? endpointName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return FailureDisposition.Retry;
    }
}
