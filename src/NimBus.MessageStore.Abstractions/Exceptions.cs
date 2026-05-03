using System;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Thrown when a query targets an endpoint whose backing storage artifacts (Cosmos
/// container, SQL rows for the endpoint, etc.) do not exist. Providers translate their
/// native not-found signals into this exception so consumers can stay provider-neutral.
/// </summary>
public class EndpointNotFoundException : Exception
{
    public string EndpointId { get; }

    public EndpointNotFoundException(string endpointId)
        : base($"Endpoint '{endpointId}' was not found in storage.")
    {
        EndpointId = endpointId;
    }

    public EndpointNotFoundException(string endpointId, Exception innerException)
        : base($"Endpoint '{endpointId}' was not found in storage.", innerException)
    {
        EndpointId = endpointId;
    }
}

/// <summary>
/// Thrown when a single message lookup misses. Providers translate their native
/// not-found signals into this exception.
/// </summary>
public class MessageNotFoundException : Exception
{
    public string EventId { get; }
    public string? MessageId { get; }

    public MessageNotFoundException(string eventId, string? messageId = null)
        : base(messageId is null
            ? $"Message for event '{eventId}' was not found in storage."
            : $"Message '{messageId}' for event '{eventId}' was not found in storage.")
    {
        EventId = eventId;
        MessageId = messageId;
    }

    public MessageNotFoundException(string eventId, string? messageId, Exception innerException)
        : base(messageId is null
            ? $"Message for event '{eventId}' was not found in storage."
            : $"Message '{messageId}' for event '{eventId}' was not found in storage.",
            innerException)
    {
        EventId = eventId;
        MessageId = messageId;
    }
}

/// <summary>
/// Thrown when the storage provider experiences a retryable failure (throttling,
/// transient connectivity, temporary deadlock). Callers may retry with backoff.
/// Providers wrap their native transient errors in this type. <see cref="RetryAfter"/>
/// is populated when the provider has signalled a recommended delay (e.g. Cosmos
/// 429 Retry-After header, SQL Server backoff hint).
/// </summary>
public class StorageProviderTransientException : Exception
{
    public TimeSpan? RetryAfter { get; }

    public StorageProviderTransientException(string message) : base(message) { }
    public StorageProviderTransientException(string message, Exception innerException) : base(message, innerException) { }
    public StorageProviderTransientException(string message, TimeSpan? retryAfter) : base(message)
    {
        RetryAfter = retryAfter;
    }
    public StorageProviderTransientException(string message, Exception innerException, TimeSpan? retryAfter)
        : base(message, innerException)
    {
        RetryAfter = retryAfter;
    }
}
