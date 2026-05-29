# Feature Specification: Message Versioning (explicit contract evolution + polymorphic dispatch)

Feature Branch: `016-message-versioning`
Created: 2026-05-29
Updated: 2026-05-29
Status: Proposed
Input: User description (GitHub issue #33 "[Backlog] Message Versioning"): "Message contracts evolve, and unversioned changes are the #1 source of production incidents in long-lived messaging systems: a producer adds a field and old consumers fail deserialization, or the meaning of a field shifts and consumers silently mis-route. NimBus needs a sanctioned way to evolve contracts so producers and consumers can roll out independently — additive nullable fields work today by accident; this issue makes it explicit and adds polymorphic dispatch for breaking changes. Proposed API: `[MessageVersion(2)] public sealed class OrderPlacedV2 : OrderPlacedV1 { public string? PromotionCode { get; init; } }`; a `IEventHandler<OrderPlacedV1>` still receives `OrderPlacedV2` (polymorphic); a `IEventHandler<OrderPlacedV2>` gets the upgraded payload. Rules: additive nullable fields are always safe (Newtonsoft.Json tolerates missing properties); `[MessageVersion(N)]` stamps `nimbus.message.version` for routing/diagnostics; inheritance-based polymorphic dispatch — a V2 message dispatches to handlers of any base version that's registered, most-derived handler wins, falls back up the chain."

## Problem

NimBus event contracts are plain CLR classes deriving from `Event` (`src/NimBus.Abstractions/Events/Event.cs:8`) and implementing `IEvent` (`src/NimBus.Abstractions/Events/IEvent.cs:6`). A contract's wire identity is its **unqualified type name**: `EventType.Id => _type.Name` (`src/NimBus.Abstractions/Events/EventType.cs:24`). That single string is the routing key end-to-end:

- On publish, `PublisherClient.GetMessageStatic(...)` sets `To`, `EventTypeId`, and `MessageContent.EventContent.EventTypeId` all to `@event.GetEventType().Id` (`src/NimBus.SDK/PublisherClient.cs:255-274`).
- On the wire it is stamped as `ApplicationProperties[UserPropertyName.EventTypeId]` (`src/NimBus.ServiceBus/MessageHelper.cs:25`).
- On receive, `EventHandlerProvider.Handle(...)` looks the handler up by **exact string match** on `context.MessageContent.EventContent.EventTypeId` (`src/NimBus.SDK/EventHandlers/EventContextHandler.cs:24-31`), and `GetHandler(...)` throws `EventHandlerNotFoundException` if there is no exact key (`EventContextHandler.cs:58-65`).

This makes contract evolution a footgun:

1. **Additive change "works by accident."** Today a producer can add a nullable field to an existing type and old consumers keep working — `EventJsonHandler<T>` deserializes with `JsonConvert.DeserializeObject<T_Event>(...)` (`src/NimBus.SDK/EventHandlers/EventJsonHandler.cs:22`), and Newtonsoft.Json silently ignores unknown JSON properties and leaves absent ones at their default. There is no *sanctioned* statement that this is safe, no version marker, and nothing distinguishing "safe additive change" from "I renamed a field and broke everyone."

2. **A breaking change forces a hard fork.** The moment a producer needs a genuinely new shape (a renamed or retyped field), the only safe move is a **new type name** — `OrderPlacedV2`. But because dispatch is an exact string match on the type name, a `OrderPlacedV2` message reaches **only** a handler registered for the literal id `"OrderPlacedV2"`. A consumer that still has `IEventHandler<OrderPlacedV1>` registered receives nothing and the message dead-letters via `EventHandlerNotFoundException`. Producer and consumer can no longer roll out independently — the consumer **must** ship a V2 handler before the producer publishes V2.

3. **No version visibility for ops.** When a V2 message dead-letters because a consumer is behind, there is nothing on the message (or in the WebApp) that says "this is version 2 of the contract." Triage has to infer it from the type-name suffix, by convention only.

NimBus needs an explicit, first-class way to (a) declare a contract's version, (b) stamp that version on the wire for routing/diagnostics, and (c) let a derived (`V2`) message fall back to a registered base-version (`V1`) handler so producers can roll out ahead of consumers. The mechanism already has the right seams: a `[SessionKey]` attribute pattern to copy (`src/NimBus.Abstractions/Events/SessionKeyAttribute.cs`), a `UserPropertyName` enum for wire properties (`src/NimBus.Core/Messages/UserPropertyName.cs`), and a single dispatch lookup (`EventHandlerProvider.GetHandler`) where a chain-walk slots in.

## Scope

In scope:
- A new `[MessageVersion(N)]` attribute in `NimBus.Abstractions` (`src/NimBus.Abstractions/Events/MessageVersionAttribute.cs`), paralleling `SessionKeyAttribute`, declaring the integer contract version on an event type.
- Surfacing the version on `IEventType` / `EventType` (a `Version` member alongside `Id`, `Name`, `SessionKeyProperty`), read by reflection from the attribute, defaulting to `1` when absent.
- Stamping the version on outgoing messages as a `nimbus.message.version` application property, added to the `UserPropertyName` enum and written in `MessageHelper.ToServiceBusMessage(...)` for routing/diagnostics. It is **advisory** (dispatch keys off the type chain, not this number) — see Resolved Questions.
- Wire-carried base-type chain: the **publisher** stamps the ordered list of base `EventTypeId`s (most-derived → base) of the published type as an application property (`nimbus.message.basetypes`), computed from the type's CLR hierarchy at publish time. This is what lets a consumer that does **not** reference the derived type still resolve a compatible base handler (see Problem item 4 / the consumer-knowledge constraint).
- Wire-carried polymorphic dispatch in `EventHandlerProvider`: when no handler is registered for the incoming `EventTypeId`, walk the **wire-carried base-type chain** (not a CLR chain the consumer cannot build) and dispatch to the first id that has a registered handler — the most-derived registered ancestor; the deserialized payload is the registered (base) type so the existing `EventJsonHandler<T>` runs unchanged. Most-derived registered handler wins; falls back up the chain.
- A documented set of evolution rules: additive nullable fields are safe in place; removals / renames / type narrowing require a new versioned type that derives from the prior one.
- Documentation: a worked migration example in `docs/message-versioning.md` (new) and an entry in `docs/sdk-api-reference.md`.
- Unit tests for dispatch resolution (most-derived wins, fall back up the chain, no-match still throws) and an E2E test (producer V2, consumer V1 only, round trip succeeds).

Out of scope:
- **Compile-time compatibility diagnostics** (removed required field, narrowed type, renamed property). The issue lists a "source-generator hook to emit compile-time compatibility warnings"; that belongs to the source-generators work (issue #35, `docs/specs/018-source-generators/spec.md`), which already names `[MessageVersion]` as an input it reads. This spec defines the runtime semantics that #35's diagnostics validate against; it does **not** add an analyzer.
- Changing how `EventTypeId` is computed. It stays `_type.Name`; versioning rides on CLR inheritance, not on a composite `name@version` id (see Resolved Questions for why).
- Schema-registry / external-contract-store integration. Versioning is declared in code via the attribute and the type hierarchy.
- Runtime *rejection* of a structurally incompatible payload (producer V3 adds a required field, consumer is V1). NimBus stays best-effort deserialize; the boundary case is carried into Open Questions.
- Versioning for non-event control messages (handoff / deferred / dead-letter notifications). Those are framework-internal and not consumer contracts.

## User Scenarios & Testing

### User Story 1 - Producer rolls out V2 ahead of a V1-only consumer (Priority: P1)

As a service owner, I want to publish `OrderPlacedV2 : OrderPlacedV1` and have it delivered to a consumer that only has `IEventHandler<OrderPlacedV1>` registered, so that I can deploy the new producer without coordinating a same-release consumer deploy.

Why this priority: This is the core promise of the feature — independent producer/consumer rollout for a breaking change. Without it, the type-name-keyed dispatch dead-letters the V2 message at the lagging consumer.

Independent Test: Spin up a subscriber that registers only `IEventHandler<OrderPlacedV1>`. Publish an `OrderPlacedV2`. Assert the V1 handler's `Handle(...)` runs and receives an `OrderPlacedV1`-typed instance (the additive `PromotionCode` is simply not visible on the base type).

Acceptance Scenarios:

1. Given a subscriber with only `OrderPlacedV1Handler : IEventHandler<OrderPlacedV1>` registered (and which does **not** reference the `OrderPlacedV2` CLR type at all), When an `OrderPlacedV2` message arrives carrying `nimbus.message.basetypes = "OrderPlacedV2,OrderPlacedV1"` (FR-025) and no exact `"OrderPlacedV2"` handler, Then `EventHandlerProvider` reads the wire chain, finds the `"OrderPlacedV1"` registration (the first registered id in the chain), deserializes the body as `OrderPlacedV1`, and invokes the V1 handler — without the consumer needing the V2 type.
2. Given the same subscriber, When the V1 handler reads only the V1 fields, Then it sees correct values (Newtonsoft.Json ignores the extra `PromotionCode` JSON property — `EventJsonHandler.cs:22`).
3. Given the V2 message carried `nimbus.message.version = 2`, When the V1 handler processes it, Then processing succeeds and the version property remains readable for diagnostics (it does not change which handler ran).

---

### User Story 2 - V2-aware consumer gets the upgraded payload (Priority: P1)

As a consumer owner who has upgraded, I want to register `IEventHandler<OrderPlacedV2>` and receive the full V2 payload (including `PromotionCode`), so that once I have shipped V2 my handler gets the richer contract — even when a V1 handler is also registered.

Why this priority: The complement of Story 1. Most-derived must win, otherwise upgrading the consumer has no effect.

Independent Test: Register both `IEventHandler<OrderPlacedV1>` and `IEventHandler<OrderPlacedV2>` in the same subscriber. Publish an `OrderPlacedV2`. Assert only the V2 handler runs and `PromotionCode` is populated.

Acceptance Scenarios:

1. Given both `OrderPlacedV1Handler` and `OrderPlacedV2Handler` are registered, When an `OrderPlacedV2` arrives, Then the exact-match `"OrderPlacedV2"` registration is selected (most-derived) and the V1 handler does NOT run.
2. Given only the V2 handler is registered, When an `OrderPlacedV2` arrives, Then the V2 handler runs and `PromotionCode` is deserialized.
3. Given only the V2 handler is registered, When an `OrderPlacedV1` (the base) arrives, Then there is **no** registration for `"OrderPlacedV1"` and **no** ancestor of `OrderPlacedV1` is registered, so the existing `EventHandlerNotFoundException` is thrown and the message dead-letters. (Polymorphism walks **up** the chain — base → ancestors — never down to derived types.)

---

### User Story 3 - Additive nullable field with no new type (Priority: P2)

As a producer making a backward-compatible change, I want to add a nullable field to an existing type without minting a new type, and have old consumers keep working, so that the common safe change stays cheap.

Why this priority: This is the everyday case; it "works today by accident" and the spec sanctions it explicitly rather than changing behaviour.

Independent Test: Add `string? Note { get; init; }` to an existing `OrderPlaced`. Publish from the new producer; consume with a build that predates the field. Assert the handler runs and ignores the unknown property.

Acceptance Scenarios:

1. Given `OrderPlaced` gains a nullable `Note`, When a consumer built before the field deserializes the message, Then `JsonConvert.DeserializeObject<OrderPlaced>` ignores the extra JSON property and the handler runs unchanged.
2. Given the producer omits `Note` (null), When a newer consumer deserializes, Then `Note` is `null` (Newtonsoft.Json leaves absent properties at default). No new type, no `[MessageVersion]` bump required.
3. Given the documented rule "additive nullable fields are safe; removals/renames require a new type," When a developer reads `docs/message-versioning.md`, Then the rule and the decision boundary are stated with the worked example.

---

### User Story 4 - Version is visible on the wire and (optionally) in the WebApp (Priority: P2)

As an operator triaging a dead-lettered message, I want to see the contract version on the message, so that I can tell at a glance whether the consumer is simply behind the producer's contract version.

Why this priority: Cheap, high ops value. The issue's own open question answers "yes" for surfacing it.

Independent Test: Publish an `OrderPlacedV2`, inspect the Service Bus message's application properties, confirm `nimbus.message.version = 2`. Confirm the WebApp event-details page shows the version.

Acceptance Scenarios:

1. Given an event type marked `[MessageVersion(2)]`, When it is published, Then the outgoing message carries `ApplicationProperties["nimbus.message.version"] = 2` (FR-020).
2. Given an event type with **no** `[MessageVersion]` attribute, When it is published, Then the version property is `1` (the default — FR-011).
3. Given the message is read back, When `GetUserProperty("nimbus.message.version")` is called (`src/NimBus.ServiceBus/ServiceBusMessage.cs:55`), Then it returns the stamped value as a string.
4. Given the WebApp event-details surface (`ClientApp/src/pages/event-details.tsx`), When an operator opens an event whose message carries a version, Then the version is rendered. (Surfacing is in scope; the exact placement is left to the implementer — see Resolved Questions.)

---

### User Story 5 - Ambiguous registration is rejected at startup, not silently (Priority: P2)

As a maintainer, I want a clear error when two unrelated types collide on the same `EventTypeId`, so that adding versioned types does not reintroduce silent handler clobbering.

Why this priority: `NimBusSubscriberBuilder` already fails loudly on `EventTypeId` collisions between distinct CLR types (`src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs:119-133`). Polymorphic dispatch must not weaken that guarantee.

Independent Test: Register two unrelated types `A.OrderPlaced` and `B.OrderPlaced` (same unqualified name). Assert the existing `InvalidOperationException` still fires at registration.

Acceptance Scenarios:

1. Given two distinct CLR types map to the same `EventTypeId` (e.g. `A.OrderPlaced` and `B.OrderPlaced`), When the subscriber is built, Then `AddHandlerRegistration` throws the existing "Two distinct event types map to the same EventTypeId" error (`NimBusSubscriberBuilder.cs:128-132`). Versioning does not relax this.
2. Given `OrderPlacedV2 : OrderPlacedV1` (genuine inheritance, distinct names), When both handlers are registered, Then there is NO collision (the ids `"OrderPlacedV1"` and `"OrderPlacedV2"` differ) and both registrations stand.

---

## Edge Cases

- **Diamond / multi-level chain.** `V3 : V2 : V1`, with handlers for V1 and V3 registered, V2 not. A `V3` message dispatches to V3 (exact). A `V2` message (if any producer still emits it) walks up: V2 not registered → V1 registered → dispatches to V1. Most-derived **registered** ancestor wins.
- **`[MessageVersion]` value lower than a base.** A type marked `[MessageVersion(1)]` deriving from a `[MessageVersion(2)]` base is a developer error (version should be monotonic down the inheritance chain). The runtime does not enforce monotonicity (the source-generator spec #35 can add a diagnostic); the *number* is advisory and does not drive dispatch, so dispatch is unaffected.
- **No `[MessageVersion]` anywhere in the chain.** All types default to version `1`; dispatch still works purely off the CLR inheritance chain. The attribute is optional; the polymorphic-dispatch behaviour does not require it.
- **Abstract base in the chain.** `OrderPlacedV1` could be abstract and never published, only used as a handler target. Dispatch still walks to it; deserializing a concrete `V2` body into the abstract base would fail, so a registered ancestor must be a concrete, deserializable type. Documented as a constraint.
- **The concrete event type cannot be resolved on the consumer.** This is the *normal* independent-rollout case (V1-only consumer, no reference to `OrderPlacedV2`) and is handled by the **wire-carried base-type chain** (FR-025/FR-030): the consumer never reflects over the V2 CLR type — it matches the producer-stamped chain ids against its own registrations. The earlier "walk the consumer's CLR base chain" design could not handle this; that is the precise gap the wire chain closes. (Resolved — see Resolved Questions.)
- **Two registered ancestors at the same depth.** Single-inheritance CLR types cannot produce two ancestors at the same depth, so "most-derived wins" is unambiguous. Interface-based contracts (multiple `IEventHandler<TInterface>`) are out of scope for v1; events derive from the `Event` base class.
- **Newtonsoft.Json `MissingMemberHandling`.** Default is `Ignore`; the safe-additive guarantee depends on this. If a consumer globally sets `MissingMemberHandling.Error`, the additive guarantee breaks. The serializer call site (`EventJsonHandler.cs:22`) uses the default settings, so the framework path is safe; consumer overrides are their own concern (documented).
- **Version property type on the wire.** Stamped as an integer in `ApplicationProperties`; read back via `GetUserProperty(...)` returns the `.ToString()` form (`ServiceBusMessage.cs:60`). Consumers parsing it must `int.Parse`. Documented.

## Requirements

### Functional Requirements

#### The `[MessageVersion]` attribute

- FR-001: A new attribute `MessageVersionAttribute` MUST be added at `src/NimBus.Abstractions/Events/MessageVersionAttribute.cs`, in namespace `NimBus.Core.Events` (matching the namespace of the sibling `SessionKeyAttribute` and `EventType`, despite the `NimBus.Abstractions` assembly folder), paralleling `SessionKeyAttribute`:
  ```csharp
  using System;

  namespace NimBus.Core.Events
  {
      /// <summary>
      /// Declares the contract version of an event type. The version is stamped on
      /// outgoing messages as the <c>nimbus.message.version</c> application property
      /// for routing/diagnostics. Dispatch itself keys off the CLR inheritance chain,
      /// not this number. Absent attribute means version 1.
      /// </summary>
      [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
      public sealed class MessageVersionAttribute : Attribute
      {
          public int Version { get; }

          public MessageVersionAttribute(int version)
          {
              if (version < 1) throw new ArgumentOutOfRangeException(nameof(version), "Message version must be >= 1.");
              Version = version;
          }
      }
  }
  ```
- FR-002: `AttributeUsage` MUST set `Inherited = false`. Unlike `[SessionKey]` (which is `Inherited = true` so a derived type keeps its base's session key — `SessionKeyAttribute.cs:11`), version is per-concrete-type: `OrderPlacedV2` declares its own `2`, and must NOT silently inherit `OrderPlacedV1`'s `1`.
- FR-003: The constructor MUST reject versions `< 1` with `ArgumentOutOfRangeException`. Version `1` is the implicit default for an unmarked type, so explicit `0` or negatives are invalid.

#### Reading the version on `IEventType` / `EventType`

- FR-010: `IEventType` (`src/NimBus.Abstractions/Events/IEventType.cs:6`) MUST gain `int Version { get; }`, alongside the existing `Id`, `Name`, `Description`, `Namespace`, `Properties`.
- FR-011: `EventType.Version` MUST read the attribute by reflection, defaulting to `1` when absent — mirroring how `SessionKeyProperty` reads `SessionKeyAttribute` (`EventType.cs:32`):
  ```csharp
  public int Version => _type.GetCustomAttribute<MessageVersionAttribute>()?.Version ?? 1;
  ```
- FR-012: `EventType.Id` MUST remain `_type.Name` (`EventType.cs:24`). Version is a separate field; it does NOT change the wire `EventTypeId` or the routing key.

#### Wire stamping

- FR-020: `UserPropertyName` (`src/NimBus.Core/Messages/UserPropertyName.cs`) MUST gain a member for the version property. The enum members today stamp as their bare names, e.g. `ApplicationProperties["EventTypeId"]` (`MessageHelper.cs:25`). To produce the issue's literal `nimbus.message.version` key, the implementation MUST either (a) add a member whose serialized key is mapped to the `nimbus.message.version` string, or (b) write the property with the literal string key at the stamp site. The literal `nimbus.message.version` is the canonical wire key (FR-021).
- FR-021: `MessageHelper.ToServiceBusMessage(...)` (`src/NimBus.ServiceBus/MessageHelper.cs:13`) MUST stamp the version onto `result.ApplicationProperties["nimbus.message.version"]` when constructing the outgoing message, sourced from the event type's `Version`. The message model MUST carry the version from publish time so the helper can read it; `PublisherClient.GetMessageStatic(...)` (`PublisherClient.cs:251-282`) is where the event's `@event.GetEventType()` is already materialised and is the natural place to capture `Version` onto the `Message`.
- FR-022: When the event type has no `[MessageVersion]`, the stamped value MUST be `1` (FR-011's default flows through). The property is always present on outgoing event messages.
- FR-023: The version property MUST be readable on receive via the existing `IServiceBusMessage.GetUserProperty(string name)` accessor (`ServiceBusMessage.cs:55-61`), which returns the value's `.ToString()` (an integer rendered as a string). No new read API is required.
- FR-024: The stamped version is **advisory** for routing/diagnostics. It MUST NOT be consulted by handler resolution (FR-030 keys off the inheritance chain, not this number). This keeps the dispatch logic single-sourced.

#### Polymorphic dispatch

The dispatch mechanism is **wire-carried**, not CLR-reflection-carried on the consumer. This is the load-bearing correctness decision: a V1-only consumer does **not** reference the `OrderPlacedV2` CLR type, so it cannot reconstruct V2's base chain by reflection. The producer (which *does* have the V2 type) stamps the ordered base-type chain on the wire; the consumer matches that chain against its own registrations by id string. (See FR-025 for the wire stamp.)

- FR-025: The publisher MUST stamp the **ordered base-type chain** of the published event type onto the outgoing message as a new application property (e.g. `nimbus.message.basetypes`), in addition to the version (FR-020). The value is the list of `EventTypeId`s from the published type's CLR inheritance chain, **most-derived first, base last**, excluding the framework `Event`/`object` roots — e.g. for `OrderPlacedV2 : OrderPlacedV1 : Event` the stamp is `"OrderPlacedV2,OrderPlacedV1"`. It is computed at publish time in `PublisherClient.GetMessageStatic(...)` where `@event.GetType()`/`GetEventType()` is materialised, walking `BaseType` until the `Event` base. A new `UserPropertyName` member carries it (FR-020's enum addition pattern). An old/unaware producer that does not stamp the chain MUST still interoperate: the consumer simply has no chain to walk and behaves as today (exact-id match or throw).
- FR-030: `EventHandlerProvider` (`src/NimBus.SDK/EventHandlers/EventContextHandler.cs:12`) MUST resolve handlers as follows. Today `GetHandler(string eventTypeId)` does an exact `_handlerBuilders.TryGetValue(...)` and throws otherwise (`EventContextHandler.cs:58-65`). The resolution MUST become: (1) try the exact incoming `EventTypeId`; (2) on miss, read the **wire-carried base-type chain** (FR-025) from the message's application properties and, walking it in order (most-derived → base), dispatch to the **first id for which a handler is registered**; (3) only if neither the exact id nor any id in the wire chain is registered does it throw `EventHandlerNotFoundException` (preserving today's behaviour for the genuinely-unhandled case). The consumer matches purely on **id strings it already knows from its own registrations** — it never needs the derived CLR type.
- FR-031: Because the consumer matches on the wire-carried id chain, the handler dictionary stays **keyed by `EventTypeId` string** exactly as today (`EventContextHandler.cs:39-56`); no CLR-ancestry computation on the consumer is required. (This is simpler than reflecting over base types and is the only approach that works when the consumer lacks the derived type — the whole point of the fix.)
- FR-032: "Most-derived registered wins" follows directly from the wire chain's ordering (most-derived first): iterate the chain in order and select the first id with a registration. Because the producer emits a single linear CLR chain, the winner is unambiguous.
- FR-033: The deserialized payload MUST be the **registered** (selected base) type, not the incoming concrete type. The existing `EventJsonHandler<T>` deserializes `JsonConvert.DeserializeObject<T_Event>(...)` where `T_Event` is the registered type (`EventJsonHandler.cs:10-22`); a V2 JSON body deserialized as V1 drops the V2-only fields cleanly (Newtonsoft.Json ignores them), which is the intended polymorphic-downcast behaviour. No change to `EventJsonHandler<T>` is required — the registered factory already binds the base type, and the body JSON is a superset of the base contract.
- FR-034: Resolution MUST be O(chain depth) per message and SHOULD cache the `(incoming-id, chain) → selected-factory` mapping after first resolution, consistent with the existing `ConcurrentDictionary` design (`EventContextHandler.cs:14`). The cache key MUST include the incoming id (different producers may send different chains for the same leaf id only in pathological cases; keying on the incoming id is sufficient in practice and documented).
- FR-035: If the incoming message carries **no** base-type chain (old/unaware producer) and there is no exact-id registration, resolution MUST throw `EventHandlerNotFoundException` exactly as today — no behavioural change for un-versioned traffic. The shared-contract alternative (the consumer references a contracts assembly containing the base type and relies on a build-time/shared registry instead of the wire chain) is explicitly NOT required by this spec: the wire-carried chain makes polymorphic dispatch work **without** forcing the consumer to take a dependency on the producer's derived contract. (Recorded in Resolved Questions.)

#### Registration integrity

- FR-040: The `NimBusSubscriberBuilder` collision guard MUST be preserved: two **distinct CLR types** sharing an `EventTypeId` still throw the existing `InvalidOperationException` (`NimBusSubscriberBuilder.cs:119-133`). Versioned types in a genuine inheritance chain have distinct ids and do not collide.
- FR-041: Registering both a base and a derived handler (`IEventHandler<OrderPlacedV1>` + `IEventHandler<OrderPlacedV2>`) MUST be allowed and MUST NOT be treated as the "multiple handlers for the same event type" ambiguity error (`NimBusSubscriberBuilder.cs:144-147`) — they are handlers for *different* event types (`OrderPlacedV1` vs `OrderPlacedV2`), each with its own `EventTypeId`.

#### Documentation

- FR-050: A new page `docs/message-versioning.md` MUST be added with: the evolution rules (additive nullable fields safe in place; removals/renames/type-narrowing require a new derived versioned type), the `[MessageVersion(N)]` usage, the polymorphic-dispatch semantics (most-derived wins, falls back up), and a worked `OrderPlacedV1 → OrderPlacedV2` migration example mirroring the issue's snippet.
- FR-051: `docs/sdk-api-reference.md` MUST document `MessageVersionAttribute`, the `IEventType.Version` member, and the `nimbus.message.version` wire property, cross-linking `docs/message-versioning.md`.
- FR-052: The docs MUST cross-reference `docs/specs/018-source-generators/spec.md` for the compile-time compatibility diagnostics (removed required field, narrowed type, renamed property), noting those are validated at build time by the analyzer, not at runtime here.

#### Tests

- FR-060: Unit tests on `EventHandlerProvider` MUST cover:
  1. Exact-id match dispatches to the exact handler (no chain walk) — regression guard.
  2. V2 message with only V1 registered dispatches to V1 (chain walk, most-derived registered ancestor).
  3. V2 message with both V1 and V2 registered dispatches to V2 only (most-derived wins; V1 does not run).
  4. `V3:V2:V1` with V1 and V3 registered: V3 message → V3; V2 message → V1.
  5. No exact and no registered ancestor → `EventHandlerNotFoundException` (preserves today's behaviour).
- FR-061: A test MUST assert `EventType.Version` reads `[MessageVersion(2)]` as `2` and defaults to `1` when absent, and that `Inherited = false` means a derived type without its own attribute reports `1`, not its base's value.
- FR-062: A test MUST assert `MessageHelper.ToServiceBusMessage(...)` stamps `nimbus.message.version` with the type's version, and `1` when unmarked.
- FR-063: An E2E / conformance test MUST: publish an `OrderPlacedV2`, consume on a subscriber registering only `IEventHandler<OrderPlacedV1>`, and assert the V1 handler runs and the round trip succeeds (matching the issue's acceptance criterion).

### Non-Functional Requirements

- NFR-001: Per-message resolution cost MUST be O(chain depth), amortised to O(1) by caching the incoming-id → factory mapping after first resolution. Typical chains are 1-3 deep; the chain walk is reflection-free after the first resolution per id.
- NFR-002: No new NuGet dependency. The attribute lives in `NimBus.Abstractions`; reflection (`GetCustomAttribute`, `IsAssignableFrom`, `BaseType`) and the existing Newtonsoft.Json serializer suffice.
- NFR-003: Backward compatible. Existing unmarked event types report version `1`, stamp `nimbus.message.version = 1`, and dispatch exactly as today (exact-id match). No existing handler registration or message round-trips break.
- NFR-004: The additive-field safety guarantee MUST hold on the framework deserialize path. `EventJsonHandler<T>` uses default Newtonsoft.Json settings (`MissingMemberHandling.Ignore`) — `EventJsonHandler.cs:22`. The spec MUST NOT introduce stricter global serializer settings.
- NFR-005: Thread safety. The handler builders live in a `ConcurrentDictionary` (`EventContextHandler.cs:14`); the added ancestry cache MUST also be concurrency-safe (a `ConcurrentDictionary`), since dispatch runs concurrently across messages.
- NFR-006: No public-contract break to `IEvent` / `IEventHandler<T>`. The only interface change is the additive `IEventType.Version` member; implementers of `IEventType` (`EventType`) are updated in the same change. `IEventType` is a small, framework-owned surface.

## Key Entities

- **`MessageVersionAttribute`** — new. `src/NimBus.Abstractions/Events/MessageVersionAttribute.cs`, namespace `NimBus.Core.Events`. `[AttributeUsage(Inherited = false)]`. Carries `int Version`. Parallels `SessionKeyAttribute`.
- **`IEventType` / `EventType`** — existing. `src/NimBus.Abstractions/Events/IEventType.cs` + `EventType.cs`. Gains `int Version` (reflection read, default `1`). `Id` stays `_type.Name`.
- **`UserPropertyName`** — existing enum. `src/NimBus.Core/Messages/UserPropertyName.cs`. Gains members for the version property (`nimbus.message.version`) and the base-type chain property (`nimbus.message.basetypes`).
- **`nimbus.message.basetypes` application property** — new wire property (FR-025). Ordered list of ancestor `EventTypeId`s (most-derived → base), stamped by the publisher, walked by the consumer to resolve a base-version handler without referencing the derived type. This is the mechanism that makes V1-only-consumer ↔ V2-producer dispatch work.
- **`MessageHelper`** — existing. `src/NimBus.ServiceBus/MessageHelper.cs`. Stamps `nimbus.message.version` in `ToServiceBusMessage(...)`.
- **`PublisherClient`** — existing. `src/NimBus.SDK/PublisherClient.cs`. `GetMessageStatic(...)` captures the event's `Version` onto the `Message` at publish time.
- **`EventHandlerProvider`** (`IEventContextHandler`) — existing, the real dispatcher. `src/NimBus.SDK/EventHandlers/EventContextHandler.cs`. `GetHandler(...)` gains the **wire-chain walk**: on an exact-id miss it matches the producer-stamped `nimbus.message.basetypes` ids against its existing id-keyed registrations. It does NOT reflect over consumer-side CLR ancestry (the consumer need not have the derived type).
- **`EventJsonHandler<T>`** — existing. `src/NimBus.SDK/EventHandlers/EventJsonHandler.cs`. Unchanged — already deserializes the body as the *registered* base type `T_Event` via Newtonsoft.Json, which is exactly the polymorphic-downcast behaviour.
- **`NimBusSubscriberBuilder`** — existing, the real event-type/handler registry (reflection scan via `DiscoverHandlerRegistrations` / `GetLoadableTypes`). `src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs`. Collision guard preserved; base+derived handlers coexist.
- **`nimbus.message.version` application property** — new wire property. Advisory; read via `IServiceBusMessage.GetUserProperty(...)`.

## Success Criteria

### Measurable Outcomes

- SC-001: A `OrderPlacedV2 : OrderPlacedV1` published to a subscriber that registers only `IEventHandler<OrderPlacedV1>` is delivered to the V1 handler and processed successfully (no dead-letter). Verified by E2E test (FR-063).
- SC-002: With both V1 and V2 handlers registered, an `OrderPlacedV2` reaches the V2 handler only; the V1 handler does not run. Verified by unit test (FR-060.3).
- SC-003: Every published event message carries `nimbus.message.version`, equal to the type's `[MessageVersion(N)]` value or `1` when unmarked. Verified by FR-062.
- SC-004: All existing dispatch behaviour is preserved: exact-id messages with an exact handler still match without invoking the chain walk; genuinely unhandled messages still throw `EventHandlerNotFoundException`. Verified by FR-060.1 and FR-060.5.
- SC-005: No existing NimBus test regresses as a result of the change.
- SC-006: `docs/message-versioning.md` exists with a worked V1→V2 migration example and the evolution rules; `docs/sdk-api-reference.md` documents the attribute, `IEventType.Version`, and the wire property.

## Assumptions

- **The real dispatcher is `EventHandlerProvider`, not a type literally named `MessageDispatcher`.** No `MessageDispatcher` or `EventTypeRegistry` type exists in NimBus; the issue's names are conceptual. `EventHandlerProvider` (`src/NimBus.SDK/EventHandlers/EventContextHandler.cs`) is the dispatch table (a `ConcurrentDictionary<string, Func<IEventJsonHandler>>` keyed by `EventTypeId`), and `NimBusSubscriberBuilder` (`src/NimBus.SDK/Extensions/NimBusSubscriberBuilder.cs`) is the discovery/registration "registry." This spec targets those real types.
- **`EventTypeId` is the unqualified type name.** `EventType.Id => _type.Name` (`EventType.cs:24`). The whole versioning design rides on CLR inheritance because the id is name-based, not assembly-qualified; an `OrderPlacedV2 : OrderPlacedV1` naturally gets a distinct id and a reconstructable chain.
- **The serializer is Newtonsoft.Json.** Confirmed: `EventJsonHandler` (`EventJsonHandler.cs:3,22`), `MessageHelper` (`MessageHelper.cs:3,87,128`), and `PublisherClient` (`PublisherClient.cs:256`) all use `JsonConvert`. The issue's claim that "Newtonsoft.Json tolerates missing properties" is correct for the default `MissingMemberHandling.Ignore` used at these call sites.
- **The transport that stamps application properties is Azure Service Bus.** `MessageHelper`/`ServiceBusMessage` are the `NimBus.ServiceBus` adapter. The `nimbus.message.version` stamp is added there; the model-level version (FR-021) is transport-agnostic so other adapters can stamp it the same way.
- **Application properties are stamped via the `UserPropertyName` enum, not via raw `nimbus.*` string literals.** Existing properties use `UserPropertyName.<Name>.ToString()` (e.g. `MessageHelper.cs:25`); the bare member name is the wire key. There is **no** existing `nimbus.*`-prefixed property in the codebase. The issue's `nimbus.message.version` literal is therefore a *new* naming convention; FR-020/FR-021 reconcile it (map a new enum member to the literal, or write the literal directly).
- **The WebApp event-details surface is `ClientApp/src/pages/event-details.tsx`** with `EventTypeViewModel` (`src/NimBus.WebApp/Models/EventTypeViewModel.cs`) on the event-type side. The stored `MessageEntity` carries `EventTypeId` (`src/NimBus.MessageStore.Abstractions/MessageEntity.cs:14`) but does not yet persist a version field — surfacing the version in the WebApp may require carrying it from the application property into the read model (see Resolved Question on WebApp surfacing).
- **Events derive from the `Event` base class** (`src/NimBus.Abstractions/Events/Event.cs`); contracts are single-inheritance CLR classes, which is what makes "most-derived wins" unambiguous. Interface-based contracts are out of scope.

## Out of Scope

- Compile-time compatibility diagnostics (removed required field, narrowed type, renamed property) — owned by source-generators (issue #35, `docs/specs/018-source-generators/spec.md`). This spec defines the runtime semantics those diagnostics enforce.
- Changing `EventTypeId` to an assembly-qualified or `name@version` composite. The name-based id is retained; versioning rides on inheritance.
- A schema registry or external contract store.
- Runtime structural compatibility checking / rejection of incompatible payloads (e.g. producer V3 with a new required field reaching a V1 consumer). NimBus stays best-effort deserialize.
- Versioning of framework control messages (handoff, deferred, dead-letter notifications).
- Down-conversion (delivering a V1-shaped payload to a V1 handler from a V2 body is already covered by deserialize-as-base; the reverse — synthesising a V2 from a V1 — is not attempted).
- Interface-based message contracts and multi-interface polymorphic dispatch (MassTransit-style). v1 is single-inheritance class hierarchies only.

## Open Questions

- **Runtime rejection of structurally incompatible versions** (producer V3 adds a *required* field; consumer V1 cannot satisfy it). The issue asks "reject, or always best-effort?" Carried forward: v1 is best-effort (Newtonsoft.Json tolerates missing properties; a V1 handler simply never sees the V3 required field). A future option could read `nimbus.message.version`, compare against the highest registered version, and dead-letter with a clear reason when the gap exceeds a configured tolerance. Not specified here.
- **Should `[MessageVersion]` monotonicity be enforced** (a derived type's version must be ≥ its base's)? The number is advisory and does not drive dispatch, so the runtime tolerates any value. A compile-time diagnostic (issue #35) is the better enforcement point.

## Resolved Questions

- **The real dispatcher is `EventHandlerProvider`; resolution is an exact string lookup on `EventTypeId` today.** Resolved by reading `src/NimBus.SDK/EventHandlers/EventContextHandler.cs:24-65`. The chain walk is inserted in `GetHandler`.
- **`EventTypeId` stays `_type.Name`; versioning rides on CLR inheritance.** Resolved — the id is name-based (`EventType.cs:24`), so derived types get distinct ids and reconstructable chains. A composite id would have been a wider, more disruptive change.
- **The version property is advisory; dispatch keys off the type chain, not the number.** Resolved — single-sources the dispatch logic and avoids two competing resolution mechanisms (FR-024).
- **Dispatch uses a wire-carried base-type chain, not the consumer's CLR reflection.** Resolved (FR-025/FR-030/FR-035) — this was originally an open question and an edge case, but it is the core correctness decision: a V1-only consumer does not reference `OrderPlacedV2`, so it cannot reconstruct V2's ancestry by reflection. The publisher stamps `nimbus.message.basetypes` (ordered most-derived → base) and the consumer matches those ids against its own registrations. This makes independent producer-ahead rollout actually work and removes the dependency on the consumer referencing the producer's derived contract. A shared-contract-assembly alternative (consumer references a contracts package and uses a shared registry) is viable but NOT required and is not adopted, because it reintroduces the coupling the wire chain removes. An old producer that does not stamp the chain degrades gracefully to exact-id-or-throw (FR-035).
- **`Inherited = false` on `[MessageVersion]`** (unlike `[SessionKey]`'s `Inherited = true`). Resolved — version is per-concrete-type; a derived type must declare its own, not silently report its base's (FR-002).
- **The serializer is Newtonsoft.Json with default `MissingMemberHandling.Ignore`,** so additive nullable fields are safe in place. Resolved by reading the call sites (`EventJsonHandler.cs:22`, `MessageHelper.cs:87`, `PublisherClient.cs:256`).
- **`EventJsonHandler<T>` needs no change** — it already deserializes the body as the *registered* base type, which is exactly the polymorphic downcast. Resolved (FR-033).
- **Should the WebApp surface the version in event details?** Yes — the issue answers this and it is cheap, high ops value (User Story 4). Resolved as in scope; the exact UI placement on `event-details.tsx` is the implementer's call. Note the read model (`MessageEntity`) does not persist a version column today, so surfacing it may require carrying the `nimbus.message.version` application property into the stored/returned message — flagged in Assumptions.
- **Existing `NimBusSubscriberBuilder` collision guard is preserved; base+derived handlers coexist legitimately.** Resolved by reading `NimBusSubscriberBuilder.cs:113-149` (FR-040/FR-041).
- **Compile-time compatibility warnings belong to issue #35 (spec 018), not here.** Resolved — referenced, not specified (Out of Scope, FR-052).
