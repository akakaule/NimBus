# Failure intelligence Admin settings

Status: implemented and locally verified (2026-09-21). User selected shared SQL/Cosmos storage.

## Scope

Implement the approved [Admin mockup](../spec/033-integration-intelligence-failure-classification/admin-mockup.html)
as a real Admin tab. Allow activation, model selection, event-payload inclusion,
history limits, additional secret keys, endpoint allow-list and guidance thresholds.
Keep the TypeSafe API key and base URL deployment-managed, never returned through
the settings API. No connection-test or classification call occurs while editing.

The user requested payload inclusion. Enable it for the intended installation via
its saved configuration, not by changing the package's default-off data-sharing
policy for every installation. Existing classifications do not automatically rerun.

## Approved storage decision

Use a separately owned configuration record in the selected SQL/Cosmos
backend, with an optimistic-concurrency revision. Do not add intelligence-specific
methods to the core message-store abstractions or misuse endpoint metadata.

An explicit early configuration-bootstrap scope reuses the WebApp storage
registration and resolved options, reads settings with a bounded timeout, and is
disposed before the real host is built. It never starts hosted services, creates
classification schema or constructs a provider. Runtime settings operations reuse
the real host's registered SQL options/Cosmos client. SQL configuration schema is
created transactionally on first save; Cosmos configuration uses a separately
provisioned `intelligencesettings` container partitioned by `/id`.

The existing Admin platform-config screen only reads/exports topology. There is no
general writable settings service to reuse. This choice affects startup loading,
deployment provisioning, migration ownership and multi-instance behavior.

## Implemented activation contract

- Admin settings routes remain available to authenticated site Owners even when
  classification routes are disabled or the provider is unconfigured.
- GET returns non-secret active settings, saved settings, revision, restart-required
  state and a credential-configured boolean. Never serialize provider options wholesale.
- PUT validates the complete allow-listed DTO and expected revision before a
  conditional save. A stale editor receives 409, not an overwritten configuration.
- Saved non-secret values override their deployment defaults after restart. Secrets
  and provider base URL always remain deployment-owned. Document this precedence.
- Each WebApp takes an immutable startup snapshot. Save does not mutate singleton
  options, revive pruned controllers, restart processes or interrupt active requests.
- A saved configuration read failure must never silently restore broader permissions
  or payload sharing. Define a contained fail-closed activation path with Admin recovery.
- Show restart requirements for all instances, not a claim that the whole deployment
  has adopted changes because the current instance restarted.

## Implementation sequence

1. Add failing contract tests for DTO validation, secret exclusion, denied site-owner
   access, CSRF protection, stale revision, persistence failure and restart semantics.
2. Implement independent settings persistence and startup snapshot loading for the
   selected storage design. Preserve existing disabled-mode provider/schema guarantees;
   document any new configuration-schema provisioning explicitly.
3. Add authenticated Admin GET/PUT routes, bounded request bodies, strict validation,
   conditional saves, sanitized responses and best-effort change/denial audit records.
   Attach rate limits through the existing convention so its kill switch still works.
4. Implement the Admin tab using existing UI primitives. Display active versus pending
   settings, payload-sharing consent, scope, limits, an illustrative state preview and
   review/save/reset. Do not display real payload data or accept keys in this page.
5. Test initial loading, forbidden/unavailable states, validation, save failures, stale
   revisions, navigation cleanup, keyboard use, mobile layout and secret-free preview.
6. Run focused backend/frontend tests, full affected suites and builds. Exercise the
   chosen real durable store, restart loading and two-editor concurrency. Update the
   operator documentation and Spec 033 activation/configuration contract.
7. Enable payload inclusion only in the user-designated deployment configuration.
   Do not submit an analysis, restart Aspire, publish, commit or push without the
   corresponding instruction.

## Completion evidence

Record actual test commands/results and limitations here. A working browser-only
form or saved-but-unused configuration is not completion. Verify that the next
explicit classification after activation receives redacted payload evidence while
unknown/unverifiable payloads remain withheld.

### Verification record (2026-09-21)

- Focused tests were introduced before their implementations and initially failed
  to compile/import the missing settings contract/component.
- Release WebApp suite: 469 passed, zero skipped, with isolated test databases on
  local SQL and a disposable Cosmos emulator. Includes conditional first-save/update
  races, stale editors, shared reads, startup override/pruning, authorization, CSRF,
  consent, secret-field rejection, safe errors and audit enum round-tripping.
  Final rerun after configuration-overlay/corruption hardening: 469 passed, zero skipped.
  Stored JSON null is rejected on both backends; actual SQL bootstrap reload and
  fail-closed handling are covered. Unrelated configuration reload behavior is preserved.
- Release Integration Intelligence suite: 40 passed, zero skipped with real SQL/Cosmos,
  including the existing redaction/evidence and two-host classification race tests.
- Full frontend suite: 52 files / 373 tests passed with `--maxWorkers=2`.
  Focused Admin/component rerun after final UI adjustments: 5 passed.
- TypeScript/Vite production build and ESLint on changed UI files passed.
- Bicep entry-point compilation passed with pre-existing nullable/TTL/linter warnings.
- Actual SPA was inspected in headless Chromium at desktop and 390px widths using
  intercepted settings responses, not a live save. Consent/review interaction passed.
  The new editor has a scoped full-width phone layout because the existing app shell
  has a fixed-width sidebar. No production navigation redesign was needed.
- Local WebApp user-secret `NimBus:IntegrationIntelligence:FailureClassification:Data:IncludeEventPayload`
  was set to `true` as requested. This is a deployment default until an Admin revision
  is saved; saved settings then take precedence. New installations remain default-off.
- No Aspire restart, real provider request, deployment, commit or push. The running
  WebApp still needs the user's rebuild/restart before serving the new backend routes.
