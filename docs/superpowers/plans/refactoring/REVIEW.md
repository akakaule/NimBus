# Review: refactoring implementation plans (`docs/plan/`)

Reviewed 2026-08-23 against the working tree at `a2af3b0` (plus uncommitted changes).
Every factual claim below was checked against the code, not inferred from the plans.

## Verdict

**Approve plans 1, 2, and 3 with the corrections in this document. Rescope plan 4. Reduce plan 5.**

These are unusually well-grounded plans. I spot-checked roughly forty concrete
assertions — file paths, interface members, test-project names, npm scripts,
conformance-suite names, concurrency primitives, exception sets, constructor
arities — and nearly all of them hold. The plans correctly anticipate the two
sharpest traps in this codebase (shared mutable `CommandOption` instances in the
CLI, and the overlapping `StrictMessageHandler` constructor arities). The
weaknesses are not in technique; they are in **sizing**: two plans spend their
risk budget on parts of the work that were already done, and one plan's stated
benefit rests on a false premise about the current tests.

| Plan | Premise verified | Main issue | Recommendation |
|------|------------------|-----------|----------------|
| 1 — narrow WebApp storage deps | Yes | Phase 2/3 split contradicts the actual inventory; stated test benefit is false | Proceed, corrected |
| 2 — decompose providers | Yes | None material | Proceed as written |
| 3 — decompose WebApp event components | Yes | One inapplicable test-runner instruction; two affected test files unlisted | Proceed, corrected |
| 4 — modularize CLI | Partly | Phase 3 is largely already done; target file has uncommitted feature work | Rescope and resequence |
| 5 — decompose `StrictMessageHandler` | Yes | Highest risk applied to the smallest target | Stop after Phase 3 |

---

## What I verified as correct

Recording this so the corrections below are read in proportion.

**Sizes and premises.** `CosmosDbClient.cs` 2648 lines; `SqlServerMessageStore.cs`
1906; `message-listing.tsx` 1358; `events-panel.tsx` 1285; `Program.cs` 918;
`StrictMessageHandler.cs` 839. The metrics, event-schema, and access-control
stores really are already extracted in both providers, so plan 2's "established
pattern" is real rather than aspirational.

**Contracts and names.** Every conformance suite plan 2 names exists in
`src/NimBus.Testing/Conformance/`, spelled correctly. `SqlServerStoreContext`
exists. `EndpointErrorListFormat` is already in the abstractions project, where
plan 2 says to keep it. `INimBusMessageStore`'s own XML doc already states the
narrow-interface guidance plan 1 is enforcing, and `IHeartbeatHistoryStore` is
correctly *outside* the aggregate — which is why plan 1's "optional capability"
carve-out for `HeartbeatService` is right.

**Plan 1's scope list is complete.** A repo-wide grep finds no
`INimBusMessageStore` consumer outside the plan's list, apart from `Startup.cs`
registration and the in-memory test store.

**Plan 2's Cosmos constraint is accurate.** `CosmosDbClient.cs:35` is a
single-flight `ConcurrentDictionary<string, Task<ICosmosContainerAdapter>>` with
failure eviction at lines 510 and 1281 — exactly the cache the plan forbids
duplicating. `CosmosDbClient` has zero `virtual` members and no subclasses in the
repo, so facade delegation carries no override-compat risk.

**Plan 3's behavioural claims are accurate**, which matters because the whole
plan depends on them. `events-panel.tsx` really does carry a monotonic
`fetchTicket` (line 341) plus a separate `tokenGeneration` for pagination (line
346), a per-event write chain with `reportRevisions` (line 321) and
`confirmedReportStates` (line 332) — and it really does mutate the API event
object in place (`event.isReported = …`, lines 668-671), which is why the plan's
"avoid mutating API event objects in new code" rule is a correctness constraint
rather than a style preference.

