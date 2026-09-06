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
