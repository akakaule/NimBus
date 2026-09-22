# Plan: save the TypeSafe API key from Admin → Failure intelligence

Tracking issue: [#134](https://github.com/akakaule/NimBus/issues/134). Spec: 033, 2026-09-22 addendum.

## Goal

Site Owners can set, replace and remove the TypeSafe API key (required for the `jev-*`
models) from the WebApp instead of only through deployment configuration. The key is
stored sealed in the existing shared settings record, is never returned by any API,
and takes effect on restart like every other saved setting.

## Design

- **Storage.** `IntelligenceSettingsDocument` gains an optional `ProtectedApiKey`
  (Data Protection payload). Same SQL row / Cosmos item, same revision fence: no new
  table, container or Bicep change. Rows saved before the field existed load with null.
- **Protection.** `services.AddDataProtection().SetApplicationName("NimBus.WebApp")`
  in `Startup` and in the pre-DI settings bootstrap so both resolve the same key ring
  (App Service: `%HOME%\ASP.NET\DataProtection-Keys`, shared across instances; local:
  `%LOCALAPPDATA%`). One-time cookie/antiforgery invalidation at upgrade.
- **Precedence.** Saved key overrides the deployment key. An unreadable saved key
  falls back to the deployment key and is reported as `unreadable`; the bootstrap never
  fails closed because of the key.
- **API.** `PUT /api/admin/failure-intelligence` adds write-only `apiKey` (trimmed,
  1–512 chars, no whitespace or control characters) and `clearApiKey`; both set → 400
  `InvalidApiKey`. Omitting both carries the sealed key forward (the controller reads
  the current record under the same expected revision). Responses add
  `credentialSource` (`saved|deployment|none`, running instance) and `savedApiKey`
  (`none|configured|unreadable`, shared record). Audit data adds
  `"apiKey":"replaced|cleared|unchanged"`.
- **UI.** Password input with `autocomplete=new-password`, cleared after save; *Remove
  the saved key* checkbox when a key exists; header shows the active key source; review
  summary names the key action; unreadable key shows an alert.

## Files

- `src/NimBus.WebApp/Services/IntegrationIntelligence/IntelligenceSecretProtector.cs` (new)
- `.../IntelligenceAdminSettings.cs` — `IsValidApiKey`, `ApplyTo(configuration, apiKey)`, document field
- `.../IntelligenceSettingsBootstrap.cs` — Data Protection in the bootstrap, `ApiKeySourceKey`, protector-aware `LoadAsync`
- `.../IntelligenceSettingsSnapshot.cs` — `CredentialSource`
- `src/NimBus.WebApp/Controllers/IntelligenceSettingsController.cs`, `src/NimBus.WebApp/Startup.cs`
- `src/NimBus.WebApp/ClientApp/src/components/admin/failure-intelligence-settings.tsx` (+ `.test.tsx`)
- `tests/NimBus.WebApp.Tests/IntelligenceSettingsApiTests.cs`, `IntelligenceSettingsTests.cs`, `IntelligenceSettingsStoreTests.cs`
- `docs/integration-intelligence.md`, spec 033 addendum

## Verification

- `dotnet test tests/NimBus.WebApp.Tests` — key lifecycle (sealed at rest, never echoed,
  replace/carry-forward/clear, bootstrap applies the saved key and reports its source,
  unreadable key falls back), legacy record deserialization, validation bounds.
- `npx vitest run src/components/admin/failure-intelligence-settings.test.tsx` — masked
  write-only field, request body shape, review wording, removal flow, no key text rendered.
- Release build of `NimBus.WebApp` and its tests (CS8767 is fatal only in Release).
- SQL/Cosmos store round trip of `ProtectedApiKey` runs under the env-gated conformance suites.
