using System.Collections.Concurrent;

namespace Erp.Api.HandoffMode;

// In-process registry of handoff jobs keyed by EventId. Singleton.
// Concurrency: ConcurrentDictionary handles the registry; DrainExpired
// snapshots and removes expired entries atomically per key so the background
// service tick and a late /api/internal/handoff-jobs POST can't double-process.
public sealed class HandoffJobTracker
{
    private readonly ConcurrentDictionary<string, HandoffJob> _jobs = new(StringComparer.Ordinal);

    public void Register(HandoffJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _jobs[job.EventId] = job;
    }

    public bool TryRemove(string eventId, out HandoffJob? job)
        => _jobs.TryRemove(eventId, out job);

    public IReadOnlyList<HandoffJob> DrainExpired(DateTime utcNow)
    {
        var drained = new List<HandoffJob>();
        foreach (var kvp in _jobs)
        {
            if (kvp.Value.DueAt <= utcNow && _jobs.TryRemove(kvp.Key, out var job))
            {
                drained.Add(job);
            }
        }
        return drained;
    }

    public IReadOnlyList<HandoffJob> GetAll()
        => _jobs.Values.ToList();
}
