# PR 110 Review Fixes Implementation Plan

## Goal

Close the four correctness gaps found while reviewing PR 110: expired-lock settlement, explicit-session contention, session-management ownership, and Release-mode SDK smoke-test startup.

## Approach

1. Add broker regressions proving that expired message locks cannot be settled and that session renew/state operations require the current session-lock owner.
2. Add SDK-level regressions proving that a contended explicit session reports `SessionCannotBeLocked` and that the test host starts the same build configuration as the running test assembly.
3. Process due broker work before every settlement lookup so expired deliveries are returned to the active queue before settlement is attempted.
4. Distinguish a nonexistent explicit session from an already-locked session, and map the latter to `com.microsoft:session-cannot-be-locked` during link attach.
5. Track each accepted receiver link by AMQP connection plus `associated-link-name`; require that association for session renew/get/set management requests and validate the resolved broker owner.
6. Run targeted regressions, the complete emulator test project in Release, and the emulator Release build. Review the final diff and commit locally without pushing.

## Constraints

- Preserve the existing behavior where an explicit session that has never materialized waits for future work.
- Fail closed when a management request lacks a valid associated receiver link.
- Keep ownership scoped to the receiver link, even when multiple receivers share one pooled AMQP connection.
- Avoid changes outside the emulator and its tests.
