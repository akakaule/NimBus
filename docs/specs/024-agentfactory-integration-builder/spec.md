# Feature Specification: AgentFactory Integration Builder (agent-built, human-gated, CI-deployed NimBus integrations)

Feature Branch: `master` (Phase 0 lands directly; the integration factory itself lives outside this repo)
Created: 2026-07-03
Status: Approved — Phase 0 (NimBus enablement) implemented alongside this spec; Phases 1–3 execute in the `nimbus-integrations` repo and AgentFactory config.
Input: "Provide ideas and plan on how to have AgentFactory build integrations for NimBus — given source and target systems, API specs/requirements: how to model events, implement adapters that integrate to the systems, wire everything in NimBus, and have it deployed."

> **How to read this document.** The [Overview](#overview-plain-language) is written for anyone. Later sections get progressively more technical. Builds on [spec 022](../022-ai-agent-bus-participation/spec.md) (AI agents as bus participants) and is a sibling to spec 023 (AI integration mapper, in flight).

---

## Overview (plain language)

NimBus exists to connect systems, but today every integration is hand-written C#: someone models the events, writes the handlers, wires the topology, and deploys the adapter. AgentFactory is a local task board that already runs the rest of that loop for ordinary software tasks: it dispatches headless Claude sessions into git worktrees, routes their output through AI + human review, and watches the resulting PR to completion.

This feature connects the two: **an integration request goes in; a reviewed, tested, deployed integration comes out.** Humans stay exactly where they add value — reviewing the event model, approving the code, owning production — and stop doing what they don't need to do: typing the adapter.

The design deliberately adds **almost no new machinery**. AgentFactory needs zero code changes (one config flag plus a workspace registration). NimBus needs three small enablement changes (this repo, Phase 0). Everything else is a new `nimbus-integrations` repository whose structure, guardrails, and CI *are* the factory.

## What changes at a glance

**In this repo (Phase 0 — implemented with this spec):**
- `ServiceBusTopologyProvisioner` moved from `NimBus.CommandLine` (a dotnet-tool package, not consumable as a library) into the published `NimBus.ServiceBus` package under `NimBus.ServiceBus.Provisioning`. The `nb` CLI keeps a thin internal wrapper; the two sample provisioner consoles now consume the library. External repos can now provision their own `IPlatform` in-process — no `nb` required.
- `Akaule.NimBus.Testing` published to NuGet (`NimBusTestFixture` + the storage conformance suite are the backbone of agent-generated tests). Repo-wide MSTest bump 2.2.10 → 3.11.1 rode along.
- `Akaule.NimBus.Agents` published to NuGet (enables the tier-1 agent-participant path for external repos).

**Outside this repo (Phases 1–3):**
- A new **`nimbus-integrations`** repository: one folder per integration (Contracts + adapter host + tests + TDD docs), a composite `Integrations.Platform` project as the single topology source of truth, an architecture-test suite that turns NimBus's silent failure modes into build failures, reference `templates/` that compile in CI, Bicep for per-adapter Function Apps, and CI that deploys to a shared dev environment on merge.
- **AgentFactory config**: a `nimbus-integrations` workspace (policy + verifyCommand) and `postMergeChecks: true` so a task completes only when its PR is merged *and* the deploy workflow is green.

**Deliberately unchanged:** AgentFactory's schema/lifecycle (no task DAG — the built-in description → plan → implementation stage pipeline is the pipeline); no runtime Service Bus topology mutation (spec 022/023 constraint holds); `nb` CLI surface (no new commands); prod deployment (human-owned, out of scope).

---

## Design decisions

| # | Decision | Chosen | Rejected alternatives |
|---|---|---|---|
| 1 | Where integrations live | **Dedicated `nimbus-integrations` repo** consuming `Akaule.NimBus.*` NuGet | Inside this repo (couples churn, wrong workspace shape); one repo per integration (ceremony per integration) |
| 2 | Orchestration shape | **One AgentFactory task per integration** on the existing 3-stage pipeline | Cross-task DAG (doesn't exist in AF; real feature work); new AF task kind |
| 3 | "Deployed" definition | **CI deploys to dev on merge**; AF watcher `postMergeChecks` makes done = merged + deployed | Human-triggered deploy (stops short); new AF deploy stage/supervisor (new machinery CI already covers) |
| 4 | Topology provisioning from the external repo | **In-process** via the extracted provisioner library (own console, CrmErpDemo pattern) | `nb topology apply --platform-assembly <dll>` (plugin surface, version coupling) |
| 5 | Scaffolding | **`templates/` of compiling reference projects** in the integrations repo + skill instructions | `dotnet new` template package (second release artifact to keep in sync); `nb new adapter` |
| 6 | Adapter deployment mechanism | **CI-native `az` zip-deploy** per adapter | Extending `nb deploy` (it builds platform apps from this repo's source; wrong tool) |
| 7 | External API ground truth | **Human-recorded fixtures are a task input**; WireMock.Net contract tests run against them | Agent-authored mocks (encodes hallucinations — the exact failure mode under guard) |
| 8 | First delivery | **Thin slice end-to-end** (one real integration through the whole pipeline) | Build all machinery first |

**Delivery tiers** (intake picks the cheapest tier that satisfies the requirement):
- **Tier 0 — mapping only** (spec 023): both contracts registered, pure shape translation → agent-authored JSONata mapping, no code, no deploy. *Falls through to tier 2 until 023 ships.*
- **Tier 1 — agent participant** (spec 022, `NimBus.Agents`): per-message LLM judgment, tolerates nondeterminism → one `IAgentHandler`, zero topology code.
- **Tier 2 — full code-first adapter** (default): external APIs, deterministic transforms, ordering/retry/outbox semantics.

---

## Architecture

```
 Intake                AgentFactory task (one per integration)                     Azure dev env
 ──────                ────────────────────────────────────────                    ─────────────
 integration request   description ──► plan ──► implementation ──► in_review      merge to main
 (template + fixtures     │             │            │                 │               │
  + secret NAMES)         │             │            │                 │               ▼
        │                 ▼             ▼            ▼                 ▼           deploy-dev.yml:
        └───► brief + tier choice   TDD-as-plan   worktree:        AI review        bicep (adapter fn app)
              + verifiable ACs      (TDD.md +     contracts,       (policy-         in-process topology apply
              [AI review: clean     events.md     handlers, host,  briefed) +       zip-deploy + smoke
               auto-advances]       inline)       tests, Platform  human gate           │
                                                  registration;    (always, on      red ⇒ workflow fails ⇒
                                                  verifyCommand    implementation)  AF watcher requeues task
                                                  green; PR            │            green ⇒ task done =
                                                                       └── merge ──►  LIVE IN DEV
```

Key mechanics this rests on (all verified in source):
- AF workers run with **cwd = workspace repoPath**, so the integrations repo's `CLAUDE.md`, `.claude/skills/`, and permission allowlist auto-load into every headless session.
- **`workspace.policy` is the only channel that reaches the AI reviewer** (it cannot read repo files); the policy carries the NimBus discipline for both worker and reviewer.
- The **plan stage is text-only** — the TDD/events content rides inline in the plan and is materialized as `docs/TDD.md` + `docs/events.md` in the implementation worktree's first commit (via the adapter-docs skill templates).
- The **watcher's `postMergeChecks` flag** re-targets a merged PR's checks to the merge commit, i.e. the deploy workflow: merged + green ⇒ done; merged + red ⇒ auto-requeue with the failing check names as a comment the next claim sees.

### Safety keystone: composite platform + architecture tests

One `Integrations.Platform` project aggregates every integration's endpoints (plus a committed baseline snapshot of this platform's event/endpoint ids). It is simultaneously the provisioning root, the catalog root, and the thing that makes NimBus's silent failure modes cheap MSTest assertions:

| Failure mode (silent today) | Architecture test |
|---|---|
| `EventTypeId` collision — catalog silently merges; forward rules cross integrations | Uniqueness across all contracts + baseline; naming convention `<System><Entity><Action>V<N>` |
| Missing `[SessionKey]` — ordering silently degrades to a random session per message | Every `Event` subclass carries `[SessionKey]` naming an existing property (explicit opt-out list) |
| No retry policy — transient failure ⇒ immediate `Failed` + session block | Build each adapter's `ServiceCollection`, assert `IRetryPolicyProvider` registered |
| Missing deferred-processor Functions trigger — deferred messages stuck `Pending` forever | Reflect Functions assemblies: both `[ServiceBusTrigger]`s present with correct `IsSessionsEnabled` |
| Handler ↔ `Consumes<T>` mismatch — session blocks or silent non-delivery | Registered `IEventHandler<T>` set == endpoint's consumed set, both directions |
| Echo loop — adapter republishes a consumed type (`From IS NULL` transport guard is insufficient) | No endpoint both consumes and produces the same `EventTypeId` (allowlist for intentional) |
| Cross-integration interference | Each integration declares only endpoints/events bearing its own system prefix |

### Secrets

Agents never possess secret values. Task specs name configuration keys only (`ExternalSystems:<System>:<Key>`); values live in Key Vault, referenced from Function App settings; deploys authenticate via GitHub OIDC federated credentials ([docs/deployment.md](../../deployment.md) §2). CI runs gitleaks + push protection; the reviewer policy flags secret-shaped literals. Consequence, stated openly: the agent can never test against the real external system — recorded fixtures cover behavior, and the post-deploy smoke (run by CI, which does hold credentials) covers reality.

---

## Phases

**Phase 0 — this repo (done with this spec):** provisioner extraction (`src/NimBus.ServiceBus/Provisioning/ServiceBusTopologyProvisioner.cs`, public ctors for connection string and `ServiceBusAdministrationClient`/TokenCredential; `nb` unchanged via internal wrapper), `Akaule.NimBus.Testing` + `Akaule.NimBus.Agents` packable, MSTest 3.11.1, this spec. Shipped by the next `v*` tag.

**Phase 1 — bootstrap `nimbus-integrations`:** repo skeleton (composite platform + provisioner console + architecture tests + templates + Bicep + CI + CLAUDE.md/guardrails + skills), pinned to one `NimBusVersion`.

**Phase 2 — AgentFactory wiring (config only):** register the workspace (verifyCommand = Release build+test; ~35-line policy), flip `postMergeChecks: true`.

**Phase 3 — thin slice:** `INT github-teams-issues` — GitHub Issues → Teams webhook, one Functions app (TimerTrigger poller + sessions-enabled consumer + deferred trigger), events `GithubIssueOpenedV1`/`GithubIssueClosedV1`, session key = issue number. Filed as a real AF task, driven to live-in-dev with humans only at review gates.

**Phase 4 — hardening (deferred until the slice proves out):** `sync-integrations` producer skill; emulator E2E job in PR CI (needs `EmulatorTopologyConfigBuilder` extraction from CrmErpDemo.Contracts); `nb topology diff` (apply deletes+recreates subscriptions on mismatch and never prunes stale rules — surface that in review); merged-PR-aware AF finish step (a task requeued after a post-merge deploy failure reuses the already-merged branch — v1 treats deploy failures as human triage); external-platform catalog/AsyncAPI export (exporters currently hardcode the built-in `PlatformConfiguration`); per-endpoint notification routing; tier 0/1 activation when spec 023 / the Agents package are ready (a policy text update).

---

## Error handling

| Situation | Behavior |
|---|---|
| Worker produces failing build/tests | `verifyCommand` blocks submission; AF keeps the task in progress |
| AI reviewer finds issues | Findings escalate to the human gate; only human-curated feedback rides the re-queue |
| Deploy-to-dev workflow red (incl. smoke failure) | Workflow fails ⇒ watcher requeues the task with failing-check comment; v1 treats this as human triage (fix-forward task) |
| Bad adapter live in shared dev | Structural containment (own topic/subscriptions, session blocks scoped, MaxDeliveryCount→DLQ); RUNBOOK: `az functionapp stop`, re-run last green deploy, WebApp resubmit/skip |
| Source schema drift vs fixtures | Contract tests fail on re-run; fixture edits are a mandatory review flag |

## Success criteria

1. An integration request filed on the AgentFactory board reaches **live in a dev NimBus environment** with human effort limited to: filling the request template (with fixtures + secret names), reviewing two doc stages, and approving/merging one PR.
2. The architecture-test suite fails the build for each seeded silent-killer (verified by deliberately introducing a collision, a missing SessionKey, and a missing deferred trigger).
3. A deliberately broken deploy turns the task back to `queued` with the failure visible on the board, and the RUNBOOK rollback restores dev.
4. No secret value ever appears in a task spec, worktree, PR, or log.

## Open questions

1. Ownership of the shared dev NimBus environment (`nb setup`) and of the baseline-catalog refresh when this platform adds events.
2. Fixture capture workflow per external system (Postman collection? capture proxy?) — the one human step every integration depends on.
3. Concurrent integration tasks: the composite platform file is a merge-conflict surface — start with one task in flight; shard into per-integration partials if it bites.
4. Key Vault secret provisioning ownership per external system.
