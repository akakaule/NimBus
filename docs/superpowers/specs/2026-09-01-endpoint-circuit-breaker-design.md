# Endpoint Circuit Breaker Design

Issue: [#114](https://github.com/akakaule/NimBus/issues/114)

## Goal

Add an opt-in circuit breaker per subscriber endpoint. Handler retry failures open the circuit, all hosted Service Bus session receivers in the process stop without settling additional messages, and processing resumes at one concurrent session for a bounded half-open probe before returning to configured concurrency.

## Boundaries

- The breaker is process-local and scoped to the single subscriber endpoint enforced by the SDK.
- Azure Functions receive loops cannot be paused; the recorder, state, metrics, and notifications still operate.
- The breaker never changes Service Bus entity status and therefore cannot conflict with operator subscription controls.
- Permanent failures, session blocks, heartbeat traffic, cancellation, and configured exception exclusions do not count by default.
- WebApp persistence and display are deferred because they require storage-contract changes.

## Components

`CircuitBreakerOptions` validates the sampling window, minimum throughput, failure percentage, break duration, half-open success count, permanent-failure policy, and exception filters.

`EndpointCircuitBreaker` is a lock-protected state machine driven by `TimeProvider`. Closed-state outcomes are retained only for the sliding sampling window. Open-state outcomes are ignored. After the break duration, the state becomes half-open; one counted failure reopens it, while the configured number of successes closes it. Each transition completes the current state-change signal and swaps in a new signal, broadcasting the transition to all active receiver waiters.

`CircuitBreakerRecorderBehavior` wraps the handler pipeline, records success or eligible failures, and rethrows the original exception unchanged. The SDK appends it to any globally configured pipeline and installs an empty pipeline only when the opt-in feature otherwise has no pipeline infrastructure.

`NimBusReceiverHostedService` treats breaker state as the desired processor mode:

- Closed: configured `MaxConcurrentSessions`.
- Open: no processor exists; wait for a state change or shutdown.
- Half-open: recreate with `MaxConcurrentSessions = 1`.

The loop reads current state before every processor creation, so a transition that happens between waits cannot be lost. Existing infrastructure-error recovery remains independent.

Transition metrics are emitted by the state machine. The OpenTelemetry gauge reads the singleton breaker state synchronously. A single hosted bridge queues state-change events and awaits lifecycle observers outside handler threads; observer failures are logged and do not stop the bridge.

## Compatibility

No circuit-related service is resolved unless `WithCircuitBreaker(...)` is called. Existing subscriber and receiver constructors remain source-compatible through optional parameters. Existing global pipeline behaviors retain their registration order, with the circuit recorder appended innermost so it observes the terminal handler result.
