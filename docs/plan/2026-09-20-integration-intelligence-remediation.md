# Spec 033 implementation remediation

Status: implemented and locally verified (2026-09-20). Scope: findings in the implementation review and second opinion.

1. Add failing regressions for expired reservations, stale completion, redaction, activation and UI recovery.
2. Fix request-scoped authorization, contained activation and explicit durable storage selection.
3. Share the reservation state-transition rules across providers; enforce atomic acquisition, immutable completion, request replay and owner fencing in SQL and Cosmos.
4. Fix outbound evidence and provider error classification; authorize before evidence and contain storage errors.
5. Decouple retention from successful core deletions using durable classification coordinates and a retrying reconciliation worker. Reconciliation checks authoritative event existence, fences active owners and removes results; failures retry without failing admin deletion. Document eventual cleanup semantics.
6. Complete Reader UI, initial loading, GET polling, unknown recovery, stale-response guards and contract checks.
7. Run Release tests, real SQL/Cosmos conformance where available, WebApp regressions and frontend checks. Record actual outcomes and any unavailable infrastructure.

All existing unrelated changes remain owned by the user. No deployment, provider call, push or package publication is part of this work.

## Decisions and verification

- Scoped host authorization/audit adapter, status and orchestration. Real Startup coverage verifies all four authentication branches, scope validation, and an administrator followed by an unprivileged principal in separate scopes.
- SQL rowversion / Cosmos ETag conditional per-failure aggregates replace the preview's non-atomic multi-record protocol. Fixed create identity prevents duplicate ownership; completion/failure are owner-fenced; expiry is unknown, not retry permission. Request bindings and successful revisions are immutable. Aggregate capacity is bounded at 1.5 MB with safe 503 refusal, not eviction.
- SQL uses its own embedded DbUp script/journal and serializes migration initialization. In-memory storage is test-only. Missing durable configuration fails closed, never falling back to volatile storage.
- Retention is eventual reconciliation every 60 seconds while enabled, independent of admin deletion. Durable source coordinates exist before the provider call. Tombstones remove results and fence active owners; outages retry. No independent Cosmos TTL. The spec and operator documentation now state these decisions.
- Security guidance informed the scoped authorization boundary, sanitized error responses, fail-closed free text and explicit outbound JSON projection. Provider IDs remain local; redaction precedes truncation.
- Reader UI loads existing results; unknown recovery uses a new forced request with a cost warning; transport retries preserve request identity; polling and navigation are bounded/isolated.

Commands and observed results:

- `dotnet test tests/NimBus.Extensions.IntegrationIntelligence.Tests -c Release --no-restore --verbosity quiet` with isolated local SQL/Cosmos connection variables: **28 passed, 0 skipped**. Includes conformance on both databases, two HTTP hosts sharing each database with a counting fake provider, exact outbound JSON, retention, authorization, architecture, and ambiguous completion tests. No TypeSafe network calls.
- `dotnet test tests/NimBus.WebApp.Tests -c Release -p:SkipSpaBuild=true --verbosity quiet -clp:ErrorsOnly`: **462 passed**. Subsequent activation/rate-limit regression run after strengthening request-isolation coverage: **16 passed**.
- `dotnet test tests/NimBus.Core.Tests -c Release --filter FullyQualifiedName~EventJsonMaskerTests --verbosity quiet -clp:ErrorsOnly`: **35 passed**.
- `npm test -- --run --maxWorkers=2`: **51 files / 365 tests passed**. The initial unrestricted parallel run had nine timeouts in five existing test files; the bounded-worker rerun passed without changing those tests. Node emitted existing experimental localStorage warnings.
- `npm run build`: passed (TypeScript and Vite production bundle).
- `az bicep build --file deploy/bicep/deploy.core.bicep --outfile <temporary path>`: passed; existing nullable-module/TTL/linter warnings remain. No Azure deployment was made.
- `git diff --check`: passed. Existing WebApp/Core warnings were not suppressed or represented as newly clean builds.

These are local validation results, not a deployment or full solution-wide test run. The storage layout is for this unreleased implementation; no destructive migration of an already-provisioned preview schema was performed.
