using System;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore;

/// <summary>
/// Cosmos DB-specific transient throttling signal. Inherits from
/// <see cref="StorageProviderTransientException"/> so consumers can catch the
/// provider-neutral type and stop branching on Cosmos specifics.
/// </summary>
public class RequestLimitException : StorageProviderTransientException
{
    public RequestLimitException()
        : base("Cosmos DB request limit exceeded")
    {
    }

    public RequestLimitException(TimeSpan? retryAfter)
        : base("Cosmos DB request limit exceeded", retryAfter)
    {
    }

    public RequestLimitException(string message)
        : base(message)
    {
    }

    public RequestLimitException(string message, TimeSpan? retryAfter)
        : base(message, retryAfter)
    {
    }

    public RequestLimitException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public RequestLimitException(string message, Exception inner, TimeSpan? retryAfter)
        : base(message, inner, retryAfter)
    {
    }
}
