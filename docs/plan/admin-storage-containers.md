# Admin Storage container management

1. Return all Cosmos containers with authoritative platform membership (catalog endpoints or reserved NimBus storage, using exact names). Keep platform deletion protected on the server.
2. Show membership badges and highlight containers outside the platform. Filter by all, in platform, or not in platform.
3. Select individual or all visible non-platform containers. Clear selection on filter/refresh changes. Confirm the exact deletion list with an irreversible-data-loss warning and typed confirmation.
4. Reuse the audited single-container delete API sequentially for bulk operations, report partial failures, and refresh the list.
5. Verify classification/protection with backend tests and filtering, selection, confirmation, cancellation, and partial failures with component tests; build the frontend.

Completed: the initial backend regression failed on the orphan-only listing and the UI regressions failed on missing controls. After implementation, all 6 AdminCosmosContainerTests and all 7 container-manager component tests pass. Production frontend build, targeted ESLint, and git diff whitespace checks pass. No live containers were deleted.
