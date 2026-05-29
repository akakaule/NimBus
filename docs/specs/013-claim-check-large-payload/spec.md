# Feature Specification: Claim-Check Pattern — Large Payload Offload to Blob Storage

Feature Branch: `013-claim-check-large-payload`
Created: 2026-05-29
Updated: 2026-05-29
Status: Proposed
Input: User description (GitHub issue #9): "Azure Service Bus has a 256 KB (Standard) / 1 MB (Premium) message size limit. Real enterprise messages frequently exceed this. Today publishers crash on send (`MessageSizeExceededException`) or developers manually offload to Blob and pass references — error-prone and inconsistent. Claim-check automates this: payloads above a configurable threshold are written to Blob by the publisher, a small reference flows through Service Bus, and the consumer rehydrates the original payload transparently before the handler runs. New package `NimBus.Extensions.ClaimCheck`; `ClaimCheckMiddleware : IMessagePipelineBehavior` on the consumer; `IClaimCheckStore` abstraction with a default `BlobClaimCheckStore`; the publisher side hooks the send path via a decorator paralleling `OutboxSender`; an application property identifies offloaded payloads; the WebApp Event Details shows the offload with a link; the Resolver / message store records the claim-check URI; orphaned blobs are cleaned up after a retention period; resubmit-as-is reuses the blob while resubmit-with-modifications uploads a new one."

## Problem

NimBus serializes the full message payload onto the Service Bus message body inline. `MessageHelper.ToServiceBusMessage(...)` builds the body as `result.Body = new BinaryData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message.MessageContent)))` (`src/NimBus.ServiceBus/MessageHelper.cs`). On the consumer side, `MessageContext.GetContent()` reads it back with `JsonConvert.DeserializeObject<MessageContent>(Encoding.UTF8.GetString(_sbMessage.Body), …)` (`src/NimBus.ServiceBus/MessageContext.cs`). There is no size guard anywhere in the publish path.

Azure Service Bus rejects any message whose total size (body + `ApplicationProperties` + system properties) exceeds the namespace tier limit — 256 KB on Standard, 1 MB on Premium. A publisher that serializes a large event therefore fails at `ISender.Send(...)` time with `ServiceBusException` / `MessageSizeExceededException`, after the business logic that produced the event has already run. Today the only workarounds are:

- Manually offloading large fields to Blob in adapter code and publishing a hand-rolled reference — re-implemented inconsistently per adapter, with no audit trail and no standard rehydration on the consumer side.
- Splitting the event, which breaks NimBus's session-ordered, single-event semantics.

This spec adds a **claim-check** capability as an opt-in extension. When a serialized payload exceeds a configurable threshold, the publisher writes the payload bytes to Blob Storage and sends a small Service Bus message carrying only a reference application property. The consumer transparently rehydrates the payload from Blob before the handler runs, so handlers continue to receive the original strongly-typed event with no API change. The claim-check URI is recorded on the message-store audit/tracking record so the WebApp can surface it, and a background sweep deletes orphaned blobs after a retention window.

## Scope

In scope:
- A new package `src/NimBus.Extensions.ClaimCheck/` mirroring the existing `src/NimBus.Extensions.Notifications/` layout (project reference to `NimBus.Core`; namespace `NimBus.Extensions.ClaimCheck`).
- An `IClaimCheckStore` abstraction with the operations Upload / Download / Delete, plus a default `BlobClaimCheckStore` backed by `Azure.Storage.Blobs`.
- A publisher-side `ClaimCheckSender : ISender` decorator that wraps the inner `ISender` (paralleling `OutboxSender` in `src/NimBus.Core/Outbox/OutboxSender.cs`) and offloads the payload when the serialized size exceeds the threshold.
- A consumer-side `ClaimCheckMiddleware : IMessagePipelineBehavior` (`src/NimBus.Core/Extensions/IMessagePipelineBehavior.cs`) that detects the reference application property, downloads the payload, and rehydrates it before `next(...)` runs.
- A new application property `nimbus.claim_check.uri` carried on the Service Bus message identifying an offloaded payload, plus the option-driven threshold, container, retention, and authentication settings.
- Recording the claim-check URI on the message store consistent with `MessageAuditEntity` / `IMessageTrackingStore.StoreMessageAudit(...)` so the WebApp Event Details surface can render it.
- A retention sweep (`BackgroundService`) that deletes blobs older than the configured `RetentionPeriod`.
- DI wiring exposed via real NimBus registration patterns — see Open Questions for how this reconciles with the issue's proposed `.UseClaimCheck(...)` fluent API, which does not exist today.
- Documentation in `docs/claim-check.md` and a `LargePayloadEndpoint` example wired into `samples/AspirePubSub/`.

Out of scope:
- Field-level / per-property offload (`[DataBusProperty]`-style). v1 is message-level: the whole serialized `MessageContent` is offloaded or none of it is.
- Encryption of blob contents at the application layer (rely on Storage-account encryption-at-rest).
- A non-Blob store (filesystem, S3). The abstraction (`IClaimCheckStore`) leaves room for it; only `BlobClaimCheckStore` ships.
- Changing the inline-body path for sub-threshold messages — small messages flow exactly as today.
- Compression of the offloaded payload (orthogonal to claim-check; the in-process response compression of spec 009 covers the WebApp, not the bus).

## User Scenarios & Testing

### User Story 1 - Large payload is offloaded transparently (Priority: P1)

As an adapter author publishing an event whose serialized size exceeds the namespace limit, I want NimBus to automatically write the payload to Blob and send a reference, so my publish succeeds without me writing any offload code.

Why this priority: This is the core capability — without it, large events fail at send time and the feature does not exist.

Independent Test: Configure a publisher with claim-check enabled and a 200 KB threshold. Publish an event whose serialized `MessageContent` is 500 KB. Assert the Service Bus message body is small (a reference), the `nimbus.claim_check.uri` application property is set, and a blob exists at that URI containing the original payload bytes.

Acceptance Scenarios:

1. Given claim-check is enabled with `SizeThresholdBytes = 200*1024`, When a message whose serialized `MessageContent` is below the threshold is sent, Then it is published inline exactly as today and no `nimbus.claim_check.uri` property is present and no blob is written.
2. Given the same configuration, When a message whose serialized `MessageContent` exceeds the threshold is sent, Then the payload is uploaded to the configured container, the Service Bus body carries only a reference, and `nimbus.claim_check.uri` is set to the blob URI.
3. Given a batch `ISender.Send(IEnumerable<IMessage>)` where some messages exceed the threshold and some do not, Then each message is independently offloaded or inlined based on its own serialized size.

---

### User Story 2 - Consumer rehydrates the original payload (Priority: P1)

As a handler author, I want to receive the original strongly-typed event regardless of whether it was offloaded, so my handler code is identical for small and large messages.

Why this priority: Transparent rehydration is the other half of the contract; an offloaded message that the consumer cannot read is worthless.

Independent Test: Send an offloaded message, then run it through the subscriber pipeline with `ClaimCheckMiddleware` registered. Assert the handler receives an `IMessageContext` whose `MessageContent` deserializes to the original event with all fields intact.

Acceptance Scenarios:

1. Given a received message carries `nimbus.claim_check.uri`, When `ClaimCheckMiddleware.Handle(...)` runs before the terminal handler, Then it downloads the blob, makes the original payload available to `IMessageContext.MessageContent`, and calls `next(context, ct)`.
2. Given a received message has no `nimbus.claim_check.uri`, When the middleware runs, Then it is a no-op pass-through and calls `next(context, ct)` immediately.
3. Given the handler completes successfully on a rehydrated message, When settlement runs, Then the message is completed and (per retention policy) the blob is eligible for later cleanup, not deleted synchronously.

---

### User Story 3 - Missing or unreadable blob dead-letters with a clear reason (Priority: P1)

As an operator, I want a message whose backing blob is missing or unreadable to be dead-lettered with an explicit reason rather than silently dropped or infinitely retried, so the failure is visible and diagnosable.

Why this priority: A dangling reference is a hard failure that must not masquerade as a transient error or a poison-loop.

Independent Test: Send an offloaded message, delete the blob, then run the message through the pipeline. Assert the message is dead-lettered with a reason naming the claim-check URI.

Acceptance Scenarios:

1. Given a message references a blob that does not exist, When `ClaimCheckMiddleware` attempts the download, Then it calls `IMessageContext.DeadLetter(reason, exception)` with a reason such as `"ClaimCheckBlobMissing"` and the URI in the description, and does NOT call `next(...)`.
2. Given the blob exists but the download fails with a transient Storage error (throttle, timeout), When the middleware catches it, Then it surfaces the error as a transient failure so the message is abandoned and retried per the endpoint's retry policy (not dead-lettered on the first attempt).
3. Given the configured store credentials are invalid (auth failure), When the download is attempted, Then the failure is treated as transient/configuration error and logged clearly, not silently swallowed.

---

### User Story 4 - Claim-check URI is auditable in the WebApp (Priority: P2)

As an operator viewing an event in the management WebApp, I want to see that its payload was offloaded to claim-check and (if I have access) a link to it, so I can inspect the real payload during troubleshooting.

Why this priority: Visibility is important for ops but the offload/rehydrate path works without it.

Independent Test: Publish an offloaded event, let the Resolver record it, open Event Details, and assert a "Payload offloaded to claim-check" indicator and a link element are present.

Acceptance Scenarios:

1. Given an offloaded event has been tracked, When the Event Details page loads, Then it shows a "Payload offloaded to claim-check" indicator with the URI.
2. Given the viewing user has access to the blob, When they click the link, Then the URI is surfaced; given they do not, the link is hidden or disabled with no error.
3. Given a non-offloaded event, When Event Details loads, Then no claim-check indicator appears (no regression to existing rows).

---

### User Story 5 - Orphaned blobs are cleaned up after retention (Priority: P2)

As a platform operator, I want blobs from delivered/expired messages removed after a retention period, so claim-check storage does not grow unbounded.

Why this priority: Cost control and storage hygiene; not required for correctness of a single message flow.

Independent Test: Upload claim-check blobs with timestamps older than `RetentionPeriod`, run the cleanup sweep, and assert the expired blobs are deleted while in-retention blobs remain.

Acceptance Scenarios:

1. Given `RetentionPeriod = 7 days` and a blob older than 7 days, When the cleanup sweep runs, Then the blob is deleted.
2. Given a blob younger than the retention period, When the sweep runs, Then the blob is retained.
3. Given the sweep encounters a delete failure on one blob, Then it logs a warning and continues with the remaining blobs (best-effort, no crash).

---

### User Story 6 - Resubmit reuses or replaces the blob correctly (Priority: P2)

As an operator resubmitting a failed offloaded event, I want resubmit-as-is to reuse the existing blob and resubmit-with-modifications to upload a fresh blob, so the resubmitted message carries the right payload.

Why this priority: Resubmit is a core WebApp action; getting the payload wrong on resubmit corrupts the event.

Independent Test: Resubmit an offloaded event unchanged via the existing resubmit flow and assert the new message reuses the original `nimbus.claim_check.uri`. Then resubmit-with-changes with a modified, still-large payload and assert a new blob URI is written.

Acceptance Scenarios:

1. Given an offloaded event is resubmitted as-is via the manager resubmit path (`EventImplementation` → `managerClient.Resubmit(...)`), When the new message is published, Then it reuses the original blob URI (no re-upload) and the retention clock for that blob is honored.
2. Given an offloaded event is resubmitted via `PostResubmitWithChangesEventIdsAsync(...)` with a modified payload that still exceeds the threshold, When the new message is published through the claim-check sender, Then a new blob is uploaded and a new `nimbus.claim_check.uri` is set.
3. Given resubmit-with-changes produces a payload now below the threshold, When published, Then it is inlined and carries no claim-check reference.

---

## Edge Cases

- Serialized payload sits exactly on the threshold boundary. The rule is offload when `size > SizeThresholdBytes` (strictly greater); equal-to-threshold stays inline. Documented in FR-010.
- The reference message itself plus application properties must remain under the bus limit. Since the reference is a short URI, this is always true; FR-012 caps the reference representation so an oversized URI cannot reintroduce the size problem.
- Claim-check enabled on the publisher but the consumer endpoint does NOT register `ClaimCheckMiddleware`. The consumer then sees a reference body it cannot deserialize into the original event — User Story 3's dead-letter path catches this (the body is not the payload). Surfaced as a configuration warning in docs and FR-031.
- Outbox is also enabled. The outbox stores the message row, not the blob; offload must happen so that the *reference* is what the outbox persists and the dispatcher later sends. Decorator ordering is addressed in FR-021 and Open Questions.
- Two messages serialize to byte-identical payloads. Each gets its own blob (URI keyed by event/message id + guid), so independent retention and resubmit do not collide.
- Blob container does not exist on first publish. `BlobClaimCheckStore` creates it if missing (idempotent `CreateIfNotExists`), or fails fast with a clear message if creation is not permitted by the credential.
- Managed Identity configured but running locally without it. The store surfaces a clear configuration error rather than a generic auth exception (FR-040).
- A scheduled message (`ISender.ScheduleMessage`) exceeds the threshold. It is offloaded the same way; the blob must outlive the scheduled delay, so retention MUST exceed the maximum schedule horizon (Assumptions).
- Very large payload exceeding the blob single-PUT limit. `BlobClaimCheckStore` uses the SDK's streaming upload so multi-hundred-MB payloads succeed.

## Requirements

### Functional Requirements

#### Publisher-side offload

- FR-001: A new `ClaimCheckSender` class MUST implement `NimBus.Core.Messages.ISender` and decorate an inner `ISender`, mirroring `OutboxSender` (`src/NimBus.Core/Outbox/OutboxSender.cs`). It MUST forward all four `ISender` members (`Send(IMessage)`, `Send(IEnumerable<IMessage>)`, `ScheduleMessage`, `CancelScheduledMessage`).
- FR-002: For each outbound `IMessage`, `ClaimCheckSender` MUST compute the serialized payload size the same way the transport does — `JsonConvert.SerializeObject(message.MessageContent)` UTF-8 byte length — to decide whether to offload. (This matches the body built in `MessageHelper.ToServiceBusMessage(...)`.) NOTE: this means the **serialized body is already materialized as a string in managed memory at the publish boundary** (NimBus's message model serializes `MessageContent` to a JSON string for the Service Bus body — there is no streaming-serialize seam in `MessageHelper` today). The stream-based store API (FR-050) therefore avoids a *second* full buffer (re-encoding the string to a `byte[]` copy and handing it to the SDK) and streams the blob transfer, but it does NOT make the publish path zero-buffer — the realistic offload ceiling is bounded by the size of that in-memory serialized string. See the size-target reconciliation in NFR-002.
- FR-010: When the serialized payload size is strictly greater than `SizeThresholdBytes`, the sender MUST upload the serialized payload to the claim-check store via `IClaimCheckStore.UploadAsync(stream, length, …)` (wrapping the already-serialized body in a `Stream` rather than copying it to a `byte[]`), replace the message's outbound payload with a small reference body, and stamp the blob URI so the transport can carry it as `nimbus.claim_check.uri`.
- FR-011: When the size is at or below the threshold, the sender MUST forward the message to the inner sender unmodified (inline publish, identical to today).
- FR-012: The claim-check reference carried on the wire MUST be a single short URI string. The sender MUST validate that the reference plus existing application properties stay within the configured bus limit, and fail fast with a clear exception if not (defensive — should never happen for a URI).
- FR-013: `SizeThresholdBytes` MUST default to a value safely under the Standard tier (suggested `200*1024`), be operator-configurable, and be documented as needing to account for application-property overhead.

#### Application property convention

- FR-020: The offload reference MUST travel as a Service Bus application property named `nimbus.claim_check.uri`. NimBus's existing transport-level properties are set via the `UserPropertyName` enum (`ApplicationProperties[UserPropertyName.X.ToString()]` in `MessageHelper.cs`), while W3C trace context uses dotted-string header keys (`traceparent` / `tracestate`). The claim-check property follows the **dotted-string** convention (like the W3C headers) rather than the `UserPropertyName` enum, because it is an extension-owned property outside `NimBus.Core`. This MUST be a named constant in the extension. (See Open Questions — whether to instead add a `UserPropertyName` member is a core-vs-extension decision.)
- FR-021: Setting `nimbus.claim_check.uri` MUST compose correctly with the existing decorator chain. The publisher builds its sender as `instrumenting → outbox → transport` (`src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs`); the claim-check decorator MUST sit **above the outbox/transport** so the reference (not the payload) is what the outbox persists and the transport sends. Final order: `instrumenting → claim-check → outbox → transport`.

#### Consumer-side rehydration

- FR-030: A `ClaimCheckMiddleware` class MUST implement `NimBus.Core.Extensions.IMessagePipelineBehavior`. Its `Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct)` MUST run before the terminal handler so the rehydrated payload is in place when the handler executes.
- FR-031: When the received message carries `nimbus.claim_check.uri`, the middleware MUST download the payload via `IClaimCheckStore.DownloadAsync(uri, ...)` and make the original `MessageContent` available to the handler through `IMessageContext.MessageContent`. Because `MessageContext.GetContent()` deserializes directly from `_sbMessage.Body` (`src/NimBus.ServiceBus/MessageContext.cs`), the rehydration path MUST provide a mechanism for the context to return the downloaded payload instead of the inline reference body. The concrete mechanism (e.g. an override hook on the message context, or a context wrapper) is a design item — see Open Questions, FR-031a.
- FR-031a: `IMessageContext` today exposes no setter or override for the deserialized body (`MessageContent` is computed from the raw `_sbMessage.Body`). The implementation MUST add a minimal, backward-compatible rehydration hook (e.g. an interface member with a default no-op, paralleling how `ParentTraceContext` was added with `{ get => default; set { } }`) OR wrap the context. No handler-facing API may change.
- FR-032: When the received message has no `nimbus.claim_check.uri`, the middleware MUST be a pass-through that immediately calls `next(...)`.
- FR-033: When the referenced blob is missing (not-found from the store), the middleware MUST dead-letter via `IMessageContext.DeadLetter(reason, exception)` with reason `"ClaimCheckBlobMissing"` and the URI in the description, MUST NOT call `next(...)`, and MUST then **throw `MessageAlreadyDeadLetteredException(reason)`** (the existing sentinel in `src/NimBus.Core/Pipeline/ValidationMiddleware.cs:50`). This matches how `ValidationMiddleware` aborts after dead-lettering (`ValidationMiddleware.cs:29-30`): without the throw, the pipeline returns normally and `MessageHandler.Handle` proceeds to `NotifyCompleted` (`src/NimBus.Core/Messages/MessageHandler.cs:58-61`), recording the event as **completed** even though it was dead-lettered. Throwing the sentinel routes through the `MessageHandler` failure path so the lifecycle records failed/dead-lettered, not completed. (Confirm `MessageAlreadyDeadLetteredException` is handled distinctly from `TransientException` in `MessageHandler` so it is not re-abandoned — it must not be re-dead-lettered nor abandoned, since `DeadLetter` already settled the message.)
- FR-034: When the download fails with a transient Storage error (throttle/timeout/auth), the middleware MUST surface it so the existing retry machinery abandons-and-retries (not dead-letter on first failure). Distinguishing not-found (permanent) from transient is required.

#### Store abstraction

- FR-050: An `IClaimCheckStore` interface MUST be **stream-based**, not `byte[]`-based, so a large payload is not forced through a second full in-memory buffer on top of the serialized body. It MUST expose at minimum: `Task<string> UploadAsync(Stream payload, long length, string keyHint, CancellationToken)` returning the blob URI; `Task<Stream> DownloadAsync(string uri, CancellationToken)` returning a readable (ideally seek-free, forward-only) stream the middleware deserializes from; `Task DeleteAsync(string uri, CancellationToken)`; and an enumeration/age query sufficient for the retention sweep. A `byte[]` signature MUST NOT be used — it would mandate buffering the whole payload in managed memory and directly contradicts NFR-002 and FR-051's streaming requirement.
- FR-051: A default `BlobClaimCheckStore` MUST implement `IClaimCheckStore` over `Azure.Storage.Blobs`, writing to `BlobContainer` (default `"nimbus-claim-check"`), creating the container if absent, and using streaming upload/download so large payloads do not buffer wholly in memory beyond necessity.
- FR-052: Blob naming MUST be collision-free and traceable — e.g. derived from event id / message id plus a GUID — so concurrent offloads of identical payloads do not overwrite each other and a URI is mappable back to an event for audit.

#### Authentication

- FR-040: `BlobClaimCheckStore` MUST support both a connection string (`BlobConnectionString`) and Managed Identity (`Azure.Identity` `DefaultAzureCredential` against a `BlobServiceUri`). Managed Identity is the recommended/default for production; connection string is supported for local/dev. When neither is configured, registration MUST fail fast with a clear message.
- FR-041: `Azure.Identity` is already a direct, version-pinned dependency of several NimBus projects (`NimBus.WebApp`, `NimBus.Resolver`, `NimBus.MessageStore.CosmosDb` all reference `Azure.Identity` 1.17.1). The new package MUST reuse that same version. `Azure.Storage.Blobs` is NOT currently referenced anywhere in the solution and is a new dependency that this package introduces.

#### Audit / message-store integration

- FR-060: The claim-check URI MUST be recorded on the message store so the WebApp can surface it. The existing audit-write path is `IMessageTrackingStore.StoreMessageAudit(eventId, MessageAuditEntity, endpointId, eventTypeId)` and the audit entity is `MessageAuditEntity` (`src/NimBus.MessageStore.Abstractions/`). Recording the URI MUST follow the established additive-field pattern used for `MessageAuditEntity` (`AccessDenied`, `Data`, `EventId`, `EndpointId` were added as nullable fields with provider follow-on) — see Open Questions for whether the URI lands on `MessageAuditEntity` vs the message-tracking record consumed by Event Details.
- FR-061: Recording the URI MUST be best-effort and MUST NOT fail the publish or the receive path if the store write fails (consistent with the audit-write contract in spec 008: absorb, log a warning, continue).

#### Retention / cleanup

- FR-070: A `BackgroundService` (registered opt-in, paralleling `AddNimBusOutboxDispatcher` in `ServiceCollectionExtensions.cs`) MUST periodically delete blobs older than `RetentionPeriod` (default 7 days).
- FR-071: The sweep MUST be best-effort — a delete failure on one blob logs a warning and continues with the rest; it MUST NOT crash the host.
- FR-072: `RetentionPeriod` MUST be operator-configurable and documented as needing to exceed the maximum scheduled-message horizon used by the publisher.

#### Resubmit support

- FR-080: Resubmit-as-is (the existing `EventImplementation` → `managerClient.Resubmit(errorResponse, endpoint, eventTypeId, eventJson)` path) MUST reuse the original `nimbus.claim_check.uri` and MUST NOT re-upload the payload.
- FR-081: Resubmit-with-modifications (`PostResubmitWithChangesEventIdsAsync(ResubmitWithChanges body, …)`) MUST route the modified payload through `ClaimCheckSender` so that, if the modified payload still exceeds the threshold, a NEW blob is uploaded and a new URI set; if it now fits inline, no reference is set.
- FR-082: Resubmit MUST NOT delete the original blob (retention owns deletion), so an as-is resubmit that reuses the URI cannot be orphaned by a concurrent cleanup before delivery (Assumptions cover the retention horizon).

#### Packaging & DI

- FR-090: The package MUST live at `src/NimBus.Extensions.ClaimCheck/` and mirror `NimBus.Extensions.Notifications` structure: `net10.0`, a `ProjectReference` to `NimBus.Core`, plus the new `Azure.Storage.Blobs` and `Azure.Identity` package references.
- FR-091: Registration MUST be exposed via real NimBus patterns. The publisher decorator parallels how `AddNimBusPublisher` chooses `OutboxSender` vs `Sender`; the consumer middleware is registered into the `MessagePipeline` like the built-in behaviors; the cleanup host parallels `AddNimBusOutboxDispatcher`. The issue's `opts.UseClaimCheck(cc => …)` / `b.UseClaimCheck()` fluent API does NOT exist in the current `NimBusPublisherOptions` / `NimBusSubscriberBuilder` surface — the chosen registration API MUST be reconciled (Open Questions) and any new fluent method documented.

#### Documentation & sample

- FR-100: `docs/claim-check.md` MUST document the threshold, application-property convention, auth modes, retention, consumer-middleware requirement, dead-letter behavior, and the outbox/decorator interaction.
- FR-101: A `LargePayloadEndpoint` sample MUST be added to `samples/AspirePubSub/` (NOTE: the issue says `samples/NimBus.Aspire/`; the real Aspire sample directory is `samples/AspirePubSub/`) demonstrating publish of an over-threshold event and transparent consumption.

#### Tests

- FR-110: Unit + integration tests MUST cover: (1) small payload — no offload, inline publish; (2) large payload — offload, reference on wire, blob written; (3) consumer rehydration round-trips the original event; (4) missing blob → dead-letter with clear reason; (5) transient download error → retry, not dead-letter; (6) resubmit-as-is reuses URI, resubmit-with-changes uploads new blob; (7) retention sweep deletes only expired blobs.
- FR-111: Store-level tests MUST run against the Azure Storage emulator (Azurite) or an in-memory `IClaimCheckStore` fake, consistent with how NimBus's storage conformance suites isolate providers.

### Non-Functional Requirements

- NFR-001: Sub-threshold messages MUST incur near-zero overhead — a single size computation (the serialize already happens on the transport boundary) and a comparison; no blob round-trip.
- NFR-002: Upload and download MUST stream (FR-050's `Stream`-based API) rather than introduce additional full `byte[]` buffers, so the blob transfer itself does not multiply memory use. **Size-target reconciliation:** because the publish path already materializes the serialized body as an in-memory string (FR-002) and `MessageContext.MessageContent` deserializes the whole rehydrated body in memory on receive (FR-031), the claim check does NOT enable true "hundreds of MB, never buffered" payloads — that target conflicts with NimBus's in-memory message model and is explicitly NOT a goal. The realistic supported ceiling is on the order of **tens of MB** (one materialized copy of the serialized payload in managed memory on each of the publish and consume sides), well above the 256 KB / 1 MB bus limits the feature exists to clear. Achieving hundreds-of-MB-without-buffering would require streaming serialize/deserialize seams in `MessageHelper`/`MessageContext` that do not exist today and are out of scope (called out in Out of Scope).
- NFR-003: The feature MUST be fully opt-in. With the extension not registered, publish/consume behavior is byte-for-byte identical to today.
- NFR-004: Audit/URI recording and retention cleanup MUST be best-effort and MUST NOT couple the reliability of a publish or a receive to the Storage SLA.
- NFR-005: The new package MUST introduce only `Azure.Storage.Blobs` (new to the solution) and `Azure.Identity` 1.17.1 (already pinned elsewhere). No other new dependencies.
- NFR-006: Public types and members MUST carry XML doc comments per project convention; test files use `#pragma warning disable CA1707, CA2007`.

## Key Entities

- **`ClaimCheckSender`** — new `ISender` decorator. Wraps the inner sender; offloads above-threshold payloads and stamps `nimbus.claim_check.uri`. Parallels `OutboxSender`.
- **`ClaimCheckMiddleware`** — new `IMessagePipelineBehavior`. Detects the reference property, rehydrates the payload before the handler, dead-letters on a missing blob.
- **`IClaimCheckStore` / `BlobClaimCheckStore`** — store abstraction and default Blob implementation (Upload / Download / Delete / age-query). Supports connection string and Managed Identity.
- **`ClaimCheckOptions`** — `SizeThresholdBytes`, `BlobContainer`, `BlobConnectionString` / `BlobServiceUri`, `RetentionPeriod`. Mirrors the issue's `cc.*` fields adapted to real NimBus options style.
- **`nimbus.claim_check.uri`** — well-known application property carrying the blob URI; the contract between publisher and consumer.
- **Claim-check cleanup `BackgroundService`** — opt-in host that deletes blobs past `RetentionPeriod`. Parallels `OutboxDispatcherHostedService`.
- **`MessageAuditEntity` / message-tracking record** — existing store entity; gains the URI per FR-060 so Event Details can render the offload indicator.

## Success Criteria

### Measurable Outcomes

- SC-001: A 500 KB event publishes successfully on a Standard-tier namespace with claim-check enabled, where the same event without claim-check fails with a size exception. Verified by integration test.
- SC-002: A handler receives the identical strongly-typed event for an offloaded message as it would for an inline message — no handler code change. Verified by round-trip test.
- SC-003: A message whose blob is missing is dead-lettered with a reason naming the claim-check URI; it is not infinitely retried. Verified by test.
- SC-004: Sub-threshold messages produce no blob and carry no `nimbus.claim_check.uri`; behavior matches the pre-feature baseline. Verified by test asserting no Storage calls.
- SC-005: The retention sweep deletes blobs older than `RetentionPeriod` and retains younger ones. Verified by test.
- SC-006: Resubmit-as-is reuses the original URI (no new blob); resubmit-with-changes on a still-large payload uploads a new blob. Verified by test.
- SC-007: The WebApp Event Details page shows a claim-check indicator for offloaded events and nothing for inline events. Verified by frontend test.
- SC-008: The full solution builds with the new package and `dotnet test src/NimBus.sln` is green.

## Assumptions

- The serialized size used for the offload decision (`JsonConvert.SerializeObject(message.MessageContent)` UTF-8 bytes) is a faithful proxy for the on-wire body size, because that is exactly what `MessageHelper.ToServiceBusMessage(...)` writes to `ServiceBusMessage.Body`. Application-property overhead is accounted for by keeping the threshold safely below the tier limit.
- `Azure.Identity` 1.17.1 is the canonical version across the solution (confirmed in `NimBus.WebApp`, `NimBus.Resolver`, `NimBus.MessageStore.CosmosDb` csproj files); the new package adopts it.
- `Azure.Storage.Blobs` is not referenced anywhere today (confirmed — no hit in `Directory.Packages.props` or any csproj); it is a genuinely new dependency.
- `RetentionPeriod` is configured to exceed the publisher's maximum scheduled-message delay so a scheduled offloaded message's blob is never swept before delivery.
- The WebApp surfaces the URI by reading the field recorded via the message-store path (FR-060); the access-control decision for the link reuses the WebApp's existing endpoint-authorization layer (no new auth model).
- One subscriber endpoint per process (the existing `AddNimBusSubscriber` guard) means a single `ClaimCheckMiddleware` registration covers the process's consume path.

## Out of Scope

- Field-level offload / `[DataBusProperty]`-style selective offload. Message-level only for v1.
- Application-layer encryption of blob contents (rely on Storage encryption-at-rest).
- Non-Blob claim-check stores (filesystem, S3). The abstraction allows them; only Blob ships.
- Compression of offloaded payloads.
- **True streaming serialize/deserialize for hundreds-of-MB payloads.** NimBus materializes the serialized body in memory on both publish (`MessageHelper`) and consume (`MessageContext.MessageContent`). Removing those in-memory copies (streaming JSON serialize on send, streaming deserialize on receive) would be needed to support arbitrarily large payloads and is a deeper message-model change outside this feature. The claim check targets the tens-of-MB range that clears the bus size limit (NFR-002), not unbounded streaming.
- Automatic threshold auto-tuning based on namespace tier detection — the operator sets the threshold explicitly.
- Cross-region replication / lifecycle-management policies on the container beyond the simple retention sweep (those are Storage-account operational concerns).

## Open Questions

- **Registration API surface.** The issue proposes `AddNimBusPublisher("storefront", opts => opts.UseClaimCheck(cc => …))` and `b.UseClaimCheck()`. Neither `NimBusPublisherOptions` nor `NimBusSubscriberBuilder` has a `UseClaimCheck` method today, and the publisher sender is built inside `AddNimBusPublisher` as `instrumenting → outbox → transport` with no extension seam for an extra decorator. Decision needed: (a) add `UseClaimCheck(...)` methods to the options/builder and an extension point in the sender-build factory, or (b) expose a separate `services.AddNimBusClaimCheck(...)` that registers the store, the cleanup host, and the consumer middleware, and have `AddNimBusPublisher` consult a registered `IClaimCheckStore` to insert `ClaimCheckSender` (paralleling how it consults `IOutbox` to insert `OutboxSender`). Option (b) fits the existing `OutboxSender` pattern most closely and avoids a publisher-options breaking change.
- **Where the URI is recorded.** `MessageAuditEntity` is the audit-row entity; Event Details renders message-tracking records. Decision needed: add a nullable `ClaimCheckUri` field to `MessageAuditEntity` (following the spec-008 additive pattern, with SQL/Cosmos/in-memory follow-on) vs. to the message-tracking record the Event Details page already reads. The latter is closer to "Event Details shows the offload."
- **`nimbus.claim_check.uri` as enum vs string constant.** Core transport properties use the `UserPropertyName` enum; extension-owned properties (and the W3C headers) use dotted-string keys. Keeping it a string constant in the extension avoids editing the core `UserPropertyName` enum, but a `UserPropertyName.ClaimCheckUri` member would let `MessageHelper`/`MessageContext` round-trip it natively. Decision: prefer the extension-owned string constant unless core needs to read it.
- **Rehydration hook on `IMessageContext`.** `MessageContent` is computed from `_sbMessage.Body` with no override. Whether to add a default-implemented interface member (like `ParentTraceContext`) for the middleware to inject the downloaded payload, or to wrap the context, needs a small design decision (FR-031a). Either must be backward-compatible and leave the handler API unchanged.

## Resolved Questions

- Message-level vs field-level offload. Resolved — message-level for v1 (matches the issue's recommendation; field-level deferred).
- Missing blob behavior. Resolved — dead-letter with a clear reason (`"ClaimCheckBlobMissing"`), not skip and not infinite retry; transient download errors retry per the endpoint policy.
- Authentication. Resolved — support both connection string and Managed Identity, Managed Identity preferred for production (reuses `Azure.Identity` already in the solution).
- Interaction with the outbox. Resolved — they compose: the claim-check decorator sits above the outbox so the outbox persists the reference, not the payload; the dispatcher later sends the reference message.
- Opt-in / zero-overhead default. Resolved — feature is fully opt-in; with the extension absent, behavior is identical to today and sub-threshold messages incur no blob round-trip.
- Sample location. Resolved — the real Aspire sample is `samples/AspirePubSub/` (the issue's `samples/NimBus.Aspire/` does not exist); the `LargePayloadEndpoint` lands there.
