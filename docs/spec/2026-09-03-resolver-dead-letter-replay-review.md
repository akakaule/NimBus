# Review — Resolver dead-letter monitoring and replay plan

| | |
|---|---|
| **Plan under review** | `docs/superpowers/plans/resolver-dead-letter-replay.md` (389 lines) |
| **Commit** | `0dfc414` on branch `docs/resolver-dead-letter-replay-plan` |
| **Date** | 2026-09-03 |
| **Method** | Every file path, API signature, exception hierarchy, and behavioural claim in the plan was checked against the working tree. Claims about Azure Service Bus were checked against Microsoft's transaction documentation. |

## Verdict

**Do not start Phase 1.** The idea is sound and well scoped — a stable throttle reason plus operator-driven DLQ replay is a real gap, and the plan's non-goals and audit-sanitisation rules are disciplined. Most of its code citations hold up.

But five blockers invalidate parts of the design, and two of them (B1, B5) restructure the plan rather than patch it. Resolving B5 and B1 turns a seven-phase plan containing a from-scratch AMQP transaction coordinator into roughly four phases with no emulator work at all.

## Findings at a glance

| # | Severity | Finding |
|---|---|---|
| B1 | Blocker | Provider-blind — the feature is inert on SQL Server |
| B2 | Blocker | Unbounded synchronous replay vs. the App Service 230 s timeout |
| B3 | Blocker | Cross-entity transaction shape unproven, no fallback, validated last |
| B4 | Blocker | `MaxDeliveryCount` lives in six places, not three |
| B5 | Blocker | Atomicity bar is stricter than the platform's own recorded standard |
| S1 | Significant | Wrong location — recreates the removed `docs/superpowers/` tree |
| S2 | Significant | "Default Aspire path uses the emulator" is false for the main AppHost |
| S3 | Significant | The stable reason is not guaranteed — the broker can win the race |
| S4 | Significant | Unbounded held-lock set across the scan |
| S5 | Significant | No concurrency guard for simultaneous replays |
| S6 | Significant | The "never dead-lettered" heartbeat remark is already false today |
| S7 | Significant | `subscriptionName` has exactly one legal value — guard rails are over-built |
| S8 | Significant | The emulator's "actor/single-writer boundary" does not exist |

---

## Blockers

### B1 — The plan is provider-blind; on SQL Server the whole feature is inert

`RequestLimitException` is Cosmos-only (`src/NimBus.MessageStore.CosmosDb/RequestLimitException.cs:11`, namespace `NimBus.MessageStore`). The SQL provider translates its throttling codes — **10928, 10929, 40501, 49918, 49919, 49920**, precisely the Azure SQL equivalents of a Cosmos 429 — into the *base* `StorageProviderTransientException`, with no throttle-specific subtype (`src/NimBus.MessageStore.SqlServer/SqlServerExceptionTranslation.cs:41-48`).

`NimBus.Resolver` references both providers and selects one at runtime (`src/NimBus.Resolver/Program.cs:46`), and the dev environment has been SQL-backed since 2026-08-08. So on SQL: no `RequestLimitException` → never reaches `HandleCosmosThrottle` → no `CosmosDbThrottled` → the replay dialog's headline scenario has nothing to select.

The plan does not mention SQL Server once in 389 lines.

**Fix.** Introduce a provider-neutral `StorageThrottledException : StorageProviderTransientException` in `NimBus.MessageStore.Abstractions`; derive Cosmos's `RequestLimitException` from it; map the SQL codes above to it; emit a provider-neutral reason. Everything else in the design survives unchanged.

### B2 — Unbounded synchronous replay vs. the App Service 230-second timeout

Plan steps 1–5 peek and receive "until empty" with no cap, no chunking, and no background job. The WebApp is a `Microsoft.Web/sites` resource on an App Service Plan (`deploy/bicep/deploy.webapp.bicep:222`, `deploy/bicep/templates/appServicePlan.bicep`), where Azure hard-kills idle requests at 230 seconds.

