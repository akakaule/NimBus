# Resolver and client SDK test coverage

Status: implemented (2026-09-24), see [Results](#results)

The Resolver (`src/NimBus.Resolver`) and the client SDK (`src/NimBus.SDK`) are the core of
NimBus: every audited message goes through `ResolverService`, and every publisher and
subscriber goes through the SDK. This plan measures their current test coverage, lists the
untested behavior that matters, and breaks the work into small PRs.

## Baseline (master @ 71423a3)

Measured with the collector built into `Microsoft.NET.Test.Sdk` (no project changes needed):

```bash
dotnet test tests/<Project> --collect "Code Coverage;Format=cobertura" --results-directory <dir>
```

The numbers below merge every test project that references these assemblies
(Resolver, SDK, ServiceBus, EndToEnd, OpenTelemetry, Outbox.SqlServer, WebApp).
Counts are sequence-point lines.

| Assembly / file | Lines | Branches |
|---|---|---|
| **NimBus.Resolver** (merged) | 639/820 (78%) | 177/298 (59%) |
| `Services/ResolverService.cs` | 561/587 (96%) | 149/206 (72%) |
| `HttpEndpointStateChangeNotifier.cs` | 28/34 (82%) | 10/16 |
| `ServiceExtensions.cs` | 50/60 (83%) | 18/28 |
| `Program.cs`, `ResolverBuilderExtensions.cs`, `Functions.cs` | 0% | 0% |
| **NimBus.SDK** (merged) | 1717/1982 (87%) | 456/646 (71%) |
| `Hosting/OutboxDispatcherHostedService.cs` | 0/32 (0%) | 0/8 |
| `Hosting/DeferredMessageProcessorHostedService.cs` | 55/86 (64%) | 15/20 |
| `Hosting/CircuitBreakerLifecycleHostedService.cs` | 30/43 (70%) | 3/6 |
| `Hosting/NimBusReceiverHostedService.cs` | 313/388 (81%) | 72/112 |
| `PublisherClient.cs` | 205/239 (86%) | 77/100 |
| `Extensions/NimBusSubscriberBuilder.cs` | 226/260 (87%) | 75/106 |
| `EventHandlers/EventContextHandler.cs` | 82/86 (95%) | 29/46 |

Looking only at each assembly's own test project gives lower figures: Resolver 71% / 53%
and SDK 78% / 62%. `NimBus.EndToEnd.Tests` covers SDK paths but never runs `ResolverService`.

## Findings

No production defects were found. These are the untested behaviors, in priority order.

### Resolver

1. **Dead-lettered projection is untested** (`ResolverService.cs:896-898`, `:754`, `:680`).
   `ResponseService.SendDeadLetterResponse` sends an `ErrorResponse` with
   `DeadLetterErrorDescription` set. The Resolver must classify it as `DeadLettered` (not
   `Failed`), call `UploadDeadletteredMessage`, and copy the description into `Reason`. Every
   permanent failure in every subscriber takes this path, and no test covers it.
2. **Nothing tests the contract between SDK responses and Resolver projections.** No test
   feeds messages produced by `StrictMessageHandler` + `ResponseService` into
   `ResolverService`. Drift between what the SDK emits (message type, `From`/`To`, dead-letter
   properties, handoff metadata, timings) and what the Resolver projects would ship
   undetected.
3. **Generic `TransientException` → `Abandon`** (`:156-160`) has no test. Nothing pins that it
   abandons rather than dead-letters or reschedules.
4. **Unmapped `MessageType` → dead-letter** (`:906`). `Unknown` and `ProcessDeferredRequest`
   are missing from `MessageTypeToStatusMap`. `ProcessDeferredRequest` cannot reach the
   Resolver today (the fan-out filter matches only `To=Resolver`/`To=<endpoint>`, and the
   request uses `To=DeferredProcessor`), so this is not a bug. The behavior should still be
   pinned by a test so a future routing change fails loudly.
5. **Best-effort side paths**: a notifier throwing on the event/heartbeat/service-health
   paths (`:315`, `:412`, `:467-468`); cancellation during the stale-copy audit (`:296-298`);
   a heartbeat with empty or malformed JSON (`:537-549`); a handoff settlement whose history
   lacks the `EventRequest` (`:728-729`); the version fallback (`:484`).
6. **Host composition is 0%.** `ResolverBuilderExtensions.AddResolver`, `Program.cs` and
   `ServiceExtensions` wire the Service Bus client (FQDN + `DefaultAzureCredential` vs
   connection string, `:`- vs `__`-separated keys), the store and the notifier. A config-key
   bug here (fixed in 6d19da6) kept the dev Resolver down for weeks. `ResolverHostConfigurationTests`
   has one test.
7. `HttpEndpointStateChangeNotifier` non-success / exception branches (`:94-108`).

### SDK

8. **Receiver startup-failure propagation** (`NimBusReceiverHostedService.cs:168-171`). When
   `StartProcessingAsync` throws (missing entity, auth), `StartAsync` must fault and the host
   must not start. This is untested, as are:
   - `OnMessageAsync` resetting the recoverable-error counter (`:363-367`)
   - `SocketException`/`WebSocketException`/`IOException` detection through the inner-exception chain (`:478-495`)
   - the recoverable-delay cap (`:433-440`)
   - errors during stop/dispose (`:244-251`, `:274-279`)
   - `ProcessorRestartDelay` (`:182-184`)
   - the ten `ValidateOptions` guards (`:497-547`)
9. **Deferred-processor error branches** (`DeferredMessageProcessorHostedService.cs:107-159`).
   Untested: the dead-letter outcome, a handler exception → abandon, the abandon itself
   failing, and host shutdown mid-message (must *not* abandon). Mistakes on these paths lose
   or duplicate messages.
10. **`OutboxDispatcherHostedService` is 0%.** Untested: a full batch re-polls immediately,
    a poll exception is logged and the loop continues, and shutdown exits cleanly.
11. **Publisher request/reply failure modes** (`PublisherClient.cs:287-312`):
    - missing reply subscription → guided `InvalidOperationException`
    - error reply → `RequestReplyException` carrying type and text
    - null receive → `TimeoutException`
    - timeout vs caller cancellation (`:311`: caller cancellation must surface as `OperationCanceledException`, not `TimeoutException`)
    - the receiver is disposed on every path

    Also untested: `Schedule`/`CancelScheduled`, `PublishBatch`, `CreateAsync`, and
    `GetBatchesStatic` with an oversized first event.
12. **Subscriber builder.** The `AddRequestHandler` registration callback (`:81-88`, the
    DI-resolved `RequestJsonHandler` + `IReplyDispatcher` path) never runs in a test.
    Assembly scanning (`AddHandlersFromAssembly*`, `:99-131`) is also untested:
    - two discovered handlers for one event → error
    - explicit-then-discovered → the explicit handler wins
    - `ReflectionTypeLoadException` tolerance
    - **order dependence**: a discovery conflict throws before a later explicit `AddHandler`
      can resolve it, although the error message tells the user to call `AddHandler`. Pin
      this with a test and make the exception message say to call `AddHandler` *first*.
13. `EventContextHandler`: a missing `EventTypeId`, an unreadable body →
    `PermanentFailureException`, and the scoped-provider path when the fallback handler is used.
14. `CircuitBreakerLifecycleHostedService`: an observer that throws is logged and the loop
    continues; shutdown mid-notification.

## Targets

| Scope | Lines | Branches |
|---|---|---|
| `ResolverService.cs` | ≥ 98% | ≥ 90% |
| NimBus.Resolver, excluding Functions-generated code | ≥ 90% | ≥ 80% |
| NimBus.SDK | ≥ 93% | ≥ 85% |

The coverage numbers are a proxy. Each test must assert an observable outcome (settlement
call, store call, status, exception, log), not just run the line.

## PR breakdown

Each PR is test-only unless noted, uses MSTest with `#pragma warning disable CA1707, CA2007`,
and passes `dotnet build src/NimBus.sln -c Release` before pushing.

1. **`test(resolver): cover dead-letter projection and failure settlement`**. Findings 1, 3, 4
   and 5, in `tests/NimBus.Resolver.Tests` (extend `ResolverServiceTests`,
   `ResolverHeartbeatTests`, `ResolverLivenessProbeTests`; reuse `FakeMessageContext`).
2. **`test(resolver): SDK-to-Resolver response contract`** (finding 2). New
   `ResolverResponseContractTests`: run a real `StrictMessageHandler` + `ResponseService`
   over a capturing `ISender` for each outcome (success, transient error, permanent failure,
   unsupported, session-blocked deferral, pending handoff + completed/failed settlement,
   skip). Feed each emitted `IMessage` to `ResolverService` backed by the in-memory store.
   Assert the resulting `UnresolvedEvent` status, endpoint, `Reason`, handoff fields and
   timings. The project already references `NimBus.Testing`.
3. **`test(resolver): host composition`** (findings 6, 7). Build the Functions host service
   collection through `AddResolver()` with in-memory configuration. Cover
   FQDN-with-credential vs connection-string, the `:` vs `__` keys, the notifier selection,
   and a missing store failing with a clear error. If `Program.cs` needs a small seam to be
   testable, extract it without behavior change.
4. **`test(sdk): receiver, deferred-processor and outbox hosted services`** (findings 8, 9,
   10, 14). Mock `ServiceBusClient` / `ServiceBusSessionProcessor` as the existing
   `NimBusReceiverHostedServiceTests` and `DeferredMessageProcessorHostedServiceTests` do;
   build event args with `ServiceBusModelFactory`.
5. **`test(sdk): publisher, subscriber builder and dispatch`** (findings 11, 12, 13). Extend
   `PublisherClientRequestTests`, `PublisherClientBatchTests`, `SubscriberRegistrationTests`
   and `EventHandlerProviderTests`. Includes the one production change in this plan: the
   exception-message fix for finding 12.
6. **Optional: `ci: publish coverage for Resolver and SDK`**. Add
   `--collect "Code Coverage;Format=cobertura"` to the CI test step and upload the report as
   an artifact. Gate on a floor only once the targets above are met.

## Verification

- Re-run the coverage command above per PR and record before/after for the files touched in
  the PR description.
- `dotnet build src/NimBus.sln -c Release` and `dotnet test src/NimBus.sln -c Release --no-build`.
- These PRs need no live SQL/Cosmos configuration. Report any skipped conformance tests as
  skipped.

## Results

All six PRs landed as commits on master. Coverage, merged across the same seven test
projects as the baseline:

| Scope | Baseline lines / branches | Now | Target |
|---|---|---|---|
| `ResolverService.cs` | 96% / 72% | 99% / 86% | 98% / 90% |
| NimBus.Resolver, excluding generated Functions code | 78% / 59%* | 95% / 87% | 90% / 80% |
| NimBus.SDK | 87% / 71% | 97% / 82% | 93% / 85% |

\* The baseline included the generated Functions code.

Every target is met except two branch targets. On `ResolverService`, the remaining
branches are mostly `activity?.` null-conditionals on the tracing path. On the SDK, they
are argument guards and null-conditionals spread across the DI extensions. What remains
uncovered in the Resolver is the Functions host bootstrap (`Program.cs`, `Functions.cs`),
which only runs inside a Functions host. The provider selection moved out of it into
`ResolverStorageProvider` and is covered.

Each new test class was mutation-checked: the production branch it protects was broken on
purpose, and at least one test failed.

CI now collects coverage on every run. The `Resolver and SDK coverage report` step writes
the summary to the job page and uploads the report as the `coverage-resolver-sdk`
artifact. It is informational, not a gate.
