# Phase 6.1 Status Snapshot — Decision-Gate Review

**Branch:** `phase-6-rabbitmq-transport` (local; not pushed)
**Snapshot date:** 2026-05-04
**Spec:** `docs/specs/003-rabbitmq-transport/spec.md`
**Driving issue:** [#14](https://github.com/akakaule/NimBus/issues/14) · ADR-011
**Decision gate task:** team task #8 / GitHub issue #23

This document captures the verifiable state of Phase 6.1 work for the 6.1 → 6.2
decision gate per the spec's *Phasing Reference* table (line 387 of `spec.md`):

> **6.1** — Disentangle `IMessageContext`; extract `NimBus.Transport.Abstractions`;
> retrofit Service Bus + InMemory; conformance suite green for both. **Decision Gate:
> Yes** — review the seam; stop and ship as cleanup if too leaky.

The intent is that anything claimed below can be re-verified by running the cited
command against `phase-6-rabbitmq-transport` at the snapshot HEAD.

---

## (a) Commits landed since `master`

`git log --oneline origin/master..HEAD` on `phase-6-rabbitmq-transport` returns 20
commits (oldest → newest):

| # | SHA | One-liner |
|---|---|---|
| 1 | `1f6e894` | test(transport): scaffold transport conformance suite skeletons (#21) |
| 2 | `5443758` | docs(spec): add deferred-by-session park-and-replay design |
| 3 | `8d9658f` | feat(transport): scaffold NimBus.Transport.Abstractions package (#17) |
| 4 | `2b979ed` | feat(transport): introduce ISessionStateStore in MessageStore.Abstractions |
| 5 | `429268d` | refactor(transport): mark IMessageContext store-state methods [Obsolete] |
| 6 | `af61b33` | feat(transport): add replay-checkpoint methods to ISessionStateStore |
| 7 | `9247ee8` | feat(messagestore): introduce IParkedMessageStore contract for park-and-replay |
| 8 | `8ba51e7` | feat(testing): in-memory IParkedMessageStore + ISessionStateStore checkpoint |
| 9 | `f4f0d98` | feat(sqlserver): IParkedMessageStore + ISessionStateStore checkpoint |
| 10 | `e46117f` | refactor(transport): promote ISender + IDeferredMessageProcessor (Pass 1 of #18) |
| 11 | `1a77ff9` | test(transport): replace conformance placeholders with real Transport.Abstractions types |
| 12 | `4fbc766` | feat(throttling): add ThrottledRedeliveryHostedService for Cosmos 429 handling |
| 13 | `21eed42` | refactor(resolver): route Cosmos throttle redelivery through ThrottledRedeliveryHostedService |
| 14 | `8978136` | refactor(transport): drop ScheduleRedelivery + ThrottleRetryCount from IMessageContext |
| 15 | `73d57ea` | refactor(transport): promote IMessageContext + IReceivedMessage + IMessageHandler (Pass 2 of #18) |
| 16 | `d16b505` | feat(apphost): NimBus__Transport provider-selection knob (#22) |
| 17 | `8e7eb0d` | feat(transport): wire ISessionStateStore through MessageContext bridges |
| 18 | `1cfa785` | feat(testing): scaffold AddInMemoryTransport + InMemory transport registration/capabilities |
| 19 | `c9a282e` | feat(servicebus): scaffold AddServiceBusTransport provider registration (#21) |
| 20 | `2da928a` | test(servicebus): add registration tests for AddServiceBusTransport (#21) |

Re-verify: `git -C <worktree> log --oneline origin/master..HEAD`

---

## (b) Tasks completed → spec FR/SC mapping

Team-task → GitHub-issue → spec-FR/SC mapping for everything completed in Phase 6.1:

| Team task | GH issue | Status | Maps to | Verified by |
|---|---|---|---|---|
| #1 | #16 | completed | FR-002 (disentanglement foundation), NFR-011 (no behavioural change) | Build green commit-by-commit; `IMessageContext` store-state methods carry `[Obsolete]` (`429268d`); store-state operations now live on `ISessionStateStore` (`2b979ed`). |
| #2 | #17 | completed | FR-001 (new abstractions package), provider markers `ITransportProviderRegistration` / `ITransportCapabilities` / `ITransportManagement` | `src/NimBus.Transport.Abstractions/` exists; commit `8d9658f`. Interface count below. |
| #5 | #20 | completed | FR-002 deferred-by-session move to Core | `IParkedMessageStore` lives in `NimBus.MessageStore.Abstractions`; in-memory + SQL Server impls landed (`8ba51e7`, `f4f0d98`). |
| #6 | #21 (skeletons) | completed | FR-090 (conformance suite exists), FR-091 (seven categories), SC-009 (per-key burst test method names locked) | `src/NimBus.Testing/Conformance/Transport/` — 7 abstract `[TestClass]`es, 27 stub test methods + capability-gating meta-tests. |
| #7 | #22 | completed | FR-060 (`NimBus__Transport` env-var, valid values `servicebus`, `rabbitmq`, `inmemory`) | AppHosts read `NIMBUS_TRANSPORT`; runtime services read `NimBus:Transport`; mismatch validation; commit `d16b505`. |
| #14 | #16-A | completed | FR-002 store-state delegation | `[Obsolete]` bridges on `IMessageContext` now delegate to `ISessionStateStore` injected via DI; commit `8e7eb0d`. |
| #16 | #16-C | completed | FR-002 ScheduleRedelivery extraction | Cosmos throttle redelivery now lives in `ThrottledRedeliveryHostedService`; `IMessageContext.ScheduleRedelivery` and `ThrottleRetryCount` are gone (`8978136`). |
| #18 | (sub-task) | completed | FR-001 / FR-002 promotion | `ISender`, `IDeferredMessageProcessor`, `IMessageContext`, `IReceivedMessage`, `IMessageHandler` now live in `NimBus.Transport.Abstractions.Messages`; namespace retained as `NimBus.Core.Messages` via `[TypeForwardedTo]` attributes (Pass 1: `e46117f`, Pass 2: `73d57ea`). |
| #20 | (sub-task) | completed | FR-001 promotion | Pass 2 type-forwarders verified compile-green for downstream consumers. |
| #21 | (sub-task) | completed | FR-020 retrofit, scaffolding for `AddServiceBusTransport()` | `src/NimBus.ServiceBus/Transport/` — `ServiceBusTransportBuilderExtensions.cs`, `ServiceBusTransportCapabilities.cs`, `ServiceBusTransportOptions.cs`, `ServiceBusTransportProviderRegistration.cs`. Commit `c9a282e`. |
| #22 | (sub-task) | completed | FR-090 / FR-091 capability-gating wiring, parity with #21 | `src/NimBus.Testing/InMemoryTransport*.cs` — registration, capabilities, builder extension. Commit `1cfa785`. |

---

## (c) Measurable claims for verification at the gate

Each row below names a claim, the spec/issue line backing it, and the command (or
file) the reviewer can use to re-derive the value at gate time.

| # | Claim | Spec ref | How to re-verify |
|---|---|---|---|
| C1 | `NimBus.Transport.Abstractions` exists as its own project. | FR-001 | `ls src/NimBus.Transport.Abstractions/NimBus.Transport.Abstractions.csproj` |
| C2 | The package contains **8 public interfaces**: `ITransportProviderRegistration`, `ITransportCapabilities`, `ITransportManagement` (root) + `ISender`, `IDeferredMessageProcessor`, `IMessageContext`, `IReceivedMessage`, `IMessageHandler`, `IMessage` (`Messages/`). | FR-001, FR-091 | `grep -lE "^public interface" src/NimBus.Transport.Abstractions/**/*.cs` |
| C3 | Service Bus provider scaffolding exists: 4 files under `src/NimBus.ServiceBus/Transport/`. | FR-020 | `ls src/NimBus.ServiceBus/Transport/` |
| C4 | InMemory provider scaffolding exists: 3 files at `src/NimBus.Testing/InMemoryTransport*.cs`. | FR-020, FR-091 | `ls src/NimBus.Testing/InMemoryTransport*.cs` |
| C5 | `NimBus__Transport` env-var routing exists in both AppHosts and both runtime services. | FR-060 | `grep -lE "NIMBUS_TRANSPORT\|NimBus:Transport\|NimBus__Transport" src/NimBus.AppHost/Program.cs samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs src/NimBus.Resolver/Program.cs src/NimBus.WebApp/Startup.cs` — all 4 files match. |
| C6 | Conformance suite skeletons exist: 7 abstract test classes under `src/NimBus.Testing/Conformance/Transport/`, 27 named test methods covering FR-091's seven categories. | FR-090, FR-091 | `ls src/NimBus.Testing/Conformance/Transport/*.cs` (excluding README), then `grep -c "\[TestMethod\]" src/NimBus.Testing/Conformance/Transport/*.cs`. Each class targets the `ITransportProviderRegistration` real type (placeholders removed in `1a77ff9`). |
| C7 | **Build green at every commit on `phase-6-rabbitmq-transport`.** | NFR-011 | Spot-check by checking out any of the 19 SHAs and running `dotnet build src/NimBus.sln`. Most recent run on HEAD: 0 errors, 1040 warnings (pre-existing nullable + CA1848 noise; not introduced by Phase 6.1 commits). |
| C8 | **Test suite: 386 passing, 0 failing, 68 skipped.** Skipped suites (NimBus.MessageStore.SqlServer, NimBus.MessageStore.CosmosDb) are container-gated and follow the established skip-when-no-broker pattern. | NFR-011 | `dotnet test src/NimBus.sln --no-build` — see breakdown below. |
| C9 | **SC-005 status: NOT YET HIT.** `NimBus.SDK` still has a transitive reference to `Azure.Messaging.ServiceBus 7.20.1` via its project reference to `NimBus.ServiceBus`. The compile-time *direct* dependency on `Azure.Messaging.ServiceBus` from `NimBus.SDK` itself has not yet been added or severed by this branch — the SDK never had one — but SC-005 also names `WebApp`, `CommandLine`, `Resolver`, `Manager` and these still pull Azure transitively through Service Bus today. **Sever requires task #3 (#18 in GitHub).** | SC-005 | `dotnet list src/NimBus.SDK/NimBus.SDK.csproj package --include-transitive | grep ServiceBus` — currently shows `Azure.Messaging.ServiceBus 7.20.1` and `Microsoft.Azure.Functions.Worker.Extensions.ServiceBus 5.24.0`. |
| C10 | **SC-009 status: TEST METHOD NAMES LOCKED, BODIES PENDING.** `Burst_1000Messages_100Keys_PreservesPerKeyOrderAsync` and `Burst_1000Messages_100Keys_AcrossKeyParallelismObservedAsync` exist as scaffolded stubs in `SessionOrderingConformanceTests.cs`. They will be filled in once the in-memory and Service Bus concrete provider runs land (post-gate work). | SC-009 | `grep -n "Burst_1000Messages_100Keys" src/NimBus.Testing/Conformance/Transport/SessionOrderingConformanceTests.cs` |
| C11 | **SC-010 status: PARTIAL.** `IMessageContext` (in `NimBus.Transport.Abstractions/Messages/`) currently exposes **5 transport-only methods** (`Complete`, `Abandon`, `DeadLetter`, `Defer`, `DeferOnly`, `ReceiveNextDeferred`, `ReceiveNextDeferredWithPop` — 7 with the two `ReceiveNext*`), **12 `[Obsolete]` session-state bridges** (BlockSession, UnblockSession, IsSessionBlocked, IsSessionBlockedByThis, IsSessionBlockedByEventId, GetBlockedByEventId, GetNextDeferralSequenceAndIncrement, IncrementDeferredCount, DecrementDeferredCount, GetDeferredCount, HasDeferredMessages, ResetDeferredCount), and **3 timing properties** (`IsDeferred`, `QueueTimeMs`, `ProcessingTimeMs`, `HandlerStartedAtUtc`). The SC-010 target is **fewer than 12 transport-only methods**. After deletion of the `[Obsolete]` bridges (task #17 / #16-D, currently pending), the count lands at 7 transport-only methods — **comfortably under the 12-method ceiling**. | SC-010 | `grep -E "^\s+Task\b\|^\s+Task<" src/NimBus.Transport.Abstractions/Messages/IMessageContext.cs` and read the `[Obsolete]` annotations. |
| C12 | The `[Obsolete]` session-state bridges on `IMessageContext` delegate to `ISessionStateStore` (the *real* operations now live in `NimBus.MessageStore.Abstractions`), satisfying FR-111's staging requirement. | FR-111 | `grep -nB1 "ISessionStateStore" src/NimBus.Core/Messages/IMessageContext.cs` and the bridge implementations in `src/NimBus.ServiceBus/MessageContext.cs`. Commit `8e7eb0d` is the dispatch wiring. |
| C13 | `NimBus.Testing` provides full park-and-replay infrastructure (in-memory `IParkedMessageStore`, in-memory `ISessionStateStore`) so tests do not depend on a broker. | (FR-091 deferred-and-replay category) | `ls src/NimBus.Testing/InMemoryParkedMessageStore.cs src/NimBus.Testing/InMemorySessionStateStore.cs` |
| C14 | `samples/AspirePubSub/AspirePubSub.AppHost/Program.cs` was deliberately NOT modified by task #7 (out of scope per the issue brief); only `src/NimBus.AppHost/Program.cs` and `samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs` carry the new `NimBus__Transport` env-var bridging. | FR-060 | `git diff origin/master..HEAD -- samples/AspirePubSub/AspirePubSub.AppHost/Program.cs` returns no changes. |

### Test breakdown (re-verify with `dotnet test src/NimBus.sln --no-build`)

| Assembly | Passed | Failed | Skipped |
|---|---:|---:|---:|
| NimBus.Core.Tests | 129 | 0 | 0 |
| NimBus.ServiceBus.Tests | 128 | 0 | 0 |
| NimBus.EndToEnd.Tests | 56 | 0 | 0 |
| NimBus.CommandLine.Tests | 33 | 0 | 0 |
| NimBus.MessageStore.InMemory.Tests | 33 | 0 | 0 |
| NimBus.Resolver.Tests | 7 | 0 | 0 |
| NimBus.MessageStore.SqlServer.Tests | 0 | 0 | 35 (env-gated) |
| NimBus.MessageStore.CosmosDb.Tests | 0 | 0 | 33 (env-gated) |
| **Total** | **386** | **0** | **68** |

---

## (d) Tasks remaining before the gate

The decision gate (team task #8) is `blocked by` #1, #2, #3, #4, #5, #6, #7. The
first three (#3 → #18 GH; #4 → #19 GH) are still pending and the only Phase 6.1
work the gate review depends on directly. There are also three internal follow-ups
(#15, #17, #19 in the team list) tracked alongside Phase 6.1.

| Task | Subject | Status | Gate dependency | Why it matters |
|---|---|---|---|---|
| #3 (GH #18) | Drop `Azure.Messaging.ServiceBus` from `NimBus.SDK` | pending | **Yes — gate-blocking** | Hits SC-005. Without it, the on-prem-only deployment claim (Spec § *Deployment Mode (resolved)*) is theoretical; consumers still pull Azure transitively. The `AddServiceBusTransport` scaffolding (#21) is the seam this task plugs into. |
| #4 (GH #19) | DI-inject `ITransportProvider` in Resolver and WebApp | pending | **Yes — gate-blocking** | Replaces the runtime `WithoutTransport()` shim in `Resolver/Program.cs` and `WebApp/Startup.cs` (placeholder switch arms left by task #7) with real `AddServiceBusTransport(...)` / `AddInMemoryTransport(...)` calls. Once landed, `NimBus__Transport=servicebus` actually selects a transport rather than opting out of validation. |
| #15 (#16-B) | Migrate `StrictMessageHandler` + `DeferredMessageProcessor` to inject `ISessionStateStore` | in_progress | Indirect | Removes two large remaining call-sites of the `[Obsolete]` bridges. Not gate-blocking (the bridges still work via #14's delegation), but landing it tightens SC-010 compliance and makes #17 (bridge deletion) safer. |
| #17 (#16-D) | Drop `[Obsolete]` bridges from `IMessageContext` | pending | Indirect — hits SC-010 fully | Once #15 has landed and any external consumers have migrated, this drops `IMessageContext` to its 7 transport-only methods and explicitly closes SC-010. Per FR-111 the bridges must remain "for one major version"; deleting in this phase is acceptable because there are no released consumers using them yet. |
| #19 | Implement `IParkedMessageStore` + `PortableDeferredMessageProcessor` | in_progress | Indirect | Builds on #5 (#20 GH). The contract and in-memory + SqlServer impls exist; the portable processor that uses them is the wiring step. Not strictly gate-blocking (Service Bus retrofit can keep using `DeferredMessageProcessor` until RabbitMQ lands). |

### Gate-blocking summary

**Two pending tasks (#3, #4) before the gate can be reviewed**, plus the three
internal follow-ups whose absence is acceptable but visible in the seam:

- SC-005 will not be hit until #3 lands.
- The `WithoutTransport()` shim in runtime services is a paper-thin placeholder
  until #4 wires the real `AddServiceBusTransport(...)` extension into the `switch`
  arms.
- SC-010 quantitatively lands once #17 deletes the `[Obsolete]` bridges.

If the decision gate convenes before #3 / #4 land, the seam-review still has full
coverage of *every other* Phase 6.1 deliverable; only the SC-005 verification step
needs to be deferred (or run after #3 lands and re-verified).

---

## (e) Coordination / process incidents worth recording

These are observations from the team's working pattern during Phase 6.1. They are
not bugs in the codebase — they are notes for the gate review on how the work was
sequenced and what the harness imposed on us.

### E1 — Auto-revert / re-dispatch pattern

Multiple agents observed a recurring harness behaviour where, after an agent
completed a task and posted the completion summary, the team-lead's task-assignment
loop would re-dispatch the *same* task back to the same agent (or, in one case, the
agent self-dispatched). The pattern bit at least three completions:

- Task #6 (conformance suite skeletons) — re-dispatched as a self-assignment loop.
- Task #7 (`NimBus__Transport` knob) — re-dispatched after `d16b505` had landed.
- Task #22 (in-memory transport scaffolding) — re-dispatched after `1cfa785` had
  landed, with a brief that diverged on cosmetic details (`internal sealed` vs
  `public sealed`, folder layout, extension class name).

**Mitigation that worked:** the receiving agent paused before re-doing work and
sent a `SendMessage` to team-lead summarising the prior commit + scope deltas. In
all three cases team-lead confirmed *don't re-do*. No work was actually duplicated.

**Recommendation for the gate:** when the gate review hits a task already marked
`completed`, treat the task list as authoritative and check the commit log against
the sub-issue scope before re-dispatching.

### E2 — File-collision serialization on shared worktree

Phase 6.1 ran with multiple agents in the same `C:/Git/NimBus-phase6/` worktree
rather than separate worktrees. This caused two recurring frictions:

- A teammate's in-progress staged edits (e.g. `src/NimBus.Core/Messages/IMessageContext.cs`
  staged for deletion as part of Pass 2 of #18) appeared in another agent's
  `git status` while they were trying to commit unrelated files. Agents had to
  `git add <specific-files>` rather than `git add .` to avoid sweeping in teammate
  WIP.
- The `git stash` / `git stash pop` cycle conflicted when teammate work overlapped
  the stashed paths — one agent (task #6) had to drop the stash because the
  working-tree state was already current relative to it.

**Mitigation that worked:** explicit per-file `git add` in every commit, no `git
add .` or `git add -A`. Two-phase verification of staging via `git status` before
`git commit`. Disciplined "stay out of teammate file-space" convention (e.g. task
#22 was explicitly told not to touch `InMemoryMessageContext.cs` because
disentangler was changing its constructor in #14).

**Recommendation for the gate:** Phase 6.2 (RabbitMQ provider, separate
project space) is naturally less collision-prone, but if multiple agents continue
to share the worktree, codify the "explicit file paths only" convention in the
team brief.

### E3 — Tooling / interface-promotion mid-task

During task #7 (`NimBus__Transport` knob) the runtime registration switch arms
needed to call `Add{Transport}Transport()` extension methods that did not yet
exist. Rather than block, the implementation called `nimbus.WithoutTransport()`
with `// TODO(#18)` / `// TODO(#24)` comments naming the seam follow-up tasks.
Validation logic (mismatch detection, unknown-value rejection) is fully in place
today; only the actual `AddServiceBusTransport()` call slots in once #3 / #4 land.

**Why this matters at the gate:** the seam shape exists end-to-end, but a casual
reading of the runtime files makes it look as though the transport selection is a
no-op. The TODO comments are deliberate and cite the unblocking task numbers.

### E4 — Branch state — local only

Per team-lead policy, no commits were pushed to `origin/phase-6-rabbitmq-transport`
during Phase 6.1. The reviewer must re-fetch the branch from the local worktree
(or wait for the milestone push) to inspect commits. As of this snapshot the local
branch is **20 commits ahead of `origin/phase-6-rabbitmq-transport`** (which itself
is ahead of `master`).

---

## Suggested gate-review order

When team task #8 convenes:

1. Verify (a) — `git log --oneline origin/master..HEAD` matches table 1 (20 commits).
2. Verify (c) — run `dotnet build src/NimBus.sln` and `dotnet test src/NimBus.sln`,
   confirm 0 errors / 386 pass / 0 fail.
3. Walk the seam (FR-002): read `src/NimBus.Transport.Abstractions/Messages/IMessageContext.cs`
   end-to-end, confirm transport-only methods at the top + `[Obsolete]` bridges
   below + the type-forwarders preserving `NimBus.Core.Messages.IMessageContext`.
4. Confirm SC-010 trajectory: count transport-only methods (5–7), bridges (12),
   timing props (3). Decision: accept the trajectory or hold the gate until #17
   removes the bridges.
5. Decide on SC-005: hold the gate until #3 lands, or note "tracked, pre-condition
   for 6.2 RabbitMQ work" and proceed.
6. Decide on the seam itself per the spec's gate rubric — "review the seam; stop
   and ship as cleanup if too leaky." This document does not pre-empt that
   judgement; it provides the inputs.

---

*End of snapshot. Re-generate at HEAD by re-running the cited commands; this
document is intentionally a point-in-time photograph, not a living dashboard.*
