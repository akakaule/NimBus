# Plan 5: Harden `StrictMessageHandler` lifecycle ordering

## Goal

Reduce change risk in `StrictMessageHandler` by making its operational ordering explicit in tests. Do not split the production state machine merely to reduce file length. Reconsider a focused extraction only when a concrete change demonstrates a reusable ownership boundary.

## Decision

The committed scope is test hardening only.

## Outcome

Implemented on 2026-08-28. The ordering harness now covers the critical lifecycle paths below, including nested deferred-context identity and heartbeat bypass behavior. It did not expose a repeated coordination problem that meets the production extraction gate, so no production code was changed and this plan is closed.

`StrictMessageHandler` is roughly 839 lines, but its shared response, settlement, session, deferred, failure-classification, discard, and retry behavior is already centralized in private helpers. Moving those helpers into `MessageLifecycleOperations` or `FailureProcessor` would mostly move code between files. It would not remove the request orchestration, and grouping more steps would hide the ordering this plan is intended to protect.

This decision supersedes the broader Plan 5 recommendation in `REVIEW.md` to commit the helper extractions. A further source review found that the proposed shared behavior is already centralized; the remaining repetition is primarily the intentional request-level orchestration.

The existing tests are extensive, but their fakes primarily record independent call counts. They prove that effects occurred, not that response publication, session changes, deferred draining, and settlement happened in the required order. The useful investment is an ordering harness around the current implementation.

After the harness lands, stop. A production extraction is optional and requires the trigger and decision gate below. Request-processor classes are not part of this plan.

## Risk posture

`StrictMessageHandler` is the core lifecycle state machine. Its await order is operational behavior:

- a response may need to be published before completion;
- a session must be unblocked before deferred siblings drain;
- a popped deferred sequence may need to be restored before an exception propagates;
- heartbeat traffic must bypass inbox and blocked-session checks;
- some exceptions intentionally propagate to `MessageHandler`, while others become responses and settlement.

Tests should protect these invariants without making harmless implementation changes difficult. Prefer assertions about the relevant partial order and forbidden operations over snapshots of every incidental call.

## Scope

In scope:

- `tests/NimBus.Core.Tests/StrictMessageHandlerTests.cs`;
- `tests/NimBus.Core.Tests/Messages/StrictMessageHandlerHeartbeatTests.cs`;
- focused inbox, retry, handoff, discard, and deferred-processing assertions already housed in those files;
- `StrictMessageHandler.cs` only if a later, explicitly approved extraction meets the decision gate.

Out of scope:

- changing message types or wire contracts;
- changing retry, failure-disposition, inbox, heartbeat, handoff, or settlement semantics;
- extracting general response, completion, block/unblock, or session-guard wrappers into another class;
- introducing a dependency container solely to shorten constructor forwarding;
- extracting `FailureProcessor` without a second consumer or a concrete change that needs an isolated failure boundary;
- creating request-specific processor classes;
- changing the separate deferred-processor architecture;
- deleting obsolete public constructors or APIs;
- replacing the message lifecycle with an external state-machine package;
- consolidating the other `FakeMessageContext` or `TestMessageContext` doubles in `ResponseServiceTests`, `ExtensionFrameworkTests`, `BuiltInMiddlewareTests`, or the heartbeat test file.

## Compatibility constraints

- Preserve every existing public constructor and public override signature.
- Preserve exact settlement count and order, session ownership checks, deferred-sequence restoration, duplicate recording behavior, cancellation propagation, exception types, and logging intent.
- Keep the 30-second best-effort deferred restore bound.
- Preserve the rule that heartbeat traffic bypasses inbox and blocked-session checks.
- Do not merge the legacy continuation path with the separate deferred processor.
- Do not alter production code while building the characterization harness. If a test exposes surprising current behavior, record it and review it separately instead of silently changing the expectation or implementation.

## Phase 1: add a shared operation trace

1. Add a small test-only operation trace shared by the fakes in `StrictMessageHandlerTests.cs`.
2. Extend that file's existing `FakeMessageContext`, `FakeResponseService`, `FakeEventContextHandler`, and—where inbox ordering matters—`FakeInboxStore` to append named operations to the trace while retaining their existing counters and captured arguments.
3. Record only lifecycle-significant operations:
   - handler invocation;
   - inbox duplicate check where relevant;
   - session ownership/block checks;
   - response publication;
   - block and unblock;
   - deferred receive, forward, count update, restore, and drain trigger;
   - complete, abandon, and dead-letter.
