# Service Bus Emulator implementation plan

Implements `docs/specs/027-service-bus-emulator/spec.md` as a loopback-only .NET 10 broker that the stock Azure Service Bus SDK can use through one Aspire-provided endpoint.

## Milestone 1: projects and broker core

- Add `NimBus.ServiceBusEmulator`, `NimBus.ServiceBusEmulator.AspireHosting`, and `NimBus.ServiceBusEmulator.Tests` to the solution.
- Pin `AMQPNetLite.Core` 2.5.1 and compile-prove the two-resource Aspire handle.
- Build test-first topic, subscription, rule, message, session, scheduling, settlement, forwarding, quota, and topology-journal state machines.

## Milestone 2: protocol surfaces

- Serve AMQP and HTTP through a bounded loopback TCP multiplexer.
- Implement MSSBCBS SASL, CBS, dynamic entity links, transfers, sessions, settlement, peek, scheduling, lock renewal, and session state.
- Implement the hardened ATOM/XML administration API and runtime properties.

## Milestone 3: integration

- Add AppHost emulator selection and connection-string propagation.
- Add the CLI topology connection-string path without shelling to Azure.
- Add SDK conformance, provisioning idempotency, restart, security, and Aspire contract tests.
- Document standalone and Aspire usage.

## Verification

- Targeted emulator tests during each milestone.
- `dotnet build src/NimBus.sln -c Release`.
- `dotnet test src/NimBus.sln` and any emulator-specific integration filters.
- NuGet vulnerability audit remains clean through the repository Release-build gate.
