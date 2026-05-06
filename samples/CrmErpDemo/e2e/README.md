# CrmErpDemo end-to-end tests

Playwright suite that drives the live CrmErpDemo Aspire AppHost end-to-end:
creates entities, lets NimBus propagate them, simulates failures, drives the
NimBus management WebApp UI to resubmit, and asserts on cross-system state.

## What's covered

| Spec | Scenario |
|---|---|
| `01-happy-path.spec.ts` | CRM → ERP propagation, ERP → CRM propagation, both endpoints stay clean |
| `02-error-mode-recovery.spec.ts` | Single message fails (ERP error mode) → resubmit via NimBus WebApp UI → success. Plus a service-mode silent-drop variant. |
| `03-blocked-messages-recovery.spec.ts` | One create + N updates published while ERP is in error mode. Head fails, siblings defer behind the session lock. Resubmit head from the WebApp; verify the deferred backlog drains in order and all endpoints return to zero. |
| `04-pending-handoff-success.spec.ts` | Handoff mode ON: create handed off, sibling updates defer behind the in-flight handoff, `HandoffJobBackgroundService` settles via `CompleteHandoff`, all rows reach Completed. |
| `05-pending-handoff-failure.spec.ts` | Handoff mode at failureRate=1.0: `FailHandoff` carries DMF error text → audit row Failed → operator Skip via NimBus REST → Skipped. |
| `06-pending-handoff-resubmit.spec.ts` | Same trigger as 05, but operator clicks Resubmit in the NimBus.WebApp UI (handoff mode disabled first); audit row leaves Failed and the ERP customer materialises. |

Setup and teardown go through REST APIs; the WebApp browser is used for the
operator action being demonstrated. Specs 02, 03 and 06 drive the WebApp
endpoints-list view (drilling in via UI click and clicking Resubmit on a
row); specs 04 and 05 are pure REST.

## Prerequisites

1. **Node 22+** (matches the rest of NimBus).
2. **The Aspire AppHost running**:
   ```bash
   dotnet run --project samples/CrmErpDemo/CrmErpDemo.AppHost
   ```
   Wait for everything to come up healthy in the Aspire dashboard (default
   `https://localhost:17080`).
3. **Local dev auth enabled** in the NimBus WebApp. The AppHost wires this in
   Development environment automatically; if you've overridden settings,
   ensure `EnableLocalDevAuthentication=true` and `ASPNETCORE_ENVIRONMENT=Development`
   are set for the `nimbus-ops` resource.

## Setup

```bash
cd samples/CrmErpDemo/e2e
npm install
npx playwright install chromium

cp .env.example .env.local
# Fill in CRM_API_URL, ERP_API_URL, NIMBUS_OPS_URL with the URLs Aspire
# assigned (visible in the dashboard's "Endpoints" column for each project).
```

## Run

```bash
# All specs, headless, report at the end
npm test

# Just the happy path
npm run test:happy

# Watch the browser drive the WebApp (useful for debugging selectors)
npm run test:headed

# Interactive UI mode (Playwright UI)
npm run test:ui

# Open last HTML report
npm run report
```

## Why serial?

The tests share the live AppHost — same Service Bus, same database, same NimBus
backing store — so they are configured `workers: 1` and `fullyParallel: false`.
Test ordering is not guaranteed across files, but each spec's `beforeAll`
resets ERP failure modes so cross-test contamination is bounded.

## What the tests assume

- ERP API exposes `/api/admin/error-mode` and `/api/admin/service-mode` PUT
  endpoints (provided by `Erp.Api/Endpoints/AdminEndpoints.cs`).
- The NimBus management WebApp uses the `CrmErpPlatformConfiguration` so
  endpoints `CrmEndpoint` and `ErpEndpoint` are visible (wired in the AppHost
  via `NimBus__PlatformType`/`NimBus__PlatformAssembly`).
- `SessionKey` on Crm/Erp events resolves to the account/customer id, which
  is what test 03 relies on for the deferred-backlog behaviour.

## Debugging tips

- **Tests time out early**: increase `PROPAGATION_TIMEOUT_MS` in `.env.local`.
  Cosmos cold starts and Service Bus handshake latency vary.
- **Selector fails in the WebApp**: the WebApp does not currently expose
  stable `data-testid` attributes; selectors rely on row text + button text.
  Use `npm run test:headed` to watch what the UI looks like vs what the
  selector targets.
- **Failed-count never goes to zero in test 03**: this usually means the
  resubmit raced against the deferred siblings being redelivered — bump
  `FAILED_MESSAGE_TIMEOUT_MS` or rerun.
- **Auth fails (302 to login)**: confirm the `nimbus-ops` Aspire resource is
  running in Development mode with `EnableLocalDevAuthentication=true`.
