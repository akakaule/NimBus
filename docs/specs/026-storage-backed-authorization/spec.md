# Feature Specification: Storage-Backed Authorization (Roles in the Message Store)

Feature Branch: `026-storage-backed-authorization`
Created: 2026-07-27
Status: Implemented (phases A–E landed 2026-07-27)
Input: User description: "Implement the same authorization feature as in DIS: roles — reader, contributor, owner and PII reader — stored in storage (Cosmos and/or SQL), not relying on Entra ID groups only."

## Problem

Before this spec, WebApp authorization hinged on a single marker — a `groups == "EIP_Management"` claim — plus code-defined `endpoint.RoleAssignments` object ids. That model had three structural problems:

1. **All-or-nothing.** A user was either a platform admin or (endpoint RoleAssignments aside) had no access at all. There was no way to grant "may look but not act", "may act on this endpoint only", or "may see raw payloads".
2. **Entra-coupled.** Entra tokens carry group object ids, never names, so the literal marker check only worked through config mapping (`EntraAdminClaimsTransformation`) or the LocalDev handler. Granting access required Azure-side changes, not a UI action.
3. **Ungated reads.** Cross-endpoint message search, metrics, the event-type catalog and several payload-returning event GETs had no authorization at all.

DIS solved this with storage-backed access-control lists. This spec ports that model, fixing two known DIS gaps on the way: DIS has **no bootstrap path** (an empty ACL locks everyone out, including the Access Control page itself) and identifies users by **email string only** (grants silently orphan on mailbox rename).

## The model

### Roles

| Role | Scope | Grants |
|---|---|---|
| **Reader** | site or endpoint | View endpoints, events, metrics, audits, metadata, event types |
| **Contributor** | site or endpoint | Reader + resubmit, resubmit-with-changes, skip, report, handoff settle, compose, reprocess, comment, delete-invalid |
| **Owner** | site or endpoint | Contributor + purge, endpoint enable/disable + send-status, metadata writes, subscription admin, role grants for the scope. **Site** Owner additionally: every `/api/admin/*` operation, cross-endpoint audit search, the site access-control lists, purge in prod/staging |
| **PII Reader** | site only | May view raw event payloads and use payload-content search predicates. **Orthogonal to the ladder** — an Owner does NOT see payloads without it (spec 021) |

- Ladder: `Owner(3) > Contributor(2) > Reader(1) > None(0)`; higher implies lower.
- **Effective endpoint role = max(site role, endpoint role).** Deliberate improvement over DIS, whose non-None site role short-circuits the per-endpoint scan entirely (a DIS site Reader with an endpoint Owner grant stays Reader there). Grants still only ever *raise* access.
- Entries are **opaque strings: an email address OR an Entra object id**. Matching compares the principal's email-shaped claims (email, `upn`, `preferred_username`, `name` — values containing `@`, lowercased/trimmed) and the `oid` claim against entries case-insensitively. Non-email display names never match.

### Compat union (also the bootstrap)

Effective access = store grants ∪ claim-based compat grants. Compat grants:

- `groups == "EIP_Management"` claim ⇒ site **Owner**. The claim is materialized by `LocalDevAuthHandler` (dev), `EntraAdminClaimsTransformation` (`Authorization:AdminGroupObjectIds` / `AdminUserObjectIds` config), and the Identity role→groups mapping — all three therefore keep working unchanged.
- Code-defined `endpoint.RoleAssignments` oid match ⇒ endpoint **Owner**.
- `BypassEndpointAuthorization` (Development only, startup fail-fast) ⇒ site Owner.

The union means a **fresh, empty store never locks out configured admins** — the first Owner signs in via compat and grants store roles in the UI. Compat can never be *reduced* by store contents. **PII Reader has no compat implication**: store grant, an Entra `PiiReader` app role, or the Development-only `Authorization:GrantPiiReaderInDevelopment` flag (startup fail-fast outside Development) are the only paths.

## Storage

One store concern, `IAccessControlStore` (`NimBus.MessageStore.Abstractions`), aggregated into `INimBusMessageStore` so every provider is compile-forced to implement it:

```
GetSiteAccessControl / SetSiteAccessControl
GetEndpointAccessControl(endpointId) / SetEndpointAccessControl / GetEndpointAccessControls()
```

Entity `AccessControlList`: `Id` (`"site"` or `"endpoint:{endpointId}"` — the prefix prevents an endpoint literally named "site" from colliding), `EndpointId`, `Readers[]`, `Contributors[]`, `Owners[]`, `PiiReaders[]` (site doc only), `UpdatedAtUtc`. Whole-document replace on write; entries persist verbatim (normalization is the WebApp's concern, dedupe is the API layer's).

