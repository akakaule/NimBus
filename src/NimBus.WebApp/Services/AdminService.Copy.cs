using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services;

// Cosmos-only cross-account copy. Walks the source endpoint + messages
// containers via filtered queries and upserts them into the target account.
// Throws NotSupportedException via EnsureCosmosOnlyOperation when the active
// storage provider isn't Cosmos.
public partial class AdminService
{
    public async Task<CopyResult> CopyEndpointDataAsync(string endpointId, string targetConnectionString, DateTime? from, DateTime? to, List<string> statuses, int? batchSize)
    {
        EnsureCosmosOnlyOperation(nameof(CopyEndpointDataAsync));
        using var targetClient = new CosmosClient(targetConnectionString);
        var sourceDb = _rawCosmosClient!.GetDatabase(DatabaseId);
        var targetDb = targetClient.GetDatabase(DatabaseId);

        // Copy events
        var sourceEndpointContainer = sourceDb.GetContainer(endpointId);
        var targetEndpointContainer = (await targetDb.CreateContainerIfNotExistsAsync(endpointId, "/id")).Container;

        var copiedEventIds = new HashSet<string>(StringComparer.Ordinal);
        int eventCount = await CopyDocuments(sourceEndpointContainer, targetEndpointContainer,
            BuildEventQuery(from, to, statuses), doc =>
            {
                var eid = doc["event"]?["EventId"]?.ToString();
                if (eid != null) copiedEventIds.Add(eid);
            }, batchSize);

        // Copy messages
        var sourceMessagesContainer = sourceDb.GetContainer(MessagesContainer);
        var targetMessagesContainer = (await targetDb.CreateContainerIfNotExistsAsync(MessagesContainer, "/eventId")).Container;

        int messageCount = await CopyDocuments(sourceMessagesContainer, targetMessagesContainer,
            BuildMessageQuery(endpointId, from, to), onDocumentCopied: null,
            batchSize, copiedEventIds);

        return new CopyResult { EventsCopied = eventCount, MessagesCopied = messageCount };
    }

    private static QueryDefinition BuildEventQuery(DateTime? from, DateTime? to, List<string> statuses)
    {
        var conditions = new List<string> { "(NOT IS_DEFINED(c.deleted) OR c.deleted != true)" };
        if (from.HasValue) conditions.Add("c.event.EnqueuedTimeUtc >= @from");
        if (to.HasValue) conditions.Add("c.event.EnqueuedTimeUtc <= @to");
        if (statuses.Count > 0) conditions.Add("ARRAY_CONTAINS(@statuses, c.status)");

        var query = new QueryDefinition("SELECT * FROM c WHERE " + string.Join(" AND ", conditions));
        if (from.HasValue) query = query.WithParameter("@from", from.Value.ToString("O"));
        if (to.HasValue) query = query.WithParameter("@to", to.Value.ToString("O"));
        if (statuses.Count > 0) query = query.WithParameter("@statuses", statuses);

        return query;
    }

    private static QueryDefinition BuildMessageQuery(string endpointId, DateTime? from, DateTime? to)
    {
        var conditions = new List<string> { "c.endpointId = @endpointId" };
        if (from.HasValue) conditions.Add("c.message.EnqueuedTimeUtc >= @from");
        if (to.HasValue) conditions.Add("c.message.EnqueuedTimeUtc <= @to");

        var query = new QueryDefinition("SELECT * FROM c WHERE " + string.Join(" AND ", conditions))
            .WithParameter("@endpointId", endpointId);
        if (from.HasValue) query = query.WithParameter("@from", from.Value.ToString("O"));
        if (to.HasValue) query = query.WithParameter("@to", to.Value.ToString("O"));

        return query;
    }

    private static async Task<int> CopyDocuments(
        Microsoft.Azure.Cosmos.Container source,
        Microsoft.Azure.Cosmos.Container target,
        QueryDefinition query,
        Action<JObject>? onDocumentCopied,
        int? batchSize = null,
        ISet<string>? eventIdFilter = null)
    {
        int count = 0;
        using var iterator = source.GetItemQueryIterator<JObject>(query);

        while (iterator.HasMoreResults)
        {
            var batch = await iterator.ReadNextAsync();
            int? remaining = batchSize.HasValue ? batchSize.Value - count : null;
            count += await CopyDocumentBatchAsync(
                batch,
                async doc => { await target.UpsertItemAsync(doc); },
                onDocumentCopied,
                remaining,
                eventIdFilter);

            if (batchSize.HasValue && count >= batchSize.Value)
                return count;
        }

        return count;
    }

    internal static async Task<int> CopyDocumentBatchAsync(
        IEnumerable<JObject> documents,
        Func<JObject, Task> upsertDocument,
        Action<JObject>? onDocumentCopied,
        int? batchSize,
        ISet<string>? eventIdFilter)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(upsertDocument);

        if (batchSize is <= 0)
        {
            return 0;
        }

        var count = 0;
        foreach (var doc in documents)
        {
            if (eventIdFilter != null)
            {
                var eventId = doc["eventId"]?.ToString();
                if (eventId == null || !eventIdFilter.Contains(eventId))
                {
                    continue;
                }
            }

            doc.Remove("ttl");
            await upsertDocument(doc);
            onDocumentCopied?.Invoke(doc);
            count++;

            if (batchSize.HasValue && count >= batchSize.Value)
            {
                break;
            }
        }

        return count;
    }
}
