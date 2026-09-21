# Operator mockup and system interaction design

Build on the existing integration-platform investigation. Deliver two linked, standalone HTML artifacts in `docs/spec/`; do not change the running NimBus application.

1. Create an operator workspace with run filtering, run details, session queues, integration admission controls, connections, release metadata and an audit view.
2. Implement synthetic recovery and skip commands. Preserve per-session blocking, leave unrelated sessions unchanged, require a reason for skip, and show an audit entry. Pausing admission must not imply cancellation of running work.
3. Create a companion architecture page with deployment/state boundaries and step-through REST, messaging, SFTP and callback scenarios. Label proposed APIs and components explicitly.
4. Explain how external application teams and coding agents create and consume integrations through versioned contracts and releases.
5. Validate HTML references, JavaScript syntax and interaction behavior. Record the browser security restriction from this session: local HTML previews are blocked; do not try an alternative rendering route.

Baseline: SQL Server; per-session FIFO and failure isolation; C#-first authoring with the console for monitoring and management is confirmed by the user. RabbitMQ and the workflow bridge remain unimplemented designs. All console data and commands are simulations, with no backend or persisted browser state.

## Delivered

- `docs/spec/integration-operator-mockup.html`: standalone operator workspace with five working navigation views, run search and filters, run timeline/payload/audit tabs, session lanes, connection inventory, release visibility, admission pause/resume, recovery and skip dialogs, role preview and demo reset.
- `docs/spec/integration-system-architecture.html`: standalone architecture with seven selectable component boundaries, Azure/on-prem transport profiles, four five-step external-system journeys, interface contracts, state ownership, deployment boundaries and a C# integration delivery model.
- Cross-linked both artifacts from the original design proposal. Updated the proposal, investigation plan and MEMORY.md with the user's C#-first/operations-console confirmation.

## Verification

- A temporary Node VM/minimal-DOM harness passed **141 behavioral assertions**. It exercises event handlers for search/filter/navigation, detail tabs and keyboard navigation, role checks, recovery, skip, reason validation, stale-command rejection, duplicate submission, audit, reset and admission controls. Recovery checks compare all other sessions, including independent sessions within the same integration, before and after each command.
- Admission-pause checks prove in-flight work remains running, a released successor waits while paused, and resume preserves other session blockers. Customer recovery migrates only its selected run to 2.1.1. Payload corrections remain separate from admission-state revisions.
- A regression assertion initially failed because admitting a queued invoice incorrectly changed its displayed payload revision/currency. Split payload revision from command/state revision; the assertion and full harness then passed.
- Architecture checks cover all seven component buttons, both transport profiles, all twenty journey steps, back/next boundaries, direct step navigation and keyboard scenario navigation.
- Structural HTML checks passed: balanced elements, unique IDs, local links/anchors, accessibility references and no external runtime dependencies or unsafe HTML insertion. Operator: 58 IDs / 5 links / 13 accessibility references. Architecture: 36 IDs / 14 links / 6 accessibility references. Updated proposal: 30 IDs / 80 links / 4 accessibility references. External links are checked for HTTPS form, not network availability.
- Inline scripts compile in Node as part of the interaction harness. Git whitespace checks passed. No NimBus runtime files, dependencies, services or deployment state were changed; solution tests are not relevant to these standalone mockups.
- Browser security policy blocked local previews earlier in this session. No workaround was attempted. **Rendered visual QA remains unverified**, including desktop/mobile appearance and real-browser accessibility. DOM tests do not prove distributed session guarantees, persistence, backend authorization or deployment readiness.
