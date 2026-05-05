# Phase 6.1 Progress Update — Post-Gate-Snapshot Status

**Companion to:** [`phase-6-1-status.md`](./phase-6-1-status.md) (committed `56351c8`, the gate-readiness baseline)
**As of:** branch `phase-6-rabbitmq-transport` tip `809e8ca`
**Open team tasks:** 1 in flight (#3 / GitHub #18); 1 pending (#8 / GitHub #23 decision gate); 7 pending Phase 6.2/6.3
**Build state:** 0 errors, 386 passing / 84 env-skipped / 0 failed

This document captures progress made since the gate-readiness snapshot landed, plus a **load-bearing design decision** that has surfaced in the final slices of #3 and needs an explicit call.

---

## 1. Commits since the gate snapshot

12 commits landed on `phase-6-rabbitmq-transport` after the gate-readiness baseline (`56351c8`):

| Commit | Closes | Phase 6.1 contribution |
|---|---|---|
| `f1566dc` | #21 follow-up / #3 prereq | `AddServiceBusTransport` now wires `ServiceBusClient` + `ServiceBusAdministrationClient` + `IServiceBusManagement` from options |
| `f9f99fb` | #4 progress | Resolver flips `WithoutTransport()` → `AddServiceBusTransport()`; drops local `ServiceBusClient` registration |
| `229a287` | #4 close | WebApp same flip; +1 ServiceBusTransportRegistrationTests test fix |
| `607a115` | **#17 close** | Drop `[Obsolete]` session-state bridges from `IMessageContext`. **SC-010 hit** at 11 members (5–7 transport + 0 obsolete + 3 timing). |
| `4cea052` | #19 progress | `CosmosDbParkedMessageStore` + Cosmos session-state checkpoint methods |
| `33ec1c4` | #19 progress | `PortableDeferredMessageProcessor` + `IPortableDeferredAuditEmitter` in `NimBus.Core/Deferral/` |
| `f9d2ef4` | **#19 close** | Conformance suite + InMemory/SqlServer/Cosmos concrete runs. **NFR-011 hit.** |
| `e8eeca3` | #3 slice 1 | Move `NimBusReceiverHostedService` → `NimBus.ServiceBus.Hosting`; SDK gets `[Obsolete]` forwarder shells |
| `3565560` | #3 slice 2 | `AddNimBusPublisher`/`Subscriber` consume `Func<string, ISender>`; SDK no longer pulls `ServiceBusClient` for `CreateSender(name)` |
| `952658a` | #3 slice 3 | Extract `PublisherClient.Request<,>` body to `ServiceBusRequestSender` + minimal `IRequestSender` abstraction in `NimBus.Transport.Abstractions` |
| `742dae3` | #3 slice 4 | Re-base `ISubscriberClient` on `IMessageHandler` (drop `IServiceBusAdapter` inheritance from interface; concrete `SubscriberClient` keeps `[Obsolete]` ASB Handle bridges) |
| `809e8ca` | #3 slice 5a | Drop legacy `PublisherClient(ServiceBusClient,...)` ctor + `CreateAsync`; transport-neutralize `GetBatchesStatic` byte-size; relocate `NimBusDiagnostics` `NimBus.ServiceBus` → `NimBus.Core/Diagnostics` |

## 2. Spec criteria — current verdicts

| Criterion | Pre-update | Post-update | Notes |
|---|---|---|---|
| Build green at every commit | YES | YES | All 12 commits build clean |
| Test count ≥ 386 / 0 failed | YES (386) | YES (386) | Test count steady; +8 conformance tests in #19 minus 16 deleted bridge tests in #17 |
| **NFR-011** (deferred-by-session park-and-replay byte-identical audit) | PARTIAL | **YES** | Closed by `f9d2ef4` |
| **SC-010** (`IMessageContext` ≤ 12 methods) | PARTIAL | **YES** | Closed by `607a115`; lands at 11 |
| **SC-005** (zero compile-time refs to `Azure.Messaging.ServiceBus` in NimBus.SDK et al.) | PARTIAL | **PARTIAL — design decision pending** | See §3 below |
| CrmErpDemo manual smoke (operator-eyes verification) | PARTIAL | PARTIAL | Manual step the human owner does at gate time |

## 3. The SC-005 design decision

Slice 5 of #3 has surfaced a concrete tension between two design constraints written into the codebase by separate teammates over the course of #3:

### The two constraints

**Constraint A** — `tests/NimBus.ServiceBus.Tests/PublicApiContractTests.cs` mandates that the concrete `SubscriberClient` keeps the `IServiceBusAdapter` implementation as `[Obsolete]` bridges. From the test:

> // The concrete class keeps the ASB-typed Handle overloads as
> // [Obsolete] bridges so existing Azure Functions code keeps working
> // for one major version.
> Assert.IsTrue(typeof(IServiceBusAdapter).IsAssignableFrom(typeof(SubscriberClient)));

This was disentangler's slice-4 design choice + abstractions-builder's earlier pre-flight recommendation: **drop the inheritance from `ISubscriberClient` (interface) so consumers program against the transport-neutral surface, but keep the four ASB-typed `Handle(...)` overloads on the concrete `SubscriberClient` for one major version** while Azure-Functions consumers migrate to injecting `IServiceBusAdapter` directly.

**Constraint B** — Spec § Success Criteria SC-005:

> SC-005: NimBus.WebApp, NimBus.CommandLine, NimBus.Resolver, NimBus.Manager, and NimBus.SDK assemblies have **zero compile-time references** to `Azure.Messaging.ServiceBus` after the refactor (verifiable via `dotnet list package --include-transitive`).

The four `[Obsolete]` ASB-typed `Handle(...)` bridges on `SubscriberClient` reference `ServiceBusReceivedMessage`, `ServiceBusSessionMessageActions`, `ServiceBusMessageActions`, `ServiceBusSessionReceiver`, and `ProcessSessionMessageEventArgs` — all from `Azure.Messaging.ServiceBus`. To compile, `NimBus.SDK.csproj` needs the `<ProjectReference Include="..\NimBus.ServiceBus\..." />`, which transitively pulls `Azure.Messaging.ServiceBus 7.20.1` into `NimBus.SDK`'s package graph. SC-005 fails the verification command.

### The contradiction

Constraint A says: keep the bridges for one major version.
Constraint B says: drop all ASB compile-time references in this version.

Both constraints can't hold simultaneously. A path forward must be picked.

### Path A — **strict SC-005 in this version**

Drop the four `[Obsolete]` `Handle(...)` bridges from `SubscriberClient`. Drop the `IServiceBusAdapter` implementation from the concrete class. Drop the `_serviceBusAdapter` field. Drop the `<ProjectReference>` to `NimBus.ServiceBus`. SC-005 hits strict.

**Cost:**
- Update `PublicApiContractTests.SubscriberClient_StillImplementsIServiceBusAdapter_ForObsoleteCompat` to assert the **opposite** (and rename to `…_ForSC005`).
- Update `tests/NimBus.ServiceBus.Tests/SubscriberClientTests.cs` to remove the three `Handle_With{X}_DelegatesToServiceBusAdapter` tests (they exercise the bridges).
- Survey + migrate any Azure Functions sample / consumer code that calls `subscriberClient.Handle(ServiceBusReceivedMessage, ...)` directly — the migration is to inject `IServiceBusAdapter` from DI instead. CrmErpDemo's `ErpEndpointFunction` was already migrated by disentangler in slice 4 commit `742dae3`, so this may be a no-op.
- Document the breaking change in the eventual release notes.

**Estimated effort:** 1–2 hours for the test/sample updates and verification.

### Path B — **defer SC-005 strict to v2; accept PARTIAL now**

Keep the bridges. Mark #3 complete-as-designed. Document SC-005 as PARTIAL pending the v2 cycle when the `[Obsolete]` window closes.

**Cost:**
- Update `phase-6-1-status.md` Section 6's SC-005 verdict from PARTIAL ("await #3 / #18") to PARTIAL ("await v2 cycle when [Obsolete] bridges drop"). Same status, different reason.
- Document the deliberate design choice in this update doc and in the eventual ADR-011 follow-up.
- Decision-gate review (#8) will need to choose between Proceed-with-PARTIAL-SC-005 (acceptable for v1 but explicitly noted) and Defer-or-Refine.

**Estimated effort:** 10 minutes for the doc updates.

### Recommendation

**Path A.** Reasoning:
1. Phase 6 is already a major-version-shaped break in spirit (transport seam relocated, four FRs land breaking-change-style migration paths). One more breaking change in the same window is consistent and lowers the future v2-cycle effort.
2. SC-005 is one of the two headline gate criteria (alongside SC-010); landing it strictly puts the decision gate in clean Proceed territory.
3. The `[Obsolete]` bridges have lived in the codebase for less than a day; no external consumers have had a chance to depend on them. The "one major version" guidance was conservative because the bridges were assumed to ship in a release; they haven't.
4. The CrmErpDemo migration in slice 4 has already exercised the path consumers will take — it works.

**Counter-argument for Path B:** Strictly preserving the disentangler's documented design intent. Honoring the existing `PublicApiContractTests` rather than rewriting them.

The user is invited to pick. Auto-mode default per "prefer action over planning" would lean Path A; the user's review is genuinely valuable here because the choice has release-notes consequences.

## 4. Updated task list status

| # | GitHub | Status | Owner | Notes |
|---|---|---|---|---|
| 1 | #16 | Completed | disentangler | Foundation |
| 2 | #17 | Completed | abstractions-builder | Transport.Abstractions package |
| 3 | #18 | **In flight — design decision pending** | disentangler / team-lead direct | Slices 1-5a landed (`e8eeca3`, `3565560`, `952658a`, `742dae3`, `809e8ca`); slice 5b-g pending Path A vs B |
| 4 | #19 | Completed | abstractions-builder | Resolver/WebApp DI cleanup |
| 5 | #20 | Completed | deferred-designer | Park-and-replay design + design doc |
| 6 | #21 | Completed | conformance-builder | Conformance test suite |
| 7 | #22 | Completed | conformance-builder | NimBus__Transport knob |
| 8 | #23 | **Pending** | — | Decision gate review; depends on #3 closure path |
| 9 | #24 | Pending | — | RabbitMQ provider (Phase 6.2) |
| 10 | #25 | Pending | — | CrmErpDemo --Transport rabbitmq (Phase 6.3) |
| 11 | #26 | Pending | — | RabbitMqOnPrem sample (Phase 6.3) |
| 12 | #27 | Pending | — | CLI --transport flag (Phase 6.3) |
| 13 | #28 | Pending | — | WebApp transport-aware topology view (Phase 6.3) |
| 14 | #16-A | Completed | disentangler | Bridge delegation through `ISessionStateStore` |
| 15 | #16-B | Completed | disentangler | StrictMessageHandler/DeferredMessageProcessor migration |
| 16 | #16-C | Completed | disentangler | `ScheduleRedelivery` extracted to Cosmos throttling hosted-service |
| 17 | #16-D | Completed | disentangler | Drop obsolete bridges; SC-010 hit at 11 members |
| 18 | — | Completed | abstractions-builder | Transport-interface promotion (Pass 1: ISender + IDeferredMessageProcessor + wire model; Pass 2: IMessageContext + IReceivedMessage + IMessageHandler) |
| 19 | — | Completed | disentangler | `IParkedMessageStore` + `PortableDeferredMessageProcessor` |
| 20 | — | Completed | abstractions-builder | Pass 2 promotion |
| 21 | — | Completed | abstractions-builder | `AddServiceBusTransport` scaffold + registration tests |
| 22 | — | Completed | conformance-builder | `AddInMemoryTransport` scaffold |
| 23 | — | Completed | conformance-builder | Phase 6.1 status snapshot + Section 6 gate readiness |
| 24 | — | Completed | abstractions-builder | `ServiceBusTransportManagement` adapter |
| 25 | — | Pending | — | Phase 6.2 design: AdminService session-ops abstraction |

**Phase 6.1 progress: 21/26 tasks completed, 1 in flight (#3, awaiting design decision), 1 pending (#8 decision gate), 3 pending Phase 6.2/6.3 work.**

## 5. Once #3 closes

The decision gate (#8) is mechanical from there:

1. Re-derive the gate-readiness checklist in `phase-6-1-status.md` against the new HEAD.
2. Flip Section 6's SC-005 verdict (YES under Path A; PARTIAL-with-rationale under Path B).
3. Confirm `dotnet test src/NimBus.sln` is still 386+/0 failed.
4. CrmErpDemo manual smoke run (the human-owner step — happy-path round-trip + failure-and-resubmit on `--Transport servicebus` default).
5. Post the gate-decision comment on GitHub issue #23: **Proceed** to Phase 6.2.

After Proceed, Phase 6.2 unlocks: #9 (RabbitMQ provider) is the headline ~1-2 weeks of work; #25 (AdminService design) is a parallel follow-up. Phase 6.3 (#10-#13) lands after the RabbitMQ provider's conformance suite is green.

## 6. Open Questions for v2 cycle

Regardless of which path is taken for #3, these remain outside Phase 6.1:

- **AdminService session-receiver abstraction** (task #25). Real `ITransportSessionOps` design work informed by both Service Bus and RabbitMQ. Phase 6.2 follow-up.
- **Service Bus warm-path wrapper for `PortableDeferredMessageProcessor`**. Disentangler punted this in #19 since the portable path is sufficient; the wrapper is a performance optimization for the warm path with native ASB session-deferral.
- **5-attempts-then-deadletter policy** for parked-replay failures. Per design §7, threshold + dead-letter routing.
- **`nimbus-ops` UI for skip-parked**. Backend `IParkedMessageStore.MarkSkippedAsync` exists; frontend affordance is unbuilt.
