# Feature Specification: Require Authentication on the GridEventsHub SignalR Hub

Feature Branch: `010-authorize-gridevents-hub`
Created: 2026-05-28
Updated: 2026-05-28
Status: Proposed (back-port candidate from DIS; see `BH.DIS.WebApp/Hubs/GridEventsHub.cs` lines 1-17).
Input: User description: "The management WebApp's SignalR hub `GridEventsHub` (mapped at the path defined by `Constants.AppEndpoints.GridEventHub`) carries real-time endpoint-status updates that are published from the storage-hook write path. The rest of the WebApp surface — MVC actions, API controllers, the SPA shell — runs behind a global `[Authorize]` policy. The hub does not. Anonymous WebSocket clients can connect, negotiate, and receive every broadcast intended for authenticated operators. We want `[Authorize]` on the hub class, matching the rest of the surface."

## Problem

NimBus's management WebApp uses SignalR (one hub today: `NimBus.WebApp.Hubs.GridEventsHub`) to push real-time updates to the SPA. The current shipping behaviour:

- The hub is declared as `public class GridEventsHub : Hub { }` — no authorize attribute.
- The hub is mapped via `endpoints.MapHub<GridEventsHub>(Constants.AppEndpoints.GridEventHub)`.
- Server-side, `StorageHookImplementation` (or its NimBus equivalent — the resolver's hook receiver) publishes `EndpointUpdate` messages to all connected clients whenever an endpoint's projected counts change.
- Client-side, the SPA opens a SignalR connection during initial render and subscribes to `EndpointUpdate` to keep the dashboards live.

The rest of the WebApp surface runs behind a global `AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()` filter (when `!Env.IsDevelopment()`) — anonymous browsers are challenged through whatever the registered auth schemes do by default. NimBus does NOT today configure a custom `OnRedirectToLogin` event handler on its cookie scheme that distinguishes API requests from MVC requests (verified: no override in `src/NimBus.Extensions.Identity/ServiceCollectionExtensions.cs` and no override in `src/NimBus.WebApp/Startup.cs`); the default cookie-scheme behaviour redirects unauthenticated browser requests to the login URL. Even the development auth handler returns an *authenticated* principal; nothing in the WebApp's design assumes an anonymous principal as a valid identity.

The hub is the one exception. SignalR's hub routing does not inherit the MVC global `[Authorize]` filter — it has its own attribute-driven authorization model. An anonymous WebSocket client (or HTTP polling fallback) that calls `POST /hubs/gridevents/negotiate` followed by the upgrade can connect, receive `EndpointUpdate` broadcasts, and observe the count deltas — including the integer counts of failed, deferred, pending, and pending-handoff messages per endpoint.

The leaked information is low-sensitivity (counts only, no payloads) but the exposure is still:

- Inconsistent with the rest of the surface, where every other read path requires auth.
- A small but real reconnaissance surface — an unauthenticated observer learns the platform's endpoint shape, traffic shape, and failure rhythm.
- A reasonable audit-finding for any compliance-conscious operator.

The fix is one attribute: `[Authorize]` on the hub class. Browser clients already carry the auth cookie when negotiating the connection, so there is zero friction for legitimate users.

## Scope

In scope:
- Adding `[Authorize]` to `NimBus.WebApp.Hubs.GridEventsHub`.
- Verifying that the SignalR negotiate / connection path respects the attribute — anonymous clients receive 401, authenticated clients connect normally.
- A brief code comment explaining the rationale at the attribute site.
- Updating tests that simulate hub connections to authenticate.

Out of scope:
- A per-method `[Authorize(Roles = ...)]` granularity. The hub today serves a single broadcast type that every authenticated user is allowed to receive. Per-role filtering of broadcast subsets is a separate feature.
- A multi-hub authorization policy framework. v1 covers the one hub that exists.
- Connection-scoped per-endpoint authorization (e.g., "only deliver `EndpointUpdate` for endpoints this user is a manager of"). The current broadcast pattern is fan-out; per-user filtering would require refactoring the publish path.
- Adding a new SignalR transport or protocol. Use the existing default transport selection (`WebSockets` → `ServerSentEvents` → `LongPolling`).
- A CSRF / origin check at the negotiate step beyond what `[Authorize]` and the cookie scheme already give. Standard SignalR cookie-auth has these covered.

## User Scenarios & Testing

### User Story 1 - Anonymous client cannot connect (Priority: P1)

As a security-conscious operator, I want an unauthenticated browser (or attacker) that opens a WebSocket to `/hubs/gridevents` to be rejected with 401, so the broadcast surface is not anonymously observable.

Why this priority: This is the entire point of the spec.

Independent Test: Open a WebSocket client (e.g., `wscat`) against the hub negotiate path without a cookie. Expect 401. Repeat with the cookie scheme; expect a successful upgrade.

Acceptance Scenarios:

1. Given an HTTP client that does not present an authentication cookie or bearer token, When it POSTs to `/hubs/gridevents/negotiate?negotiateVersion=1`, Then the response is 401 Unauthorized.
2. Given a SignalR client SDK with no auth configured, When it calls `start()`, Then the start promise rejects with an HTTP 401 error.
3. Given the same anonymous client (Negotiate has succeeded for an authenticated session, then auth expired), When the cookie expires mid-session and the client attempts to reconnect, Then the reconnect fails with 401 and the SPA shows the standard "session expired" affordance.

---

### User Story 2 - Authenticated browser sees broadcasts normally (Priority: P1)

As an operator who has signed into the WebApp, I want the hub to continue working exactly as today — endpoint counts update live, the dashboards refresh, SignalR transports negotiate without intervention.

Why this priority: Any regression here defeats the spec by breaking the dashboard.

Independent Test: Sign in. Open the Endpoints page. Trigger a state change on any endpoint. Confirm the row's count updates without a page refresh.

Acceptance Scenarios:

1. Given an authenticated browser session, When the SPA initialises the SignalR connection, Then the connection succeeds (WebSocket transport when supported).
2. Given the connection is up, When a state-change event is published server-side, Then the client receives the `EndpointUpdate` invocation and the dashboard cell updates.
3. Given the user logs out, When the next reconnect is attempted, Then the connection fails with 401 and the SPA falls back to its existing offline indicator (no JavaScript console error spam).

---

### User Story 3 - Local-development bypass continues to work (Priority: P1)

As a NimBus developer running the local Aspire stack, I want the hub to continue connecting under the development auth bypass — without needing to log in to a real AAD tenant.

Why this priority: The local dev story must not regress. NimBus's `LocalDevAuthHandler` returns a fixed authenticated principal under dev; the hub should accept it just like every other authorized surface does.

Independent Test: Run the AppHost in development. Open the WebApp. Confirm `LocalDevAuthHandler` issues the dev principal, the SignalR connection succeeds, and the dashboards update live.

Acceptance Scenarios:

1. Given the WebApp is running with `LocalDevAuthHandler` enabled (and explicit dev opt-in per its own gate), When the SPA initialises the SignalR connection, Then the connection succeeds against the dev principal.
2. Given the WebApp is running in development WITHOUT the dev auth handler enabled (the explicit opt-in is off), When the SPA initialises the SignalR connection, Then the connection fails with 401 — the same failure mode as production.

---

### User Story 4 - Backwards-compatible at the API/contract level (Priority: P2)

As a NimBus operator running a CI pipeline that today connects to the hub anonymously (legacy / accidental usage), I want a clear failure with a remediation path, not a silent change in observed behaviour.

Why this priority: Catches the very small probability that some external script connects today. The clear 401 plus documentation is the remediation path.

Independent Test: A CI smoke-test script that opens an anonymous connection today, after the change, fails with 401 and a documentation pointer.

Acceptance Scenarios:

1. Given an anonymous external client, When the connection is attempted, Then the response is 401 and the server logs include `LogInformation`-level entry naming the rejection (one per unique source over a short window, to avoid log floods).
2. Given the release notes for the version that ships this change call out the new authentication requirement on the hub, When an operator reads them, Then they know what to do (sign in or configure a service principal, depending on their setup).

---

## Edge Cases

- A browser whose auth cookie was issued by a previous build (pre-hub-authorize) — the cookie is still a valid principal; the hub accepts the connection. No upgrade-path regression.
- A SignalR client polling-fallback connection (long-polling) — the same `[Authorize]` attribute applies on the negotiate request and on each polled `POST`. Anonymous polling is rejected per-request.
- A reconnect during a transient network blip — SignalR's automatic reconnect uses the existing cookie / token, which is still valid. The reconnect succeeds without re-auth.
- The cookie expires while a connection is open. Established connections continue until the next reconnect (matches the rest of the WebApp's behaviour — auth is at the connection / request boundary).
- A misconfigured deployment in production with `BypassEndpointAuthorization = true` — that flag affects `EndpointAuthorizationService.IsManagerOfEndpoint`, not authentication. The hub still requires an authenticated principal. (Authentication and authorization are distinct.)
- An automated health-check probe that today opens a connection anonymously — the probe will fail with 401. Operators reconfigure the probe to use a service principal, or remove it (the standard `/health` endpoint is the right probe surface, not the hub).
- A development browser with the global authorize filter skipped (production-flag `Env.IsDevelopment()` true) — the filter skip applies to MVC; SignalR has its own attribute-based pipeline. `[Authorize]` on the hub still triggers, but routes to the dev scheme which returns an authenticated principal. The connection succeeds.

## Requirements

### Functional Requirements

#### Hub change

- FR-001: `NimBus.WebApp.Hubs.GridEventsHub` MUST be decorated with `[Authorize]` from the `Microsoft.AspNetCore.Authorization` namespace.
- FR-002: The attribute MUST be applied at the class level. Per-method `[Authorize]` is not required because the hub has no `[AllowAnonymous]` carve-outs.
- FR-003: A brief code comment above the attribute MUST explain the rationale: the hub broadcasts endpoint state to all authenticated operators; the attribute closes the anonymous-WebSocket path. (Single-line; the comment in DIS is a useful starting point.)

#### Pipeline behaviour (verification, not change)

- FR-010: The SignalR `MapHub<GridEventsHub>(...)` registration MUST run in a section of `Configure(...)` where authentication and authorization middleware are already wired. NimBus's existing `app.UseAuthentication(); app.UseAuthorization();` ordering before `app.UseEndpoints(...)` is sufficient. (Verify; no change expected.)
- FR-011: A cookie-scheme `OnRedirectToLogin` event handler MUST be added so anonymous requests to the hub path return **401 Unauthorized** instead of a 302 redirect to the login URL. Without this, an anonymous SignalR negotiate gets a 302 the SignalR client cannot follow — a confusing failure mode that surfaces as "negotiate failed: unexpected response" rather than a clean auth error. The handler is **new work**, not an extension to an existing override:
  - NimBus does not currently configure `OnRedirectToLogin`. Default behaviour is to redirect every anonymous browser request to `/Account/Login` (or the configured equivalent).
  - The handler MUST be added in the cookie-scheme configuration block in `src/NimBus.Extensions.Identity/ServiceCollectionExtensions.cs` (or wherever the cookie scheme is registered for the current identity mode — local-identity vs. Entra). It MUST check the request path against `Constants.AppEndpoints.GridEventHub` (`/hubs/gridevents`) and return 401 for that branch.
  - The handler SHOULD also short-circuit anonymous `/api/*` requests to 401 (matches DIS) so the SPA's API surface returns a clean 401 rather than a 302 the fetch caller will misinterpret as an opaque CORS / redirect failure. This is an opportunistic improvement carried by the same change set.
- FR-012: Equivalent `OnRedirectToAccessDenied` handling SHOULD be added in the same block, returning **403 Forbidden** for hub and API paths instead of the default redirect to `/Account/AccessDenied`. Symmetric with FR-011 and the same one-line check.

#### Client (verification, not change)

- FR-020: The SPA's SignalR connection setup MUST continue to use the default cookie credentials (`withCredentials: true` on the `HttpConnectionOptions`, which is the default with cookie auth). No code change needed; the cookie is sent automatically. Verify in the existing client wiring.
- FR-021: The SPA MUST tolerate the connection failing with 401 by displaying its existing "session expired / sign in again" affordance (already present, since the rest of the API surface can return 401).

#### Tests

- FR-030: An integration test MUST verify that an anonymous HTTP client receives 401 on the SignalR negotiate POST.
- FR-031: An integration test MUST verify that a client carrying the dev-auth principal (in test environment, the `LocalDevAuthHandler` or a stub) negotiates and connects successfully.
- FR-032: An integration test MUST verify that broadcasts are received by the authenticated client (smoke test of the `EndpointUpdate` invocation path).
- FR-033: Existing hub tests, if any, MUST be updated to authenticate. Anonymous-connection tests are rewritten to assert the 401 instead of a successful negotiate.

#### Documentation

- FR-040: Release notes (per `docs/release-notes/` or equivalent) MUST call out that `GridEventsHub` now requires authentication and that anonymous WebSocket consumers will receive 401.
- FR-041: `docs/architecture.md` (or the WebApp authentication doc) MUST note that the SignalR hub is part of the global authorize surface — closing a previous gap where the hub was anonymous.

### Non-Functional Requirements

- NFR-001: The change MUST add zero latency to the connection path. `[Authorize]` is a single attribute lookup; the cookie validation runs anyway as part of auth middleware.
- NFR-002: The change MUST be invisible to legitimate users. Authenticated browsers connect, reconnect, and receive broadcasts exactly as today.
- NFR-003: No new dependency. `Microsoft.AspNetCore.Authorization` is already referenced.
- NFR-004: The rejection MUST be auditable. Each 401 on the hub MUST surface in standard request-logging (the `Microsoft.AspNetCore.Authentication` and `Microsoft.AspNetCore.Authorization` loggers already produce `LogInformation` entries on rejection). No bespoke audit-row write is needed — the rejection happens before any user-identifying context is established.
- NFR-005: The change MUST be reversible — removing the attribute restores the pre-spec behaviour. There is no migration, no schema change, no contract change.

## Key Entities

- **`GridEventsHub`** — existing SignalR `Hub`. Single change: `[Authorize]` attribute on the class.
- **Cookie scheme `CookieAuthenticationEvents` (NEW)** — currently absent on NimBus. Added in the cookie-scheme registration (in `NimBus.Extensions.Identity.ServiceCollectionExtensions` or the equivalent Entra registration), with `OnRedirectToLogin` returning 401 and `OnRedirectToAccessDenied` returning 403 for hub-path and `/api/*` requests.
- **`Constants.AppEndpoints.GridEventHub`** — existing constant (`/hubs/gridevents`) naming the hub path. Used for both the `MapHub<>(...)` call and the cookie-scheme redirect-check.

## Success Criteria

### Measurable Outcomes

- SC-001: An anonymous client `POST /hubs/gridevents/negotiate?negotiateVersion=1` returns 401. Verified by integration test FR-030.
- SC-002: An authenticated browser session continues to receive live `EndpointUpdate` broadcasts. Verified by integration test FR-031 and FR-032, and by manual confirmation against a running local stack.
- SC-003: Local-development connections (under `LocalDevAuthHandler`) continue to work without code change. Verified by running the Aspire stack and observing live dashboards.
- SC-004: The connection failure surface in the SPA is the existing 401-handling path; no new error UI is required.
- SC-005: No existing WebApp test fails as a result of the change. Tests that previously connected anonymously are updated.

## Assumptions

- The hub today contains only the default `Hub` lifecycle methods and broadcast endpoints — no `[AllowAnonymous]` carve-outs that the attribute would conflict with. Verified by inspection (`GridEventsHub.cs` is currently a default `Hub` subclass with an empty constructor).
- The SPA's SignalR client setup uses the cookie scheme by default. Verified by the existing wiring; `withCredentials` is on for the connection.
- No `CookieAuthenticationEvents` are configured on the cookie scheme today; the FR-011 / FR-012 handlers are net-new code, not extensions to an existing handler.
- The platform's existing dev auth handler (`LocalDevAuthHandler`) returns an authenticated principal that satisfies `[Authorize]` without role requirements. Verified.

## Out of Scope

- A per-endpoint hub authorization gate ("only push `EndpointUpdate` for endpoints this user manages"). The current broadcast model is global fan-out; per-user filtering is a separate refactor.
- Additional hubs (none today). The attribute pattern documented here is the template if a second hub is added.
- A token-based auth scheme on the hub for service-to-service consumers. Cookie auth is the current pattern; bearer auth can be added later under the same `[Authorize]` policy.
- A CSRF check on the negotiate endpoint beyond what the cookie scheme already provides.
- A dashboard or metric tracking hub connection volume by auth state. Standard request-logging is the v1 observability surface.

## Open Questions

- **Should we add an explicit `[Authorize]` policy name, or use the default `RequireAuthenticatedUser` policy?** Default is sufficient — the rest of the WebApp surface uses the default policy too. Adding a named policy would diverge for no benefit.

## Resolved Questions

- `[Authorize]` at the class level, not per-method. Resolved — single attribute, zero carve-outs, lowest-friction landing.
- Add a new `CookieAuthenticationEvents.OnRedirectToLogin` handler that returns 401 on hub and `/api/*` paths. Resolved — NimBus does not currently configure this event; adding it is mandatory for the spec to deliver the promised behaviour, otherwise anonymous negotiate gets a 302 the SignalR client cannot follow.
- Hub path is `/hubs/gridevents` (verified at `src/NimBus.WebApp/Constants.cs:12` and the `MapHub<>` registration in `Startup.cs`). Resolved — earlier drafts referenced `/grideventshub` from a DIS naming pattern; NimBus's constant is canonical.
- The change does not require a contract or schema change. Resolved — additive policy attribute; no migration; reversible.
- Local-dev continues to work via the existing `LocalDevAuthHandler`. Resolved — the attribute is satisfied by any authenticated principal, including the dev stub.
- No bespoke audit-row writing on rejected connections. Resolved — the standard ASP.NET Core auth loggers already produce the relevant log entries; promoting them to an audit row would require user identity, which is not yet established at the rejection point.
