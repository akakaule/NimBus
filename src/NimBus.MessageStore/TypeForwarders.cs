// Compatibility shim for the legacy NimBus.MessageStore package. The types it
// previously contained have moved to NimBus.MessageStore.Abstractions and
// NimBus.MessageStore.CosmosDb. The forwarders below preserve compile-time
// resolution for downstream consumers that have not yet updated their package
// references. New consumers should depend on the destination packages directly.
//
// Scheduled for removal in a future major version.

using System.Runtime.CompilerServices;

// === Types now in NimBus.MessageStore.Abstractions ===

// Root namespace
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.MessageEntity))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.MessageAuditEntity))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.MessageAuditType))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.MessageFilter))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.MessageSearchResult))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.AuditFilter))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.AuditSearchResult))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.AuditSearchItem))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.EventFilter))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.EndpointRole))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.EndpointState))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.ResolutionStatus))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.UnresolvedEvent))]

// States namespace
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.EndpointStateCount))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.SessionStateCount))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.EndpointMetadata))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.EndpointSubscription))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.Heartbeat))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.HeartbeatStatus))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.TechnicalContact))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.BlockedMessageEvent))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.InvalidEvent))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.PendingMessageEvent))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.SearchResponse))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.EndpointMetricsResult))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.EndpointEventTypeCount))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.TimeSeriesResult))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.TimeSeriesBucket))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.FailedMessageInfo))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.EndpointLatencyMetricsResult))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.EndpointLatencyAggregate))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.States.LatencyAggregate))]

// === Types now in NimBus.MessageStore.CosmosDb ===

[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.ICosmosDbClient))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.CosmosDbClient))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.ICosmosClientAdapter))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.ICosmosDatabaseAdapter))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.ICosmosContainerAdapter))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.CosmosClientAdapter))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.CosmosDatabaseAdapter))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.CosmosContainerAdapter))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.MessageDocument))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.AuditDocument))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.RequestLimitException))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.MessageStoreBuilderExtensions))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.CosmosDbMessageStoreBuilderExtensions))]

// HealthChecks namespace
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.HealthChecks.CosmosDbHealthCheck))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.HealthChecks.ResolverLagHealthCheck))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.HealthChecks.ResolverLagHealthCheckOptions))]
[assembly: TypeForwardedTo(typeof(NimBus.MessageStore.HealthChecks.HealthCheckExtensions))]
