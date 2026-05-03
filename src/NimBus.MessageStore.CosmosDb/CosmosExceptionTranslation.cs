using System;
using System.Net;
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
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new RequestLimitException("Cosmos DB request limit exceeded", ex, ex.RetryAfter);
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
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new RequestLimitException("Cosmos DB request limit exceeded", ex, ex.RetryAfter);
        }
    }
}
