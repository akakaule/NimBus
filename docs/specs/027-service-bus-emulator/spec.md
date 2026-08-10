# Spec 027 — NimBus Service Bus Emulator

| | |
|---|---|
| **Status** | Draft for implementation handover |
| **Date** | 2026-08-09 |
| **Depends on** | Azure.Messaging.ServiceBus 7.20.1 (repo pin; floor for emulator admin support). Compatibility is *claimed* only for 7.20.x, which the research verified (7.20.2) and TST-3 pins; newer SDK versions must pass TST-3 before the claim extends. Aspire 13.4.6, .NET 10 |
| **Deliverables** | `src/NimBus.ServiceBusEmulator/`, `src/NimBus.ServiceBusEmulator.AspireHosting/`, `tests/NimBus.ServiceBusEmulator.Tests/`, AppHost integration |

A local, in-process Azure Service Bus emulator that the **unmodified** Azure Service Bus SDK talks to, so the whole NimBus stack (provisioner, SDK hosts, Resolver, WebApp, CLI) runs under Aspire with **zero NimBus code changes**. It implements exactly the Service Bus surface NimBus uses — nothing more — and replaces the official Microsoft emulator, whose two blocking limitations are documented below.

Feasibility is **already validated**: a prototype AMQP.Net Lite broker completed a full `SendMessageAsync` round-trip (SASL → CBS → attach → transfer → disposition) against the stock 7.20.x SDK during the research for this spec. The non-obvious wire details discovered there are captured as hard requirements in §6–§7.

---

## 1. Motivation

NimBus local development today needs a **real Azure namespace** (user-secret connection string) for the main AppHost. The `CrmErpDemo` sample can run the official Microsoft emulator (`mcr` image 2.0.0) behind a flag, and that experience exposed the exact problems this spec eliminates:

1. **No usable admin plane.** The official emulator's HTTP admin endpoint lives on a separate container port (5300), which the SDK's connection-string URL synthesis never finds — so `ServiceBusAdministrationClient` needs a second, hand-built connection string, and Aspire doesn't expose it (aspire#14041). Consequences in this repo:
   - `ServiceBusTopologyProvisioner` cannot run → the demo AppHost **skips the provisioner entirely** in emulator mode (`samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs:104-107`) and pre-declares topology via a static `UserConfig` JSON (`EmulatorTopologyConfigBuilder.cs`, 376 lines) that must stay byte-identical with the provisioner — a second source of truth the code itself flags as a risk.
   - Every WebApp operator surface backed by `IServiceBusManagement` (topic/subscription listing, runtime counts, pause/resume, purge, rebuild — `docs/service-bus-subscription-admin.md`) degrades or breaks. This is the largest untested surface in local dev.