**Plan 5's behavioural claims are accurate.** `DeferredRestoreTimeout =
TimeSpan.FromSeconds(30)` (line 17); the heartbeat short-circuit really does sit
before the inbox pre-check and the session guard (lines 156-165);
`TransientException`, `EventHandlerNotFoundException`, `PermanentFailureException`,
and cancellation-guarded `OperationCanceledException` really are propagated
directly (lines 688-704). The constructor-ambiguity warning is not hypothetical:
the 7-parameter overload ends in
`IPermanentFailureClassifier permanentFailureClassifier = null` and the
8-parameter overload adds `IFailureDispositionClassifier?` after it, so a change
to that pair can silently make existing call sites ambiguous.

**Plan 4's CLI traps are correctly identified.** A single `CommandOption`
instance (`sbConnectionString`) is `AddOption`'d onto subcommands under three
different top-level groups — shared mutable state that works only because one
command executes per invocation. The plan flags it. `InternalsVisibleTo
Include="NimBus.CommandLine.Tests"` is already in the csproj, so "tests may call
the factory without invoking a process" is achievable today. The duplication the
plan targets is real: `Enum.TryParse<ResolutionStatus>` at three sites and
`DateTime.TryParse(… AdjustToUniversal | AssumeUniversal …)` at four.

---

## Plan 1 — Narrow WebApp storage dependencies

### 1.1 The Phase 2 / Phase 3 split contradicts the inventory (must fix)

The plan defers `EventImplementation`, `AgentImplementation`, and
`StorageHookImplementation` to Phase 3 as "multi-contract consumers". I ran the
inventory the plan asks for in Phase 1. All three use exactly **one** contract:

| Consumer | Contracts actually used |
|---|---|
| `MetricsImplementation` | `IMetricsStore` (5 members) |
| `MessageImplementation` | `IMessageTrackingStore` (`SearchMessages`) |
| `AuditImplementation` | `IMessageTrackingStore` (`SearchAudits`) |
| `AuditLogService` | `IMessageTrackingStore` (`StoreMessageAudit`) |
| `HandoffSettlementService` | `IMessageTrackingStore` (`GetEvent`) |
| **`EventImplementation`** | **`IMessageTrackingStore` only** (19 members) |
| **`AgentImplementation`** | **`IMessageTrackingStore` only** (`GetNextPendingHandoffEvent`) |
| **`StorageHookImplementation`** | **`IMessageTrackingStore` only** (`DownloadEndpointStateCount`) |
| `HeartbeatService` | `IEndpointMetadataStore` (8) + `IServiceHealthStore` (3) + optional `IHeartbeatHistoryStore` |
| `EndpointImplementation` | `IMessageTrackingStore` (6) + `ISubscriptionStore` (3) + `IEndpointMetadataStore` (3) |
| `SeedDataService` | `IMessageTrackingStore` (8) + `ISubscriptionStore` (1) + `IEndpointMetadataStore` (1) |
| **`AdminService`** | **none — see 1.2** |

So Phase 2 covers **eight** consumers, not five, and Phase 3 has only **three**
genuine multi-contract classes. `EventImplementation` uses 19 members of one
interface — a large but single-contract dependency, and labelling it
"multi-contract" would misdirect whoever reviews that PR.

Rewrite Phase 2 to name all eight single-contract consumers, and Phase 3 to name
`HeartbeatService`, `EndpointImplementation`, and `SeedDataService`. None of the
three exceeds the plan's own three-contract threshold, so the "consider
decomposing into an application service" branch never fires — say so, rather than
leaving an open decision inside the plan.

### 1.2 `AdminService`'s store dependency is dead code (must fix)

`AdminService` declares `private readonly INimBusMessageStore _cosmosClient`
(line 33), takes it in the constructor (line 50), assigns it (line 59) — and
never calls a single member, in either half of the partial class.
`AdminService.Copy.cs` does not reference it at all.

The plan lists both files as Phase 3 migration targets. The correct action is to
**delete the parameter**, not narrow it. Call this out explicitly: the
constructor change is a source break for any direct construction in tests, and a
reviewer following the plan as written would otherwise pick an arbitrary narrow
interface for a dependency that should not exist at all.

While you are there, drop `AdminService.Copy.cs` from the scope list — it has no
storage dependency.

### 1.3 The stated test benefit is not real (must fix)

Exit criterion: *"Tests no longer implement dozens of unused aggregate methods
for migrated consumers."*

They do not implement them today. `tests/NimBus.WebApp.Tests` contains exactly
three store doubles, all in `HeartbeatServiceTests.cs` (lines 533, 592, 603), and
all three **derive from** `NimBus.Testing.Conformance.InMemoryMessageStore` — a
shared 1098-line implementation that already satisfies every narrow interface as
well as the aggregate. Narrowing a constructor from `INimBusMessageStore` to
`IMessageTrackingStore` requires no test change whatsoever, because
`InMemoryMessageStore` still satisfies the narrowed parameter.

That does not sink the plan — the coupling argument stands on its own, and the
ADR-010 follow-up is worth closing. But Phase 2 step 3 ("Replace broad fake
stores in their unit tests with small handwritten fakes") asks for work that
trades one shared, conformance-tested double for a dozen bespoke ones. **Drop
that step.** Keep `InMemoryMessageStore`; it is an asset, not the problem. And
restate the exit criterion in terms of what actually improves: each consumer's
declared surface matches its used surface.

### 1.4 The SQL registration test does not exist (must fix)

Phase 4 says to "extend `CosmosDbMessageStoreRegistrationTests` **and the
corresponding SQL registration tests**". The Cosmos one exists.
`tests/NimBus.MessageStore.SqlServer.Tests` contains six files, none of which is
a registration test. Phase 4 must *create* it.

### 1.5 The verification gate over-claims (should fix)

Plan 1's per-batch gate runs `NimBus.MessageStore.SqlServer.Tests` and
`NimBus.MessageStore.CosmosDb.Tests`. Both are environment-gated and skip
silently without a live SQL Server or Cosmos emulator. Plan 2 carries the right
warning — *"Do not treat skipped provider tests as proof"* — and plan 1 should
carry the identical sentence plus a pointer to the container prerequisites. A
green run made entirely of skipped tests is the exact failure mode Phase 4 exists
to close.

### 1.6 The inventory resolves an open question in the plan's favour

Phase 1 hedges: *"Do not forbid `INimBusMessageStore` across the entire assembly
until the inventory proves that no legitimate aggregate consumer remains."* The
inventory above proves it. After 1.1 and 1.2, **zero** WebApp classes need the
aggregate, so the end state can be an assembly-wide architecture guard rather
than an enumerated allowlist — simpler, and it cannot rot as consumers are added.

That leaves `INimBusMessageStore` with no production consumers at all (only
`InMemoryMessageStore`, the two providers, and the `Startup.cs` registration).
Decide deliberately whether it stays as a published convenience contract for
third-party consumers, and record that decision in the ADR-010 update Phase 4
already schedules. Do not let it be deleted as a side effect.

---

## Plan 2 — Decompose storage-provider implementations

**No material issues.** This is the strongest plan of the five and the one with
the clearest payoff: 4554 lines across two files reduced to delegating facades,
against provider-agnostic conformance suites that already exist and already cover
every concern being extracted.

Three things it gets right that are easy to get wrong, and worth preserving
verbatim in review: extracting message tracking **last** (largest and most
coupled contract); refusing to fragment a single contract across loosely
coordinated classes; and forbidding a SQL-text move and a SQL-text change in the
same commit.

Two small additions:

- Phase 5 step 4 says to update `docs/architecture.md`. Name the specific
  targets: line 759 (*"Cosmos client logic is concentrated in a large
  `CosmosDbClient`"*) **and** line 770, which cites
  `src/NimBus.MessageStore/CosmosDbClient.cs` — a path that has not existed since
  the provider split. Fix the stale path while you are in there.
- `SqlServerMessageStore` is `sealed`; `CosmosDbClient` is not, though it has no
  `virtual` members and no subclasses. Sealing `CosmosDbClient` in Phase 5 would
  make the "facade, not a base class" contract explicit — but it is a public API
  change, so raise it as a separate decision rather than folding it into the
  refactor.

---

## Plan 3 — Decompose WebApp event components

The Phase 1 characterization list is excellent and specific — it names the exact
races the code actually guards against, which is why I trust it. Two corrections.

### 3.1 The `localStorage` instruction is inapplicable (must fix)

> *"If the full Vitest run lacks browser `localStorage`, set a process-local Node
> `--localstorage-file` pointing to a unique temporary file."*

`vite.config.ts` sets `test: { environment: 'jsdom' }`. jsdom supplies
`localStorage`. There is no missing-storage condition to work around, and
`--localstorage-file` belongs to Node's experimental Web Storage implementation,
which is not in play. Delete the paragraph — as written it will send somebody
chasing a runner flag for a problem they do not have. Keep the sentence that
follows it, which is the valuable one: *"Do not change application code to
accommodate the test runner."*

### 3.2 Two affected test files are unlisted (should fix)

Phase 2 extracts "table row/cell view-model construction", but the scope section
names only `events-panel.paint.test.tsx` and `message-listing.test.tsx`. Also in
the blast radius:

- `src/components/endpoint-details/events-panel.columns.test.ts` — column
  construction is precisely what Phase 2 moves.
- `src/components/event-details/audit-listing.test.tsx` — sibling component,
  likely shares extracted derivations.

Add both to the scope list and to the per-phase test commands.

### 3.3 One thing to hold the line on

Phase 3's instruction *"Do not combine fetch and reporting into one 'page model'
hook; they have independent concurrency rules and test lifecycles"* is the single
most important sentence in this plan. The two mechanisms in `events-panel.tsx`
are genuinely independent — `fetchTicket`/`tokenGeneration` guard read freshness,
`reportRevisions`/`confirmedReportStates` guard write ordering — and merging them
would reintroduce exactly the coupling the plan exists to remove. Flag any PR
that combines them.

---

## Plan 4 — Modularize CLI composition (rescope)

Sound in technique, mis-sized in scope, and colliding with work in flight.

### 4.1 The target file has uncommitted feature work on it right now (blocking)

`docs/plan/README.md` rule: *"Do not mix these refactors with feature work."*
The working tree currently has, uncommitted:

```
 M src/NimBus.CommandLine/Program.cs
 M src/NimBus.CommandLine/CommandSupport.cs
 M src/NimBus.CommandLine/NimBus.CommandLine.csproj
 M src/NimBus.CommandLine/AppDeploymentService.cs
 M src/NimBus.CommandLine/PlatformLoader.cs
 M src/NimBus.CommandLine/ServiceBusTopologyProvisioner.cs
