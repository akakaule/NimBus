# Deferred message inspection and tracking recovery

## Problem

An endpoint's tracking row can remain Deferred after the corresponding broker message
has gone. Reprocess only requests a deferred-subscription drain and cannot fix that row.

## Implementation

1. Add an endpoint-scoped inspection API. Read the current row and its event history;
   show the latest terminal outcome for the same endpoint and session, with message ID
   and timestamp. Historical completion is evidence, not proof that a newer attempt completed.
2. Peek (never receive or settle) the endpoint and Deferred subscriptions and their dead-letter
   queues. Match event and session, paginate with a bounded scan and timeout. Report Present,
   NotFound, or Unknown; incomplete scans and errors must never imply absence.
   Live verification found the local emulator does not expose transfer-dead-letter
   peeking. Consult the administration API's transfer-dead-letter counter first:
   an explicitly zero count reports an empty queue; a positive count still requires
   peeking, and a failed count lookup remains Unknown. Label counter-based evidence.
3. Offer a tracking-only Skip for Deferred rows older than 15 minutes when the inspection
   found no matching broker messages. Require a reason and the inspected row version;
   repeat inspection on submission. Do not publish a SkipRequest, clear session state,
   delete history, or remove other messages. Explain that this does not cancel in-flight work.
4. Add an atomic conditional Deferred-to-Skipped storage operation in SQL, Cosmos and the
   in-memory provider; compare last message ID and update time. External providers fail
   closed until they implement the operation. Keep the original message identity and payload.
5. Apply endpoint Reader/Contributor permissions and record the operator, reason, prior
   state and inspection evidence in the audit trail. Surface audit failures separately
   from a successful state transition.
6. Add a detail-page inspection panel with independent history/broker results, explicit
   unknown/error states, reason/confirmation for Skip, and refreshed details/audits afterward.
   Ignore asynchronous results from a previous event route.

## Verification

- Write failing tests before behavior: endpoint/session-scoped history, broker match,
  pagination/truncation/errors, stale/recent/changed row rejection, permissions and audit.
- Run provider conformance tests for conditional transitions (including concurrent/stale
  updates and preservation of unrelated rows).
- Run focused WebApp tests and UI tests, regenerate API contracts, build frontend/backend.
- Stop the local Aspire demo before rebuilding shared assemblies; restart and verify its
  health and the actual inspection API against a stored Deferred record when available.

## Limits

Broker peeking is a point-in-time observation, not a transaction with the tracking store.
Absence cannot prove successful business processing, and skipping the tracking record
cannot cancel an already executing handler or prevent a future external replay. Preserve
history and make this limitation explicit in the UI. No automatic reconciliation or bulk
deletion is introduced.

## Verification results

- Observed failing storage, inspection, API and UI tests before implementing each behavior.
- WebApp suite: 491 passed, 2 skipped; final focused recovery suite: 17 passed,
  including empty/nonempty/unavailable transfer queue runtime counts.
- Event-detail frontend tests: 66 passed; TypeScript/Vite production build passed.
- In-memory provider suite: 254 passed.
- Cosmos conditional-write/adapter tests: 32 passed, including ETag conflicts and deletion
  between read and replacement. A live Cosmos service was not configured for this run.
- Live SQL Server: 6 conditional-transition tests passed. Found and fixed timestamp
  rounding by explicitly binding the expected DATETIME2 version as DbType.DateTime2;
  a deterministic full-precision regression test covers it.
- Live demo: WebApp healthy after restart. Inspection of the reported CrmEndpoint
  event returned HTTP 200, all six broker locations NotFound, history outcome Unknown,
  and canSkip=true. The event itself was not modified during verification.
