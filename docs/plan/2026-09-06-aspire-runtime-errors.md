# CRM/ERP Aspire runtime error fixes

## Evidence and scope

The running demo exhibits a datetime/datetime2 key mismatch in heartbeat-gap MERGE,
overlapping retried contact upserts that violate PK_Contacts, and AMQP drain/session
lifecycle errors. Preserve unrelated working-tree changes and demo data. No push/PR.

## Implementation and verification

1. Add a fractional-second heartbeat-gap round-trip/re-upsert regression; demonstrate
   failure against SQL Server in an isolated test schema. Explicitly bind history
   timestamp parameters as DateTime2 and rerun the SQL history conformance suite.
2. Add a concurrent CRM contact-upsert regression against an isolated SQL database,
   checking one contact and one creation audit. Serialize the keyed read/write in a
   database transaction, propagate cancellation, and verify concurrent replay.
3. Reproduce emulator drain/attach lifecycle failures with SDK-level tests. Fix
   demonstrated lifecycle defects and replace silent background exception swallowing
   with structured diagnostic logging and appropriate cleanup. Test idle drains,
   session renewal and cancellation/closure while attaching or receiving.
4. Run the affected suites and build checks using isolated build outputs where needed
   to avoid the running Aspire app's file locks. Rebuild only affected demo resources
   through Aspire. Check fresh logs and heartbeat history, exercise concurrent upserts,
   and verify the demo is healthy. Record any remaining transport uncertainty honestly.

## Design constraints

Keep timestamp typing local to the SQL history provider; no global Dapper type-map
change. Preserve EF audit generation and existing contact origin semantics. Use
parameterized SQL and database-owned synchronization (not process-local locks).
Do not alter development authentication or suppress transport errors to mask defects.

## Confirmed transport cause

A receiver closed before an asynchronous session accept completes reaches AMQPNetLite
2.5.3's OnDetach while the listener link is still in Start, breaking the shared
connection. A raw AMQP regression reproduces the failure while sending on another
link. Track pending attaches and serialize completion against LinkRemoteClose;
finish the rejected attach handshake before the library handles the incoming detach.
Release a broker session if cancellation wins after acquisition. Retain structured
logging and cleanup for independently reproduced receive-pump failures.

## Verification results

- SQL Server heartbeat history: 6 tests passed against the demo's SQL Server in
  isolated test schemas. The fractional-key regression failed with PK_HeartbeatGaps
  before explicit DateTime2 binding and passed afterward.
- CRM demo tests: 17 passed, including 16 overlapping contact upserts against an
  isolated database, a single creation audit, and preservation of contact origin.
- Emulator: all 60 tests passed, including SDK smoke/fidelity scenarios, 200
  concurrent sessions, pending-attach cancellation, empty drains, session renewal,
  and failed-pump logging/cleanup. The initial stress failure led to the pending
  attach investigation; the focused test failed against the previous assembly
  and passed against the fix.
- Aspire rebuilt the CRM/ERP demo successfully, then rebuilt only the emulator
  for the final transport change. Its subscription message counts were all zero
  before that rebuild. All long-running demo resources are Running/Healthy;
  provisioning is Finished.
- Live contact `69e74ab0-1b37-416d-91ed-d06418673134`: 16 concurrent requests returned
  HTTP 200; one creation audit; the later update produced one update audit and
  preserved Partner origin.
- Live account `3c31871f-8052-4222-a722-fa7e332cb324`: captured Pending plus a stored
  PendingHandoffResponse, updated the same account, captured the update as Deferred
  and zero matching ERP rows before settlement. Handoff completed automatically at
  approximately 14:24:24 UTC; the update replayed by 14:24:27. ERP contains
  `Runtime fix handoff UPDATED` / `FIX-UPDATED`; both session lists are empty.
  Original handoff-mode settings were restored after job registration.
- Live heartbeat folding advanced and closed the previously conflicting
  DataPlatformEndpoint gap keyed at `2026-09-02T21:20:46.2533333`.
- Timestamped console check: 3,632 lines from 14:21:23 through 14:27:39 UTC on
  2026-09-06, with no recurrence of OnDetach/Start, SessionLockLost, drain failure,
  PK_Contacts, or PK_HeartbeatGaps after the final emulator restart. This is a
  bounded observation window, not a long-duration soak test.
- Existing logging still records intentional SessionBlockedException deferral as
  Error during the handoff check; the subsequent automatic replay succeeded.
  Development authentication/webhook warnings remain configured as before.
- `git diff --check` passed. Unrelated staged and working-tree changes were preserved.

Detailed test logs and live JSON evidence are in the user's temporary directory
under `nimbus-heartbeat-green.log`, `nimbus-crm-green.log`,
`nimbus-emulator-final.log`, `nimbus-live-contact-verification.json`,
`nimbus-fixed-handoff-*.json`, and `nimbus-final-log-evidence.json`.
