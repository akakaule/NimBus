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

        var copiedEventIds = new HashSet<string>();
        int eventCount = await CopyDocuments(sourceEndpointContainer, targetEndpointContainer,
            BuildEventQuery(from, to, statuses), doc =>
            {
                var eid = doc["event"]?["EventId"]?.ToString();
                if (eid != null) copiedEventIds.Add(eid);
                return doc["id"]?.ToString() ?? "unknown";
            }, batchSize);

        // Copy messages
        var sourceMessagesContainer = sourceDb.GetContainer(MessagesContainer);
        var targetMessagesContainer = (await targetDb.CreateContainerIfNotExistsAsync(MessagesContainer, "/eventId")).Container;

        int messageCount = await CopyDocuments(sourceMessagesContainer, targetMessagesContainer,
            BuildMessageQuery(endpointId, from, to), doc => doc["id"]?.ToString() ?? "unknown",
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
        Func<JObject, string> getDocId,
        int? batchSize = null,
        HashSet<string> eventIdFilter = null)
    {
        int count = 0;
        using var iterator = source.GetItemQueryIterator<JObject>(query);

        while (iterator.HasMoreResults)
        {
            var batch = await iterator.ReadNextAsync();
            foreach (var doc in batch)
            {
                if (eventIdFilter != null)
                {
                    var evId = doc["eventId"]?.ToString();
                    if (evId == null || !eventIdFilter.Contains(evId))
                        continue;
                }

                doc.Remove("ttl");
                await target.UpsertItemAsync(doc);
                count++;

                if (batchSize.HasValue && count >= batchSize.Value)
                    return count;
            }

            if (batchSize.HasValue && count >= batchSize.Value)
                break;
        }

        return count;
    }
}