4. Give nested/deferred contexts an identity in the trace so settlement assertions distinguish the continuation message from the deferred event.
5. Keep the heartbeat-specific double local to `StrictMessageHandlerHeartbeatTests.cs`. Add only the minimum recording needed to prove the heartbeat short-circuit precedes and bypasses inbox/session guards.

Do not introduce a production tracing abstraction. This harness belongs to the tests.

## Phase 2: characterize the critical sequences

Add or strengthen focused tests for the sequences where reordering changes behavior:

1. Normal event: handle → resolution response → complete.
2. Handler failure: handle → error response → block → complete → optional retry response.
3. Blocked event: deferral response → forward to the deferred subscription → increment deferred count → complete.
4. Pending handoff: handle → pending-handoff response → block → complete, with no resolution response.
5. Retry success: verify ownership → handle → unblock → deferred drain trigger → resolution response → complete.
6. Retry duplicate while blocked by this event: duplicate check → unblock → deferred drain trigger → duplicate response → complete.
7. Retry or resubmission discard: any required unblock/drain occurs before discard response → complete.
8. Resubmission handoff while another event owns the block: pending-handoff response → complete, with no block, unblock, drain, or resolution.
9. Skip: verify ownership → unblock → deferred drain trigger → skip response → complete.
10. Handoff completion: verify ownership → unblock → deferred drain trigger → resolution response → complete without invoking the user handler.
11. Handoff failure: verify ownership → error response → complete, with no unblock or deferred drain.
12. Continuation unexpected/transient failure after pop: pop → nested handling failure → restore the deferred reference before propagation or outer settlement.
13. Caller cancellation: restore when required, then propagate cancellation without response, retry, completion, abandon, or dead-letter side effects owned by the cancelled flow.
14. Heartbeat: heartbeat response → complete, with no inbox check, session guard, block, defer, or user-handler invocation.

Reuse existing tests where they already cover the scenario; add ordering assertions rather than duplicating the entire setup in a second test. A small number of table-driven cases is acceptable when setup and expected semantics are genuinely identical, but do not force every exception branch into one table.

For each relevant context, assert settlement exactly once. Also assert important absences, such as no resolution response for pending handoff and no unblock for handoff failure.

## Phase 3: stop and review the evidence

After the ordering harness passes against the unchanged production handler:

1. Review whether the trace exposed an actual repeated coordination problem.
2. Record any surprising existing behavior as a separate bug or design decision; do not fold semantic changes into this refactor.
3. Close this plan unless the focused extraction gate below is met.

File length alone, private-helper count, or a desire for symmetrical classes does not satisfy the gate.

## Conditional extraction gate

A production extraction may proceed only when all of the following are true:

- a concrete feature or bug requires coordinated changes to the same deferred-sequence behavior in at least two request flows;
- the proposed collaborator owns one cohesive domain responsibility rather than general message lifecycle plumbing;
- the ordering harness covers every branch that would move;
- the extraction reduces duplicated decisions or dependencies, not merely the line count of `StrictMessageHandler`;
- a focused design review concludes that the benefit exceeds the added indirection.

The only currently plausible boundary is an internal `DeferredSequenceCoordinator` containing the existing deferred-specific behavior:

- forwarding a blocked message to the deferred subscription and maintaining the count;
- receiving and verifying the next legacy deferred reference;
- restoring a popped reference under the 30-second best-effort bound;
- choosing between the legacy continuation request and the separate `ProcessDeferredRequest` drain trigger.

It must not absorb general response publication, completion, authorization, failure classification, retry policy, or session ownership decisions. `StrictMessageHandler` must continue to show the request-level order explicitly.

If this extraction is approved, implement it in a separate PR, move one deferred operation group at a time, and run the complete ordering suite after each move. Otherwise leave production code unchanged.

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

For a test-only implementation, review the final diff to confirm that no production source or public API changed. For a later approved extraction, review the diff specifically for changed await order, catch ordering, cancellation tokens, logging metadata, obsolete compatibility annotations, and SDK construction.

## Proposed pull requests

Committed scope:

1. `test(core): characterize strict message lifecycle ordering`

Only after the conditional extraction gate is explicitly approved:

2. `refactor(core): isolate deferred sequence coordination`
3. Update `docs/architecture.md` in the same PR if the production boundary changes.

## Exit criteria

- The critical response, session, deferred, and settlement sequences are asserted in tests.
- Relevant contexts are proven to settle exactly once.
- Cancellation and heartbeat bypass behavior are explicitly protected.
- The committed test-hardening PR changes no production behavior or public API.
- `StrictMessageHandler` remains the readable, compatible public lifecycle facade.
- The plan closes after the harness unless the conditional extraction gate is documented as satisfied.
- Core, SDK, Service Bus, end-to-end, and full-solution Release gates pass.