A large DLQ replay dies mid-flight. Because each message commits its own transaction, the result is a partially applied replay with no resumable state — and the audit row, written after the call completes, never persists at all.

**Fix.** Cap the selection, and design the operation as chunked or resumable.

### B3 — The atomicity premise is unproven, has no fallback, and is validated last

Microsoft's [transaction documentation](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-transactions) states that to receive from a subscription and send to a topic in one transaction, *the transfer entity must be a topic*. It does not document a **dead-letter subqueue** receiver as the transaction entity, which is exactly what this design requires. With `EnableCrossEntityTransactions`, the via-entity is fixed by the first entity the client touches — and the plan never specifies receiver/sender creation order.

Meanwhile Phase 3 builds an AMQP transaction coordinator in the emulator *before* anyone proves the shape works against real Azure (Phase 7), and plan `:377` forbids any fallback.

**Fix.** A Phase 0 spike against a real Standard namespace, before any emulator work. See also B5, which may remove the need entirely.

### B4 — Phase 1 is silently incomplete and will regress the budget it centralises

The plan lists three sites for the `10` literal. There are six:

| Site | Listed in plan? |
|---|---|
| `src/NimBus.ServiceBus/Provisioning/ServiceBusTopologyProvisioner.cs:303` | yes |
| `src/NimBus.Management.ServiceBus/ServiceBusManagement.cs:104` | yes |
| `src/NimBus.ServiceBus/AsyncApi/AsyncApiExporter.cs:391` | yes |
| `src/NimBus.ServiceBus/AsyncApi/AsyncApiExporter.cs:498` (spec-022 dynamic-event map) | **no** |
| `src/NimBus.ServiceBusEmulator/Admin/AdminXml.cs:51` | **no** |
| `src/NimBus.ServiceBusEmulator/Broker/BrokerModels.cs:44` | **no** |

The consequential omission is `ServiceBusManagement.CreateSubscription`, which hardcodes not only `MaxDeliveryCount = 10` but `LockDuration`, `RequiresSession`, and the batching/DLQ flags. It is reached by `EndpointManagement.ClearEndpoint` (`src/NimBus.Management.ServiceBus/EndpointManagement.cs:25`), which the WebApp invokes from six call sites in `Controllers/ApiContract/EndpointImplementation.cs`.

So once Phase 1 raises the shared constant, **any operator running Clear Endpoint silently resets that subscription back to 10** — desyncing the broker limit from the Resolver's logical limit and breaking the exact invariant Phase 1 exists to establish. The two emulator defaults produce the same divergence in emulator-backed e2e tests.

**Fix.** Phase 1 needs a repo-wide `MaxDeliveryCount` sweep, not a three-file edit.

### B5 — The atomicity bar is stricter than the platform's own recorded standard

Spec 027's permanent non-goal reads, verbatim (`docs/specs/027-service-bus-emulator/spec.md:51`):

> AMQP transactions / `TransactionScope` / send-via (NimBus has zero usage; the send-then-complete in `MessageContext.ScheduleRedelivery` is **deliberately non-atomic**).

NimBus has already decided that non-atomic send-then-complete is acceptable — for the *same operation shape*, in the Resolver's hot path, running constantly in production. The plan forbids that ordering as a fallback (`:377`) and spends its largest, riskiest phase building a transaction coordinator to permit complete-then-send instead.

Consider what each ordering buys:

- **send-then-complete** — on failure, a message is **duplicated**. Every replay already gets a fresh `MessageId`.
- **complete-then-send** — on failure, a message is **lost** from the DLQ.

The plan bans the safe non-atomic ordering and mandates a transaction in order to enable the dangerous one.

**Recommended change.** Adopt send-then-complete with a documented duplicate-on-failure caveat. This deletes Phase 3 entirely, dissolves B3's unproven premise, removes all emulator transaction work, and is consistent with what the platform already accepts elsewhere.

