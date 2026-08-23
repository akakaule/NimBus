# Plan 3: Decompose WebApp event components

## Goal

Separate data orchestration, operator actions, derived view models, and presentation in the two largest event-management components while preserving routes, API traffic, visual layout, accessibility names, and race-safety behavior.

## Scope

Primary targets:

- `ClientApp/src/components/endpoint-details/events-panel.tsx`
- `ClientApp/src/components/event-details/message-listing.tsx`
- `ClientApp/src/components/endpoint-details/events-panel.paint.test.tsx`
- `ClientApp/src/components/endpoint-details/events-panel.columns.test.ts`
- `ClientApp/src/components/event-details/message-listing.test.tsx`
- `ClientApp/src/components/event-details/audit-listing.test.tsx`
- directly related event-detail components and feature-local functions

This is a structural refactor. Redesigning the UI, changing API contracts, replacing state management libraries, changing table behavior, or regenerating the API client is out of scope.

## Design principles

- Extract behavior before markup so existing render tests continue to describe the user-visible contract.
- Prefer focused hooks and pure functions over a global store or new state-management dependency.
- Preserve monotonic-ticket guards for route/filter changes and enrichment calls. Every async state commit must reject stale generations.
- Preserve serialized per-event report writes and revision-aware rollback.
- Avoid mutating API event objects in new code. Introduce immutable updates behind characterization tests rather than carrying mutation into extracted hooks.
- Keep generated types at the API boundary; use small local view models only where they simplify rendering.
- Do not change CSS classes or accessible labels during extraction unless a failing accessibility test requires it.

## Phase 1: strengthen characterization tests

Add failing or missing tests before extraction.

For `events-panel`:

1. A slow old filter response cannot replace a newer search result.
2. A page response from an old continuation-token generation cannot append to a refreshed result set.
3. Session-status enrichment paints after the initial rows and cannot update a stale generation.
4. Rapid Report → Undo and Report → Undo → Report writes reach the API in order.
5. Failure rolls back only to the last server-confirmed report state.
6. Hide-reported automatic refill remains bounded.

For `message-listing`:

1. Button states transition correctly on success and failure for resubmit, skip, delete, reprocess, and resubmit-with-changes.
2. Handoff completion/failure preserves operator feedback and never double-submits while busy.
3. Payload selection, formatting, redaction sentinels, and request-history fallback remain unchanged.
4. Navigating between events resets event-scoped modal and optimistic state.

Run each new test against the current component and confirm it either passes as characterization or fails for the intended missing coverage before production extraction.

## Phase 2: extract pure derivation modules

Move deterministic logic first:

- event-filter construction and session-count reduction;
- actionable-status and actionable-event decisions;
- table row/cell view-model construction;
- payload selection, routing, duration, JSON formatting, and handoff-result derivation.

Place functions near their owning feature, not in a generic `utils.ts`. Add table-driven Vitest tests for edge cases. Keep React nodes out of pure row models where practical; render cells in focused presentation functions.

## Phase 3: extract `events-panel` hooks

1. Create `use-endpoint-events.ts` to own initial fetch, generation tickets, continuation tokens, pagination, background session hydration, loading state, and bounded hide-reported refill.
2. Inject the API operations the hook needs so hook tests use small fakes without mocking the generated client module.
3. Create `use-event-reporting.ts` to own write chains, revisions, confirmed state, optimistic immutable updates, rollback, and notifications.
4. Keep URL/draft filter synchronization in `events-panel` unless a dedicated filter hook produces a clearly smaller public contract.
5. Reduce `events-panel.tsx` to filter composition, table-column configuration, action wiring, and rendering.

Do not combine fetch and reporting into one “page model” hook; they have independent concurrency rules and test lifecycles.

## Phase 4: decompose `message-listing`

Extract in this order:

1. `use-event-actions.ts` for resubmit, skip, delete, reprocess, and resubmit-with-changes request state.
2. `use-handoff-actions.ts` for complete/fail state, dialogs, optimistic hero state, and operator feedback.
3. `event-action-bar.tsx` for terminal failure actions.
4. `event-payload-panel.tsx` for payload display and redaction handling.
5. Focused dialog components for resubmit-with-changes, deletion confirmation, and handoff settlement.
6. Move the inline comment section to its own feature component if it still remains in the file.

Keep `message-listing.tsx` as the page-level composition component. Do not split small static fragments solely to reduce line count.

## Phase 5: stabilize component boundaries

1. Replace module-level generated-client mocks with injected operation fakes in new hook tests.
2. Keep a smaller set of integration-style component tests proving the composed DOM and user flows.
3. Run Prettier only on touched files and review formatting separately from behavior changes.
4. Confirm the production build does not alter the API client. If generation changes `api-client/index.ts`, restore only the generated change produced by the build after confirming it is unrelated; never overwrite pre-existing user changes.
5. Capture before/after screenshots if any layout or styling changes are intentionally accepted. For a strictly structural refactor, document that no visual change was intended.

## Verification

From `src/NimBus.WebApp/ClientApp` with Node.js 22:

```powershell
npm run lint
npm test -- --run src/components/endpoint-details/events-panel.paint.test.tsx
npm test -- --run src/components/endpoint-details/events-panel.columns.test.ts
npm test -- --run src/components/event-details/message-listing.test.tsx
npm test -- --run src/components/event-details/audit-listing.test.tsx
npm test -- --run
npm run build
```

Do not change application code to accommodate the test runner.

Then run:

```powershell
dotnet test tests/NimBus.WebApp.Tests/NimBus.WebApp.Tests.csproj -c Release
dotnet build src/NimBus.sln -c Release
```

## Proposed pull requests

1. `test(webapp): characterize event-panel concurrency and actions`
2. `refactor(webapp): extract endpoint-event data hooks`
3. `refactor(webapp): extract event-reporting state`
4. `refactor(webapp): decompose message listing actions and panels`

## Exit criteria

- The two composition components no longer own API concurrency algorithms directly.
- Async stale-result, pagination-generation, and report-ordering tests pass.
- Operator actions, routes, accessible names, and visual layout remain unchanged.
- Client lint, full Vitest, production build, WebApp server tests, and solution Release build pass.
