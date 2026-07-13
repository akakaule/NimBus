using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore;

/// <summary>
/// Internal helpers that translate Cosmos-specific exceptions into provider-neutral
/// equivalents at the message store boundary. Consumers see only the abstractions
/// surface — they should never need to <c>using Microsoft.Azure.Cosmos;</c>.
/// </summary>
internal static class CosmosExceptionTranslation
{
    private const string TransientFailureMessage = "Cosmos DB message store is temporarily unavailable.";

    internal static bool IsTransient(CosmosException exception) => exception.StatusCode switch
    {
        HttpStatusCode.RequestTimeout => true,
        HttpStatusCode.Gone => true,
        HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.InternalServerError => true,
        HttpStatusCode.BadGateway => true,
        HttpStatusCode.ServiceUnavailable => true,
        HttpStatusCode.GatewayTimeout => true,
        (HttpStatusCode)449 => true, // Cosmos-specific Retry With response.
        _ => false,
    };

    internal static void ThrowIfTransient(CosmosException exception)
    {
        if (!IsTransient(exception))
        {
            return;
        }

        var retryAfter = exception.RetryAfter > TimeSpan.Zero
            ? exception.RetryAfter
            : null;
        if (exception.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new RequestLimitException(TransientFailureMessage, retryAfter);
        }

        // Do not attach the native exception: provider messages can contain
        // account, host, or database details that must not cross this boundary.
        throw new StorageProviderTransientException(TransientFailureMessage, retryAfter);
    }

    public static async Task<T> TranslateTransientAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (CosmosException ex)
        {
            ThrowIfTransient(ex);
            throw;
        }
    }

    public static async Task TranslateTransientAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (CosmosException ex)
        {
            ThrowIfTransient(ex);
            throw;
        }
    }

    internal static FeedIterator<T> Wrap<T>(FeedIterator<T> iterator) =>
        new TransientTranslatingFeedIterator<T>(iterator);

    private sealed class TransientTranslatingFeedIterator<T> : FeedIterator<T>
    {
        private readonly FeedIterator<T> _inner;

        public TransientTranslatingFeedIterator(FeedIterator<T> inner) => _inner = inner;

        public override bool HasMoreResults => _inner.HasMoreResults;

        public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default) =>
            TranslateTransientAsync(() => _inner.ReadNextAsync(cancellationToken));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    public static async Task<T> TranslateAsync<T>(Func<Task<T>> action, string? endpointId = null)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new EndpointNotFoundException(endpointId ?? "(unknown)", ex);
        }
        catch (CosmosException ex) when (IsTransient(ex))
        {
            ThrowIfTransient(ex);
            throw;
        }
    }

    public static async Task TranslateAsync(Func<Task> action, string? endpointId = null)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new EndpointNotFoundException(endpointId ?? "(unknown)", ex);
        }
        catch (CosmosException ex) when (IsTransient(ex))
        {
            ThrowIfTransient(ex);
            throw;
        }
    }
}
