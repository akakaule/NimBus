# Refactoring implementation plans

## Purpose

This directory turns the maintainability audit into five independently executable plans. Each plan preserves current behavior, starts with characterization or architecture tests, and uses small RED-GREEN-REFACTOR increments.

## Plans

1. [Narrow WebApp storage dependencies](01-narrow-webapp-storage-dependencies.md)
2. [Decompose storage-provider implementations](02-decompose-storage-provider-implementations.md)
3. [Decompose WebApp event components](03-decompose-webapp-event-components.md)
4. [Modularize CLI composition](04-modularize-cli-composition.md)
5. [Decompose `StrictMessageHandler`](05-decompose-strict-message-handler.md)

## Recommended sequence

```text
Plan 1: narrow store consumers ──> Plan 2: split provider internals

Plan 3: WebApp event components ────────────────────────────────┐
Plan 4: CLI composition ────────────────────────────────────────┼─> Plan 5: core lifecycle
                                                               ┘
```

Plans 3 and 4 are independent of the storage work and may run in parallel in separate worktrees. Plan 5 should run last: it changes the most sensitive orchestration code and benefits from a quieter branch and the testing patterns established by the other refactors.

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

For WebApp changes, also run the ClientApp lint, tests, and production build using Node.js 22 as described in Plan 3.
