# ERP demo: selectable failure reasons for the Integration Intelligence showcase

**Date:** 2026-09-21
**Status:** implemented in the same change set

## Goal

The erp-web *Error mode* toggle already makes every ERP adapter handler throw, but it throws one
uninformative `HandlerErrorModeException`. Integration Intelligence (Spec 033) classifies a failure
from `ErrorContent.ErrorType` + `ErrorText` only (no stack trace, see
`ResponseService.CreateErrorContent`), so the demo had nothing meaningful to classify. Add a
failure-reason dropdown so an operator can pick which realistic failure the handlers simulate,
one per classification category, and compare the resulting analyses.

## Design

- **Single source of truth**: `CrmErpDemo.Contracts/Demo/ErpFailureReasons.cs` holds the catalog
  (`Id`, `Title`, `Description`, `ExpectedCategory`, `Disposition`) and the exception factory.
  Contracts is already referenced by `Erp.Api` and `Erp.Adapter.Functions`, so the stored
  selection, the thrown exception and the UI cannot drift. `HandlerErrorModeException` moves
  there unchanged (same simple name, so the recorded `ErrorType` is identical).
- **Reason ids equal the target category id** (`transient_dependency`, `business_rule`, …) plus
  `handler_exception` as the default/control case. `messaging_platform` is deliberately absent: a
  handler cannot authentically fake a lock-lost or entity-not-found transport failure.
- **Dispositions are honest**: only `contract_schema` throws a type the
  `DefaultPermanentFailureClassifier` dead-letters (`FormatException`); the rest stay `Failed`
  so the retry/resubmit story still works. A test asserts the advertised disposition against the
  real classifier.
- **API compatibility**: `PUT /api/admin/error-mode` keeps accepting `{ enabled }`; `reason` is
  optional and validated (400 with the known ids on an unknown value). `GET` gains `reason`;
  `GET /api/admin/error-mode/reasons` returns the catalog. The e2e helper and the demo film keep
  working unchanged because the button labels (`Error mode: ON/OFF`) are untouched.
- **Adapter**: `IServiceModeClient.IsErrorModeEnabledAsync` becomes `GetErrorModeAsync` returning
  `(Enabled, Reason)`; `ErrorModeGuard` resolves the reason and throws the factory's exception,
  quoting the inbound event type where the message mentions the payload.
- **UI**: a `<select aria-label="Failure reason">` next to the existing header button; the amber
  banner names the simulated failure, its NimBus outcome and the expected category, and points at
  *Analyze failure* in the NimBus WebApp.

## Files

| Area | File |
|---|---|
| Catalog + factory | `samples/CrmErpDemo/CrmErpDemo.Contracts/Demo/ErpFailureReasons.cs` (new) |
| API state | `samples/CrmErpDemo/Erp.Api/ErrorModeState.cs` |
| API endpoints | `samples/CrmErpDemo/Erp.Api/Endpoints/AdminEndpoints.cs` |
| Adapter client | `samples/CrmErpDemo/Erp.Adapter.Functions/Clients/IServiceModeClient.cs`, `ServiceModeClient.cs` |
| Adapter guard | `samples/CrmErpDemo/Erp.Adapter.Functions/Handlers/ErrorModeGuard.cs` |
| SPA | `samples/CrmErpDemo/Erp.Web/src/api.ts`, `src/App.tsx` |
| e2e helper | `samples/CrmErpDemo/e2e/helpers/erp-api-client.ts` (optional `reason` arg) |
| Tests | `tests/CrmErpDemo.AppHost.Tests/ErpFailureReasonsTests.cs` (new) |
| Docs | `samples/CrmErpDemo/README.md`, `docs/integration-intelligence.md`, adapter `docs/TDD.md` |

## Verification

- `dotnet build samples/CrmErpDemo/Erp.Api -c Release`, `... Erp.Adapter.Functions -c Release`: succeeded.
- `dotnet test tests/CrmErpDemo.AppHost.Tests -c Release`: 29 passed, 1 skipped (6 new).
- `npm run build` in `Erp.Web` (tsc -b + vite): succeeded. `npx tsc --noEmit` in `e2e`: clean.
- Manual: restart the CrmErpDemo AppHost, pick a reason, flip error mode ON, edit a CRM account,
  open the failed message in the NimBus WebApp and click *Analyze failure*.

## Not done / follow-ups

- No Playwright spec drives the dropdown yet; `02-error-mode-recovery` still uses the generic
  failure on purpose. A showcase spec would need Integration Intelligence enabled with a mocked
  provider in the e2e environment.
- The CRM side's error mode (circuit-breaker outage simulation) is a different feature and was
  left alone.
