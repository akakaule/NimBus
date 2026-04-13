using System;
using System.Collections.Generic;
using System.Linq;

namespace NimBus.Core.Messages;

/// <summary>
/// Default implementation that classifies common .NET exception types as permanent failures.
/// Extend via <see cref="AddPermanentExceptionType{T}"/> or <see cref="AddPermanentExceptionNamePattern"/>.
/// </summary>
public class DefaultPermanentFailureClassifier : IPermanentFailureClassifier
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

    public bool IsPermanentFailure(Exception exception)
    {
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
