# On-prem NimBus feasibility investigation

Scope: architecture research and a standalone HTML proposal; no runtime or transport implementation.

1. Trace session ownership, state, blocked-message recovery, transport composition, and management dependencies in the current repository.
2. Verify RabbitMQ FIFO, consumer failover, acknowledgements, routing, and durability against official documentation; compare a small set of alternatives.
3. Propose the minimum viable on-prem architecture preserving per-session failure isolation, with explicit ordering and crash-recovery contracts.
4. Produce `docs/spec/on-prem-nimbus-feasibility.html` with architecture diagrams, an illustrative FIFO walkthrough, source links, migration scope, and proof-of-concept gates.
5. Validate the standalone artifact, its links and interactions, and inspect its rendered layout when browser tooling is available.

Confirmed by the user: SQL Server is the on-prem storage baseline; a failed message must block only its own session/entity while unrelated sessions continue, including those sharing a broker partition. Partition-only blocking is ruled out as the target design. No throughput or availability targets have been supplied. Findings are design analysis, not a tested RabbitMQ implementation.

## Investigation result

- Completed repository tracing and official documentation review; report created at `docs/spec/on-prem-nimbus-feasibility.html`.
- Recommended a bounded RabbitMQ quorum-queue + SQL session-runtime POC. Included partition-only, per-session queue, stream, Artemis, and emulator alternatives.
- Kept the guarantee boundary explicit: SQL fencing alone cannot prevent stale remote business effects. External version/fence enforcement or reconciliation is required for strict observable FIFO during failover.
- Artifact checks passed: 48 links inspected for local target/anchor validity, 22 unique IDs, JavaScript syntax, and 15 state assertions in a minimal DOM harness. `git diff --check` passed.
- Rendered browser inspection could not be completed: browser URL security policy blocked opening the local HTML. No alternate route was attempted.
- No runtime code changed, no broker POC/benchmark performed, and no commit/push made. The report states these limits.
