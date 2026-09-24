using System;
using System.Collections.Generic;
using System.Linq;

namespace NimBus.Core.Messages;

/// <summary>
/// Failure disposition classifier that dead-letters common permanent .NET exception types and
/// retries everything else. Extend via <see cref="AddPermanentExceptionType{T}"/> or
/// <see cref="AddPermanentExceptionNamePattern"/>, and register it with
/// <c>ConfigurePermanentFailureClassifier</c> or <c>WithFailureDispositions</c>.
/// </summary>
public class DefaultPermanentFailureClassifier : IFailureDispositionClassifier
{
    private readonly List<Type> _permanentTypes = new()
    {
        typeof(FormatException),
        typeof(InvalidCastException),
        typeof(ArgumentException),          // includes ArgumentNullException, ArgumentOutOfRangeException
        typeof(NotSupportedException),
    };

    private readonly List<string> _permanentNamePatterns = new()
    {
        "Serialization",      // JsonSerializationException, SerializationException
        "Deserialization",
        "Validation",         // ValidationException, FluentValidation, etc.
    };

    /// <inheritdoc />
    public FailureDisposition Classify(Exception exception, string eventTypeId, string? endpointName)
        => IsPermanentFailure(exception) ? FailureDisposition.DeadLetter : FailureDisposition.Retry;

    /// <summary>
    /// Returns true if the exception represents a permanent failure that will never succeed on
    /// retry (for example deserialization, validation or argument errors).
    /// </summary>
    public bool IsPermanentFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var exType = exception.GetType();

        if (_permanentTypes.Any(t => t.IsAssignableFrom(exType)))
            return true;

        var typeName = exType.Name;
        if (_permanentNamePatterns.Any(p => typeName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    /// <summary>
    /// Registers an additional exception type as a permanent failure.
    /// Uses IsAssignableFrom, so derived types are also matched.
    /// </summary>
    public DefaultPermanentFailureClassifier AddPermanentExceptionType<T>() where T : Exception
    {
        _permanentTypes.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Registers a pattern that matches against the exception type name.
    /// For example, "Timeout" would match "SqlTimeoutException".
    /// </summary>
    public DefaultPermanentFailureClassifier AddPermanentExceptionNamePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Pattern cannot be null, empty, or whitespace.", nameof(pattern));

        if (_permanentNamePatterns.Any(p => string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase)))
            return this;

        _permanentNamePatterns.Add(pattern);
        return this;
    }
}
