using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;

namespace NimBus.WebApp.Services;

/// <summary>
/// Short-TTL cache for read-only message-store results that back high-frequency,
/// user-facing views (Monitor wall status counts, Insights metrics aggregates).
///
/// <para>Deliberately a targeted service rather than a store decorator: operational
/// code paths (e.g. <see cref="AdminService"/> resubmit decisions) must always see
/// live store data, so callers opt in per call site. Cache below the authorization
/// layer only — keys must identify the data (endpoint id, period), never the user,
/// and cached values must not contain per-user filtering.</para>
/// </summary>
public interface IStoreResultCache
{
    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, or runs
    /// <paramref name="factory"/> (single-flight: concurrent callers share one
    /// in-flight invocation) and caches its result for <paramref name="ttl"/>.
    /// Faulted factory results are evicted immediately — exceptions propagate
    /// to every awaiting caller but are never cached.
    /// </summary>
    Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory);
}

/// <summary>
/// <see cref="IMemoryCache"/>-backed implementation. Registered as a singleton
/// (controllers are transient — a per-request instance would never hit).
/// </summary>
public sealed class StoreResultCache : IStoreResultCache
{
    private readonly IMemoryCache _cache;
    private readonly object _creationLock = new();

    public StoreResultCache(IMemoryCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        // Lazy<Task<T>> gives single-flight semantics: the first caller creates
        // and starts the task, concurrent callers await the same instance. The
        // lock only guards cache-entry creation, never the factory itself.
        Lazy<Task<T>> lazy;
        lock (_creationLock)
        {
            lazy = _cache.GetOrCreate(key, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = ttl;
                return new Lazy<Task<T>>(factory);
            });
        }

        try
        {
            return await lazy.Value;
        }
        catch
        {
            // Never cache failures — the next caller must retry the store.
            _cache.Remove(key);
            throw;
        }
    }
}
