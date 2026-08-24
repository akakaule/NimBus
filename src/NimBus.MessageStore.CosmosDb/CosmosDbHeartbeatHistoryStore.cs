using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore;

internal sealed class CosmosDbHeartbeatHistoryStore : IHeartbeatHistoryStore
{
    private const int HeartbeatHistoryTtlSeconds = 90 * 24 * 60 * 60;
    private static readonly ItemRequestOptions SuppressContentOnWrite = new() { EnableContentResponseOnWrite = false };
    private readonly Func<Task<ICosmosContainerAdapter>> _getHeartbeatUptimeDaysContainer;
    private readonly Func<Task<ICosmosContainerAdapter>> _getHeartbeatGapsContainer;
    private readonly Func<Task<ICosmosContainerAdapter>> _getSettingsContainer;

    public CosmosDbHeartbeatHistoryStore(
        Func<Task<ICosmosContainerAdapter>> getHeartbeatUptimeDaysContainer,
        Func<Task<ICosmosContainerAdapter>> getHeartbeatGapsContainer,
        Func<Task<ICosmosContainerAdapter>> getSettingsContainer)
    {
        _getHeartbeatUptimeDaysContainer = getHeartbeatUptimeDaysContainer;
        _getHeartbeatGapsContainer = getHeartbeatGapsContainer;
        _getSettingsContainer = getSettingsContainer;
    }

    public bool PrunesHeartbeatHistoryAutomatically => true;
    // ── Durable endpoint heartbeat history ──

    public async Task<List<HeartbeatUptimeDay>> GetHeartbeatUptimeDays(DateTime fromDayUtc)
    {
        var container = await _getHeartbeatUptimeDaysContainer();
        // No ORDER BY: a multi-property ORDER BY needs a composite index the
        // container does not define (default indexing policy — both lazy SDK
        // creation and the bicep create it that way), so Cosmos rejects the
        // query with BadRequest. The result set is small (endpoints × ≤90
        // days); sort in memory instead.
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.DayUtc >= @fromDayUtc")
            .WithParameter("@fromDayUtc", fromDayUtc.Date);
        var iterator = container.GetItemQueryIterator<HeartbeatUptimeDay>(query);
        var rows = new List<HeartbeatUptimeDay>();
        while (iterator.HasMoreResults)
        {
            rows.AddRange(await iterator.ReadNextAsync());
        }

        return rows
            .OrderBy(day => day.EndpointId, StringComparer.Ordinal)
            .ThenBy(day => day.DayUtc)
            .ToList();
    }

    public async Task<bool> UpsertHeartbeatUptimeDays(IEnumerable<HeartbeatUptimeDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var container = await _getHeartbeatUptimeDaysContainer();
        foreach (var day in days)
        {
            day.Id = string.IsNullOrWhiteSpace(day.Id)
                ? $"{day.EndpointId}|{day.DayUtc:yyyy-MM-dd}"
                : day.Id;
            day.TimeToLiveSeconds = HeartbeatHistoryTtlSeconds;
            await container.UpsertItemAsync(day, new PartitionKey(day.EndpointId), SuppressContentOnWrite);
        }

        return true;
    }

    public async Task<List<HeartbeatGap>> GetHeartbeatGaps(DateTime fromUtc)
    {
        var container = await _getHeartbeatGapsContainer();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE IS_NULL(c.ToUtc) OR c.ToUtc >= @fromUtc ORDER BY c.FromUtc DESC")
            .WithParameter("@fromUtc", fromUtc);
        var iterator = container.GetItemQueryIterator<HeartbeatGap>(query);
        var rows = new List<HeartbeatGap>();
        while (iterator.HasMoreResults)
        {
            rows.AddRange(await iterator.ReadNextAsync());
        }

        return rows;
    }

    public async Task<bool> UpsertHeartbeatGaps(IEnumerable<HeartbeatGap> gaps)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        var container = await _getHeartbeatGapsContainer();
        foreach (var gap in gaps)
        {
            gap.Id = string.IsNullOrWhiteSpace(gap.Id)
                ? $"{gap.EndpointId}|{gap.FromUtc:O}"
                : gap.Id;
            gap.TimeToLiveSeconds = gap.ToUtc.HasValue ? HeartbeatHistoryTtlSeconds : -1;
            await container.UpsertItemAsync(gap, new PartitionKey(gap.EndpointId), SuppressContentOnWrite);
        }

        return true;
    }

    public async Task<bool> TryClaimHeartbeatHistoryFold(DateTime dueBefore)
    {
        var container = await _getSettingsContainer();
        try
        {
            var response = await container.ReadItemAsync<HeartbeatSettings>(
                HeartbeatSettings.SingletonId,
                new PartitionKey(HeartbeatSettings.SingletonId));
            var settings = response.Resource ?? new HeartbeatSettings();
            if (settings.LastHeartbeatFoldAtUtc.HasValue && settings.LastHeartbeatFoldAtUtc.Value > dueBefore)
            {
                return false;
            }

            settings.LastHeartbeatFoldAtUtc = DateTime.UtcNow;
            await container.UpsertItemAsync(
                settings,
                new PartitionKey(settings.Id),
                new ItemRequestOptions { IfMatchEtag = response.ETag });
            return true;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            try
            {
                await container.CreateItemAsync(
                    new HeartbeatSettings { LastHeartbeatFoldAtUtc = DateTime.UtcNow },
                    new PartitionKey(HeartbeatSettings.SingletonId));
                return true;
            }
            catch (CosmosException conflict) when (conflict.StatusCode == HttpStatusCode.Conflict)
            {
                return false;
            }
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task PruneHeartbeatHistory(DateTime cutoffUtc)
    {
        var daysContainer = await _getHeartbeatUptimeDaysContainer();
        var dayQuery = new QueryDefinition("SELECT * FROM c WHERE c.DayUtc < @cutoffDayUtc")
            .WithParameter("@cutoffDayUtc", cutoffUtc.Date);
        var dayIterator = daysContainer.GetItemQueryIterator<HeartbeatUptimeDay>(dayQuery);
        while (dayIterator.HasMoreResults)
        {
            foreach (var day in await dayIterator.ReadNextAsync())
            {
                await daysContainer.DeleteItemAsync<HeartbeatUptimeDay>(day.Id, new PartitionKey(day.EndpointId));
            }
        }

        var gapsContainer = await _getHeartbeatGapsContainer();
        var gapQuery = new QueryDefinition(
            "SELECT * FROM c WHERE NOT IS_NULL(c.ToUtc) AND c.ToUtc < @cutoffUtc")
            .WithParameter("@cutoffUtc", cutoffUtc);
        var gapIterator = gapsContainer.GetItemQueryIterator<HeartbeatGap>(gapQuery);
        while (gapIterator.HasMoreResults)
        {
            foreach (var gap in await gapIterator.ReadNextAsync())
            {
                await gapsContainer.DeleteItemAsync<HeartbeatGap>(gap.Id, new PartitionKey(gap.EndpointId));
            }
        }
    }


}