---

## Significant

**S1 — Wrong location.** `docs/superpowers/plans/` contradicts the standing instruction to write plans to `docs/spec/` and not to recreate `docs/superpowers/`. The plan file is that directory's only occupant. The repository now carries four parallel homes: `docs/plan`, `docs/spec`, `docs/specs`, `docs/superpowers`.

**S2 — The emulator-default claim is false for the main AppHost.** Plan `:71` says "the repository's default Aspire path now uses `NimBus.ServiceBusEmulator`". Only CrmErpDemo defaults to it (`?? "true"`, `samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs:38-42`). `src/NimBus.AppHost` — the AppHost in CLAUDE.md's Build & Run section — has no default (`Program.cs:25-28`) and runs against real Service Bus. This claim is the sole justification for Phase 3.

**S3 — The stable reason is not guaranteed.** The logical attempt limit (10) equals the subscription's `MaxDeliveryCount` (10). On the lock-expiry fallback path, if the Resolver does not settle on delivery 10, the broker dead-letters first with `MaxDeliveryCountExceeded` — which the plan's non-goals explicitly refuse to interpret. Use `MaxDeliveryCount − 1`, or document the race.

**S4 — Unbounded held-lock set.** Replay step 4 holds and renews locks on every non-selected message for the duration of the scan. On a large DLQ that is thousands of concurrent locks against a five-minute maximum lock duration. No cap is specified.

**S5 — No concurrency guard.** Two Owners replaying simultaneously: the second finds messages locked and, per step 6, reports them *failed*. Not mentioned.

**S6 — The heartbeat remark is already wrong.** Phase 2 promises to update the remark claiming heartbeat traffic is "never dead-lettered" (`src/NimBus.Resolver/Services/ResolverService.cs:197-201`) to cover the Cosmos final attempt. That remark is *already* false: the transient catches at `:244-252` and `:298-303` return without settling, deferring termination to the subscription's `MaxDeliveryCount`, so the broker dead-letters the heartbeat after 10 redeliveries regardless. Widen the fix rather than framing it as a new exception.

**S7 — The API is over-built; `subscriptionName` has one legal value.** The Resolver topic is a system topic (`TopologyDescriptor.IsSystemTopic:86-87`) and `ForSystemTopic` returns exactly one subscription — `ResolverSubscription()` at `:345-351`, named `Constants.ResolverId`, session-enabled, `ForwardTo: null`, terminal. The plan's four-point target validation, its 400-vs-404 matrix, and its new unsupported-target exception all defend a parameter with a single valid value.

Drop the path parameter. The route becomes `/api/admin/servicebus/resolver/deadletters`, validation collapses to "the actual subscription exists and matches the expected shape", and Phase 5 loses an API surface, a validation path, an exception type, and roughly four of its nine tests. Separately, the plan hardcodes the string `"Resolver"`; `Constants.ResolverId` exists (`src/NimBus.Core/Constants.cs:7`).

**S8 — The emulator's "actor/single-writer boundary" does not exist.** Phase 3 instructs the implementer not to "bypass the existing actor/single-writer boundary" (`:171`). `BrokerNamespace` guards everything with a single namespace-wide monitor — `private readonly object _gate = new();` (`src/NimBus.ServiceBusEmulator/Broker/BrokerNamespace.cs:5`, 42 usages) — with no actors, channels, or mailboxes anywhere. The actor design is spec-only.

This matters beyond terminology: a transaction coordinator must hold staged state *across* multiple network round-trips (declare → operations → discharge) on top of one coarse lock. That is a genuinely hard concurrency problem the plan does not acknowledge, and it makes Phase 3 harder, not merely larger.

---

## Minor and citation issues

