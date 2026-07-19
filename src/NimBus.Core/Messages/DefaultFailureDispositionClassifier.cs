using System;

namespace NimBus.Core.Messages;

/// <summary>
/// Preserves the legacy permanent-failure behavior by mapping permanent failures to
/// <see cref="FailureDisposition.DeadLetter"/> and all other failures to
/// <see cref="FailureDisposition.Retry"/>.
/// </summary>
public sealed class DefaultFailureDispositionClassifier : IFailureDispositionClassifier
{
#pragma warning disable CS0618
    private readonly IPermanentFailureClassifier? _permanentFailureClassifier;

    /// <summary>
    /// Initializes a classifier that optionally bridges an existing permanent-failure classifier.
    /// </summary>
    /// <param name="permanentFailureClassifier">
    /// The legacy classifier to bridge, or <c>null</c> to classify every exception as retryable.
    /// </param>
    public DefaultFailureDispositionClassifier(IPermanentFailureClassifier? permanentFailureClassifier = null)
    {
        _permanentFailureClassifier = permanentFailureClassifier;
    }
#pragma warning restore CS0618

    /// <inheritdoc />
    public FailureDisposition Classify(Exception exception, string eventTypeId, string? endpointName)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return _permanentFailureClassifier?.IsPermanentFailure(exception) == true
            ? FailureDisposition.DeadLetter
            : FailureDisposition.Retry;
    }
}