?? src/NimBus.CommandLine/BicepTemplateProvider.cs
?? src/NimBus.CommandLine/DeploymentArtifacts.cs
```

That is the ADR-015 deployment-distribution work, and it touches `Program.cs` —
plan 4's primary target. Starting plan 4 now guarantees the merge conflict the
delivery rules are written to avoid. **Land ADR-015 first**, then start plan 4
from a clean `Program.cs`. Add this as an explicit precondition in the plan.

### 4.2 Phase 3 is mostly already done (must fix)

Phase 3 — "move top-level command modules mechanically", four commits, help
output and exit codes compared for each group — implies `Program.cs` is a flat
918-line blob of inline command construction. It is not. It already contains
exactly the eight registration methods the plan proposes as eight files, mapping
1:1:

```
ConfigureInfraCommands      (100)   ConfigureEndpointCommands   (402)
ConfigureTopologyCommands   (169)   ConfigureContainerCommands  (515)
ConfigureDeployCommands     (254)   ConfigureCatalogCommands    (757)
ConfigureSetupCommand       (299)   ConfigureAsyncApiCommands   (849)
```

`Main` is 33 lines of composition. So Phase 3 reduces to *"move eight existing
methods into eight files"* — near-zero risk — and the exit criterion "every
top-level command has one command-registration owner" is **already satisfied**;
the plan would only be relocating the owners.

Collapse Phase 3 from four ceremonial commits to one or two, and drop the
per-group help-output comparison: a pure method move to a new file cannot change
help text, and the ritual will consume review attention that Phases 4 and 5 need.

### 4.3 Resequence — the value is in Phases 4 and 5

With Phase 3 reduced to a file move, the real content of this plan is:

- **Phase 4** — the duplication is genuine and worth removing:
  `Enum.TryParse<ResolutionStatus>` at `Program.cs:600`, `661`, `726`;
  `DateTime.TryParse(… AdjustToUniversal | AssumeUniversal …)` at `497`, `673`,
  `684`, `742`; `int.TryParse` batch-size validation at `695`. These are
  copy-paste clones with independently drifting error messages.
- **Phase 5** — dependency injection is what makes the CLI testable at all.

Run Phase 1 (characterization) → Phase 2 (factory) → Phase 4 (parsers) → Phase 3
(file moves, now trivial) → Phase 5. Deduplicating the parsers behind
table-driven tests first means the file moves happen on already-tested code.

### 4.4 Disposal ownership is unaddressed (should fix)

`Main` currently does `using var app = new CommandLineApplication { … }`. Moving
construction into `CliApplicationFactory.Create` makes the factory return an
undisposed `IDisposable`, transferring ownership to every caller including tests.
That is both an analyzer question (CA2000 and Meziantou's disposable rules are
active repo-wide) and a real contract. State it: `Create` returns an owned
disposable, `Main` keeps the `using`, and tests must dispose too.

### 4.5 One constraint to keep

The plan's warning about shared `CommandOption` instances is correct and
non-obvious. `sbConnectionString` is a single object added to subcommands under
`topology`, `endpoint`, and `container`; `dbConnectionString` likewise; and
callbacks read `.Value()` after parse. Splitting registration across files must
not tempt anyone into constructing per-module copies — the environment-variable
fallback and the "overrides environment variable" help text both depend on the
single shared instance. Consider adding a characterization test that asserts
option *identity*, not just option presence.

---

## Plan 5 — Decompose `StrictMessageHandler` (reduce)

The plan is honest about its own risk, correctly sequences the processors from
least to most coupled, and its Phase 1 ordering harness is the right tool. The
problem is the ratio.

### 5.1 The risk/benefit is inverted (should fix)

`StrictMessageHandler.cs` is **839 lines** — the *smallest* of the five targets,
one third the size of `CosmosDbClient.cs`. Its test file,
`StrictMessageHandlerTests.cs`, is **1823 lines**: the tests are 2.2× the
production code. And the plan concedes: *"A broad rewrite would be riskier than
the current size."*

So plan 5 proposes the highest-risk change in the roadmap, against the smallest
file, guarded by the roadmap's densest existing test suite, for a size reduction
smaller than any other plan delivers.

Phases 2 and 3 are clearly worth it — `MessageLifecycleOperations` and
`FailureProcessor` remove real repetition across seven overrides, and both
extract *helpers*, not *ordering*. Phase 4 is different: it moves the ordering
itself into five new classes, and ordering is the thing the plan spends two pages
establishing must not change.

**Recommendation:** land Phases 1-3 and stop. Re-evaluate Phase 4 against the
handler as it stands afterward — if `MessageLifecycleOperations` and
`FailureProcessor` absorb enough, the residual per-flow methods may not justify
five more classes and five more indirections through a shared collaborator. Frame
Phase 4 as conditional in the plan rather than committed.

### 5.2 "Extend the existing" test double is ambiguous (must fix)

Phase 1 step 1: *"Add a recording `IMessageContext` test double **or extend the
existing one**."* There are five:

```
tests/NimBus.Core.Tests/StrictMessageHandlerTests.cs:1742                   FakeMessageContext
tests/NimBus.Core.Tests/Messages/StrictMessageHandlerHeartbeatTests.cs:261  FakeMessageContext
tests/NimBus.Core.Tests/ResponseServiceTests.cs:578                         FakeMessageContext
tests/NimBus.Core.Tests/ExtensionFrameworkTests.cs:136                      FakeMessageContext
tests/NimBus.Core.Tests/BuiltInMiddlewareTests.cs:19                        TestMessageContext
```

As written, a reader could reasonably start by consolidating all five — a
separate refactor with its own blast radius, silently absorbed into the riskiest
plan in the roadmap. Name the one to extend (the `StrictMessageHandlerTests`
double is the natural choice) and say explicitly that consolidating the others is
out of scope.

### 5.3 A scoped test file is missing (should fix)

Scope names `StrictMessageHandlerTests.cs` and "related heartbeat … tests", but
`tests/NimBus.Core.Tests/Messages/StrictMessageHandlerHeartbeatTests.cs` (321
lines) deserves an explicit entry. It covers the pre-inbox heartbeat
short-circuit at `StrictMessageHandler.cs:156-165` — one of the compatibility
constraints Phase 1 must characterize, and the easiest to break when
`HandleEventRequest` moves into `EventRequestProcessor`, since the bypass sits
*above* the guards a processor would naturally own.

---

## Cross-cutting

### C.1 The plans live in a third location

`docs/plan/` is new. The repo already has `docs/specs/` (027, 028), and the
standing convention for spec and plan documents is `docs/spec/`. Three
directories for one class of document. Pick one and move these — the roadmap will
be referenced from PR descriptions for weeks, so the path should be the one
people already look in.

### C.2 The source audit is not in the repo

`README.md` opens with *"This directory turns the maintainability audit into five
independently executable plans"* and never links it. Six months from now nobody
will be able to check whether a plan faithfully represents its finding, or
whether a finding has since been overtaken. Either commit the audit alongside
these plans or drop the reference and let the plans stand alone.

### C.3 The completion gate needs plan 2's caveat

README's final gate is `dotnet test src/NimBus.sln -c Release --no-build`. The
Cosmos and SQL Server conformance suites in that run are environment-gated and
skip without live backends. Plan 2 says *"Do not treat skipped provider tests as
proof"*; the README presents the same command as the roadmap's completion
criterion without that caveat. Add it, and name the container prerequisites.

Separately, `Release` is the right choice and worth keeping: `CS8767`
(parameter-nullability mismatch on interface implementations) is not on the
`WarningsNotAsErrors` allowlist and fails Release while Debug stays green. Plans
1, 2, and 5 all add interface implementations, so a Debug-only loop would let
that through to CI. State the reason in the README so nobody "optimizes" the gate
down to Debug.

### C.4 The sequencing diagram is malformed

```text
Plan 3: WebApp event components ────────────────────────────────┐
Plan 4: CLI composition ────────────────────────────────────────┼─> Plan 5: core lifecycle
                                                               ┘
