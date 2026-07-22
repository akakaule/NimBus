# Consumer inbox

NimBus subscribers can opt into a consumer inbox that recognizes a redelivery after the same `MessageId` has already completed successfully on the same endpoint. The inbox complements the publisher outbox, but it does not make arbitrary application side effects transactional or exactly once.

The deduplication key is the `(endpoint, MessageId)` pair. NimBus fan-out forwards copies of one published message — with the same broker `MessageId` — to every subscribed endpoint, so endpoints that share one physical inbox store (one SQL table, one Cosmos container) stay isolated: one endpoint's record never turns another endpoint's first delivery into a duplicate skip. Identifiers are opaque, byte-exact keys on every provider: the durable providers key on a shared SHA-256 hash of the delimited `(endpoint, MessageId)` encoding, so ids that differ only by case or trailing whitespace are distinct everywhere (SQL Server's native `NVARCHAR` comparison would otherwise pad trailing spaces and conflate them).

## Registration

Register one inbox provider, then enable the inbox on the subscriber and select that provider:

```csharp
builder.Services.AddNimBusSqlServerInbox(connectionString);

builder.Services.AddNimBusSubscriber("BillingEndpoint", subscriber =>
{
    subscriber.AddHandler<OrderPlaced, OrderPlacedHandler>();
    subscriber.UseInbox(options =>
    {
        options.DeduplicationStore = InboxStore.SqlServer;
        options.RetentionPeriod = TimeSpan.FromDays(7);
        options.CleanupInterval = TimeSpan.FromHours(1);
    });
});
```

The inbox is opt-in. A subscriber that does not call `UseInbox` does not resolve an inbox store, add a handler decorator, or run the cleanup service.

The supported providers are:

- `AddNimBusSqlServerInbox(...)` from `NimBus.Inbox.SqlServer`;
- `AddNimBusCosmosInbox(...)` from `NimBus.MessageStore.CosmosDb`;
- `AddNimBusInMemoryInbox()` from `NimBus.Testing`, intended for tests and local harnesses.

Selecting a provider without registering its package fails during host startup with a configuration error. The in-memory provider is not a durable production store and should not be used where deduplication must survive a restart.

Enabling the inbox does not backfill IDs for messages that succeeded before deployment. A redelivery of one of those older messages can therefore run once and create its first inbox record. Plan the rollout and retention window with that compatibility boundary in mind.

## Processing order

For a usable `MessageId`, the subscriber performs these operations in order:

1. Check whether the ID has already been processed on this endpoint. The check runs before the session-state guards, so a redelivered duplicate is surfaced as a duplicate even when its session state has moved on (for example a successfully handled RetryRequest whose session is already unblocked, or an EventRequest arriving while its session is blocked by a different event).
2. If it is new, invoke the application handler.
3. Only after the handler returns successfully, record the ID.
4. Publish the normal Resolver response and settle the broker message.

A fresh delivery costs exactly one store check and one record: the hosted compositions run the pre-session-guard check in the message handler for every dispatching entry point (event, retry, and resubmission requests), and the decorator at the handler seam then only records successes.

A duplicate whose session is no longer blocked at all also triggers the deferred-sibling drain before completing — this covers a crash between the first attempt's session unblock and its deferred drain, where the redelivered duplicate is the only remaining trigger. A session blocked by an unrelated later event is left untouched; that blocker's settlement owns the drain.

Recording before the handler would be unsafe: if the handler then failed, the broker redelivery would look processed and the work would be lost. Record-on-success keeps a failed attempt eligible for redelivery. A check or record store failure also follows the transient-failure path so the broker can redeliver; a store outage is never interpreted as a duplicate.

When the check finds an existing ID, NimBus does not invoke the application handler. It emits the `OnDuplicateDetected` lifecycle signal, records bounded-cardinality telemetry, publishes a `Skipped` outcome to the Resolver with reason `DuplicateDetected`, and completes the broker message. Logs contain message identity and routing metadata, never the message payload.

Messages with a missing, blank, or longer-than-512-character `MessageId` (or a missing or longer-than-260-character endpoint identity) cannot be safely deduplicated. NimBus logs a warning and invokes the handler without consulting or updating the inbox. It never drops such work. This bypass also holds on the real Service Bus transport, whose message context throws for a `MessageId` absent from the wire — inbox processing, lifecycle notification, and diagnostic logging all read identity through non-throwing accessors.

A handler that signals a pending handoff is **not** recorded in the inbox. The pending response and the session block are established after the handler returns, and both must be recreatable if a crash forces redelivery; recording the message would turn that redelivery into a duplicate skip that never re-establishes the pending state. Pending-handoff idempotency is governed by session blocking and the handoff settlement flow instead.

## Retention and cleanup

`InboxOptions.RetentionPeriod` defaults to seven days. `CleanupInterval` defaults to one hour. The opt-in cleanup hosted service deletes a bounded batch of records older than `UtcNow - RetentionPeriod` on each pass. Cleanup propagates the host cancellation token, stops promptly during shutdown, and logs a failed pass before trying again on the next interval.

SQL Server stores records in an independently provisioned inbox table with a **nonclustered** primary key on a 32-byte identity hash — never on the raw `NVARCHAR` pair, whose trailing-space padding under SQL comparison rules would conflate distinct ids — plus the raw identifier columns for diagnostics and a clustered index on the creation timestamp for efficient bounded purges. The purge uses `READPAST` together with `READCOMMITTEDLOCK`, so it also works on databases with `READ_COMMITTED_SNAPSHOT` enabled (the Azure SQL default). Cosmos DB stores inbox documents in a dedicated container keyed by the same identity hash, uses explicit bounded cleanup with ETag-conditioned deletes (a record recreated between the purge query and the delete survives), and does not depend on container TTL. A custom `ICosmosContainerAdapter` must override the options-aware delete overload to run cleanup; the interface default fails closed with `NotSupportedException` rather than silently dropping the ETag precondition. The in-memory provider keeps timestamps in a concurrent map.

There is no numbered data migration or application-table rewrite. The SQL provider uses an idempotent inline schema bootstrap for its new table; set `SqlServerInboxOptions.AutoCreateTable` to `false` only when deployment tooling provisions the same schema ahead of time. The Cosmos provider creates its dedicated container if it is absent. Grant those create permissions during provisioning, or pre-provision the resources before processing starts.

Choose retention to exceed the longest plausible broker redelivery, replay, and operational recovery window. After a record expires, the same ID is treated as new and can run again.

## Guarantees and remaining windows

The inbox deduplicates redeliveries whose first processing attempt reached the inbox record. It deliberately does not claim exactly-once side effects:

- The handler's database transaction and the inbox write are separate. If handler side effects commit and the process or inbox write fails before the record is durable, redelivery invokes the handler again.
- Two genuinely concurrent first deliveries can both pass the check before either records the ID. Both handlers can run; the provider's record operation is idempotent so both successful writes do not fail.
- A crash after the inbox record but before broker settlement causes a redelivery, but that delivery is recognized and skipped.

Process-manager transitions must therefore remain idempotent. Derive outgoing message IDs deterministically from stable workflow identity, logical transition, and workflow version or attempt, and make application-side state transitions safe to retry. Do not use the inbox as a substitute for a transaction in the handler's own data store.

See [Orchestration](orchestration.md) for the process-manager conventions and deterministic identity guidance.

The inbox key is the `(endpoint, MessageId)` pair, not a content hash. Separate messages that reuse an ID are treated as the same logical delivery. Conversely, the current Manager resubmit flow creates a new transport message ID, so an operator-requested resubmit remains a deliberate new attempt. Any replay mechanism that preserves the original ID is skipped while its inbox record remains within retention.
