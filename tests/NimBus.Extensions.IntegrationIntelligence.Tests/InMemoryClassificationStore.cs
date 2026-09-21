using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace NimBus.Extensions.IntegrationIntelligence.Storage;

/// <summary>Test-only store; not shipped with the extension.</summary>
public sealed class InMemoryClassificationStore(TimeProvider? clock = null) : AtomicClassificationStore(clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Json, int Version)> _documents = new(StringComparer.Ordinal);

    protected override Task<(ClassificationDocument Document, string? Version)> ReadAsync(string failureId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_documents.TryGetValue(failureId, out var stored)
                ? (JsonConvert.DeserializeObject<ClassificationDocument>(stored.Json)!, (string?)stored.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : (new ClassificationDocument { FailureMessageId = failureId }, (string?)null));
        }
    }

    protected override Task<bool> TryWriteAsync(ClassificationDocument document, string? version, CancellationToken ct)
    {
        lock (_gate)
        {
            _documents.TryGetValue(document.FailureMessageId, out var stored);
            var current = stored.Version == 0 ? null : stored.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (current != version) return Task.FromResult(false);
            _documents[document.FailureMessageId] = (JsonConvert.SerializeObject(document), stored.Version + 1);
            return Task.FromResult(true);
        }
    }

    protected override async IAsyncEnumerable<ClassificationDocument> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        string[] documents;
        lock (_gate) documents = _documents.Values.Select(v => v.Json).ToArray();
        foreach (var json in documents)
        {
            ct.ThrowIfCancellationRequested();
            yield return JsonConvert.DeserializeObject<ClassificationDocument>(json)!;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
