# Feature Specification: Source Generators (compile-time event-type registry, handler binding, NIMBUS diagnostics)

Feature Branch: `018-source-generators`
Created: 2026-05-28
Updated: 2026-05-28
Status: Proposed
Input: User description (GitHub issue #35 "Source Generators"): "NimBus discovers event types via reflection at startup — scanning loaded assemblies for IMessage implementations, reading [SessionKey] and (future) [MessageVersion] attributes, building registries used by the dispatcher and serializer. Reflection adds startup latency, defeats trimming/AOT, and pushes errors that should be compile-time (missing handler, ambiguous session key, version mismatch) to runtime. Source generators move this to compile time: faster cold start, AOT-friendly, IDE red squiggles for misconfiguration. Proposed scope: (1) event-type registry generator emitting a static GeneratedEventTypeRegistry; (2) handler-binding generator emitting explicit IEventHandler<T> registrations + diagnostics; (3) session-key validator; (4) options validator. Packaged as an analyzer in NimBus.Abstractions so SDK consumers get it automatically; existing reflection paths kept as fallback."

## Problem

NimBus discovers handlers and event types at startup through reflection, not compile time. The canonical scan is in `NimBus.SDK.Extensions.NimBusSubscriberBuilder`:

- `AddHandlersFromAssembly(Assembly)` → `DiscoverHandlerRegistrations(assembly)` → `GetLoadableTypes(assembly)` which calls `assembly.GetTypes()` (falling back to `ReflectionTypeLoadException.Types` on partial load failures).
- For each loadable, non-abstract, non-generic class it reflects over `Type.GetInterfaces()`, filters for closed `IEventHandler<T>` where `T : IEvent` (`IsEventHandlerInterface`), and pairs `(EventType, HandlerType)`.
- Each pair becomes a `HandlerRegistration` keyed on an **EventTypeId** computed at runtime via `new EventType(eventType).Id` — and `EventType.Id` is just `_type.Name` (the unqualified CLR type name; see `src/NimBus.Abstractions/Events/EventType.cs`).
- `EventHandlerProvider` (in `NimBus.SDK.EventHandlers`) keys its dispatch `ConcurrentDictionary<string, …>` on that same string EventTypeId, computed again by reflection in `RegisterHandler`.

The `[SessionKey]` attribute (`src/NimBus.Abstractions/Events/SessionKeyAttribute.cs`) carries a `string PropertyName`, read at runtime via `EventType.SessionKeyProperty` (`_type.GetCustomAttribute<SessionKeyAttribute>()?.PropertyName`). Whether that string names a real `string`-typed property is **never validated at compile time** — a typo in `[SessionKey(nameof(OrderId))]` (or a `[SessionKey("OrderId")]` literal) surfaces, at best, as a runtime session-id failure.

Consequences:

- **Cold start cost.** Every subscriber host pays a `GetTypes()` + per-type `GetInterfaces()` reflection walk at registration time.
- **Trimming / AOT hostility.** `assembly.GetTypes()`, `Type.MakeGenericType`, and `Activator.CreateInstance(typeof(EventJsonHandler<>).MakeGenericType(...))` (in `EventHandlerProvider.RegisterHandler`) are exactly the patterns the .NET trimmer and Native AOT cannot statically reason about. The Aspire publisher sample (`samples/AspirePubSub/AspirePubSub.Publisher`) cannot be `PublishTrimmed`/`PublishAot`-clean while these paths dominate.
- **Errors deferred to runtime.** A missing handler for a wired event, two distinct CLR types collapsing onto the same EventTypeId (the unqualified-name collision the builder throws on at *startup* today), and an invalid `[SessionKey]` reference are all discoverable at compile time but currently are not.

This spec adds an incremental source-generator project that emits a compile-time event-type registry and explicit handler bindings, plus NIMBUS-prefixed analyzer diagnostics, while keeping the existing reflection scan as a runtime fallback for assemblies that do not carry generated output.

## Scope

In scope:
- A new project `src/NimBus.SourceGenerators/` targeting `netstandard2.0` (the analyzer/generator target floor), referencing `Microsoft.CodeAnalysis.CSharp` 4.x and implementing **incremental generators only** (`IIncrementalGenerator`).
- **Event-type registry generator.** For each compilation that defines `IEvent` implementations, emit a static partial class `GeneratedEventTypeRegistry` (namespace-stable, one per compiled assembly) that lists every event type with its EventTypeId (the unqualified type name, matching `EventType.Id`), its `[SessionKey]` property name (if any), and a version slot (reserved; populated by spec 016 — see Out of Scope). A small consumption shim in the SDK prefers the generated registry when present and falls back to the reflection scan otherwise.
- **Handler-binding generator.** For each `IEventHandler<T>` implementation discovered in the compilation, emit explicit registration metadata equivalent to what `NimBusSubscriberBuilder.DiscoverHandlerRegistrations` produces by reflection, so `AddHandlersFromAssembly[Containing]` can consume a generated list instead of calling `assembly.GetTypes()`.
- **NIMBUS diagnostics** (analyzer side of the same package):
  - `NIMBUS001` — an event type is wired **for handling** (referenced from an explicit `AddHandler<TEvent,THandler>` registration site in this compilation) but has **no** `IEventHandler<T>` implementation in the compilation. The mere presence of `[SessionKey]` on an event does NOT make it "wired" — publisher-only and shared-contract assemblies legitimately define session-keyed events with no handler.
  - `NIMBUS002` — a `[SessionKey(...)]` reference names a property that does not exist on the event type, or exists but is not of type `string`.
  - `NIMBUS003` — two distinct CLR event types map to the same EventTypeId (unqualified type-name collision) — the same condition `NimBusSubscriberBuilder` throws on at runtime, surfaced at compile time.
  - `NIMBUS004` — a registered handler type is neither default-constructible nor resolvable from DI (no public constructor whose parameters are satisfiable), so per-message `GetRequiredService` would throw at dispatch.
- **Packaging.** The generator + analyzers ship **inside `NimBus.Abstractions`** as an analyzer asset (`<IncludeBuildOutput>`/analyzer `PackagePath="analyzers/dotnet/cs"`), so every project that references NimBus (transitively through the SDK) gets the diagnostics and generated registry with no opt-in. This mirrors how the solution already distributes analyzers globally (AsyncFixer, Meziantou.Analyzer, SecurityCodeScan.VS2019, SonarAnalyzer.CSharp, StyleCop.Analyzers via `Directory.Packages.props` `GlobalPackageReference`).
- **Documentation.** A new `docs/diagnostics.md` documenting each NIMBUS code with cause + fix.
- **AOT smoke test.** A `PublishTrimmed` (and best-effort `PublishAot`) pass over `samples/AspirePubSub/AspirePubSub.Publisher` proving the publisher path no longer trips trimmer warnings from NimBus event/handler discovery.

Out of scope:
- **Message-versioning diagnostics.** `[MessageVersion]` (spec 016 / issue #33) does not exist in the codebase today. The registry reserves a version slot and the generator infrastructure is built to be reused, but version-compat diagnostics (e.g. a NIMBUS005-class "version mismatch") belong to that spec. This spec must not invent `[MessageVersion]`.
- **Removing the reflection scan.** `NimBusSubscriberBuilder.DiscoverHandlerRegistrations` / `GetLoadableTypes` stay. Generated output takes precedence *when present*; the reflection path remains the fallback (dynamically-loaded plugin assemblies, assemblies that predate the generator, tests that build types at runtime).
- **Changing the EventTypeId scheme.** EventTypeId stays the unqualified type name (`EventType.Id => _type.Name`). The generator computes the same value; it does not introduce a fully-qualified or attribute-driven id.
- **A separate opt-in `NimBus.SourceGenerators` NuGet package.** Default is embed-in-Abstractions (see Open Questions).
- **Strongly-typed runtime options classes.** The options validator (FR-040) is a *compile-time analyzer* over `AddNimBusPublisher`/`AddNimBusSubscriber` call sites; it does not refactor `NimBusPublisherOptions`/`NimBusSubscriberOptions` or their runtime binding.

## User Scenarios & Testing

### User Story 1 - Generated registry replaces the startup reflection scan (Priority: P1)

As an adapter author hosting a NimBus subscriber, I want the set of event types and handlers in my assembly to be discovered at compile time and emitted into `GeneratedEventTypeRegistry`, so my process does not pay a `GetTypes()` reflection walk on every cold start and so the lookup is trimming-safe.

Why this priority: This is the core value of the feature — faster, AOT-friendly startup. Every other story builds on the generated registry existing.

Independent Test: Build an adapter assembly with two events and two handlers. Inspect the generator output (snapshot test) for a `GeneratedEventTypeRegistry` containing both EventTypeIds and both handler bindings. Run the host with the reflection scan instrumented; confirm `assembly.GetTypes()` is not called for the generated assembly.

Acceptance Scenarios:

1. Given an assembly with `OrderPlaced : Event` and `OrderPlacedHandler : IEventHandler<OrderPlaced>`, When the assembly compiles, Then a `GeneratedEventTypeRegistry` is emitted listing `OrderPlaced` with EventTypeId `"OrderPlaced"` and a binding to `OrderPlacedHandler`.
2. Given the SDK consumption shim, When `AddHandlersFromAssemblyContaining<TMarker>()` runs against a generated assembly, Then it enumerates the generated bindings instead of calling `assembly.GetTypes()`, producing the identical `HandlerRegistration` set (same EventTypeId, same `IsExplicit=false`).
3. Given an assembly that does NOT carry generated output (e.g. a dynamically loaded plugin), When the same call runs, Then the reflection fallback (`DiscoverHandlerRegistrations`) executes unchanged and registration succeeds.

---

### User Story 2 - Missing handler is a compile error, not a runtime surprise (Priority: P1)

As a developer, I want NIMBUS001 to flag an event that is wired up but has no `IEventHandler<T>` implementation, so the gap shows as an IDE squiggle instead of an `EventHandlerNotFoundException` at dispatch time.

Why this priority: A wired event with no handler currently fails only when a message of that type arrives — potentially in production. Moving it to compile time is the single biggest correctness win.

Independent Test: In a test compilation, declare an event referenced from an `AddHandler<TEvent,THandler>` site but provide no handler implementation. Assert the analyzer reports NIMBUS001 at that registration site. Separately, declare an event carrying `[SessionKey]` with no handler and no `AddHandler` reference (a shared-contract/publisher-only shape) and assert NIMBUS001 does **not** fire.

Acceptance Scenarios:

1. Given an event type wired via `builder.AddHandler<OrderPlaced, OrderPlacedHandler>()` where `OrderPlacedHandler` is removed, When the project builds, Then NIMBUS001 is reported on the `AddHandler` invocation.
2. Given an event type with a handler present, When the project builds, Then no NIMBUS001 is reported.
3. Given a project under a Release build (`TreatWarningsAsErrors`), When NIMBUS001 fires at its default severity (Warning), Then the build fails — matching the repo's Release policy.

---

### User Story 3 - Invalid [SessionKey] reference is caught at compile time (Priority: P1)

As a developer using `[SessionKey(nameof(OrderId))]`, I want NIMBUS002 when the named property does not exist on the event or is not a `string`, so a typo or wrong-type session key cannot silently break ordered delivery.

Why this priority: Session ordering is a headline NimBus feature (ADR-001). A bad session key degrades to per-message sessions or runtime failures that are hard to trace.

Independent Test: Declare `[SessionKey("OrderIdz")]` (typo) and `[SessionKey(nameof(Amount))]` where `Amount` is `decimal`. Assert NIMBUS002 fires on both, and does not fire on a valid `[SessionKey(nameof(OrderId))]` where `OrderId` is `string`.

Acceptance Scenarios:

1. Given `[SessionKey("OrderIdz")]` on an event with no `OrderIdz` member, When the project builds, Then NIMBUS002 is reported on the attribute argument.
2. Given `[SessionKey(nameof(Amount))]` where `Amount` is `decimal`, When the project builds, Then NIMBUS002 is reported with a message stating the property must be `string`.
3. Given `[SessionKey(nameof(OrderId))]` where `OrderId` is a public `string` property, When the project builds, Then no diagnostic is reported and the generated registry records `SessionKeyProperty = "OrderId"`.

---

### User Story 4 - Duplicate EventTypeId surfaces before runtime (Priority: P2)

As a maintainer of a large catalog, I want NIMBUS003 when two distinct CLR types collapse onto the same EventTypeId (unqualified name), so the ambiguity the subscriber builder throws on at startup is visible at build time instead.

Why this priority: The runtime guard already exists (`NimBusSubscriberBuilder` throws "Two distinct event types map to the same EventTypeId"). Compile-time detection is a strict improvement but the runtime net already catches it, so P2.

Independent Test: Declare `A.OrderPlaced : Event` and `B.OrderPlaced : Event` (same unqualified name, different namespaces) both with handlers. Assert NIMBUS003 fires naming both fully-qualified types.

Acceptance Scenarios:

1. Given two event types `Sales.OrderPlaced` and `Fulfillment.OrderPlaced` both implementing `IEvent`, When the project builds, Then NIMBUS003 is reported naming both full type names and the shared EventTypeId `"OrderPlaced"`.
2. Given the same two types live in different *compilations* (different assemblies), When each compiles alone, Then NIMBUS003 does not fire (the collision is cross-assembly and only the runtime builder can see both) — documented as a known limitation in `docs/diagnostics.md`.

---

### User Story 5 - Handler that cannot be resolved is flagged (Priority: P2)

As a developer, I want NIMBUS004 when a registered handler type has no public constructor that DI can satisfy (and is not default-constructible), so I learn at build time rather than via a `GetRequiredService` throw at dispatch.

Why this priority: Real, but narrower than NIMBUS001 — most handlers have a constructor whose dependencies are registered. The analyzer can only reason about constructor *shape*, not the full DI graph, so it is best-effort.

Independent Test: Declare a handler whose only constructor is non-public, and one whose constructor takes a primitive that DI cannot supply. Assert NIMBUS004 fires; assert it does not fire for a handler with a parameterless or interface-dependency constructor.

Acceptance Scenarios:

1. Given a handler with only a `private` constructor, When the project builds, Then NIMBUS004 is reported on the handler type.
2. Given a handler with a public constructor taking `(ILogger<T>)`, When the project builds, Then NIMBUS004 is not reported (interface/service dependencies are assumed resolvable).
3. Given a handler with a parameterless constructor, When the project builds, Then NIMBUS004 is not reported.

---

### User Story 6 - Aspire publisher trims and runs (Priority: P2)

As a release engineer, I want `samples/AspirePubSub/AspirePubSub.Publisher` to `PublishTrimmed` without NimBus-originated trimmer warnings and to run correctly, proving the generated path removed the reflection that defeated trimming.

Why this priority: AOT/trim-clean is the secondary headline benefit. It validates the registry actually displaced the reflection rather than merely sitting alongside it on the publisher path.

Independent Test: `dotnet publish` the publisher with `PublishTrimmed=true`; assert zero `IL2xxx` trim warnings attributed to NimBus event/handler discovery; run the published binary and publish one event end-to-end against the emulator.

Acceptance Scenarios:

1. Given the generator is active, When the Aspire publisher is published trimmed, Then no NimBus event-discovery trim warning is emitted.
2. Given the published publisher runs, When it publishes an event, Then the message is delivered (the EventTypeId stamped matches the generated registry value).

---

## Edge Cases

- **Assembly with no event types.** The generator emits no `GeneratedEventTypeRegistry` (or an empty one); the SDK shim falls through to the reflection path, which also yields nothing. No diagnostic.
- **Generic event type** (`OrderPlaced<T>`). The runtime scan already skips `ContainsGenericParameters` types; the generator must skip open generics too. Closed constructions are not separate CLR declarations, so they are not enumerated.
- **Event implementing `IEvent` indirectly** (via a base class such as `Event`). The generator must walk the base chain / interface set the same way `typeof(IEvent).IsAssignableFrom(...)` does, so `OrderPlaced : Event` is recognised even though `Event` (not `OrderPlaced`) declares the interface.
- **`[SessionKey]` on an abstract base event.** The attribute is `Inherited = true`. The generator resolves the property against the *concrete* event type's members, honouring inheritance, before emitting NIMBUS002.
- **`[SessionKey("literal")]` vs `[SessionKey(nameof(X))]`.** Both compile to the same attribute string argument. NIMBUS002 validates the resolved string against the type's members regardless of whether `nameof` or a literal was used.
- **Partial event class split across files.** The generator sees the merged symbol from the semantic model; members declared in any partial part are visible to the NIMBUS002 check.
- **Handler implementing two `IEventHandler<T>` for two events.** Each closed interface yields its own binding/registry row — matching the reflection scan's `SelectMany`.
- **Two handlers for one event in the same assembly (non-explicit).** The runtime builder throws "Multiple handlers were discovered". The generator emits both bindings and leaves the ambiguity to the existing runtime guard (it is not a NIMBUS001/003 case); documented in `docs/diagnostics.md`.
- **Dynamically loaded / reflection-emit assemblies.** No generated output exists; the SDK shim uses the reflection fallback. This is the explicit reason the fallback is retained.
- **Publisher-only / shared-contract assembly: events (with or without `[SessionKey]`) defined but no handler and no `AddHandler` site.** Not "wired for handling" — NIMBUS001 MUST NOT fire. This is the dominant shape of a contracts package or a publisher service: the event type and its `[SessionKey]` are declared here, the `IEventHandler<T>` lives in the consumer project. A `[SessionKey]`-bearing event is explicitly NOT a NIMBUS001 trigger (FR-030). (NIMBUS002 still validates the `[SessionKey]` argument itself in such assemblies — that check is independent of whether a handler exists.)
- **Cross-assembly EventTypeId collision.** Not visible to a per-compilation generator; only the runtime builder catches it. NIMBUS003 is intra-compilation only (documented limitation).

## Requirements

### Functional Requirements

#### Generator project & packaging

- FR-001: A new project `src/NimBus.SourceGenerators/NimBus.SourceGenerators.csproj` MUST be added, targeting `netstandard2.0`, with `<EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>` and `<IsPackable>false</IsPackable>` (it ships *inside* `NimBus.Abstractions`, not as its own package).
- FR-002: The project MUST reference `Microsoft.CodeAnalysis.CSharp` 4.x and `Microsoft.CodeAnalysis.Analyzers`, with `PrivateAssets="all"` and `Pkg…`/`GeneratePathProperty` as needed. Because `Directory.Packages.props` sets `ManagePackageVersionsCentrally=false`, the analyzer package versions are declared on the project's own `PackageReference` items (not as central `<PackageVersion>` entries).
- FR-003: All generators MUST be incremental (`IIncrementalGenerator` with `IncrementalValueProvider`/`IncrementalValuesProvider` pipelines). Non-incremental `ISourceGenerator` is not permitted.
- FR-004: `NimBus.Abstractions.csproj` MUST reference the generator project as an analyzer asset so the build output is packed under `analyzers/dotnet/cs` and applied to every consumer (`<ProjectReference Include="..\NimBus.SourceGenerators\NimBus.SourceGenerators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />` plus the matching pack metadata). This makes the analyzer transitive to everything that references the SDK, consistent with the global-analyzer distribution pattern in `Directory.Packages.props`.
- FR-005: The generator package MUST NOT add a runtime dependency to consumers — it contributes generated source and diagnostics only. `Microsoft.CodeAnalysis.*` MUST NOT flow to consumers as a runtime reference.

#### Event-type registry generator

- FR-010: The generator MUST identify every type in the compilation that implements `NimBus.Core.Events.IEvent` (directly or through a base type such as `NimBus.Core.Events.Event`), excluding abstract types and open generics — mirroring the runtime filter `type is { IsClass: true, IsAbstract: false } && !type.ContainsGenericParameters`.
- FR-011: For each such type, the generator MUST compute its EventTypeId as the **unqualified type name**, identical to `EventType.Id` (`_type.Name`). It MUST NOT use the namespace-qualified name.
- FR-012: The generator MUST emit a static class `GeneratedEventTypeRegistry` (one per compiled assembly, in a deterministic NimBus-owned namespace) exposing the event types with, per entry: EventTypeId, the CLR type, the `[SessionKey]` property name (or null), and a reserved version field defaulting to a null/empty placeholder (populated by spec 016; this spec leaves it empty).
- FR-013: The `[SessionKey]` property name MUST be read from `NimBus.Core.Events.SessionKeyAttribute.PropertyName` resolved on the concrete type (honouring `Inherited = true`), matching `EventType.SessionKeyProperty`.
- FR-014: The SDK MUST gain a consumption shim that, given a target assembly, prefers the generated registry/bindings when the assembly carries them and otherwise falls back to the existing reflection scan. The shim MUST produce a `HandlerRegistration` set (EventTypeId, EventType, HandlerType, IsExplicit) byte-for-byte equivalent to what `NimBusSubscriberBuilder.DiscoverHandlerRegistrations` produces today for the same source.
- FR-015: When generated output is present, `EventHandlerProvider`/the dispatch path MUST be able to resolve the per-event `EventJsonHandler<T>` adapter without `Activator.CreateInstance(typeof(EventJsonHandler<>).MakeGenericType(...))` — the generated bindings supply a statically-typed factory so the AOT-hostile `MakeGenericType` call is bypassed on the generated path.

#### Handler-binding generator

- FR-020: For each non-abstract, non-generic class in the compilation implementing a closed `IEventHandler<T>` where `T : IEvent`, the generator MUST emit a binding row `(EventTypeId, EventType, HandlerType)`, mirroring `IsEventHandlerInterface` + the `SelectMany` over `GetInterfaces()` in the reflection scan.
- FR-021: A handler implementing multiple `IEventHandler<T>` MUST emit one binding per closed interface.
- FR-022: Generated bindings MUST be marked as non-explicit registrations (`IsExplicit = false`), since assembly-scan registrations are non-explicit today; explicit `AddHandler<TEvent,THandler>()` calls remain authored in user code and continue to take precedence per the existing builder dedupe rules.

#### Diagnostics

- FR-030: `NIMBUS001` (missing handler) MUST be reported only when an event type is **wired for handling** — i.e. it appears as the `TEvent` of an explicit `AddHandler<TEvent,THandler>` registration site in this compilation — but no `IEventHandler<TEvent>` implementation for it exists in the compilation. The diagnostic location MUST be the `AddHandler` invocation. Default severity: Warning. The trigger MUST NOT include "the event carries `[SessionKey]`" nor "the event type is merely defined": a `[SessionKey]`-bearing event with no handler and no `AddHandler` reference is the normal shape of a **shared-contract or publisher-only assembly** (the producer declares the contract and its session key; the subscriber lives in a different project). Firing NIMBUS001 there would be a false positive — and because Release builds treat warnings as errors (NFR-005), a false positive would break the build of every publisher/contract project that references the SDK. Restricting the trigger to `AddHandler` sites guarantees NIMBUS001 only fires where a handler was actually expected.
- FR-031: `NIMBUS002` (invalid session key) MUST be reported when a `[SessionKey(...)]` argument names a member that (a) does not exist on the concrete event type, or (b) exists but is not of type `string`. The location MUST be the attribute argument. Default severity: Warning. (Rationale: `SessionKeyAttribute.PropertyName` is consumed as a `string` property name; `EventType.SessionKeyProperty` feeds session-id derivation, which only makes sense for a `string` member.)
- FR-032: `NIMBUS003` (duplicate EventTypeId) MUST be reported when two distinct event-type symbols in the same compilation share an EventTypeId, naming both fully-qualified type names and the shared id — the compile-time analogue of the `InvalidOperationException` `NimBusSubscriberBuilder` throws at startup. Default severity: Warning.
- FR-033: `NIMBUS004` (handler not DI-resolvable) MUST be reported when a handler type is neither default-constructible (no accessible parameterless constructor) nor has an accessible constructor whose parameters are all non-primitive service-like types (interfaces, abstract classes, or concrete services). Primitive/string/struct constructor parameters that DI cannot supply trigger the diagnostic. Default severity: Warning. The analyzer MUST document that it reasons about constructor *shape* only, not the full DI graph.
- FR-034: All diagnostics MUST use the `NIMBUS` prefix with the numeric ids above and a stable category (e.g. `NimBus.Usage`). Each MUST carry a `helpLinkUri` pointing at the matching section of `docs/diagnostics.md`.
- FR-035: Diagnostics MUST be individually suppressible via standard mechanisms (`#pragma warning disable NIMBUSxxx`, `.editorconfig` `dotnet_diagnostic.NIMBUSxxx.severity`).

#### Options validator

- FR-040: An analyzer MUST validate `AddNimBusPublisher`/`AddNimBusSubscriber` call sites for compile-time-detectable misconfiguration analogous to the existing runtime guards — e.g. an empty/whitespace `Endpoint` literal (the runtime throws `ArgumentException "Endpoint must be specified."`). Where the value is a literal, the analyzer reports it; where it is only known at runtime, no diagnostic is emitted. Reserved-event-type-id and connection-string-env-var checks are best-effort and only over literals.
- FR-041: The options validator MUST NOT change the runtime `NimBusPublisherOptions`/`NimBusSubscriberOptions` types or their binding. It is purely a compile-time check layered over existing call sites.

#### Fallback & precedence

- FR-050: The reflection scan (`NimBusSubscriberBuilder.DiscoverHandlerRegistrations` / `GetLoadableTypes`) MUST remain functional and unchanged in behaviour for assemblies without generated output.
- FR-051: When both generated output and a reflection result are available for the same assembly, the generated output MUST take precedence; the result set MUST be identical, so precedence is observable only as a performance/trimming difference, not a behavioural one.
- FR-052: The generated path MUST preserve the existing runtime guards for conditions a per-compilation generator cannot see (cross-assembly EventTypeId collision, multiple-handlers-for-one-event ambiguity). Those `InvalidOperationException`s in `NimBusSubscriberBuilder` MUST still be reachable.

#### Documentation

- FR-060: A new `docs/diagnostics.md` MUST document NIMBUS001–NIMBUS004 (and the options-validator diagnostic), each with: id, title, category, default severity, cause, an example of triggering code, and the fix. Known limitations (cross-assembly NIMBUS003, best-effort NIMBUS004) MUST be stated.
- FR-061: `docs/architecture.md` (or `docs/sdk-api-reference.md`) MUST gain a short section describing the generated-registry-vs-reflection-fallback model and how to disable/suppress the generator if needed.

#### Tests

- FR-070: The generator project MUST have **snapshot tests** (e.g. via `Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing` / a Verify-style snapshot) asserting the emitted `GeneratedEventTypeRegistry` and handler-binding source for representative inputs (single event+handler, multiple, `[SessionKey]` present, generic skipped, base-class `IEvent`).
- FR-071: Analyzer tests MUST assert each NIMBUS diagnostic fires on a triggering compilation and does NOT fire on a valid one, including location assertions for NIMBUS002 (attribute argument).
- FR-072: An equivalence test MUST assert the SDK shim's generated-path `HandlerRegistration` set equals the reflection-path set for the same source assembly.
- FR-073: The existing E2E suite (`tests/NimBus.EndToEnd.Tests`) MUST continue to pass with the generator active (no behavioural regression in publish/receive, routing, sessions).
- FR-074: A trim/AOT smoke test over `samples/AspirePubSub/AspirePubSub.Publisher` MUST be part of acceptance (CI step or documented manual gate): `PublishTrimmed` with zero NimBus-originated trim warnings, plus a publish-one-event run.

### Non-Functional Requirements

- NFR-001: The generators MUST be incremental and cache-friendly — re-running on an unchanged compilation MUST not re-emit (correct use of `IncrementalGenerator` providers and value equality on the extracted models), so IDE responsiveness is not degraded.
- NFR-002: Generated source MUST be deterministic (stable ordering of registry entries, stable naming) so build outputs are reproducible and snapshot tests are stable.
- NFR-003: The generator MUST add no runtime NuGet dependency to consumers (FR-005); the only shipped artifact is generated C# plus the analyzer assembly under `analyzers/dotnet/cs`.
- NFR-004: The generated path MUST be trimming- and AOT-friendly — no `assembly.GetTypes()`, no `Type.MakeGenericType`/`Activator.CreateInstance` over event/handler types on the generated path. This is the measurable condition for User Story 6.
- NFR-005: Diagnostics MUST not produce false positives on the existing solution: building `src/NimBus.sln` with the generator active MUST yield zero NIMBUS warnings on first-party code (or any that do appear MUST be fixed, since Release builds treat warnings as errors).
- NFR-006: The generator MUST target `netstandard2.0` (the supported analyzer runtime), even though the rest of the solution targets `net10.0` — analyzers load into the compiler/IDE host, not the app.

## Key Entities

- **`GeneratedEventTypeRegistry`** — new generated static class, one per compiled assembly. Lists event types with EventTypeId (`= type.Name`), `[SessionKey]` property name, CLR type, and a reserved version slot. Consumed by the SDK shim in preference to the reflection scan.
- **`NimBusSubscriberBuilder` (existing)** — `DiscoverHandlerRegistrations` / `GetLoadableTypes` / `AddHandlerRegistration`. Retained as the fallback; the new shim feeds it generated rows when present. Its runtime guards (duplicate EventTypeId, multiple handlers) remain the cross-assembly safety net.
- **`EventType` / `IEventType` (existing, `src/NimBus.Abstractions/Events/EventType.cs`)** — defines `Id => _type.Name` and `SessionKeyProperty`. The generator computes the same EventTypeId; the registry mirrors `SessionKeyProperty`.
- **`SessionKeyAttribute` (existing)** — `string PropertyName`, `Inherited = true`. NIMBUS002 validates the named member exists and is `string`.
- **`IEventHandler<T>` (existing, `src/NimBus.SDK/EventHandlers/IEventHandler.cs`)** — `where T : IEvent`. The handler-binding generator enumerates closed implementations; NIMBUS001/NIMBUS004 reason about them.
- **`EventHandlerProvider` (existing)** — dispatch table keyed on EventTypeId string; today builds `EventJsonHandler<T>` via `Activator.CreateInstance(...MakeGenericType...)`. The generated path supplies statically-typed factories so this AOT-hostile call is bypassed.
- **`NimBus.SourceGenerators` project** — new `netstandard2.0` incremental-generator + analyzer assembly, packed into `NimBus.Abstractions` under `analyzers/dotnet/cs`.
- **`docs/diagnostics.md`** — new doc; the canonical NIMBUS diagnostics reference.

## Success Criteria

### Measurable Outcomes

- SC-001: For an assembly carrying generated output, the subscriber registration path does not call `assembly.GetTypes()` (verified by instrumentation/counter in a test).
- SC-002: The generated-path `HandlerRegistration` set is identical (EventTypeId, EventType, HandlerType, IsExplicit) to the reflection-path set for the same source (FR-072 equivalence test passes).
- SC-003: NIMBUS001–NIMBUS004 each fire on a triggering compilation and stay silent on a valid one; NIMBUS002's location is the attribute argument (analyzer tests pass).
- SC-004: `samples/AspirePubSub/AspirePubSub.Publisher` publishes trimmed (`PublishTrimmed`) with zero NimBus-originated trim warnings and successfully publishes an event when run.
- SC-005: `dotnet build src/NimBus.sln` and `dotnet test src/NimBus.sln` pass with the generator active, including the existing E2E suite, with zero NIMBUS warnings on first-party code.
- SC-006: Snapshot tests for `GeneratedEventTypeRegistry` and handler bindings are stable across repeated builds (deterministic output).
- SC-007: A consumer that references only the NimBus SDK/Abstractions automatically receives the analyzer and generated registry with no extra package reference or MSBuild opt-in.

## Assumptions

- EventTypeId is and remains the unqualified CLR type name (`EventType.Id => _type.Name`). The generator deliberately reproduces this, including its known cross-namespace collision behaviour, rather than changing the id scheme.
- `IEvent`, `Event`, `EventType`, `IEventType`, and `SessionKeyAttribute` live (physically) under `src/NimBus.Abstractions/Events/` but are declared in the `NimBus.Core.Events` namespace and forwarded from `NimBus.Core` via `src/NimBus.Core/TypeForwarders.cs`. The generator matches on the `NimBus.Core.Events.IEvent` symbol regardless of which assembly defines it.
- `IEventHandler<T>` is declared in `NimBus.SDK.EventHandlers` (`where T : IEvent`). The generator matches that symbol; the analyzer is shipped from `NimBus.Abstractions` but its rules reference the SDK interface by full name (resolved from the consumer's compilation, which references the SDK).
- The Aspire sample referenced by the issue as "NimBus.Aspire" is actually `samples/AspirePubSub/` (publisher at `samples/AspirePubSub/AspirePubSub.Publisher`). That is the AOT smoke-test target.
- `Microsoft.CodeAnalysis.CSharp` 4.x is available and compatible with the .NET 10 SDK/Roslyn shipped in CI. Analyzer projects target `netstandard2.0` per Roslyn's host requirements.
- The solution already distributes analyzers globally (`GlobalPackageReference` in `Directory.Packages.props`); embedding one more analyzer via `NimBus.Abstractions` is consistent with that established pattern.
- `[MessageVersion]` does not exist yet; the version slot in the registry is a forward-compatibility placeholder for spec 016 and is left empty by this spec.

## Out of Scope

- Message-versioning attributes and version-compat diagnostics (spec 016 / issue #33) — only the reusable generator infrastructure and an empty version slot are provided.
- Replacing or removing the reflection scan; it stays as the fallback.
- Changing the EventTypeId derivation, the wire format, or the dispatch keying.
- A standalone opt-in `NimBus.SourceGenerators` NuGet package (default is embed-in-Abstractions).
- Refactoring `NimBusPublisherOptions`/`NimBusSubscriberOptions` into strongly-typed bound options; the options validator is compile-time only.
- Cross-assembly diagnostics (the per-compilation generator cannot see other assemblies; NIMBUS003 is intra-compilation, backstopped by the runtime builder guard).
- A code-fix provider (auto-generate a missing handler stub, fix a `[SessionKey]` typo). Diagnostics only in v1; code fixes are a possible follow-up.

## Open Questions

- **Package placement.** Ship inside `NimBus.Abstractions` (everyone who references NimBus gets it, transparent, no opt-in) or as a separate opt-in `NimBus.SourceGenerators` package? The issue's lean and this spec's default is **embed in Abstractions** (transparent, no downside). Confirm there is no scenario where a consumer references `NimBus.Abstractions` but wants the analyzer *off* by default (it can still be suppressed via `.editorconfig`).
- **NIMBUS001 "wired" definition.** The issue frames it as missing handler "for each `services.AddNimBusSubscriber(...)` discovered". But discovery is via `AddHandlersFromAssembly`/`AddHandler`, not `AddNimBusSubscriber` directly, and many events are publish-only (no handler expected in the publishing assembly). This spec scopes NIMBUS001 to events that are *explicitly wired* (`AddHandler<TEvent,THandler>` reference or `[SessionKey]` present) to avoid false positives on publish-only events. Confirm this is the desired trigger condition rather than "every `IEvent` in the assembly".
- **`[SessionKey]` non-string types.** The attribute stores a raw `string PropertyName` and `EventType.SessionKeyProperty` returns it verbatim; the runtime does not currently enforce that the named property is `string`. NIMBUS002 *adds* that constraint at compile time. Confirm session keys must always be `string` (the spec assumes yes, since the session id is a string), or whether NIMBUS002 should only check existence and leave type to a warning sub-level.
- **NIMBUS004 precision.** Constructor-shape analysis cannot see the full DI graph (e.g. a concrete dependency registered elsewhere). Should NIMBUS004 default to `Info`/`Hidden` rather than `Warning` to avoid Release-build failures on legitimately-resolvable handlers? This spec defaults it to Warning but flags the false-positive risk.

## Resolved Questions

- The reflection scan stays as a fallback; generated output takes precedence when present. Resolved — dynamically-loaded assemblies and runtime-built types require it, and it de-risks the rollout (generated and reflection paths must produce identical results, FR-051/FR-072).
- EventTypeId scheme is unchanged (unqualified type name). Resolved — changing it would break the wire contract and the existing dispatch keying in `EventHandlerProvider`.
- Generators are incremental-only (`IIncrementalGenerator`). Resolved — per the issue's explicit "incremental generators only" and IDE responsiveness (NFR-001).
- NIMBUS003 (duplicate EventTypeId) is intra-compilation; the existing runtime `InvalidOperationException` in `NimBusSubscriberBuilder` remains the cross-assembly backstop. Resolved — a per-compilation generator cannot see other assemblies.
- The AOT smoke-test target is `samples/AspirePubSub/AspirePubSub.Publisher` (the issue's "NimBus.Aspire" is a misnomer). Resolved by repo inspection.
- Message-versioning diagnostics belong to spec 016; this spec only reserves the registry version slot and the reusable generator infra. Resolved.
