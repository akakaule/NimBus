# Endpoint Circuit Breaker Implementation Plan

Issue: [#114](https://github.com/akakaule/NimBus/issues/114)

## 1. Core state machine (RED -> GREEN)

- Add failing Core tests for option validation, sampling-window thresholds, open/half-open/closed transitions, probe failure, exception filtering, heartbeat exclusion, one-shot transition waits, and unchanged exception propagation.
- Add circuit options, state contracts, the thread-safe state machine, transition counter, and recorder behavior.

## 2. SDK registration and receiver control (RED -> GREEN)

- Add failing registration tests proving `WithCircuitBreaker` is opt-in, singleton per endpoint, and effective with or without `AddNimBus`.
- Add failing receiver-loop tests for open pause, concurrency-one half-open restart, full-concurrency close, broadcast behavior, and cancellation while paused.
- Register the breaker/recorder/lifecycle bridge and update the processor loop with no changes to the unconfigured recovery path.

## 3. Observability and notifications (RED -> GREEN)

- Add failing metric tests for transition tags and numeric state gauge without high-cardinality identifiers.
- Add failing lifecycle/notification tests for Critical open and Information close events, keyed by endpoint and state.
- Implement the gauge, lifecycle context/bridge, and notification option/observer behavior.

## 4. Documentation and verification

- Add `docs/circuit-breaker.md`; cross-link error handling and pipeline middleware; update the feature table and replace the stale backlog sketch with `#114`.
- Run targeted Release tests after each slice.
- Run `dotnet build src/NimBus.sln -c Release` and `dotnet test src/NimBus.sln -c Release --no-build`.
- If local Service Bus dependencies are available, run the adapter smoke scenario; otherwise document the exact environmental limitation and rely on processor-loop integration tests.