- **Three paths are one directory off.** `BrokerNamespace.cs` and `BrokerModels.cs` are in `src/NimBus.ServiceBusEmulator/Broker/`; `AdminImplementation.cs` is in `src/NimBus.WebApp/Controllers/ApiContract/`.
- **The plan overstates its own premise.** It justifies `IMessageDeliveryContext` partly on the delivery count being unreachable, but `IServiceBusMessage.DeliveryCount` already exists (`src/NimBus.ServiceBus/ServiceBusMessage.cs:27`, implemented `:74`) and `MessageContext` already holds `_sbMessage`. The new interface is a defensible *shape* choice; say that instead of implying plumbing is missing.
- **`MaxThrottleRetries` is already 10** (`ResolverService.cs:28`), so "replace with the shared limit" is numerically a no-op. Worth stating, since it means Phase 2 changes no behaviour on its own.

### Retracted during review

An apparent `Constants` namespace inconsistency (plan `:34` vs `:99`) is **not** a defect. There is exactly one `Constants` class in all of `src/`: `src/NimBus.Core/Constants.cs`, namespace `NimBus.Core.Messages`. The file path simply does not mirror the namespace, and both plan references are correct.

### A criticism deliberately not raised

The plan does **not** silently contradict spec 027's permanent non-goal on AMQP transactions. Plan `:72` and `:167` both state explicitly that spec 027 must be updated. The plan is transparent about reversing that decision; whether the reversal is worth its cost is B5.

---

## Verified accurate in the plan

- `TopologyDescriptor.FindSubscription`'s signature and parameter order match the plan's call exactly (`TopologyDescriptor.cs:386-395`).
- `RequestLimitException → StorageProviderTransientException → Exception` is real, so the prescribed catch order is compiler-required, not stylistic.
- `ScheduleRedelivery` completes the original only after a successful schedule, and converts a scheduling failure to `TransientException` leaving the message unsettled (`src/NimBus.ServiceBus/MessageContext.cs:756-771`) — the two-path budget model is accurate.
- Existing backoff is both exponential and `RetryAfter`-hinted, with the hint winning only when longer — exactly as described.
- `BulkOperationResult` has precisely the `processed` / `succeeded` / `failed` / `errors` shape the plan wants and is already reused by `PurgeSubscriptionAsync`.
- `AccessRole.Owner` exists; the `MessageAuditType` append-only rule is real and documented (`MessageAuditEntity.cs:98-103`).
- `ServiceBusClient` is a plain no-options singleton (`Startup.cs:433-465`) with zero existing `EnableCrossEntityTransactions` usage, so that refactor is greenfield.
- No mocking library exists in *any* test project repo-wide — that constraint is a real convention.
- Every test file the plan cites exists, and the emulator test project is in `src/NimBus.sln:177`, so the verification commands are valid.
- "Do not use the combined regular+transfer count" is a sharp catch: `subscription-manager.tsx:546` currently sums both.
- All Phase 6 gating fields (`deadLetterMessageCount`, `requiresSession`, `forwardTo`) exist in the contract.
- Running backend tests in Release is correct — that is where `TreatWarningsAsErrors` applies.

---

## Recommended restructure

1. **Phase 0 (new).** Decide B5. If send-then-complete is accepted, Phase 3 disappears and B3 dissolves. If atomicity is retained, spike the cross-entity transaction against a real Standard namespace *before* any emulator work.
2. **Resolve B1.** Make the throttle exception and reason provider-neutral, covering SQL Server.
3. **Phase 1.** Sweep all six `MaxDeliveryCount` sites, including `ServiceBusManagement.CreateSubscription` and both emulator defaults.
4. **Phase 2.** Unchanged, plus the wider heartbeat-comment fix (S6).
5. **Phases 4–5.** Bound the operation per B2; simplify the API per S7.
6. **Phases 6–7.** Sound as written.
7. **Move the plan and this review to `docs/spec/`** and delete the recreated `docs/superpowers/` tree.