```

The trailing `┘` connects to nothing and the `┐`/`┼` do not form a valid join.
Either fix the box-drawing or replace it with two lines of prose — the ordering
it encodes (1→2 hard, 3 and 4 parallel, 5 last) is already stated correctly in
the text immediately below it.

Worth noting that only the 1→2 dependency is real. Plan 5's stated dependency —
*"benefits from … the testing patterns established by the other refactors"* — is
soft, and plan 5's Phase 1 harness is unlike anything in plans 1-4. If plan 5 is
reduced per 5.1, it can run in parallel with 3 and 4 rather than gating on them.

---

## Summary of required changes

**Plan 1:** correct the Phase 2/3 split per the inventory table (§1.1); change
`AdminService` from "migrate" to "delete the unused dependency" and drop
`AdminService.Copy.cs` from scope (§1.2); delete the "replace broad fakes with
handwritten fakes" step and restate the exit criterion (§1.3); note that the SQL
registration test must be created, not extended (§1.4); add plan 2's
skipped-provider-tests warning (§1.5); resolve the assembly-wide-guard hedge and
schedule an explicit decision on `INimBusMessageStore`'s future (§1.6).

**Plan 2:** add the stale `docs/architecture.md:770` path to Phase 5; raise
sealing `CosmosDbClient` as a separate decision.

**Plan 3:** delete the `--localstorage-file` paragraph (§3.1); add
`events-panel.columns.test.ts` and `audit-listing.test.tsx` to scope (§3.2).

**Plan 4:** add "ADR-015 CLI work must land first" as a precondition (§4.1);
rewrite Phase 3 acknowledging the eight `Configure*` methods already exist, and
collapse its commits (§4.2); resequence to 1 → 2 → 4 → 3 → 5 (§4.3); document
`Create`'s disposable ownership (§4.4); add an option-identity characterization
test (§4.5).

**Plan 5:** mark Phase 4 conditional and land Phases 1-3 first (§5.1); name the
single test double to extend and exclude the other four (§5.2); add
`StrictMessageHandlerHeartbeatTests.cs` to scope (§5.3).

**README:** relocate to the repo's single spec/plan directory (§C.1); commit or
drop the audit reference (§C.2); add the skipped-provider caveat and the
Release-gate rationale to the completion gate (§C.3); fix the diagram (§C.4).
