# ADR-014: Commands Are Events with an Enforced Single Consumer

## Status
Accepted (introduced 2026-07)

## Context

NimBus routes every message by event type: the publisher stamps `To = EventTypeId`, and one forward subscription per *declared consumer* copies the message to each consuming endpoint's topic. A publisher cannot address a specific endpoint through the public API, and "this message has exactly one recipient" is expressible only as a catalog convention — declare one consumer and hope nobody adds a second. If someone does, the "command" silently becomes a broadcast with no compile-time or runtime signal.

When users asked for command messages ("do this", one logical recipient, imperative intent) alongside events ("this happened", any number of observers), we had three options:

1. **Pure naming convention** — imperative class names, single declared consumer, README discipline. Zero enforcement; the silent-broadcast failure mode remains.
2. **First-class point-to-point routing** — a `Send(command, targetEndpoint)` API addressing `To = '{endpointId}'` directly, or real queue entities. True point-to-point, but a new send path, provisioning changes across all topology emitters, and Resolver semantics review — significant machinery for a guarantee the catalog can already express.
3. **Marker type + catalog enforcement** — a `Command` base class deriving from `Event` (wire, pipeline, ordering, audit all unchanged) plus validation that every command type has exactly one declared consumer, failing provisioning otherwise.

## Decision

Option 3. `NimBus.Core.Events.Command` is an empty marker base class extending `Event`. `NimBus.Core.PlatformValidation.EnsureCommandConsumers(IPlatform)` enforces at provisioning time that every catalog event type whose CLR type derives from `Command` has **exactly one** consuming endpoint:

- **Zero consumers is an error** — every send would dead-letter.
- **More than one is an error** — the instruction would fan out as a broadcast.

The check runs in `ServiceBusTopologyProvisioner.ApplyAsync` (covering `nb topology apply` and in-process provisioner consoles). Platforms constructed from exported configuration (the WebApp path) carry no CLR types and are skipped by construction — enforcement happens where the code-first catalog lives.

The command/event distinction is therefore **real but contractual, not mechanical**: same topics, same session ordering, same Resolver audit, same handlers (`IEventHandler<TCommand>`). What changes is the catalog contract the platform is willing to provision.

## Consequences

- Adding a second `Consumes<SomeCommand>()` fails topology provisioning with an error naming the command and all consumers — the silent-broadcast failure mode is gone.
- No new routing infrastructure, no dual send paths, no changes to storage or the Resolver.
- Commands remain observable in the WebApp exactly like events.
- Enforcement is only as early as the first provisioning run; a host that never provisions never validates. Acceptable: topology apply is already the gate every deployment path goes through.
- If genuine point-to-point routing (competing consumers, load-balanced queues) is ever needed, that is a separate feature; this ADR does not preclude it.
- Convention: imperative names for commands (`PlaceCustomerOnCreditHold`), past tense for events (`CustomerCreated`). Request/reply request types are good candidates for `Command`, since multiple responders would race to reply.
