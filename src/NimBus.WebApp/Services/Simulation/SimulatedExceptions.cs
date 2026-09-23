using System;
using NimBus.Core.Messages;

namespace NimBus.WebApp.Services.Simulation;

/// <summary>
/// Thrown by a simulated handler for a retryable failure. Deliberately not a NimBus
/// <c>TransientException</c>: that type abandons the message instead of taking the
/// error-response, session-block and retry path the simulation exercises.
/// </summary>
public sealed class SimulatedTransientException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SimulatedTransientException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public SimulatedTransientException() : base("Simulated transient failure") { }

    /// <summary>Creates the exception.</summary>
    public SimulatedTransientException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Thrown by a simulated handler for a poison message; classified as dead-letter.</summary>
public sealed class SimulatedPermanentException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SimulatedPermanentException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public SimulatedPermanentException() : base("Simulated poison message") { }

    /// <summary>Creates the exception.</summary>
    public SimulatedPermanentException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Dead-letters <see cref="SimulatedPermanentException"/>; everything else is retried.</summary>
public sealed class SimulatedFailureClassifier : IFailureDispositionClassifier
{
    /// <inheritdoc />
    public FailureDisposition Classify(Exception exception, string eventTypeId, string? endpointName) =>
        exception is SimulatedPermanentException ? FailureDisposition.DeadLetter : FailureDisposition.Retry;
}
