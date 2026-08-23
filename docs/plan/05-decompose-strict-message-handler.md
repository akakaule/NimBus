# Plan 5: Decompose `StrictMessageHandler`

## Goal

Reduce repetition and change risk in `StrictMessageHandler` by extracting shared lifecycle and failure helpers while keeping request ordering in the public handler unless a later decision proves further decomposition worthwhile.

## Risk posture

`StrictMessageHandler` is the core lifecycle state machine, but at roughly 839 lines it is the smallest target in this roadmap and already has a test file more than twice its size. Its ordering is operational behavior: a response may need to be published before completion, sessions must be unblocked before deferred siblings drain, and some exceptions are intentionally rethrown while others are converted into settlement. Phases 1–3 are the committed scope. Moving request flows into separate processors is conditional and requires a new review after the helper extractions land.

## Scope

In scope:

- `NimBus.Core/Messages/StrictMessageHandler.cs`
- focused internal lifecycle collaborators in `NimBus.Core/Messages`
- `tests/NimBus.Core.Tests/StrictMessageHandlerTests.cs`
- `tests/NimBus.Core.Tests/Messages/StrictMessageHandlerHeartbeatTests.cs`
- related inbox, retry, handoff, and deferred-processing tests
- DI construction in the SDK where required

Out of scope:

- changing message types or wire contracts;
- changing retry, failure-disposition, inbox, heartbeat, or handoff semantics;
- changing the separate deferred-processor architecture;
- deleting obsolete public constructors or APIs;
- replacing the message lifecycle with an external state-machine package;
- consolidating the other `FakeMessageContext` or `TestMessageContext` doubles in `ResponseServiceTests`, `ExtensionFrameworkTests`, `BuiltInMiddlewareTests`, or the heartbeat test file.

## Target design

Keep `StrictMessageHandler : MessageHandler` as the only public entry point. The committed target is deliberately small:

```text
StrictMessageHandler
├── MessageLifecycleOperations    (response, block/unblock, completion, drain)
└── FailureProcessor              (classification, discard, retry scheduling)
```

Request-specific methods remain in `StrictMessageHandler` through Phase 3. A conditional follow-up may introduce processors only if the decision gate in Phase 4 is met.

## Compatibility constraints

- Preserve every existing public constructor. Older overloads remain backward-compatible bridges to the new internal composition.
- Do not add overlapping same-arity nullable overloads; existing calls such as `new StrictMessageHandler(..., null, null)` must remain unambiguous.
- Preserve the base `MessageHandler` contract and public override signatures.
- Preserve exact settlement count and order, session ownership checks, deferred-sequence restoration, duplicate recording behavior, cancellation propagation, exception types, and logging intent.
- Keep the 30-second best-effort deferred restore bound.
- Preserve the rule that heartbeat traffic bypasses inbox and blocked-session checks.
- Do not merge the legacy continuation path with the separate deferred processor.

## Phase 1: build an ordering test harness

1. Extend only `FakeMessageContext` in `tests/NimBus.Core.Tests/StrictMessageHandlerTests.cs` to capture ordered operations:
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

Do not consolidate or modify the four other message-context doubles as part of this plan. The heartbeat-specific double remains local to `StrictMessageHandlerHeartbeatTests.cs`; that file must explicitly preserve the heartbeat-before-inbox-and-session-guards behavior.

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

## Phase 4: conditional decision gate

Stop after Phase 3 and review the resulting handler before creating request-processor classes. Continue only if all of the following are true:

- repeated multi-step orchestration remains in at least two request flows after `MessageLifecycleOperations` and `FailureProcessor` are in place;
- extracting that orchestration has a clearer ownership boundary than leaving the readable flow methods together;
- the ordering harness covers every branch that would move;
- a focused design review concludes that the benefit exceeds the added indirection.

If those conditions are not met, close the plan after documentation and verification. The default decision is to stop.

## Conditional Phase 5: migrate request processors

If Phase 4 explicitly approves further work, migrate one processor at a time in this order:

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

After each processor, repeat the Phase 4 decision: stop if the remaining flow methods are clearer in the facade than behind another collaborator.

## Documentation and facade cleanup

1. Keep `StrictMessageHandler` as the public SDK-visible entry point and preserve all public dispatch overrides.
2. Keep XML documentation on all public members.
3. Remove only private helpers proven unused after the approved extractions.
4. Update `docs/architecture.md` to describe the lifecycle and failure helper boundaries. Mention request processors only if Conditional Phase 5 actually occurs.
5. Add an architecture test ensuring new collaborators remain internal and the public handler remains the SDK-visible entry point.

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
3. `docs(core): document strict lifecycle helper boundaries`

Only after an approved Phase 4 decision:

4. `refactor(core): extract approved request processors`

## Exit criteria

- `StrictMessageHandler` remains the compatible public facade.
- The ordering harness covers every request flow and handled exception branch.
- Shared lifecycle and failure behavior has focused direct tests.
- Settlement, response, session, deferred, retry, duplicate, heartbeat, and cancellation ordering is unchanged.
- No public constructor or wire-contract break is introduced.
- Request processors are not required for completion; if introduced, the Phase 4 decision is documented.
- Core, SDK, Service Bus, end-to-end, and full solution Release gates pass.
