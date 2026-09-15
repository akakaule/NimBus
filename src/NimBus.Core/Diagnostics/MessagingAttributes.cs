namespace NimBus.Core.Diagnostics;

/// <summary>
/// Interned attribute key constants. Aligned with OpenTelemetry messaging
/// semantic conventions v1.41 (general spans + Azure messaging). NimBus-specific
/// concepts use the <c>nimbus.*</c> namespace. Public so transport providers can
/// add transport-specific attributes on the canonical span surface.
/// </summary>
public static class MessagingAttributes
{
    // OTel messaging.* semconv 1.41 — general
    public const string System = "messaging.system";
    public const string OperationType = "messaging.operation.type";
    public const string OperationName = "messaging.operation.name";
    public const string DestinationName = "messaging.destination.name";
    public const string DestinationTemplate = "messaging.destination.template";
    public const string MessageId = "messaging.message.id";
    public const string MessageConversationId = "messaging.message.conversation_id";
    public const string MessageBodySize = "messaging.message.body.size";

    // OTel messaging.servicebus.* — Azure Service Bus specific
    public const string ServiceBusMessageDeliveryCount = "messaging.servicebus.message.delivery_count";
    public const string ServiceBusMessageEnqueuedTime = "messaging.servicebus.message.enqueued_time";
    public const string ServiceBusMessageSessionId = "messaging.servicebus.message.session.id";
    public const string ServiceBusDestinationSubscriptionName = "messaging.servicebus.destination.subscription_name";

    // NimBus-specific
    public const string NimBusEventType = "nimbus.event_type";
    public const string NimBusSessionKey = "nimbus.session.key";
    public const string NimBusOutcome = "nimbus.outcome";
    public const string NimBusPermanentFailure = "nimbus.permanent_failure";
    public const string NimBusDeliveryCount = "nimbus.delivery_count";
    public const string NimBusHandlerType = "nimbus.handler.type";
    public const string NimBusEndpoint = "nimbus.endpoint";
    public const string NimBusHasParentTrace = "nimbus.has_parent_trace";
    public const string NimBusStoreOperation = "nimbus.store.operation";
    public const string NimBusStoreProvider = "nimbus.store.provider";
    public const string NimBusAuditType = "audit_type";
    public const string NimBusDeferredBatchSize = "nimbus.deferred.batch_size";
    public const string NimBusOutboxBatchSize = "nimbus.outbox.batch_size";

    // Resolver store-retry attribution (see NimBusMeters.ResolverStoreRetry).
    /// <summary>Why the Resolver could not persist: <c>throttled</c> (Cosmos 429) or <c>transient</c> (any other transient store failure).</summary>
    public const string NimBusStoreReason = "nimbus.store.reason";
    /// <summary>What the Resolver did with the message: <c>rescheduled</c>, <c>dead_lettered</c> or <c>abandoned</c>.</summary>
    public const string NimBusRetryAction = "nimbus.retry.action";
    /// <summary>Which side chose the applied retry delay: <c>provider</c> (the store's RetryAfter hint) or <c>backoff</c> (the Resolver's exponential schedule).</summary>
    public const string NimBusDelaySource = "nimbus.delay.source";

    // OTel error attribution
    public const string ErrorType = "error.type";
}