| Provider | Shape |
|---|---|
| Cosmos DB | Container `accesscontrol`, PK `/id`, lazily created; point-read for the site doc, `STARTSWITH(c.id, "endpoint:")` scan for endpoint docs |
| SQL Server | `Schema/0017_AccessControl.sql`: `AccessControl(Id NVARCHAR(220) PK, ContentJson NVARCHAR(MAX), UpdatedAtUtc DATETIME2)`; listed in `SqlServerSchemaInitializer.RequiredTables` |
| InMemory | `ConcurrentDictionary` with copy-on-read/write |

Deliberate divergence from DIS: per-endpoint ACLs do NOT piggyback on `EndpointMetadata` — NimBus's metadata write path constructs a fresh document without reading first and would clobber them.

Conformance: `AccessControlStoreConformanceTests` in `NimBus.Testing` + one subclass per provider test project (Cosmos/SQL env-gated as usual).

## Enforcement

`IEndpointAuthorizationService` (scoped) resolves once per request:

- `HasRoleAsync(AccessRole required, string? endpointId = null)` — the single check every controller gate calls.
- `CanReadPiiAsync()` — realizes spec 021's `CurrentUserCanReadPii()`.
- `GetCurrentUserAccessAsync()` — full resolution for `/api/access-control/me`.

The old `IsManagerOfEndpoint`/`IsPlatformAdministrator`/`GetMessageAuditEntity` members are removed; all ~50 call sites migrated. Checks stay imperative per-method (DIS parity); every privileged denial writes an access-denied audit via `IAuditLogService`.

**Caching**: `AccessControlSnapshotProvider` (singleton) caches the full ACL snapshot (site + all endpoint docs) for 45s with single-flight loading. Mutations invalidate it, so grants take effect immediately on that instance; other instances converge within the TTL (DIS accepts the same). Store faults serve last-known-good, else an empty snapshot for 5s — store roles fail closed while compat claims keep admins in.

**PII redaction (phase D, interim until spec 021's field-level masking)**: non-PiiReaders get `EventJson`/`EventContent` replaced server-side with the literal `[REDACTED]` (`PayloadRedaction`) on event details (every return path), event/message lookups, history, App Insights logs, endpoint-status event lists and message-search results; subscription payload filters are nulled. Payload-content search predicates are rejected with 403 for non-PiiReaders (a hit/miss count is an oracle even with redacted results). The SPA renders a lock notice for `[REDACTED]` payloads.

## Management surface

API (tag `AccessControl`, NSwag-generated from `api-spec.yaml`):

| Route | Gate |
|---|---|
| `GET /api/access-control`, `POST/DELETE /api/access-control/roles` | site Owner |
| `GET/POST/DELETE /api/access-control/endpoint/{endpointId}[/roles]` | endpoint Owner; role `piiReader` ⇒ 400 (site-scoped) |
| `GET /api/access-control/me` | authenticated |

Mutations are read-modify-write with case-insensitive trim/dedupe (grant) and case-insensitive removal (revoke), audited as `GrantRole`/`RevokeRole` (appended to `MessageAuditType`; Data = `{scope, role, entry}`), and bump the snapshot generation.

SPA: `/AccessControl` page — four site role cards + endpoint-scoped section (three roles, Owner-filtered endpoint selector). Sidebar shows Admin to site Owners and Access Control to site/endpoint Owners (render-side only; the server enforces). `hooks/use-access.ts` caches `/api/access-control/me` per page load.

## Non-goals / deferred

- **SignalR grant reconciliation**: `GridEventsHub` still broadcasts endpoint state *counts* (never payloads) to any authenticated user — spec 010 follow-up.
- **Field-level `[Sensitive]` masking**: spec 021; this spec's whole-payload redaction is the interim gate and 021's reveal role now exists.
- **Concurrent ACL edit protection**: read-modify-write race accepted for v1; ETag (Cosmos) / rowversion (SQL) noted as future hardening.
- Retrofitting Entra group syncing, or removing the compat union — `EIP_Management` ⇒ site Owner is permanent, not just bootstrap.

## Operational notes

- Fresh deploys: SQL `0017` auto-applies via DbUp (`VerifyOnly` deployments must apply it before flipping). Cosmos: the `accesscontrol` container is declared in `deploy/bicep/templates/cosmosDB.bicep` — lazy SDK creation only works with account keys, because Entra data-plane RBAC (which deployed apps use) permits item reads/writes but NOT container management. Reads fail soft (empty lists, compat keeps admins in) but grants fail until the container exists.
- Rollout is zero-downtime for existing sites: with an empty store, compat reproduces the pre-026 access exactly; the Reader floor (phase D) is the only tightening — previously any authenticated user could read metrics/search/event types.
- Bootstrap runbook: ensure `Authorization__AdminUserObjectIds` (or `AdminGroupObjectIds`) covers the first operator → they sign in as site Owner → grant store roles on the Access Control page.
