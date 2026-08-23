# Plan 5: Decompose `StrictMessageHandler`

## Goal

Reduce the size and change risk of `StrictMessageHandler` by moving request-specific orchestration behind internal collaborators while keeping the public handler, constructor compatibility bridges, message semantics, and settlement ordering unchanged.

## Why this plan is last

`StrictMessageHandler` is the core lifecycle state machine. Its ordering is operational behavior: a response may need to be published before completion, sessions must be unblocked before deferred siblings drain, and some exceptions are intentionally rethrown while others are converted into settlement. A broad rewrite would be riskier than the current size. This plan therefore uses characterization tests and migrates one flow at a time.

## Scope

In scope:

- `NimBus.Core/Messages/StrictMessageHandler.cs`
- focused internal lifecycle collaborators in `NimBus.Core/Messages`
- `StrictMessageHandlerTests.cs` and related heartbeat, inbox, retry, handoff, and deferred-processing tests
- DI construction in the SDK where required

Out of scope:

- changing message types or wire contracts;
- changing retry, failure-disposition, inbox, heartbeat, or handoff semantics;
- changing the separate deferred-processor architecture;
- deleting obsolete public constructors or APIs;
- replacing the message lifecycle with an external state-machine package.

## Target design

Keep `StrictMessageHandler : MessageHandler` as the only public entry point. Its overrides delegate to internal request processors sharing a small lifecycle-operations collaborator:

```text
StrictMessageHandler
├── EventRequestProcessor
├── RetryRequestProcessor
├── ResubmissionRequestProcessor
├── ManagerRequestProcessor       (skip and handoff settlement)
├── ContinuationRequestProcessor
├── MessageLifecycleOperations    (response, block/unblock, completion, drain)
└── FailureProcessor              (classification, discard, retry scheduling)
```

The exact class split may be reduced if two processors are inseparable, but ownership must follow message flow rather than arbitrary method-count targets.

## Compatibility constraints

- Preserve every existing public constructor. Older overloads remain backward-compatible bridges to the new internal composition.
- Do not add overlapping same-arity nullable overloads; existing calls such as `new StrictMessageHandler(..., null, null)` must remain unambiguous.
- Preserve the base `MessageHandler` contract and public override signatures.
- Preserve exact settlement count and order, session ownership checks, deferred-sequence restoration, duplicate recording behavior, cancellation propagation, exception types, and logging intent.
- Keep the 30-second best-effort deferred restore bound.
- Preserve the rule that heartbeat traffic bypasses inbox and blocked-session checks.
- Do not merge the legacy continuation path with the separate deferred processor.

## Phase 1: build an ordering test harness

1. Add a recording `IMessageContext` test double or extend the existing one to capture ordered operations:
   - response publication;
   - block/unblock;
   - receive/defer/restore;
   - continuation publication;
   - complete, abandon, and dead-letter.
2. Add a recording response service and event handler.
3. Write table-driven characterization tests for every public override covering success and each handled exception branch.
4. Assert operation sequences, not only final state. Examples:
   - normal request: handle → resolution response → complete;
   - handler failure: error response → block → complete → optional retry response;
   - retry success: verify owner → handle → unblock → drain → response → complete;
   - skip: authorize → verify owner → unblock → drain → skip response → complete;
   - continuation unexpected failure: restore the deferred reference before rethrowing.
5. Add explicit “settled exactly once” assertions and cancellation tests.
6. Run the new tests against the current handler and correct the expected sequence to match proven behavior, not assumptions.

## Phase 2: introduce dependency composition without moving flows

1. Create an internal immutable dependency container for the existing collaborators if it reduces constructor forwarding.
2. Keep all public constructor overloads and have them build the same internal dependency graph.
3. Extract `MessageLifecycleOperations` for thin operations currently repeated across flows: responses, complete, block/unblock, session guards, duplicate completion, and deferred draining.
4. Keep ordering in the existing handler methods during this phase.
5. Run all characterization tests after each extracted operation group.

Do not hide multiple side effects behind a vaguely named helper. Operations such as “unblock and drain” may be grouped only when every caller requires the same order and the tests assert it.

## Phase 3: extract failure processing

1. Move exception classification from `HandleEventContent` into `FailureProcessor`.
2. Move discard response/logging and retry-policy evaluation into the same cohesive collaborator.
3. Preserve direct propagation of `TransientException`, `EventHandlerNotFoundException`, `PermanentFailureException`, and caller-requested cancellation.
4. Preserve the legacy permanent-failure classifier bridge and its obsolete annotations.
5. Run failure-disposition, retry-policy, cancellation, logging, and dead-letter tests.

## Phase 4: migrate request processors one at a time

Use this order from least coupled to most coupled:

1. `ManagerRequestProcessor`: skip, handoff completed, and handoff failed.
2. `ContinuationRequestProcessor`.
3. `EventRequestProcessor`.
4. `RetryRequestProcessor`.
5. `ResubmissionRequestProcessor`.

For each processor:

1. Add a direct processor test using the recording harness.
2. Move the existing method body with no semantic cleanup.
3. Delegate the public override from `StrictMessageHandler`.
4. Run direct processor tests and the complete `StrictMessageHandlerTests` suite.
5. Compare logs, thrown exceptions, response calls, session state, deferred draining, and settlement order.
6. Commit before starting the next processor.

Retry and resubmission move last because they combine duplicate detection, session ownership, handoff parking, unblock/drain behavior, failure handling, and compatibility paths.

## Phase 5: simplify the public facade

1. Reduce `StrictMessageHandler` to public constructors, collaborator construction, and public dispatch overrides.
2. Keep XML documentation on all public members.
3. Remove only private helpers proven unused after processor extraction.
4. Update `docs/architecture.md` to describe the handler as a facade over request-specific lifecycle processors.
5. Add an architecture test ensuring the public handler remains the SDK-visible entry point and internal processors do not become public API.

## Verification

Fast loop:

```powershell
dotnet test tests/NimBus.Core.Tests/NimBus.Core.Tests.csproj -c Release --filter "FullyQualifiedName~StrictMessageHandler"
dotnet test tests/NimBus.Core.Tests/NimBus.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Heartbeat"
```

Cross-layer regression gate:

```powershell
dotnet test tests/NimBus.Core.Tests/NimBus.Core.Tests.csproj -c Release
dotnet test tests/NimBus.SDK.Tests/NimBus.SDK.Tests.csproj -c Release
dotnet test tests/NimBus.ServiceBus.Tests/NimBus.ServiceBus.Tests.csproj -c Release
dotnet test tests/NimBus.EndToEnd.Tests/NimBus.EndToEnd.Tests.csproj -c Release
```

Final gate:

```powershell
dotnet build src/NimBus.sln -c Release
dotnet test src/NimBus.sln -c Release --no-build
```

Review the final diff specifically for changed await order, catch ordering, cancellation tokens, logging metadata, and obsolete compatibility annotations.

## Proposed pull requests

1. `test(core): characterize strict message lifecycle ordering`
2. `refactor(core): extract lifecycle and failure operations`
3. `refactor(core): extract manager and continuation processors`
4. `refactor(core): extract event retry and resubmission processors`
5. `docs(core): document strict lifecycle processor boundaries`

## Exit criteria

- `StrictMessageHandler` remains the compatible public facade.
- Every request flow has focused direct tests plus facade-level regression coverage.
- Settlement, response, session, deferred, retry, duplicate, heartbeat, and cancellation ordering is unchanged.
- No public constructor or wire-contract break is introduced.
- Core, SDK, Service Bus, end-to-end, and full solution Release gates pass.
