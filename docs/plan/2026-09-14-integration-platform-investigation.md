# Code-first integration platform investigation

Scope: investigate a BizTalk/Logic Apps-inspired integration layer that can extend NimBus; produce an HTML proposal and compare existing open-source and commercial alternatives. No runtime implementation or installation.

1. Inspect NimBus extension, messaging, handoff, management, and agent seams.
2. Verify candidate products against official documentation: authoring model, REST/messaging/file transfer, operations console, hosting, persistence, and licensing model.
3. Define connector, integration, workflow, session-ordering, and control-plane boundaries; propose a build-versus-adopt strategy.
4. Design a repeatable coding-agent development contract and a limited validation pilot.
5. Produce and validate `docs/spec/code-first-integration-platform.html` with architecture, illustrative operations UI, and a requirements-based comparison.

Baseline carried from user confirmations: SQL Server for on-prem; failures block only their session/entity while unrelated sessions continue. C#/.NET-first authoring with the console for monitoring and management was confirmed by the user on 14 September 2026. Cloud-connected and fully disconnected hosting are distinguished when comparing products.

Browser constraint carried from this session: local HTML navigation was denied by the browser URL security policy. Do not work around that denial. Use artifact structure and interaction checks and disclose the lack of rendered-browser QA.

## Results and validation

- Created the standalone HTML proposal with architecture, connector contracts, state ownership, durable workflow handoff, an agent development contract and a staged pilot.
- Compared ten alternatives using primary product documentation. Recommended a common pilot for NimBus + Elsa + SQL Server and WSO2 Integrator + ICP; no product is claimed to satisfy the exact session guarantees without testing.
- Clearly separated existing NimBus capabilities, proposed APIs and packages, and the unimplemented on-prem transport proposal. The user subsequently confirmed C#-first authoring and an operations console.
- Validated 78 link references for local-path/anchor resolution and HTTPS external-link form; external availability is not established by this structural check. All 30 IDs are unique.
- Inline JavaScript syntax passed Node checking. A minimal DOM harness passed 48 assertions for four product filters, three scenarios, recovery, reset and scenario switching.
- No external scripts or embedded remote resources; no innerHTML. Git whitespace checks passed.
- Rendered visual QA remains unverified because browser local-URL access was denied earlier in this session. No alternate route was attempted.
- Runtime proof, failover, engine selection, licensing/edition confirmation and agent onboarding benchmarks remain pilot work; no platform components were installed or implemented.
