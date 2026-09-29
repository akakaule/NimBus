# Configurable audit types

Operators asked to choose which operator actions reach the audit log: a busy site writes a
`SearchEvents` row on every list refresh, which buries the resubmits and role grants the log
exists for. Also, the endpoint Audit tab prints raw camelCase actions (`searchEvents`) while
the Audit Log page prints `Search events`.

## Behaviour

- A platform-wide singleton, `AuditSettings`, holds the **disabled** audit types by
  `MessageAuditType` name. Nothing stored means every type is recorded, so existing
  deployments keep today's behaviour.
- `AuditLogService` skips both sinks (message store and the App Insights structured event)
  for a disabled type, with two exceptions that are always recorded:
  - access-denied attempts — a rejected privileged action is a security signal;
  - `UpdateAuditSettings` (new, appended last to the enum) — turning auditing down must
    itself be audited.
- Settings are read through a singleton cache with a short TTL (30 s), so the hot path
  (`SearchEvents` on every list refresh) does not add a store round trip; a save on this
  instance refreshes the cache immediately. A read failure records the row (fail open).
- Scope is the operator audit channel (`IAuditLogService`). Rows written straight to the
  event history — comments, Resolver `Retry` rows, the deferred-skip and reconcile markers —
  are event trail, not access audit, and are unaffected. The Admin UI does not offer
  `Comment` or `Retry`.

## Storage

Follows `HeartbeatSettings` (a singleton on `IEndpointMetadataStore`):

- `NimBus.MessageStore.Abstractions/States/AuditSettings.cs` — `Id`, `DisabledAuditTypes`.
- `IEndpointMetadataStore.GetAuditSettings()` / `SetAuditSettings(AuditSettings)`.
- Cosmos: fixed-id document in the existing settings container.
- SQL Server: migration `0022_AuditSettings.sql` (one row, JSON array column); table added
  to the `VerifyOnly` required-table list.
- In-memory store, `CosmosDbClient` / `SqlServerMessageStore` forwarding.
- Conformance: defaults when unwritten, round trip, overwrite.

## API and UI

- `GET/PUT /api/admin/audit/settings` (`AuditSettings { disabledAuditTypes[] }`), site Owner
  only, PUT audited as `updateAuditSettings` on success and denial. Unknown type names are
  rejected with 400; `updateAuditSettings` cannot be disabled.
- Admin → **Audit** tab: one checkbox per audit type, grouped, with Save.
- Shared `formatAuditType` in `functions/audit.functions.ts`, used by the Audit Log page and
  the endpoint Audit tab.

## Verification

- WebApp tests: disabled type skipped, access-denied still written, locked type still
  written, cache fail-open; controller Owner gate and validation.
- Conformance suite (in-memory locally; SQL/Cosmos when configured).
- Vitest for the Admin tab and the endpoint Audit tab casing.
- Release build + `npm run build` (NSwag regeneration from `api-spec.yaml`).
