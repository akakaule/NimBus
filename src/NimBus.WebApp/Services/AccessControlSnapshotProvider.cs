using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services;

/// <summary>
/// Point-in-time view of every access-control list: the site document plus all
/// endpoint-scoped documents, keyed by endpoint id.
/// </summary>
public sealed class AccessControlSnapshot
{
    public static readonly AccessControlSnapshot Empty = new(null, new Dictionary<string, AccessControlList>(StringComparer.OrdinalIgnoreCase));

    public AccessControlSnapshot(AccessControlList? site, IReadOnlyDictionary<string, AccessControlList> endpoints)
    {
        Site = site;
        Endpoints = endpoints;
    }

    public AccessControlList? Site { get; }

    public IReadOnlyDictionary<string, AccessControlList> Endpoints { get; }
}

/// <summary>
/// Caches the full ACL snapshot so per-request role resolution never hits the
/// store on the hot path. Mutating APIs call <see cref="Invalidate"/> so grants
/// and revocations take effect immediately on this instance; other instances
/// converge within the TTL (DIS accepts the same multi-instance staleness).
/// </summary>
public interface IAccessControlSnapshotProvider
{
    Task<AccessControlSnapshot> GetSnapshotAsync();

    /// <summary>Drops the cached snapshot so the next read reloads from the store.</summary>
    void Invalidate();
}

/// <summary>
/// Singleton snapshot cache (the consuming authorization service is scoped, so a
/// shorter lifetime would never hit). Store faults fail closed for store-granted
/// roles — the last-known-good snapshot is served when available, otherwise an
/// empty snapshot is cached briefly; claim-based compat grants (EIP_Management)
/// are unaffected, so configured admins are never locked out by an outage.
/// </summary>
public sealed class AccessControlSnapshotProvider : IAccessControlSnapshotProvider, IDisposable
{
    // 45s mirrors DIS's access-control cache; 5s failure TTL bounds hammering a
    // faulting store while keeping recovery quick.
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(5);

    private readonly IAccessControlStore _store;
    private readonly ILogger<AccessControlSnapshotProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private AccessControlSnapshot? _current;
    private DateTime _expiresAtUtc = DateTime.MinValue;

    public AccessControlSnapshotProvider(IAccessControlStore store, ILogger<AccessControlSnapshotProvider> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<AccessControlSnapshot> GetSnapshotAsync()
    {
        var snapshot = Volatile.Read(ref _current);
        if (snapshot != null && DateTime.UtcNow < _expiresAtUtc)
            return snapshot;

        // Single-flight: one loader refreshes, concurrent requests reuse its result.
        await _refreshLock.WaitAsync();
        try
        {
            snapshot = Volatile.Read(ref _current);
            if (snapshot != null && DateTime.UtcNow < _expiresAtUtc)
                return snapshot;

            try
            {
                var site = await _store.GetSiteAccessControl();
                var endpointDocs = await _store.GetEndpointAccessControls();
                var endpoints = new Dictionary<string, AccessControlList>(StringComparer.OrdinalIgnoreCase);
                foreach (var doc in endpointDocs)
                {
                    if (!string.IsNullOrEmpty(doc.EndpointId))
                        endpoints[doc.EndpointId] = doc;
                }

                snapshot = new AccessControlSnapshot(site, endpoints);
                Volatile.Write(ref _current, snapshot);
                _expiresAtUtc = DateTime.UtcNow + SuccessTtl;
                return snapshot;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Access-control snapshot refresh failed; store-granted roles are unavailable until the store recovers");
                // Serve stale-if-available, otherwise an empty (deny) snapshot; retry soon.
                snapshot = Volatile.Read(ref _current) ?? AccessControlSnapshot.Empty;
                Volatile.Write(ref _current, snapshot);
                _expiresAtUtc = DateTime.UtcNow + FailureTtl;
                return snapshot;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Invalidate() => _expiresAtUtc = DateTime.MinValue;

    public void Dispose() => _refreshLock.Dispose();
}
