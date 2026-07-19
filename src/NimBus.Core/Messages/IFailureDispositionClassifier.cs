using System;

namespace NimBus.Core.Messages;

/// <summary>
/// Selects how NimBus handles an exception thrown while processing an event.
/// </summary>
public interface IFailureDispositionClassifier
{
    /// <summary>
    /// Classifies a handler exception for the current event and subscriber endpoint.
    /// </summary>
    /// <param name="exception">The original exception thrown by the event handler.</param>
    /// <param name="eventTypeId">The event type identifier from the inbound message.</param>
    /// <param name="endpointName">The subscriber endpoint receiving the message.</param>
    /// <returns>The failure disposition to apply.</returns>
    FailureDisposition Classify(Exception exception, string eventTypeId, string? endpointName);
}
