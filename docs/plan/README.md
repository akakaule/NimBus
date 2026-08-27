# Refactoring implementation plans

## Purpose

This directory coordinates five independently executable refactoring plans. Each plan preserves current behavior, starts with characterization or architecture tests, and uses small RED-GREEN-REFACTOR increments. The plans incorporate the source-verified findings in [REVIEW.md](REVIEW.md).

## Plans

1. [Narrow WebApp storage dependencies](01-narrow-webapp-storage-dependencies.md) — complete
2. [Decompose storage-provider implementations](02-decompose-storage-provider-implementations.md) — complete
3. [Decompose WebApp event components](03-decompose-webapp-event-components.md)
4. [Modularize CLI composition](04-modularize-cli-composition.md)
5. [Harden `StrictMessageHandler` lifecycle ordering](05-decompose-strict-message-handler.md) — complete

## Recommended sequence

Plan 1 must precede Plan 2 so consumers narrow their dependencies before provider internals move. Plans 3 and 4 are independent of the storage work and may run in parallel in separate worktrees. Plan 5's ordering-test work is also independent. Its optional deferred-sequence extraction requires the explicit trigger and decision gate defined in that plan rather than completion of the other refactors.

## Delivery rules

- Use one branch or worktree per plan. Do not mix these refactors with feature work.
- Break each plan into the proposed pull requests; do not submit the entire roadmap as one change.
- Preserve public and obsolete compatibility bridges unless a plan explicitly says otherwise.
- Add the failing test or architecture guard first, observe it fail for the intended reason, then make the smallest production change that passes it.
- Run targeted tests after every migration step and the plan's full verification gate before declaring the plan complete.
- Use Conventional Commits. Commit locally; pushing and opening pull requests require an explicit request.
- If an extraction exposes ambiguous ownership or requires a contract change not covered here, stop and amend the relevant plan before continuing.

## Completion order

The five plans are complete only when their individual exit criteria pass and the final integration gate succeeds:

```powershell
dotnet restore src/NimBus.sln
dotnet build src/NimBus.sln -c Release
dotnet test src/NimBus.sln -c Release --no-build
```

Use Release deliberately: repository analyzers promote warnings such as interface nullability mismatches to errors there, while a Debug-only loop can miss failures that CI catches.

The solution test command is not sufficient proof when live provider suites skip. Complete storage-related plans with a SQL Server 2022 test instance exposed through `NIMBUS_SQL_TEST_CONNECTION` and a Cosmos instance/emulator configured through `NIMBUS_COSMOS_TEST_CONNECTION` or `NIMBUS_COSMOS_TEST_ENDPOINT` plus `NIMBUS_COSMOS_TEST_KEY`. Set `NIMBUS_COSMOS_TEST_REQUIRED=1` when Cosmos conformance must fail instead of skip, and inspect the test summary for inconclusive/skipped provider tests.

For WebApp changes, also run the ClientApp lint, tests, and production build using Node.js 22 as described in Plan 3.