2. **Warm-up flakiness.** Emulator 2.0.0 drops AMQP connections and throws `MessagingEntityNotFound` during warm-up (documented in `samples/CrmErpDemo/e2e/demo/README.md:16-17` and `docs/demo-video-script.md:31`); the demo-video harness must use a real namespace because of it.
3. **Operational weight.** Two containers (emulator + SQL Server 2022 backing store), static topology requiring container restart on change, 100 MB entity cap (provisioner wants 5120), 50-entity quota, a 1-hour max TTL, no persistence, no official support. (Note on TTL: this emulator imposes **no** TTL cap, but NimBus's own `IsEmulator` detection keys off `UseDevelopmentEmulator=true` and still *provisions* the 1-hour `Deferred` TTL locally — a retained, acceptable local-dev behavior, see ASP-3.)

A purpose-built emulator running as a plain .NET Aspire project resource fixes all three: one process, one connection string, both planes on one port, the provisioner runs unchanged, and topology/TTL/quota constraints disappear.

### Why the SDK cooperates

Since 7.20.1, both SDK clients are emulator-aware off a single connection string:

- `UseDevelopmentEmulator=true` → `ServiceBusClient` uses **plain TCP** (no TLS) and honors a **custom port** in the endpoint (verified empirically against 7.20.2; the "port is fixed at 5672" claim in emulator-installer issue #139 is false for the current .NET SDK).
- `ServiceBusAdministrationClient` switches to **plain HTTP** on the same host/port when the flag is present (7.20.1 changelog fixed custom-port handling).

Both planes derive host *and port* from the same `Endpoint=` value, so a broker that serves AMQP and HTTP **on one TCP port** needs no second connection string. That is the cornerstone decision of this design (§5.1).

---

## 2. Goals and non-goals

### Goals

- **G1** — The unmodified Azure SDK (data + admin planes) works against the emulator with a single connection string: `Endpoint=sb://localhost:{port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=<any>;UseDevelopmentEmulator=true`.
- **G2** — Zero changes to NimBus product code. `ServiceBusTopologyProvisioner.ApplyAsync` provisions the full topology at runtime; a second apply is a **zero-churn no-op** (the byte-identical rule round-trip invariant, §7.1).
- **G3** — Full NimBus feature coverage: session-ordered processing, rules with SET actions, auto-forwarding, request/reply, NimBus-level deferral (the `Deferred` subscription flow — plain routing, no SB defer), scheduling, purge/drain, subscription admin, runtime counts.
- **G4** — First-class Aspire resource: `AddNimBusServiceBusEmulator()` in the main AppHost, provisioner **not** skipped, WebApp admin screens fully functional locally.
- **G5** — Deterministic and fast: no warm-up flakiness, instant start, suitable for e2e tests (Playwright suites, `07-agent-enrichment` and future ones).

### Non-goals (out of scope permanently unless revisited)

- AMQP transactions / `TransactionScope` / send-via (NimBus has zero usage; the send-then-complete in `MessageContext.ScheduleRedelivery` is deliberately non-atomic).
- Duplicate detection **enforcement** (`RequiresDuplicateDetection` is never set; the `DuplicateDetectionHistoryTimeWindow` property must merely round-trip).
- Partitioned entities, geo-DR, autoscale, quotas, large-message support beyond the configured max, JMS, AMQP WebSockets (NimBus never sets `TransportType`; SDK default is AmqpTcp).
- Entra ID / real SAS signature validation (accept-all auth; see §6.2, §8.4).
- SB message deferral (`receive-by-sequence-number`, defer disposition, deferred peek state) — dead code in NimBus, rejected loudly; see §3.
- Performance/load testing fidelity. Correct under concurrency, not benchmarked.
- A management UI. The NimBus WebApp *is* the management UI and it works against this emulator.

### Explicitly deferred (P2 — implement only if needed)

- Queue entities (NimBus is 100 % topics/subscriptions; the ATOM endpoints for queues may return 501 initially — but see Open Question OQ-2).
- `CorrelationRuleFilter` (zero occurrences in `src/`).
- DLQ **browsing** via `{entity}/$DeadLetterQueue` receive links (`SubQueue.DeadLetter` has zero occurrences; DLQ *counts* are P0 via runtime properties, DLQ storage itself is P0 because max-delivery-count overflow must land somewhere).
- AMQP rule management ops (`add-rule`/`remove-rule`/`enumerate-rules` — only `ServiceBusRuleManager` uses them; NimBus does rule CRUD over HTTP).
- `batch-delete-messages`, `get-message-sessions` management ops.
- SQL Server / SQLite persistence providers (§9).

---

## 3. Resolved: the SB defer API is dead code — out of scope

The `DeferMessageAsync`/`ReceiveDeferredMessage*` call sites that exist in `src/` are **not reachable from any live flow**; NimBus's deferral is its own mechanism (regular messages routed to the `Deferred` subscription and replayed by `DeferredMessageProcessor` — no SB defer involved). Verified on current `master`:

- **Write path dead.** The blocked-session flow calls `DeferMessageToSubscription` (`StrictMessageHandler.cs:206` → `SendToDeferredSubscription` + `Complete`). The private `DeferMessage` wrapper (`StrictMessageHandler.cs:492`) is the *only* production caller of `IMessageContext.Defer` (the real SB API, `MessageContext.cs:319` → `ServiceBusSession.cs:107-115`) and is itself **never called**; `DeferOnly` has zero production callers. Only unit tests exercise them.
- **Read paths are legacy-drain only.** `ReceiveNextDeferred(WithPop)` and `IsSessionBlocked` iterate `SessionState.DeferredSequenceNumbers`, whose only writer is the dead `Defer` (`MessageContext.cs:325`). On a fresh environment the list is always empty, so `ReceiveDeferredMessagesAsync` never hits the wire.
- **Admin/purge/CLI `State == Deferred` branches** (`AdminService.Purge.cs`, `SubscriptionAdminService.cs:410`, `Endpoint.cs`) are defensive compatibility for legacy state in real Azure namespaces. A fresh emulator namespace can never contain an SB-deferred message, so these branches simply never trigger there.

**Consequences for the emulator** (all requirements formerly tagged `[DEFER]` are removed):

- No deferred-message store. `receive-by-sequence-number` → `statusCode` 501. `update-disposition` supports `completed`/`abandoned`/`suspended` only; the defer disposition (`modified` with `undeliverable-here=true`) and a `"defered"` disposition-status are rejected with a clear error naming this section — so any future reintroduction of SB defer in NimBus fails loudly instead of silently misbehaving.
- Peek returns active messages only; `x-opt-message-state` may be omitted (SDK default is `Active`), keeping all purge-path `State` branches on their no-op arms.

**Follow-up recommendation for NimBus** (separate change, not part of this spec): mark the dead surface — `IMessageContext.Defer`/`DeferOnly`/`ReceiveNextDeferred*`/`RestoreNextDeferred` and `ServiceBusSession`'s defer members — `[Obsolete]` per repo convention, so the emulator's 501s are unreachable by construction.

---

## 4. Required surface — what NimBus actually uses

This section is the contract. Anything not listed here is intentionally unimplemented (fail loud: reject unknown operations with a clear `NotSupported` error naming this spec).

### 4.1 Data plane (all clients constructed with **default options** — no `ServiceBusClientOptions` anywhere in `src/`)

| SDK API | NimBus usage |
|---|---|
| `ServiceBusClient` ctor (conn-string or FQNS+credential) | WebApp `Startup.cs:405,410`, Resolver, CLI. Emulator path is always conn-string |
| `CreateSender(topic)` → `SendMessageAsync`, `SendMessagesAsync(IList)` | Topics only, never queues. No `CreateMessageBatchAsync`/`ServiceBusMessageBatch` — NimBus pages client-side (64 000-byte budget, `PublisherClient.cs:43`) and sends plain lists |
| `ScheduleMessageAsync` → seq no; `CancelScheduledMessageAsync(seq)` | `Sender.cs:30,33`, throttle redelivery `ServiceBusSession.cs:215` |
| `CreateSessionProcessor(topic, sub, opts)` | THE consumer path. `MaxConcurrentSessions=8`, `MaxAutoLockRenewalDuration=5m`, `SessionIdleTimeout=30s`, `PrefetchCount=0`, `AutoCompleteMessages=false`, `MaxConcurrentCallsPerSession` unset (=1) |
| `CreateProcessor(topic, sub)` non-session | DeferredProcessor trigger only, `MaxConcurrentCalls=1`, `AutoCompleteMessages=false` |
| `AcceptSessionAsync(topic, sub, sessionId)` | Always explicit id from app code; **`AcceptNextSessionAsync` never called by NimBus code** — but the **session processor calls it internally**, so next-available accept is P0 |
| `CreateReceiver(topic, sub)` | Peek/drain paths; **peeks session-enabled subscriptions from a non-session receiver** (`SubscriptionAdminService.cs:344`) |
| `ReceiveMessagesAsync(100, 5s)` / `ReceiveMessageAsync(timeout)` | Drain loops; reply receive `PublisherClient.cs:286` |
| `PeekMessagesAsync(100[, fromSequenceNumber])` | Purge/drain/CLI; paginates `last.SequenceNumber + 1`; purge paths branch on `State`, which is always `Active` here (§3) |
| Settlement | `Complete` (4 SDK surfaces: receiver, session receiver, `ProcessMessageEventArgs`, Functions `ServiceBusMessageActions`), `Abandon` (never with properties), `DeadLetter(reason, description≤4096)`. Defer/receive-deferred: dead code, excluded (§3) |
| Lock renewal | Never explicit — **only** the processors' auto-renewal (`renew-lock` / `renew-session-lock` management ops fire on the wire) |
| Session state | `GetSessionStateAsync` / `SetSessionStateAsync` — UTF-8 JSON payload; cleared with **empty `BinaryData`** on the Functions path and **null** on raw-receiver paths; null-or-empty read = fresh state |
| Message fields written | `Body`, `MessageId`, `SessionId`, `CorrelationId`, `ReplyTo`, `ReplyToSessionId`, `ContentType` (CloudEvents), `TimeToLive` (replies: 5 min), `Subject`+`To`+`PartitionKey`+`TransactionPartitionKey` (throttle-redelivery clone only), **`ScheduledEnqueueTime` — always set, usually "now"** → a past/now value must mean *immediate* enqueue |
| Application properties | ~25 keys from `UserPropertyName` + `ThrottleRetryCount`, `ReplyStatus`, `ErrorType`, `ErrorText`, `cloudEvents:*`/`ce-*`, `traceparent`/`tracestate`. Types are **mixed string/int/long and must round-trip** (readers call `?.ToString()`) |
| Received fields read | `LockToken`, `SessionId`, `Body`, `MessageId`, `CorrelationId`, `DeliveryCount`, `SequenceNumber`, `EnqueuedTime`, `ContentType`, `ReplyTo`, `ReplyToSessionId`, `ApplicationProperties`, `State`, `PartitionKey`, `TransactionPartitionKey`, `TimeToLive`, `Subject`, `To`. (`LockedUntil`, `ExpiresAt`, `DeadLetterSource`, SDK `DeadLetterReason` never read) |
| Error contract | See §7.3 — specific `ServiceBusFailureReason`s are pattern-matched and load-bearing |

### 4.2 Admin plane (`ServiceBusAdministrationClient`, default options)

| SDK API | NimBus usage |
|---|---|
| `CreateTopicAsync` | `SupportOrdering=true`, `DuplicateDetectionHistoryTimeWindow=10m`, `EnableBatchedOperations=true`, `MaxSizeInMegabytes=5120` (skipped under emulator detection — our emulator should simply accept it, making the branch moot) |
| `CreateSubscriptionAsync` | `MaxDeliveryCount=10`, `LockDuration=30s`, `EnableBatchedOperations=true`, `EnableDeadLetteringOnFilterEvaluationExceptions=true`, `RequiresSession`, conditional `ForwardTo`, conditional `DefaultMessageTimeToLive` (Deferred: 14 d) |
| `CreateRuleAsync` | `SqlRuleFilter` + optional `SqlRuleAction` only. **Never** correlation/true filters from NimBus code |
| `TopicExistsAsync`, `SubscriptionExistsAsync` | check-then-act idempotency; **no** 409 handling exists — emulator must still return 409 on duplicate create (races surface loudly, matching real service) |
| `GetSubscriptionAsync`, `GetRuleAsync`, `GetTopicAsync` | reconcile-before-create |
| `GetTopicsAsync`, `GetSubscriptionsAsync`, `GetRulesAsync` | `.AsPages()` iteration → `$skip`/`$top` paging required |
| `GetTopicsRuntimePropertiesAsync`, `GetSubscriptionsRuntimePropertiesAsync` | **collection forms only** (never per-entity). Fields consumed: topic `Name`, `SubscriptionCount`, `ScheduledMessageCount`, `SizeInBytes`; subscription `SubscriptionName`, `ActiveMessageCount`, `DeadLetterMessageCount`, `TransferMessageCount`, `TransferDeadLetterMessageCount`, `TotalMessageCount`, `AccessedAt` |
| `UpdateSubscriptionAsync`, `UpdateTopicAsync` | get → mutate → PUT-with-`If-Match:*`. Mutated: `Status` (`Active`/`ReceiveDisabled`/`SendDisabled`), `ForwardTo` (cleared with `""`, never null) |
| `DeleteSubscriptionAsync`, `DeleteRuleAsync` | provisioner reconcile + admin ops. `DeleteTopicAsync`/queues: never |
| Settings fields read back | topic `Name`,`Status`; subscription `SubscriptionName`,`TopicName`,`Status`,`RequiresSession`,`ForwardTo`; rule `Name`,`Filter`,`Action` |
| Error contract | 404 must surface as **both** `RequestFailedException(404)` (HTTP) and be tolerated where code catches `ServiceBusException/MessagingEntityNotFound` — returning proper HTTP 404 gives the SDK what it needs for both |

### 4.3 Topology shape produced by the provisioner (the emulator's primary workload)

Topics = endpoint ids + literal `Resolver`. Per endpoint topic `E`, subscriptions:
`E` (session, 3 rules), `E-reply` (session, `ReplyFilter`), `Resolver` (forward → `Resolver` topic, 2 rules incl. SET action), `Deferred` (session, TTL, filter), `DeferredProcessor` (filter), one `{consumer}` per consuming endpoint (forward → consumer, per-event-type SET-action rules), `AgentDyn-{target}` (dynamic, `dyn-{eventTypeId}` rules). `$Default` is deleted everywhere except the Resolver topic's own subscription. Exact strings in §7.1.

---

## 5. Architecture

```
                     one TCP port (Aspire-assigned)
                              │
                     ┌────────▼────────┐
                     │ TcpMultiplexer   │  first byte: 0x41 'A' → AMQP, ASCII HTTP verb → HTTP
                     └───┬─────────┬───┘
              AMQP bytes │         │ HTTP bytes
                 ┌───────▼──┐   ┌──▼───────────┐
                 │ AmqpFront│   │ AdminFront    │
                 │ (AMQP.Net│   │ (Kestrel,     │
                 │  Lite    │   │  ATOM XML +   │
                 │  listener│   │  /health)     │
                 └───────┬──┘   └──┬───────────┘
                         │         │
                 ┌───────▼─────────▼───────┐
                 │       BrokerCore         │  single source of truth, lock-free reads,
                 │  NamespaceState          │  per-entity ordered apply
                 │  ├ TopicEntity[]         │
                 │  │  └ SubscriptionEntity[]  (message log, sessions, locks, DLQ,
                 │  │       rules, scheduled, counters)
                 │  ├ FilterEngine (SQL subset, verbatim round-trip)
                 │  └ TimerWheel (lock expiry, scheduled due, TTL, session long-poll)
                 └───────────┬─────────────┘
                             │ (optional, P2)
                     ┌───────▼────────┐
                     │ IStateJournal   │  in-memory no-op (default) · SQLite · SQL Server
                     └────────────────┘
```

**Projects**

| Project | Contents |
|---|---|
| `src/NimBus.ServiceBusEmulator/` | Everything above as a library + a minimal `Program.cs` host (Kestrel bootstrap, config via env vars). Runnable stand-alone: `dotnet run --project src/NimBus.ServiceBusEmulator -- --port 5672` |
| `src/NimBus.ServiceBusEmulator.AspireHosting/` | `AddNimBusServiceBusEmulator()` extension (§10) |
| `tests/NimBus.ServiceBusEmulator.Tests/` | MSTest, `#pragma warning disable CA1707, CA2007`, conformance + fidelity suites (§11) |

**Dependencies**: `AMQPNetLite.Core` (listener APIs are public and aimed at brokers; `Microsoft.Azure.Amqp`'s listener side is `internal` and unusable). ASP.NET Core (in-box) for the admin plane. No other runtime deps in the default configuration.

**Prior art to read before coding**: [gkinsman/AlmostServiceBus](https://github.com/gkinsman/AlmostServiceBus) independently arrived at the same multiplexer + AMQP.Net Lite + delivery-tag-rewriting design and reports full Azure SDK compatibility. Do not vendor it (license/audit review first, and its scope is broader than ours), but its `EmulatorContainer` replacement for `ContainerHost` is the proven approach for the delivery-tag and batch-decoding hooks.

### 5.1 `[NET]` Network front door

- **NET-1** — Single TCP listener. Peek the first byte of each accepted connection without consuming: `0x41` (`A` of `AMQP`) → hand the (pushed-back) stream to the AMQP front-end; any ASCII HTTP method initial → hand to Kestrel (bind Kestrel to an in-process transport, e.g. `IConnectionListenerFactory` over the multiplexer, or an internal loopback forward — implementation's choice, but the socket the SDK sees is one port).
- **NET-2** — No TLS anywhere. `UseDevelopmentEmulator=true` makes both SDK clients speak plaintext.
- **NET-3** — Port is configuration (`--port` / `NIMBUS_SBEMULATOR_PORT`); default 5672 for stand-alone use, Aspire-assigned in AppHost use.
- **NET-4** — `GET /health` is a **readiness** probe, not a static 200: it returns `200 {"status":"ok"}` only once the listener is bound, the AMQP front-end is accepting connections, the admin front-end is serving, and the broker core is initialized; before that the socket is either not yet bound or returns `503`. This is what lets `WaitFor(servicebus)` genuinely eliminate provisioner warm-up races (ASP-1/ASP-2).

### 5.2 `[SEC]` Security boundary — fail closed

Plaintext transport, accept-all CBS, and header-presence-only HTTP auth are acceptable **only while loopback is guaranteed**. That must be enforced, not assumed:

- **SEC-1** — Bind `127.0.0.1`/`::1` only. Refuse to start on any non-loopback address unless the operator passes an explicit `--unsafe-listen-nonloopback` flag, which logs a prominent warning. The Aspire resource never marks the endpoint external (ASP-1).
- **SEC-2** — XML hardening on the admin plane: `DtdProcessing.Prohibit`, no external entity resolution, no `XmlResolver`; reject request bodies over 1 MiB with `413`.
- **SEC-3** — Connection/frame bounds: cap concurrent TCP connections (default 128), AMQP max-frame-size and links/sessions per connection at sane defaults; excess connections are refused, not queued unboundedly.
- **SEC-4** — Memory bounds: session state ≤ 256 KiB per session (matches Azure's order of magnitude); a configurable total stored-bytes budget (default 512 MiB) across all entities — when exceeded, reject incoming transfers with `amqp:resource-limit-exceeded` (→ SDK `QuotaExceeded`) rather than growing without limit. Bounded channels everywhere (actor mailboxes, forward pumps) with backpressure, never unbounded queues.

---

## 6. AMQP data plane

### 6.1 `[SASL]` Handshake

- **SASL-1** — Advertise mechanisms **`MSSBCBS`** and `ANONYMOUS` (and optionally `PLAIN`). The SDK registers a SASL-ANONYMOUS handler under the literal name `MSSBCBS` and **fails the connection if the server list doesn't include it** (verified empirically: offering only `ANONYMOUS` → client throws). With AMQP.Net Lite: `saslSettings.EnableMechanism("MSSBCBS", SaslProfile.Anonymous)`.

### 6.2 `[CBS]` Claims-based security

- **CBS-1** — Implement the `$cbs` node as a request/response pair. Request: `application-properties` `operation="put-token"`, `type="servicebus.windows.net:sastoken"` (accept `jwt` too), `name=<audience>`, body = amqp-value string token.
- **CBS-2** — Always accept. Reply correlating on `correlation-id` = request `message-id` with application properties **`status-code`** (int, hyphenated!) = `202` and `status-description`. Do not verify signatures. Note the audience will be `amqps://…` even on a plaintext connection (SDK signs with the TLS scheme) — irrelevant since we don't validate, but don't "sanity-check" the scheme.
- **CBS-3** — ⚠ Spelling trap: `$cbs` replies use **`status-code`**; `$management` replies use **`statusCode`**. Getting either wrong produces a bare client-side `NullReferenceException` with no diagnostic. Encode both as named constants with a comment pointing here.

### 6.3 `[LNK]` Link addressing and attach

- **LNK-1** — Entity address = attach `Target.Address` (client sender) or `Source.Address` (client receiver), **with a leading `/` to strip** (observed: `/queue.1`). Recognized shapes: `{topic}`, `{topic}/Subscriptions/{sub}`, `{entityPath}/$DeadLetterQueue` (P2 receive), `{entityPath}/$management`, `$cbs`. Entity resolution is **case-insensitive** (WebApp/CLI audits lowercase everything); names are stored and returned as created.
- **LNK-2** — Attach responses **must set `max-message-size`** (verified: omitting it → every send fails with "larger than is currently allowed (-1 bytes)"). Default 262 144; configurable to 1 048 576 ("Premium mode").
- **LNK-3** — Reject attach to a nonexistent entity with AMQP error `amqp:not-found` (→ SDK `MessagingEntityNotFound`; `PublisherClient.cs:279` turns this into the "run `nb topology apply`" hint — keep the mapping exact).
- **LNK-4** — Reject **receive** attach on a subscription with non-empty `ForwardTo`, and on `ReceiveDisabled` entities; reject **send** attach on `SendDisabled` topics. The WebApp purge guards (`SubscriptionAdminService.cs:254-276`) rely on these rejections existing.
- **LNK-5** — Lock token == delivery tag: **every outgoing delivery tag must be exactly 16 bytes, the little-endian .NET GUID** of the lock token; settlement arrives addressed by that same tag. AMQP.Net Lite assigns 4-byte counter tags by default — inject the GUID tag via the connection `IHandler` `SendDelivery` event (fires before the default assignment; only assigned `if (delivery.Tag == null)`).
- **LNK-6** — Honor `flow` frames incl. the `drain` flag (receiver batch loops use credit=100 + drain on timeout; echo drain completion with credit consumed).

### 6.4 `[SES]` Sessions

- **SES-1** — A session receiver attach carries `Source.FilterSet["com.microsoft:session-filter"]`. Value = session id string (explicit accept) or **null** (next-available — used constantly by `ServiceBusSessionProcessor`).
- **SES-2** — The attach **response** must echo the filter with the **resolved** session id string; the SDK throws `SessionFilterMissing` if absent, and treats a null value as retryable-failure.
- **SES-3** — Session lock expiry returns as link **property** `com.microsoft:locked-until-utc` = **.NET ticks** (`long`, 100 ns units since year 1 — NOT epoch ms; the per-message annotation `x-opt-locked-until` is by contrast a normal AMQP timestamp).
- **SES-4** — Explicit accept of session `S`: succeed on any **unlocked** session id, whether or not messages or state exist yet — a session is a name, not a resource that must pre-exist. This is load-bearing for request/reply: `PublisherClient` sends the request and immediately accepts its unique reply session *before the reply necessarily exists* (`PublisherClient.cs:257,276`), then waits on `ReceiveMessageAsync(timeout)` for the reply to materialize. If `S` is currently **locked by another receiver**, wait up to the client's `com.microsoft:timeout` for release, then reject attach with `com.microsoft:session-cannot-be-locked` (→ `SessionCannotBeLocked`). That contention case — not "no messages" — is how the five NimBus sites that catch it actually encounter it (e.g. `DeferredMessageProcessor` accepting a session the endpoint's own processor currently holds).
- **SES-5** — Next-available accept: pick an unlocked session with ≥1 *deliverable* (active, due, unlocked) message; if none, wait up to the client's `com.microsoft:timeout` link property (uint ms), then reject attach with `com.microsoft:timeout` (→ `ServiceTimeout`, which the session processor swallows and retries — this is its idle loop).
- **SES-6** — Session lock: one owner per (subscription, session); duration = subscription `LockDuration` (30 s); renewed via `renew-session-lock`; on expiry the session becomes acceptable elsewhere and any late settlement/state call from the old owner fails with `com.microsoft:session-lock-lost` (→ `SessionLockLost`, mapped to `TransientException` in eight `MessageContext` sites).
- **SES-7** — Within a locked session, deliver strictly FIFO by sequence number, one credit at a time as granted (the ordering guarantee of ADR-001; `MaxConcurrentCallsPerSession=1` on the client side means the broker just must not reorder).
- **SES-8** — Session state: opaque byte payload per (subscription, session), capped at 256 KiB (SEC-4). `get-session-state` → return bytes or AMQP null; `set-session-state` with null or empty **clears** it. State survives session lock cycling; a session with only state (no messages) remains explicitly acceptable (SES-4) — `AdminService.Resubmit.cs:208-215` depends on it.
- **SES-9** — Close and disconnect semantics. **Clean close** (link detach, or the owning AMQP connection closes/drops in a way the broker observes): release the session lock immediately; unsettled deliveries return to `Active` in order, their delivery count incrementing on the **next** delivery; the session is immediately re-acceptable. **Unobserved half-open connections** (no detach, no TCP close): the lock is held until `LockDuration` expiry per SES-6. Lock renewal via `renew-session-lock` may extend a session lock indefinitely past `LockDuration` (the processor renews for up to `MaxAutoLockRenewalDuration` = 5 min). Tests must cover: receiver `DisposeAsync`, abrupt TCP reset, processor stop/restart mid-session, and a session held via renewal for &gt;3× `LockDuration`.

### 6.5 `[XFER]` Transfers in

- **XFER-1** — Single messages: standard transfer to the topic node. Run the send pipeline: content stamping (sequence number, enqueued time) → rule evaluation per subscription (§7.2) → per-matching-subscription enqueue (SET actions apply to *that subscription's copy*) → hand off to the forward pump when `ForwardTo` is set (BRK-6; never a synchronous hop).
- **XFER-2** — Batched messages: message format `0x80013700` — body is a list of `Data` sections, **each one a complete encoded AMQP message**; decode and enqueue individually (a broker that ignores the format stores one opaque blob and breaks `SendMessagesAsync`). Envelope-hoisted `message-id`/`group-id`/partition-key are informational only.
- **XFER-3** — `x-opt-scheduled-enqueue-time` annotation: park until due; **a value ≤ now means enqueue immediately** — NimBus stamps `ScheduledEnqueueTime` on *every* message (`MessageHelper.cs:101`), usually "now".
- **XFER-4** — Enforce max message size (LNK-2 value) per message → `amqp:link:message-size-exceeded`; enforce nothing else (no entity quotas).
- **XFER-5** — Preserve application-property AMQP types (string/int/long) through store and redelivery.
- **XFER-6** — Messages sent to a topic with **zero matching subscriptions are dropped** (real ASB semantics; NimBus relies on rules for routing, not on send failure). `SupportOrdering=true` topics: preserve arrival order per session into each subscription.

### 6.6 `[MGMT]` Entity `$management` node operations

Request messages carry application-properties `operation`, `com.microsoft:server-timeout`, optional `associated-link-name` (ties message-scoped ops to the lock-holding link); body = amqp-value map. Responses carry **`statusCode`** (int), `statusDescription`, optional `errorCondition` (camelCase — see CBS-3). Required operations (all names prefixed `com.microsoft:`):

**A. Locks**
- **MGMT-1** `renew-lock` — body `{lock-tokens: uuid[]}` → `{expirations: timestamp[]}`. Unknown/expired token → 410 + `com.microsoft:message-lock-lost`.
- **MGMT-2** `renew-session-lock` — body `{session-id}` → `{expiration}`. Lost → 410 + `com.microsoft:session-lock-lost`.

**B. Peek**
- **MGMT-3** `peek-message` — body `{from-sequence-number, message-count[, session-id]}` → `{messages: [{message: <full encoded AMQP message bytes>}...]}`, `statusCode` 200, or **204 when empty**. Returns **active** messages in sequence order, non-destructive, no locks; `x-opt-message-state` may be omitted (SDK default `Active` — the purge paths' `State` branches stay on their no-op arms, §3). Works from non-session receivers against session subscriptions (LNK-1 note; peek is link-scoped to the subscription, not session-filtered unless `session-id` given).
- **MGMT-4** Scheduled messages **must not** appear in subscription peeks (they live topic-side; they surface in `ScheduledMessageCount` on the *topic* runtime properties instead).

**C. Scheduling**
- **MGMT-5** `schedule-message` — body `{messages: [{message: bytes, message-id, session-id?, partition-key?}...]}` → `{sequence-numbers: long[]}`. The returned value is a **scheduling identifier used only for cancellation**. At the due time the message runs the normal send pipeline (§6.5) and each subscription copy is stamped with a **new** delivery sequence number, [matching Azure](https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sequencing); the delivery TTL clock starts at **activation** (`x-opt-enqueued-time` = activation time), not at scheduling. Messages in `Scheduled` state are not subject to delivery TTL (BRK-1).
- **MGMT-6** `cancel-scheduled-message` — body `{sequence-numbers}` → 200; unknown → 404 + `com.microsoft:message-not-found`.

**D. Settlement fallback**
- **MGMT-7** `update-disposition` — body `{disposition-status: "completed"|"abandoned"|"suspended", lock-tokens[, deadletter-reason, deadletter-description, properties-to-modify, session-id]}`. This is the SDK's **fallback settlement path** after link reconnect (it retries here when a link disposition returns `Rejected`/`amqp:not-found`) — both paths (§6.7 and this) must hit the same state machine. The `"defered"` status (note the service's historical misspelling) is rejected per §3.

**E. Session state**
- **MGMT-9** `get-session-state` / `set-session-state` — per SES-8; body keys `session-id`, `session-state` (binary or null).

Unimplemented operations (`receive-by-sequence-number`, `add-rule`, `enumerate-rules`, `get-message-sessions`, `batch-delete-messages`, …) → `statusCode` 501 with a description naming this spec.

### 6.7 `[SET]` Settlement via link dispositions

| Disposition received | Meaning | Broker action |
|---|---|---|
| `accepted` | Complete | Remove message; counters update |
| `modified`, `undeliverable-here` unset | Abandon | Release lock, keep at head of session/queue order, increment delivery count on **next** delivery |
| `modified`, `undeliverable-here=true` | Defer | Reject with a clear error (§3 — SB defer is dead code in NimBus; loud failure beats silent mis-storage) |
| `rejected` (with or without error) | Dead-letter | Move to subscription DLQ. When error present: condition `com.microsoft:dead-letter`, `Info["DeadLetterReason"]`/`Info["DeadLetterErrorDescription"]` (**PascalCase**) → store and surface as application properties on the DLQ copy. Accept descriptions up to and including 4096 chars (NimBus truncates to exactly 4096, `MessageContext.cs:21-22`) |

- **SET-1** — Do **not** implement `released`-as-abandon; the modern SDK never sends it (the public protocol guide's claim that `modified` "isn't used" is wrong — trust this table, which was read from SDK source and verified live).
- **SET-2** — Settlement on an expired message lock → disposition error `com.microsoft:message-lock-lost`; on an expired session lock → `com.microsoft:session-lock-lost`.
- **SET-3** — Delivery count: incremented per delivery attempt (redelivery after abandon or lock expiry). When it **exceeds `MaxDeliveryCount`** (10) → auto-dead-letter with reason `MaxDeliveryCountExceeded`. The AMQP `header.delivery-count` on the wire is the count of **prior** attempts (0 on first delivery); the SDK adds 1 to surface `DeliveryCount`. ⚠ Verify this off-by-one against the SDK converter in M1 before freezing.

### 6.8 `[ANN]` Message annotations & properties written by the broker on delivery

`x-opt-sequence-number` (long, per-subscription monotonic — see BRK-2), `x-opt-enqueued-time` (timestamp), `x-opt-locked-until` (timestamp), `x-opt-message-state` (may be omitted — always `Active`, §3), `x-opt-deadletter-source` (DLQ deliveries, P2), `header.delivery-count`, `header.ttl` where set; session id rides standard `properties.group-id`, `ReplyToSessionId` = `properties.reply-to-group-id`, message id = `properties.message-id` (no `x-opt` for it).

---

## 7. Broker core semantics

### 7.1 `[FID]` Fidelity invariants (the two sharpest constraints in the codebase)

- **FID-1 — SQL expressions round-trip byte-identical.** `ServiceBusTopologyProvisioner.RuleMatches` (`:290-296`) compares the read-back `SqlRuleFilter.SqlExpression` / `SqlRuleAction.SqlExpression` **ordinally** against what it sent. Store the **verbatim string** as the source of truth; parse to an AST for evaluation only; never normalize whitespace, quotes, casing, or trailing semicolons (note the provisioner's own asymmetry: `ForwardAction` ends with `;`, `RedirectAction` doesn't). Violation symptom: every `nb topology apply` silently deletes and recreates every rule. Acceptance test TST-1 pins this.
- **FID-2 — `ForwardTo` round-trips as a bare entity name.** `ForwardToMatches` (`:234-252`) compares trailing path segments case-insensitively and tolerates `name`, `lowercased`, or `sb://host/name`. Returning the stored bare name is safe; anything not ending in the entity name causes delete/recreate churn on subscriptions.

Exact strings the emulator will round-trip (from `TopologyDescriptor.cs:92-114`, `$Default` = TrueFilter `1=1`):

```
user.To = '{E}'
user.To = 'Continuation'            → SET user.To = '{E}'; SET user.From = 'Continuation'
user.To = 'Retry'                   → SET user.To = '{E}'; SET user.From = 'Retry'
user.To = '{E}-reply'                                     (rule name: ReplyFilter)
user.To = 'Resolver'                → SET user.From = '{E}'
user.To = 'Deferred' AND user.OriginalSessionId IS NOT NULL
user.To = 'DeferredProcessor'
user.EventTypeId = '{et}' AND user.From IS NULL
   → SET user.From = '{p}'; SET user.EventId = newid(); SET user.To = '{c}';
```

### 7.2 `[FLT]` SQL filter/action engine

Required grammar (everything the repo emits — reject the rest with a clear parse error at rule-creation time, mirroring ASB's 400):

- Predicates: `user.<prop> = '<string literal>'`, `<expr> AND <expr>`, `user.<prop> IS NULL`, `user.<prop> IS NOT NULL`, integer-literal comparison `1=1` (for `$Default`/TrueFilter). Comparison against application properties by exact key (`user.` prefix strips to the property bag; system-property references like bare `sys.` are not needed).
- Actions: sequence of `SET user.<prop> = '<string literal>'` and `SET user.<prop> = newid()` separated by `;`, optional trailing `;`. `newid()` → new GUID string.
- Evaluation: per subscription, first stamp a *copy* of the message, evaluate the filter against the copy's application properties; on match apply the action **to that subscription's copy only**, then enqueue (then forward, if `ForwardTo`).
- `EnableDeadLetteringOnFilterEvaluationExceptions=true`: an evaluation error (e.g. type mismatch) dead-letters into that subscription's DLQ rather than dropping. (Rare with this grammar; implement as a guard, not a feature.)
- Rule fan-in follows [Azure's documented semantics](https://learn.microsoft.com/en-us/azure/service-bus-messaging/topic-filters): all matching rules **without** actions collapse into **one** copy of the message; each matching rule **with** an action produces its **own** transformed copy, stamped with a `RuleName` application property naming the matched rule. (NimBus's provisioned rule sets are filter-disjoint, so it only ever sees one match per subscription — but the emulator must match Azure here so the dual-target overlapping-rule test, TST-3, passes.)
- `$Default` (TrueFilter) is auto-created on subscription creation and deletable; the provisioner deletes it on all but one subscription.

### 7.3 `[ERR]` Error mapping (load-bearing subset)

| Emulator condition (AMQP / HTTP) | SDK `ServiceBusFailureReason` | NimBus dependence |
|---|---|---|
| `com.microsoft:session-cannot-be-locked` on attach (session locked by another receiver, SES-4) | `SessionCannotBeLocked` | treated as a graceful "nothing to do" no-op ×5 sites |
| mgmt 404 + `com.microsoft:message-not-found` | `MessageNotFound` | `cancel-scheduled-message` unknown seq (legacy deferred paths also map here but never fire, §3) |
| `com.microsoft:session-lock-lost` | `SessionLockLost` | → `TransientException` ×8 in `MessageContext` |
| `com.microsoft:message-lock-lost` | `MessageLockLost` | never caught by reason — still map correctly |
| `amqp:not-found` on attach / HTTP 404 | `MessagingEntityNotFound` | reply-subscription hint, recreate tolerance |
| `com.microsoft:timeout` | `ServiceTimeout` | session-processor idle loop (SES-5); also in the transient-restart set |
| `com.microsoft:server-busy` | `ServiceBusy` | transient-restart set — emulator should never emit it in normal operation |

The processor-restart heuristic (`NimBusReceiverHostedService.cs:389-412`) restarts after 5 recoverable errors in 2 minutes — a correctly behaving emulator (no spurious disconnects) never triggers it; that alone removes the official emulator's warm-up flakiness class.

### 7.4 `[BRK]` Message lifecycle

- **BRK-1** — Per-subscription message store; states: `Scheduled(topic-side) → Active → {Locked → (Completed|Abandoned→Active|DLQ)}`. TTL expiry applies from any non-locked **delivery** state; `Scheduled` is exempt — its identity is the cancellation sequence number, and the delivery TTL clock only starts at activation (MGMT-5).
- **BRK-2** — Sequence numbers: monotonically increasing per **topic**, stamped at enqueue into subscriptions (copies share the topic sequence number — sufficient for NimBus's `+1` peek pagination; matches ASB observable behavior closely enough. ⚠ If implementation finds the SDK exposes per-subscription gaps oddly, per-subscription counters are acceptable — nothing in NimBus compares sequence numbers across subscriptions).
- **BRK-3** — Locks: per-message GUID token, `LockDuration` (30 s) from subscription config, renewable (MGMT-1), expiry via timer wheel → back to Active + delivery-count increment on next delivery.
- **BRK-4** — TTL: message TTL = min(message `header.ttl`, subscription/topic `DefaultMessageTimeToLive` where set — Deferred sub: 14 d; replies carry 5 min). `DeadLetteringOnMessageExpiration` is never enabled → expired messages are **removed silently**.
- **BRK-5** — DLQ: per-subscription sub-queue. Inbound paths: explicit dead-letter (SET table), max-delivery-count (SET-3), filter-eval exception (FLT). Counts surface in runtime properties; receive links on `/$DeadLetterQueue` are P2.
- **BRK-6** — Auto-forward is an **asynchronous per-subscription forward pump**, not a synchronous hop. A subscription with non-empty `ForwardTo` and `Active` status has a pump that drains its messages in order and re-sends each through the target topic's full pipeline via the target actor's mailbox — never while holding the source topic's writer (BRK-9), so A→B and B→A forwarding cannot deadlock. The pump activates on: message arrival, `ForwardTo` being set or restored, and status flipping back to `Active` — this is what makes the WebApp's pause/resume work, which deliberately detaches `ForwardTo`, lets backlog **accumulate**, and re-attaches it on resume expecting the backlog to then flow (`SubscriptionAdminService.cs:163-245`). Semantics: NimBus's `user.From IS NULL` convention remains the application-level loop guard, but the broker also enforces a **hop limit of 4** (matching Azure's chained-auto-forward limit); exceeding it, or a missing or `SendDisabled` forward target, moves the message to the subscription's **transfer DLQ**. `TransferMessageCount` = messages awaiting the pump on a forwarding subscription; `TransferDeadLetterMessageCount` counts transfer-DLQ arrivals — both surface truthfully in runtime properties (the WebApp displays them split, `docs/service-bus-subscription-admin.md:18`).
- **BRK-7** — `EntityStatus`: enforce `Active`/`ReceiveDisabled`/`SendDisabled` on attach (LNK-4) and on running links (detach with `amqp:not-allowed` when status flips mid-flight is acceptable; NimBus flips status only around drains).
- **BRK-8** — `AccessedAt` per entity: bump on any data-plane operation; `CreatedAt`/`UpdatedAt` maintained but unconsumed.
- **BRK-9** — Concurrency model: one logical writer per topic (channel/actor); reads (peek, runtime props) snapshot-consistent. Cross-topic interaction happens **only** via asynchronous mailbox handoff (the forward pump, BRK-6) — no actor ever blocks awaiting another actor, so forwarding cycles cannot deadlock.

---

## 8. HTTP admin plane (ATOM/XML)

- **ADM-1** — Accept `api-version` ∈ {`2017-04`, `2021-05`, `2024-05`} (7.20.2 sends `2021-05`; 7.21+ sends `2024-05`); ignore the value.
- **ADM-2** — Routes (case-insensitive; `{t}`=topic, `{s}`=subscription, `{r}`=rule):

| Route | Verbs | Notes |
|---|---|---|
| `/{t}` | GET, PUT, DELETE | PUT create (409 if exists, unless `If-Match:*` → update). `?enrich=True` adds runtime counts |
| `/{t}/Subscriptions/{s}` | GET, PUT, DELETE | same pattern |
| `/{t}/Subscriptions/{s}/Rules/{r}` | GET, PUT, DELETE | |
| `/{t}/Subscriptions` | GET | list, `$skip`/`$top` paging, `?enrich=True` for runtime-collection |
| `/{t}/Subscriptions/{s}/Rules` | GET | list |
| `/$Resources/topics` | GET | list all topics, paging, `enrich` |
| `/$Resources/queues` | GET | return empty feed (SDK may probe; NimBus never lists queues) |
| `/{q}` queue CRUD | — | 501 + explanatory body (revisit under OQ-2) |
| `/health` | GET | NET-4 |

- **ADM-3** — Payloads: ATOM `entry` (ns `http://www.w3.org/2005/Atom`) wrapping `TopicDescription`/`SubscriptionDescription`/`RuleDescription` (ns `http://schemas.microsoft.com/netservices/2010/10/servicebus/connect`). **Build the serializer test-first against captured SDK requests** (the research probe approach): create each entity type with the SDK, snapshot the PUT bodies as test fixtures, and assert our GET responses parse back into `*Properties` objects with every §4.2 field intact. Accept lenient element order on input; emit the service's canonical order on output.
- **ADM-4** — Rule XML: `Filter` with `i:type="SqlFilter"` (`SqlExpression` verbatim — FID-1 — plus `CompatibilityLevel>20</`), `i:type="TrueFilter"` for `$Default` (expression `1=1`); `Action` absent/`EmptyRuleAction` or `i:type="SqlRuleAction"` (`SqlExpression` verbatim).
- **ADM-5** — Runtime properties are the **same GET with `enrich=True`**, adding `MessageCount`, `SizeInBytes`, `SubscriptionCount`, `AccessedAt`, `CreatedAt`, `UpdatedAt`, and `CountDetails` (`ActiveMessageCount`, `DeadLetterMessageCount`, `ScheduledMessageCount`, `TransferMessageCount`, `TransferDeadLetterMessageCount`) — exactly the fields in §4.2. Collection-with-enrich must work (`GetTopicsRuntimePropertiesAsync`, `GetSubscriptionsRuntimePropertiesAsync` are the only forms NimBus calls).
- **ADM-6** — Auth: require an `Authorization: SharedAccessSignature …` header to be *present*; do not validate the signature (audience signs `https://` even over plain http — another reason not to). Missing header → 401 (keeps accidental unauthenticated tooling honest).
- **ADM-7** — Errors: 404 with an ATOM error body for missing entities (the SDK maps to `RequestFailedException(404)` / `MessagingEntityNotFound`); 409 for duplicate create; 400 for filter parse errors. **Never 500 for a missing entity** — the SDK's retry pipeline hammers 5xx four times before surfacing.
- **ADM-8** — `PUT` update semantics: `If-Match: *` present → treat as update of `Status` and `ForwardTo` (the only fields NimBus mutates); other property changes may be accepted-and-stored. `ForwardTo` cleared by empty string. `ServiceBusSupplementaryAuthorization` headers: accept and ignore.
- **ADM-9** — `GET /$namespaceinfo`: implement as a stub (`NamespaceProperties` with `MessagingSku=Standard`); zero NimBus usage but trivially cheap insurance.

---

## 9. Storage

**Decision: in-memory is the default and the only P0 store.** Rationale:

- The provisioner runs on every AppHost start and recreates topology in seconds — durability of *topology* is free already.
- In-flight local-dev messages are transient by nature; the official emulator offers no persistence either, and nobody in this repo has asked for it.
- The broker is timer- and lock-centric; making a database the source of truth would force distributed-lock semantics onto a single-process problem. The official emulator's mandatory SQL Server side-car is one of its cited costs.

**Optional durability (P2)** via a narrow journal interface, applied write-behind from the per-topic writer (BRK-9 makes replay trivial — single-writer event log per topic):

```csharp
interface IStateJournal
{
    ValueTask AppendAsync(BrokerEvent evt, CancellationToken ct);   // EntityCreated/RuleUpserted/MessageEnqueued/Settled/...
    IAsyncEnumerable<BrokerEvent> ReplayAsync(CancellationToken ct);
    ValueTask CheckpointAsync(BrokerSnapshot snapshot, CancellationToken ct);
}
```

- **STO-1** (P2) — `SqliteStateJournal`: single file next to the AppHost run, zero infra, survives restarts. Recommended durable option if one is ever needed.
- **STO-2** (P3) — `SqlServerStateJournal`: same interface against the Aspire SQL container the AppHost already runs (schema via DbUp like `NimBus.MessageStore.SqlServer`). Only worth building if a team standardizes on inspecting broker state via SQL, or wants durability without local files. The requested "SQL Server for storage" option is thus supported by design but deliberately not the default — a database as primary store buys nothing for the local-dev use case and costs a second moving part, which is precisely the official emulator's mistake.
- **STO-3** — Default `NullStateJournal` (no-op). Store choice via config: `NIMBUS_SBEMULATOR_STORE=memory|sqlite|sqlserver`.

---

## 10. Aspire integration

- **ASP-1** — `NimBus.ServiceBusEmulator.AspireHosting` exposes:

```csharp
var servicebus = builder.AddNimBusServiceBusEmulator<Projects.NimBus_ServiceBusEmulator>("servicebus");
// IResourceWithConnectionString
```

Implemented as a **ProjectResource** (no container, no image pulls, sub-second start). Concrete contracts, since the hosting library cannot reference the AppHost's generated `Projects.*` metadata itself:
  - The extension is **generic over the project type** (`AddNimBusServiceBusEmulator<TProject>(name)` with `TProject : IProjectMetadata`), mirroring `AddProject<TProject>`; a non-generic overload taking a project path exists for exotic setups.
  - One named `tcp` endpoint (not `http` — the SDK dials raw AMQP first); **not** `IsExternal`.
  - `ConnectionStringExpression` is built from **endpoint expressions**, never hardcoded host/port: `Endpoint=sb://{ep.Property(Host)}:{ep.Property(Port)};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=nimbus-local;UseDevelopmentEmulator=true`.
  - The emulator process learns its listen port via `NIMBUS_SBEMULATOR_PORT` = `{ep.Property(TargetPort)}` injected as an environment variable on the project resource.
  - A health check polls `http://{ep.Property(Host)}:{ep.Property(Port)}/health` (the multiplexer serves HTTP on the same port); combined with NET-4's real readiness semantics, `.WaitFor(servicebus)` guarantees the provisioner only starts against a serving broker.

- **ASP-2** — `src/NimBus.AppHost/Program.cs` changes:
  1. Keep a flag like CrmErpDemo's (`UseEmulator` config / `NIMBUS_SB_EMULATOR` env; flip the default to emulator once M4 lands): emulator resource vs `AddConnectionString("servicebus")`.
  2. Fix line 64: `AzureWebJobsServiceBus` must come from `servicebus.Resource.ConnectionStringExpression`, not `builder.Configuration["ConnectionStrings:servicebus"]!` — the raw-config read is null for any resource that materializes its connection string at start. (CrmErpDemo already does this correctly at its lines 122/234/252.)
  3. **Do not skip the provisioner** — that's the point. `provisioner` runs unchanged; `.WaitFor(servicebus)` before it.
- **ASP-3** — `ServiceBusTopologyProvisioner.IsEmulator` detection (`UseDevelopmentEmulator=true` substring, `ServiceBusTopologyProvisioner.cs:77-81`) fires against our connection string too — the flag is non-negotiable (it's what makes the SDK speak plaintext), so **NimBus will still provision the 1-hour `Deferred` TTL and skip `MaxSizeInMegabytes=5120` locally**. This is explicitly retained: the emulator itself imposes no TTL or size caps and would accept the production values, but distinguishing "our emulator" from Microsoft's inside NimBus would require a NimBus code change, violating G2. Local runs losing 13 days and 23 hours of parking TTL on `Deferred` is irrelevant for dev workflows. If it ever matters, the follow-up is a NimBus-side change (e.g. an explicit `NimBus:ServiceBus:EmulatorProfile` setting overriding the substring detection) — out of this spec's scope. Document in the emulator README.
- **ASP-4** — CrmErpDemo migration (follow-up, not this spec's deliverable): swap `RunAsEmulator` for `AddNimBusServiceBusEmulator`, delete the provisioner-skip branches, and retire `EmulatorTopologyConfigBuilder` (–376 lines and a two-sources-of-truth risk). Do it only after M4's compat suite is green.
- **ASP-5** — Functions-based Resolver: the WebJobs Service Bus extension builds its `ServiceBusClient` from the same connection string via the same SDK. **Verification item for M2**: confirm the extension version bundled by `NimBus.Resolver` transitively references `Azure.Messaging.ServiceBus` ≥ 7.20.1 (custom-port emulator fix); if older, add a direct package reference to lift it.
- **ASP-6** — Stand-alone use (`dotnet run --project src/NimBus.ServiceBusEmulator -- --port 5672`) documented for non-Aspire workflows and CI service-container-style usage. Optional CLI sugar `nb emulator run` is P3.

---

## 11. Testing & acceptance

Framework: MSTest, same conventions as sibling test projects. All tests run the emulator in-process (no network flakes) except where noted.

- **TST-1 — Zero-churn provisioning (the flagship acceptance test).** Build the full `TopologyDescriptor` topology for a representative platform (reuse the test platform from `EmulatorTopologyConfigBuilderTests`), run `ServiceBusTopologyProvisioner.ApplyAsync` **twice** against the emulator; assert via the emulator's **operation log** — a structured, in-memory audit the broker keeps of every admin-plane mutation (`{verb, entityPath, kind: Create|Update|Delete, timestamp}`), exposed to tests via an internal accessor — that the second apply performed **zero creates, zero deletes, and zero mutating PUTs**. This single test pins FID-1, FID-2, ADM-3/4, and the reconcile semantics.
- **TST-2 — SDK conformance suite.** One test per requirement ID in §6–§8, written **against the public SDK** (not the emulator's internals). Every test is tagged into one of three categories that make TST-3 well-defined:
  - **`[CommonFidelity]`** — must pass identically on the emulator and real Azure: session processor consumes in order; next-available idles correctly (SES-5); explicit accept of a not-yet-existing session succeeds and receives the reply when it is published afterwards (SES-4 — start the accept *before* publishing); accept of a session locked by another receiver throws `SessionCannotBeLocked`; close/disconnect semantics (SES-9: dispose, TCP reset, processor restart, long renewal); NimBus-level deferral end-to-end (message routed to `Deferred` subscription, replayed by `DeferredMessageProcessor` idioms); schedule + cancel (unknown seq → `MessageNotFound`); scheduled activation assigns a new delivery sequence number (MGMT-5); TTL expiry; max-delivery → DLQ count; abandon → redelivery; lock expiry → redelivery; batch send format; peek pagination; forwarding with SET actions end-to-end (publish `EventTypeId=X` → arrives on consumer subscription with rewritten `From`/`EventId`/`To`); pause → backlog accumulates → resume → backlog forwards (BRK-6); overlapping-rule fan-in incl. `RuleName` stamping (§7.2); reply round-trip (`PublisherClient` semantics); status-flip guards (LNK-4); admin CRUD + runtime properties + paging; 404/409 mapping.
  - **`[EmulatorOnly]`** — verifies deliberate emulator behavior that Azure does not share: defer disposition and `receive-by-sequence-number` rejected loudly (§3 — Azure *supports* deferral, so these never run against Azure); unimplemented-operation 501s; SEC-1 loopback refusal; quota rejection (SEC-4).
  - **`[AzureDivergence]`** — a short, documented list of intentional divergences (currently: SB deferral unsupported; no quotas below SEC-4's safety bounds; auth not validated). Each entry links the spec section that justifies it. Anything divergent and *not* on this list is a bug.
- **TST-3 — Dual-target compatibility runs.** The `[CommonFidelity]` subset runs env-gated against a **real Azure namespace** (`NIMBUS_SBEMULATOR_COMPAT_CS`), following the repo's existing conformance-gate pattern for Cosmos/SQL. Divergence between targets is a red build. This is the guard against "emulator-shaped" tests that encode our own bugs.
- **TST-4 — AppHost e2e.** `samples/AspirePubSub` full cycle on the emulator: provisioner → publish → session processing → resolver tracking → WebApp shows the message; plus the previously-untestable WebApp subscription-admin flows (pause/resume/purge/rebuild) driven through `IServiceBusManagement`, exercised as a new **AspirePubSub-owned** e2e (M4 must not depend on CrmErpDemo: Playwright `07-agent-enrichment` runs against `CrmErpDemo.AppHost` and only switches to this emulator with the CrmErpDemo migration, ASP-4/M5).
- **TST-5 — No regressions.** Existing suites (`NimBus.EndToEnd.Tests`, unit tests) untouched and green; `dotnet build -c Release` clean (CS8767 warning gotcha applies to new projects — Release-build locally before pushing).

---

## 12. Milestones

| # | Scope | Exit criterion |
|---|---|---|
| **M1** | Multiplexer, SASL(`MSSBCBS`)/CBS, attach/transfer/dispositions for non-session subscriptions, in-memory entities, ATOM CRUD for topics/subscriptions/rules (fixture-driven serializer), SQL filter engine with verbatim round-trip | `SendMessageAsync`+`ReceiveMessagesAsync` round-trip; TST-1 passes |
| **M2** | Sessions (explicit + next-available + state + locks), `$management` ops A/B/E, scheduled messages, TTL, delivery counts, DLQ, status enforcement, error mapping table | Session-processor conformance tests green; ASP-5 verified |
| **M3** | `update-disposition` fallback, batch format, runtime properties incl. collection-enrich, paging, loud-rejection paths for excluded features (§3) | Full TST-2 green |
| **M4** | Aspire resource + AppHost integration + TST-3/TST-4; README + docs update (incl. fixing the stale "no local emulator path" claims in `README.md:182` and `samples/CrmErpDemo/README.md:318`) | Main AppHost runs fully local; WebApp admin screens work |
| **M5** (optional) | P2 items on demand: SQLite journal, DLQ browsing, queues, correlation filters; CrmErpDemo migration (ASP-4, incl. switching Playwright `07-agent-enrichment` to this emulator) | — |

Suggested sizing: M1–M2 are the bulk (broker core + wire fidelity); M3–M4 are wide but mechanical against this spec.

---

## 13. Risks & mitigations

| Risk | Mitigation |
|---|---|
| SDK-internal wire details drift across versions (mgmt op encodings, api-version bumps) | Pin floor 7.20.1; TST-3 dual-target runs catch divergence; ADM-1 accepts multiple api-versions |
| AMQP.Net Lite listener gaps (delivery-tag hook, drain, batch decode) | All three already identified with concrete hooks (LNK-5, LNK-6, XFER-2); prototype validated the core path; AlmostServiceBus corroborates feasibility of the remainder |
| ATOM XML fidelity — admin client silently mis-parses a hand-rolled response | ADM-3 fixture-first workflow: every serializer test round-trips through the real SDK parser |
| Off-by-one/format traps (`status-code` vs `statusCode`, ticks vs timestamps, delivery-count) | Encoded as named requirements (CBS-3, SES-3, SET-3) with dedicated tests; treat each ⚠ marker in this spec as a test case |
| Functions extension bundles an older SDK (ASP-5) | Check in M2; direct package reference lifts it |
| Emulator-only tests encode emulator bugs | TST-3 is non-negotiable before M4 sign-off |
| Session next-available semantics subtly differ from ASB under contention (8 concurrent sessions) | TST-3 includes a contended-session test; SES-5 long-poll matches observed SDK behavior |

---

## 14. Open questions

- ~~OQ-1~~ — *Resolved:* the SB defer API is dead code on `master` (§3); SB deferral is out of scope. Follow-up: `[Obsolete]`-mark the dead NimBus surface (§3).
- **OQ-2** — Queue entities: NimBus never uses queues, but non-NimBus consumers in samples (PartnerPortal, CloudEventsInterop non-NimBus consumer) use the raw SDK — verify they are topic-only too (research indicates yes). If any needs a queue, promote queues from 501 to a thin wrapper (a queue is a topic with one implicit subscription in this model).
- **OQ-3** — Default-on: after M4, should the main AppHost default to the emulator (real namespace behind the flag instead)? Recommended yes; decide at M4 review.

---

## 15. Reference index

**NimBus contract sites** (the inventory this spec is derived from): `src/NimBus.ServiceBus/Provisioning/TopologyDescriptor.cs` (all filter strings, `:50-55` byte-identical warning), `ServiceBusTopologyProvisioner.cs` (`:234-252` ForwardToMatches, `:290-296` RuleMatches, `:298-320` subscription options), `src/NimBus.ServiceBus/MessageHelper.cs` (wire format), `ServiceBusSession.cs` (settlement surfaces), `MessageContext.cs` (defer/redelivery/error mapping), `src/NimBus.SDK/Hosting/NimBusReceiverHostedService.cs` (processor options + restart heuristic), `src/NimBus.Management.ServiceBus/ServiceBusManagement.cs` + `EndpointManagement.cs` (admin ops, byte-identical warning), `src/NimBus.WebApp/Services/SubscriptionAdminService.cs` + `AdminService.Purge.cs` + `AdminService.Resubmit.cs` (drain/purge idioms), `src/NimBus.CommandLine/Endpoint.cs` (CLI twins), `samples/CrmErpDemo/CrmErpDemo.Contracts/EmulatorTopologyConfigBuilder.cs` (official-emulator precedent to retire).

**Protocol sources**: Azure SDK for .NET Service Bus source (`AmqpConnectionScope`, `AmqpCbsLink`, `AmqpMessageConverter`, `ManagementConstants`, `ServiceBusConnectionStringProperties`, `HttpRequestAndResponse`, `ServiceVersionExtensions`), SDK CHANGELOG 7.20.1, [Service Bus AMQP protocol guide](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-amqp-protocol-guide) (⚠ its settlement paragraph is wrong — see SET-1), [AMQP request/response operations](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-amqp-request-response), [official emulator overview](https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator) + [local-testing doc](https://learn.microsoft.com/en-us/azure/service-bus-messaging/test-locally-with-service-bus-emulator), [amqpnetlite listener docs](https://azure.github.io/amqpnetlite/articles/listener.html), [aspire#14041](https://github.com/microsoft/aspire/issues/14041), [AlmostServiceBus](https://github.com/gkinsman/AlmostServiceBus). Empirical verifications (custom port honored, `MSSBCBS` required, `status-code`/`statusCode` split, `max-message-size` mandatory, leading-slash addresses) were performed against Azure.Messaging.ServiceBus 7.20.2 during the research phase for this spec.
